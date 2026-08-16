using System.CommandLine;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki eval [--k N] [--fail-under N]` (issue #11 part A). Read-only: scores
// the vault's routing against the human-owned golden questions in
// `eval.yaml` and never writes anything, not even a log line.
//
// EXIT CODE, decided explicitly. A failing eval is not "your input was
// rejected", so reusing exit 1 for it would muddy the one contract the agent
// branches on. So: reporting is always exit 0, and `--fail-under <score>`
// - opt-in, for CI - exits **4**, a code no other command produces and which
// means only "the measurement came in under the bar you set". Nothing about a
// low score is an error in the sense 1/2/3 describe. See spec amendment W.
public static class EvalCommand
{
    // Matches the retrieval playbook's "select at most 10 candidate pages"
    // budget. If that number ever changes, these two must change together -
    // the metric is only meaningful because it measures the set the agent is
    // actually handed.
    private const int DefaultK = 10;

    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var kOption = new Option<int>("--k")
        {
            DefaultValueFactory = _ => DefaultK,
            Description = $"How many candidate pages the router may surface (default {DefaultK}, matching the retrieval playbook's budget)",
        };
        var failUnderOption = new Option<int?>("--fail-under")
        {
            Description = "Exit 4 when the overall recall score is below this percentage (for CI); otherwise always exits 0",
        };

        var eval = new Command("eval",
            "Score the vault's retrieval against the golden questions in eval.yaml (recall@k). Never part of lint.")
        {
            kOption,
            failUnderOption,
        };

        eval.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var k = parseResult.GetValue(kOption);
            var failUnder = parseResult.GetValue(failUnderOption);

            var vault = ctx.ResolveVault();
            var service = new EvalService();
            var report = service.Run(vault, k);

            if (ctx.Json)
            {
                ctx.EmitOk(report);
            }
            else
            {
                RenderReport(ctx.Out, report);
            }

            // The report is emitted either way before the threshold is
            // applied: a caller that set a bar still needs to see WHICH
            // questions missed, not just that the run failed.
            return failUnder is not null && report.Score < failUnder.Value ? 4 : 0;
        }));

        return eval;
    }

    private static void RenderReport(TextWriter output, EvalReport report)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Question");
        table.AddColumn("Recall");
        table.AddColumn("Missing");

        foreach (var r in report.Results)
        {
            table.AddRow(
                Markup.Escape(r.Ask),
                $"{r.RecallPercent}%",
                Markup.Escape(string.Join(", ", r.Missing)));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{Markup.Escape(report.HumanSummary())}[/]");
    }
}
