using System.CommandLine;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;
using Wiki.State;

namespace Wiki.Cli.Commands;

// `wiki audit next|record|list` (issue #12): faithfulness auditing, split so
// the CLI never needs an LLM.
//
//   wiki audit next --json         -> deterministically selects a page and
//                                     emits it plus its cited source ids
//      the agent reads the page and the raw sources COLD and judges
//   wiki audit record <page-id> --verdict supported|unsupported --note "…"
//                                  -> records the verdict; `unsupported`
//                                     files an issue
//
// `next` is read-only and files nothing. `record` is the only writer here.
// Neither is ever part of `wiki lint` - lint must stay deterministic and
// free, and this is a separate command and a separate tick rung.
public static class AuditCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var audit = new Command("audit",
            "Faithfulness auditing: the CLI selects and records, the agent judges, the human disposes");
        audit.Add(BuildNext(vaultOption, jsonOption, stdout, stdin));
        audit.Add(BuildRecord(vaultOption, jsonOption, stdout, stdin));
        audit.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        audit.Add(BuildReanchor(vaultOption, jsonOption, stdout, stdin));

        // Bare `wiki audit` (issue #44): the two integrity reports side by
        // side and clearly apart - the audit log's hash chain (is the history
        // of changes intact?) and page faithfulness (are pages true to their
        // sources?). They answer different questions and neither stands in
        // for the other. Read-only: a broken chain is reported, not repaired
        // (mutations refuse a broken chain; `audit reanchor` restores them), so the exit code is 0
        // and the agent branches on chain.intact.
        //
        // `--deep` (issue #45) also verifies every archived segment and the
        // links between them; without it only the live segment is read, so
        // the check stays cheap however long the history grows.
        var deepOption = new Option<bool>("--deep")
        {
            Description = "Also verify every archived audit-log segment and the links between segments",
        };
        audit.Add(deepOption);

        audit.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            var log = Wiki.Audit.AuditLog.For(vault);
            var chain = log.VerifyLive();
            var deep = parseResult.GetValue(deepOption) ? log.VerifyDeep() : null;
            var faithfulness = new AuditService().Summary(vault);
            var report = new AuditReport(chain, faithfulness, deep);

            if (ctx.Json)
                ctx.EmitOk(report);
            else
                RenderReport(ctx.Out, report);
            return 0;
        }));
        return audit;
    }

    private static void RenderReport(TextWriter output, AuditReport report)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });
        var c = report.Chain;

        console.MarkupLine("[bold]Audit log chain[/] ([grey]" + Markup.Escape(c.Segment) + " segment, " + Markup.Escape(c.Path) + "[/])");
        if (!c.Exists)
            console.MarkupLine("  [grey]no audit log yet[/]");
        else if (c.Intact)
            console.MarkupLine($"  [green]intact[/]: {c.Entries} line(s)");
        else
        {
            console.MarkupLine($"  [red]BROKEN[/]: {c.Problems.Length} problem(s) in {c.Entries} line(s)");
            foreach (var p in c.Problems)
                console.MarkupLine($"  - line {p.Line}: {Markup.Escape(p.Code)} - {Markup.Escape(p.Message)}");
        }

        if (report.Deep is { } deep)
        {
            console.MarkupLine("[bold]Full chain[/] (--deep): " + (deep.Intact ? "[green]intact[/]" : "[red]BROKEN[/]"));
            foreach (var s in deep.Segments)
            {
                var state = s.Intact ? "[green]intact[/]" : "[red]BROKEN[/]";
                console.MarkupLine($"  {Markup.Escape(s.Segment)}: {state}, {s.Entries} line(s)" + (s.Reanchored ? " [grey](re-anchored)[/]" : ""));
                foreach (var p in s.Problems)
                    console.MarkupLine($"    - line {p.Line}: {Markup.Escape(p.Code)} - {Markup.Escape(p.Message)}");
            }
        }

        var f = report.Faithfulness;
        console.MarkupLine("[bold]Page faithfulness[/]");
        console.MarkupLine($"  {f.Audited} audited page(s): {f.Supported} supported, {f.Unsupported} unsupported");
    }

    private static Command BuildNext(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var next = new Command("next",
            "Select the page most worth auditing and emit it with the ids of the sources it cites");

        next.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            var service = new AuditService();
            var target = service.Next(vault);

            if (ctx.Json)
            {
                ctx.EmitOk(target);
            }
            else
            {
                RenderTarget(ctx.Out, target);
            }
            // "Nothing to audit" is a legitimate, common answer, not an error
            // the agent has to catch - exit 0 with hasTarget:false and a reason.
            return 0;
        }));

        return next;
    }

    private static Command BuildRecord(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("page-id")
        {
            Description = "Page id (ULID) the verdict applies to",
        };
        // Not CLI-Required: an unknown value is a blocking validation
        // (code=invalid-verdict) raised by the service, so the agent gets our
        // envelope rather than System.CommandLine's usage text.
        var verdictOption = new Option<string>("--verdict")
        {
            Required = true,
            Description = "supported | unsupported",
        };
        var noteOption = new Option<string?>("--note")
        {
            Description = "What was found. Required for 'unsupported': name the claim and the source that fails to support it",
        };

        var record = new Command("record",
            "Record a faithfulness verdict; 'unsupported' files an unsupported-claim issue")
        {
            idArgument,
            verdictOption,
            noteOption,
        };

        record.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var pageId = parseResult.GetRequiredValue(idArgument);
            var verdict = parseResult.GetRequiredValue(verdictOption);
            var note = parseResult.GetValue(noteOption);

            var vault = ctx.ResolveVault();
            var service = new AuditService();
            var result = service.Record(vault, pageId, verdict, note);
            ctx.EmitOk(result);
            return 0;
        }));

        return record;
    }

    private static Command BuildList(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var verdictOption = new Option<string?>("--verdict")
        {
            Description = "Filter to a single verdict: supported | unsupported",
        };

        var list = new Command("list", "List the last recorded verdict per audited page")
        {
            verdictOption,
        };

        list.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var verdict = parseResult.GetValue(verdictOption);

            var vault = ctx.ResolveVault();
            var service = new AuditService();
            var records = service.List(vault, verdict);

            var rows = new AuditRecordData[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                var a = records[i];
                rows[i] = new AuditRecordData
                {
                    PageId = a.PageId,
                    Slug = a.Slug,
                    Verdict = a.Verdict,
                    Note = a.Note,
                    AuditedAt = a.AuditedAt,
                    Audits = a.Audits,
                };
            }

            if (ctx.Json)
            {
                ctx.EmitOk(rows);
            }
            else
            {
                RenderList(ctx.Out, rows);
            }
            return 0;
        }));

        return list;
    }

    // `wiki audit reanchor --reason <text>` (issue #45): the explicit step that
    // restores writes after the live audit chain broke. This command only
    // validates and reports; the archiving itself is done by the audit step
    // on the mutation boundary (AuditStep), under the vault lock, because that
    // step holds the live log open for the whole command.
    private static Command BuildReanchor(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var reasonOption = new Option<string?>("--reason")
        {
            Description = "Why the break is understood and writes may resume. Recorded in the re-anchor line",
        };

        var reanchor = new Command("reanchor",
            "Set a broken live audit log aside as an archived segment and restart the chain from it, recording why")
        {
            reasonOption,
        };

        reanchor.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var reason = parseResult.GetValue(reasonOption)?.Trim();
            if (string.IsNullOrEmpty(reason))
                throw new ValidationException("missing-reason",
                    "--reason is required: say why the break is understood and writes may resume");

            var vault = ctx.ResolveVault();
            var log = Wiki.Audit.AuditLog.For(vault);
            var live = log.VerifyLive();
            if (live.Intact)
                throw new StateConflictException("audit-chain-intact",
                    "the live audit chain is intact; there is nothing to re-anchor", log.LogPath);

            // --- Validation complete ---
            var segment = log.NextSegmentFile();
            ctx.Audit.Action = "audit.reanchor";
            ctx.Audit.Subject = segment;
            ctx.Audit.Field("reason", reason).Field("problems", live.Problems.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ctx.EmitOk(new Wiki.Audit.ReanchorResult(segment, live.Problems.Length, reason));
            return 0;
        }));

        return reanchor;
    }

    private static void RenderTarget(TextWriter output, AuditTarget target)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        if (!target.HasTarget)
        {
            console.MarkupLine($"[grey]{Markup.Escape(target.HumanSummary())}[/]");
            return;
        }

        var body = new System.Text.StringBuilder();
        body.Append("[bold]id[/]: ").AppendLine(Markup.Escape(target.PageId ?? ""));
        body.Append("[bold]why[/]: ").AppendLine(Markup.Escape(target.Why));
        body.Append("[bold]summary[/]: ").AppendLine(Markup.Escape(target.Summary ?? ""));
        body.AppendLine("[bold]cited sources[/]:");
        foreach (var s in target.Sources)
            body.Append("  - ").AppendLine(Markup.Escape($"{s.Id} — {s.Title} ({s.Category}, {s.Status})"));
        if (target.Body is not null)
            body.AppendLine().Append(Markup.Escape(target.Body));

        console.Write(new Panel(body.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader($"[[{target.Slug}]]"),
        });
    }

    private static void RenderList(TextWriter output, AuditRecordData[] rows)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Slug");
        table.AddColumn("Verdict");
        table.AddColumn("Audited");
        table.AddColumn("Times");
        table.AddColumn("Note");

        foreach (var r in rows)
        {
            table.AddRow(
                Markup.Escape(r.Slug),
                Markup.Escape(r.Verdict),
                Markup.Escape(r.AuditedAt),
                r.Audits.ToString(),
                Markup.Escape(r.Note ?? ""));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{rows.Length} audited page(s)[/]");
    }
}
