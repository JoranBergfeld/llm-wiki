using System.CommandLine;
using System.Collections.Generic;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki search <terms> [--type] [--limit] [--regex]`: the agent's retrieval
// primitive (spec §13) - a deterministic, non-semantic scan of every page's
// raw text, returning matching lines only. Never full bodies: routing (this
// command / `wiki index show`) and reading (`wiki page show`) stay separate
// steps on purpose, so an agent can't accidentally pull a whole page into
// context just by searching for a word in it.
public static class SearchCommand
{
    // "Note your choice" per the task brief: 50 keeps a default search from
    // flooding an agent's context (each hit is one line, so 50 hits is still
    // small), while being generous enough that a reasonably common term
    // doesn't get silently truncated to a handful of results. Same order of
    // magnitude as `wiki page list`'s unbounded-but-vault-sized result sets.
    private const int DefaultLimit = 50;

    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var termsArgument = new Argument<string>("terms")
        {
            Description = "Plain-text substring (default) or regex pattern (--regex) to search for",
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter to a single page type: summary | entity | concept | overview",
        };
        var limitOption = new Option<int>("--limit")
        {
            DefaultValueFactory = _ => DefaultLimit,
            Description = $"Cap on total hits returned (default {DefaultLimit})",
        };
        var regexOption = new Option<bool>("--regex")
        {
            Description = "Treat <terms> as a case-insensitive regex instead of a plain-text substring",
        };
        var kindOption = new Option<string?>("--kind")
        {
            Description = "Restrict to wiki pages or raw sources: page | source (default: both)",
        };

        var search = new Command("search", "Plain-text/regex search over page and source frontmatter + bodies; returns matching lines only, never full bodies")
        {
            termsArgument,
            typeOption,
            kindOption,
            limitOption,
            regexOption,
        };

        search.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var terms = parseResult.GetRequiredValue(termsArgument);
            var typeRaw = parseResult.GetValue(typeOption);
            var type = string.IsNullOrEmpty(typeRaw) ? (PageType?)null : PageTypeX.Parse(typeRaw);
            var limit = parseResult.GetValue(limitOption);
            var regex = parseResult.GetValue(regexOption);
            var kindRaw = parseResult.GetValue(kindOption);
            var kind = string.IsNullOrEmpty(kindRaw) ? (SearchKind?)null : SearchKindX.Parse(kindRaw);

            var vault = ctx.ResolveVault();
            var service = new SearchService();
            var report = service.Search(vault, terms, type, kind, limit, regex);

            if (ctx.Json)
            {
                ctx.EmitOk(report);
            }
            else
            {
                RenderHitsTable(ctx.Out, report);
            }
            return 0;
        }));

        return search;
    }

    private static void RenderHitsTable(TextWriter output, SearchReport report)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Kind");
        table.AddColumn("Path");
        table.AddColumn("Line");
        table.AddColumn("Title");
        table.AddColumn("Match");

        foreach (var hit in report.Hits)
        {
            table.AddRow(
                Markup.Escape(hit.Kind),
                Markup.Escape(hit.Path),
                hit.Line.ToString(),
                Markup.Escape(hit.Title),
                Markup.Escape(hit.MatchLine));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{Markup.Escape(report.HumanSummary())}[/]");
        if (report.Truncated)
            console.MarkupLine("[yellow]results truncated - raise --limit to see the rest[/]");
    }
}
