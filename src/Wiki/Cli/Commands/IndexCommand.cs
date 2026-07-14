using System.CommandLine;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki index show` (Task 19): emits wiki/index.md's routing entries as
// structured JSON instead of markdown, so an agent can route without ever
// reading (or parsing) the file. Read-only - PageService.IndexShow only
// scans frontmatter (PageStore.Enumerate), never writes anything.
public static class IndexCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var index = new Command("index", "Query the routing index without reading index.md");
        index.Add(BuildShow(vaultOption, jsonOption, stdout, stdin));
        return index;
    }

    private static Command BuildShow(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter to a single page type: summary | entity | concept | overview",
        };

        var show = new Command("show", "Emit index.md's entries (grouped/ordered the same way) as JSON")
        {
            typeOption,
        };

        show.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var typeRaw = parseResult.GetValue(typeOption);
            var type = string.IsNullOrEmpty(typeRaw) ? (PageType?)null : PageTypeX.Parse(typeRaw);

            var vault = ctx.ResolveVault();
            var service = new PageService();
            var results = service.IndexShow(vault, type);

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderTable(ctx.Out, results);
            }
            return 0;
        }));

        return show;
    }

    private static void RenderTable(TextWriter output, System.Collections.Generic.IReadOnlyList<PageSummary> entries)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Slug");
        table.AddColumn("Type");
        table.AddColumn("Status");
        table.AddColumn("Title");
        table.AddColumn("Summary");
        table.AddColumn("Sources");

        foreach (var e in entries)
        {
            table.AddRow(
                Markup.Escape(e.Slug),
                Markup.Escape(e.Type),
                Markup.Escape(e.Status),
                Markup.Escape(e.Title),
                Markup.Escape(e.Summary),
                e.SourcesCount.ToString());
        }

        console.Write(table);
        console.MarkupLine($"[grey]{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}[/]");
    }
}
