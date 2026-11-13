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

        // Bare `wiki audit` (issue #44): the two integrity reports side by
        // side and clearly apart - the audit log's hash chain (is the history
        // of changes intact?) and page faithfulness (are pages true to their
        // sources?). They answer different questions and neither stands in
        // for the other. Read-only: a broken chain is reported, not repaired
        // (blocking writes and re-anchoring are #45), so the exit code is 0
        // and the agent branches on chain.intact.
        audit.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            var chain = Wiki.Audit.AuditLog.For(vault).VerifyLive();
            var faithfulness = new AuditService().Summary(vault);
            var report = new AuditReport(chain, faithfulness);

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
