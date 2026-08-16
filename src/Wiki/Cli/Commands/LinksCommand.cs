using System.CommandLine;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki links check [--external] [--timeout <ms>] [--concurrency <n>]`
// (issue #2). Opt-in external URL liveness. Never part of `wiki lint`, never
// a precondition for anything, never blocks a write - see LinksService for
// the full reasoning, which is mostly about not corrupting `occurrences`.
//
// Exit code is 0 whether or not links are broken. A broken external URL is a
// finding filed for someone to work, exactly like a lint finding; it is not
// "your input was rejected". Only the command's own argument validation
// (a nonsense --timeout or --concurrency) exits 1.
public static class LinksCommand
{
    // 10s: long enough for a slow but working host, short enough that a
    // whole-vault sweep of unreachable URLs finishes in reasonable time.
    private const int DefaultTimeoutMs = 10_000;

    // 4: polite by default. This fans out across arbitrary third-party hosts,
    // and the command is a diagnostic, not a race.
    private const int DefaultConcurrency = 4;

    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var links = new Command("links", "Inspect the external (non-wikilink) URLs pages cite");
        links.Add(BuildCheck(vaultOption, jsonOption, stdout, stdin));
        return links;
    }

    private static Command BuildCheck(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var externalOption = new Option<bool>("--external")
        {
            Description = "Actually probe each URL over the network. Without this the command is a pure offline inventory.",
        };
        var timeoutOption = new Option<int>("--timeout")
        {
            DefaultValueFactory = _ => DefaultTimeoutMs,
            Description = $"Per-request timeout in milliseconds (default {DefaultTimeoutMs})",
        };
        var concurrencyOption = new Option<int>("--concurrency")
        {
            DefaultValueFactory = _ => DefaultConcurrency,
            Description = $"How many URLs to probe at once (default {DefaultConcurrency})",
        };

        var check = new Command("check",
            "List the external URLs pages cite; with --external, probe each one and file broken-external-link issues")
        {
            externalOption,
            timeoutOption,
            concurrencyOption,
        };

        check.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var external = parseResult.GetValue(externalOption);
            var timeout = parseResult.GetValue(timeoutOption);
            var concurrency = parseResult.GetValue(concurrencyOption);

            var vault = ctx.ResolveVault();
            var service = new LinksService();
            var report = service.Check(vault, external, timeout, concurrency);

            if (ctx.Json)
            {
                ctx.EmitOk(report);
            }
            else
            {
                RenderReport(ctx.Out, report);
            }
            return 0;
        }));

        return check;
    }

    private static void RenderReport(TextWriter output, LinksCheckReport report)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("URL");
        table.AddColumn("Status");
        table.AddColumn("Code");
        table.AddColumn("Pages");

        foreach (var r in report.Results)
        {
            table.AddRow(
                Markup.Escape(r.Url),
                Markup.Escape(r.Status),
                r.HttpStatus?.ToString() ?? "",
                Markup.Escape(string.Join(", ", r.Pages)));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{Markup.Escape(report.HumanSummary())}[/]");
    }
}
