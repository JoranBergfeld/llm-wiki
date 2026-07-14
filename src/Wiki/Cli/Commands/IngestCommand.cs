using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;
using Wiki.State;

namespace Wiki.Cli.Commands;

// `wiki ingest ...` command group (spec §10/§8): the ledger state-machine CLI
// - status/advance/resume wrap IngestService's precondition-checked
// transitions and the "fresh session, zero context" resume guarantee.
public static class IngestCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var ingest = new Command("ingest", "Drive a source through the ingest ledger (registered -> summarized -> integrated -> linted)");
        ingest.Add(BuildStatus(vaultOption, jsonOption, stdout, stdin));
        ingest.Add(BuildAdvance(vaultOption, jsonOption, stdout, stdin));
        ingest.Add(BuildResume(vaultOption, jsonOption, stdout, stdin));
        return ingest;
    }

    private static Command BuildStatus(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var sourceIdArgument = new Argument<string?>("source-id")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Source id to show; omit to list every source not yet 'linted'",
        };

        var status = new Command("status", "Show ledger state: one source's entry, or every source not yet 'linted'")
        {
            sourceIdArgument,
        };

        status.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var sourceId = parseResult.GetValue(sourceIdArgument);

            var vault = ctx.ResolveVault();
            var service = new IngestService();
            var entries = service.Status(vault, sourceId);

            if (sourceId is not null)
            {
                var single = ToData(entries[0]);
                if (ctx.Json)
                {
                    ctx.EmitOk(single);
                }
                else
                {
                    RenderStatusPanel(ctx.Out, single);
                }
            }
            else
            {
                var data = entries.Select(ToData).ToArray();
                if (ctx.Json)
                {
                    ctx.EmitOk(data);
                }
                else
                {
                    RenderStatusTable(ctx.Out, data);
                }
            }
            return 0;
        }));

        return status;
    }

    private static Command BuildAdvance(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var sourceIdArgument = new Argument<string>("source-id")
        {
            Description = "Source id whose ledger entry to advance",
        };
        var toOption = new Option<string>("--to")
        {
            Required = true,
            Description = "Target ledger state: summarized | integrated | linted",
        };
        var touchedOption = new Option<string?>("--touched")
        {
            Description = "Comma-separated ids of entity/concept pages touched integrating this source (required for --to integrated; may be empty)",
        };

        var advance = new Command("advance", "Validate the target state's precondition and record the ledger transition")
        {
            sourceIdArgument,
            toOption,
            touchedOption,
        };

        advance.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var sourceId = parseResult.GetRequiredValue(sourceIdArgument);
            var to = LedgerStateX.Parse(parseResult.GetRequiredValue(toOption));
            var touched = SplitCsv(parseResult.GetValue(touchedOption));

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfig();
            var service = new IngestService();
            service.Advance(vault, cfg, sourceId, to, touched);

            ctx.EmitOk(new IngestAdvanceResult(sourceId, LedgerStateX.ToWire(to)));
            return 0;
        }));

        return advance;
    }

    private static Command BuildResume(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var sourceIdArgument = new Argument<string>("source-id")
        {
            Description = "Source id to resume ingest for",
        };

        var resume = new Command("resume", "Print exactly what remains for a source: states + expected artifacts")
        {
            sourceIdArgument,
        };

        resume.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var sourceId = parseResult.GetRequiredValue(sourceIdArgument);

            var vault = ctx.ResolveVault();
            var service = new IngestService();
            var plan = service.Resume(vault, sourceId);

            var view = new ResumePlanView(plan.SourceId, LedgerStateX.ToWire(plan.Current), plan.RemainingStates, plan.ExpectedArtifacts);
            if (ctx.Json)
            {
                ctx.EmitOk(view);
            }
            else
            {
                RenderResumePanel(ctx.Out, view);
            }
            return 0;
        }));

        return resume;
    }

    private static LedgerEntryData ToData(LedgerEntry e) => new()
    {
        SourceId = e.SourceId,
        State = LedgerStateX.ToWire(e.State),
        Touched = e.Touched,
        IntegratedAt = e.IntegratedAt,
        RegisteredAt = e.RegisteredAt,
    };

    private static string[] SplitCsv(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void RenderStatusTable(TextWriter output, System.Collections.Generic.IReadOnlyList<LedgerEntryData> entries)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("SourceId");
        table.AddColumn("State");
        table.AddColumn("Touched");
        table.AddColumn("RegisteredAt");
        table.AddColumn("IntegratedAt");

        foreach (var e in entries)
        {
            table.AddRow(
                Markup.Escape(e.SourceId),
                Markup.Escape(e.State),
                Markup.Escape(string.Join(", ", e.Touched)),
                Markup.Escape(e.RegisteredAt ?? ""),
                Markup.Escape(e.IntegratedAt ?? ""));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{entries.Count} source(s) not yet linted[/]");
    }

    private static void RenderStatusPanel(TextWriter output, LedgerEntryData e)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var body = new System.Text.StringBuilder();
        body.Append("[bold]state[/]: ").AppendLine(Markup.Escape(e.State));
        body.Append("[bold]touched[/]: ").AppendLine(Markup.Escape(string.Join(", ", e.Touched)));
        body.Append("[bold]registered_at[/]: ").AppendLine(Markup.Escape(e.RegisteredAt ?? ""));
        body.Append("[bold]integrated_at[/]: ").AppendLine(Markup.Escape(e.IntegratedAt ?? ""));

        var panel = new Panel(body.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader(Markup.Escape(e.SourceId)),
        };
        console.Write(panel);
    }

    private static void RenderResumePanel(TextWriter output, ResumePlanView view)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var body = new System.Text.StringBuilder();
        body.Append("[bold]current[/]: ").AppendLine(Markup.Escape(view.Current));
        body.Append("[bold]remaining[/]: ").AppendLine(Markup.Escape(string.Join(" -> ", view.RemainingStates)));
        body.AppendLine("[bold]expected artifacts:[/]");
        foreach (var artifact in view.ExpectedArtifacts)
            body.Append("  - ").AppendLine(Markup.Escape(artifact));

        var panel = new Panel(body.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader(Markup.Escape(view.SourceId)),
        };
        console.Write(panel);
    }
}
