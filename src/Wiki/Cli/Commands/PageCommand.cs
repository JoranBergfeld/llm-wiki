using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki page ...` command group. Task 12/13 wired up `upsert` (create +
// update); this task (14) adds the read-only query pair `show`/`list`.
// `rename`/`set-status`/`backlinks` land in later tasks as further siblings
// under the same `page` group.
public static class PageCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var page = new Command("page", "Create, update, and query wiki pages");
        page.Add(BuildUpsert(vaultOption, jsonOption, stdout, stdin));
        page.Add(BuildShow(vaultOption, jsonOption, stdout, stdin));
        page.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        return page;
    }

    private static Command BuildUpsert(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var typeOption = new Option<string>("--type")
        {
            Required = true,
            Description = "Page type: summary | entity | concept | overview",
        };
        var titleOption = new Option<string>("--title")
        {
            Required = true,
            Description = "Page title",
        };
        // Not CLI-Required: a missing --summary is a *blocking validation*
        // (code=summary-required) raised by PageService, not a parser error,
        // so the agent gets our JSON envelope instead of System.CommandLine's
        // generic usage text.
        var summaryOption = new Option<string?>("--summary")
        {
            Description = "One-line routing description (required; stored as frontmatter 'summary')",
        };
        var idOption = new Option<string?>("--id")
        {
            Description = "Existing page id to update (update path lands in a later task)",
        };
        var sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated source ids this page cites",
        };
        var tagsOption = new Option<string?>("--tags")
        {
            Description = "Comma-separated free-form tags",
        };
        var allowDanglingOption = new Option<bool>("--allow-dangling")
        {
            Description = "Permit wikilinks whose target doesn't exist yet (instead of rejecting the write)",
        };
        // Documents intent per the CLI spec; body resolution itself always
        // falls back to stdin when --body-file isn't given, so this flag is
        // not required to be present for stdin to be read.
        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read the page body from stdin (default body source when --body-file is omitted)",
        };
        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Read the page body from this file instead of stdin",
        };

        var upsert = new Command("upsert", "Create (no --id) or update (--id) a wiki page")
        {
            typeOption,
            titleOption,
            summaryOption,
            idOption,
            sourcesOption,
            tagsOption,
            allowDanglingOption,
            stdinOption,
            bodyFileOption,
        };

        upsert.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var type = PageTypeX.Parse(parseResult.GetRequiredValue(typeOption));
            var title = parseResult.GetRequiredValue(titleOption);
            var summary = parseResult.GetValue(summaryOption) ?? "";
            var id = parseResult.GetValue(idOption);
            var sources = SplitCsv(parseResult.GetValue(sourcesOption));
            var tags = SplitCsv(parseResult.GetValue(tagsOption));
            var allowDangling = parseResult.GetValue(allowDanglingOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);

            var body = ResolveBody(ctx, bodyFile);

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfig();
            var req = new UpsertRequest(type, title, id, summary, sources, tags, body, allowDangling);

            var service = new PageService();
            var result = service.Upsert(vault, cfg, req);
            ctx.EmitOk(result);
            return 0;
        }));

        return upsert;
    }

    private static Command BuildShow(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idOrNameArgument = new Argument<string>("id-or-name")
        {
            Description = "Page id (ULID) or slug/name to look up",
        };
        var frontmatterOnlyOption = new Option<bool>("--frontmatter-only")
        {
            Description = "Omit the body; return only frontmatter fields",
        };

        var show = new Command("show", "Show a single page's frontmatter (and body)")
        {
            idOrNameArgument,
            frontmatterOnlyOption,
        };

        show.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var idOrName = parseResult.GetRequiredValue(idOrNameArgument);
            var frontmatterOnly = parseResult.GetValue(frontmatterOnlyOption);

            var vault = ctx.ResolveVault();
            var service = new PageService();
            var view = service.Show(vault, idOrName, frontmatterOnly);

            if (ctx.Json)
            {
                ctx.EmitOk(view);
            }
            else
            {
                RenderShowPanel(ctx.Out, view);
            }
            return 0;
        }));

        return show;
    }

    private static Command BuildList(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter to a single page type: summary | entity | concept | overview",
        };
        var statusOption = new Option<string?>("--status")
        {
            Description = "Filter to a single page status: active | pending-review | needs-review | archived",
        };

        var list = new Command("list", "List pages, optionally filtered by --type and/or --status")
        {
            typeOption,
            statusOption,
        };

        list.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var typeRaw = parseResult.GetValue(typeOption);
            var statusRaw = parseResult.GetValue(statusOption);
            var type = string.IsNullOrEmpty(typeRaw) ? (PageType?)null : PageTypeX.Parse(typeRaw);
            var status = string.IsNullOrEmpty(statusRaw) ? (PageStatus?)null : PageStatusX.Parse(statusRaw);

            var vault = ctx.ResolveVault();
            var service = new PageService();
            var results = service.List(vault, type, status);

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderListTable(ctx.Out, results);
            }
            return 0;
        }));

        return list;
    }

    private static void RenderShowPanel(TextWriter output, PageView view)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var body = new System.Text.StringBuilder();
        body.Append("[bold]id[/]: ").AppendLine(Markup.Escape(view.Id));
        body.Append("[bold]type[/]: ").AppendLine(Markup.Escape(view.Type));
        body.Append("[bold]status[/]: ").AppendLine(Markup.Escape(view.Status));
        body.Append("[bold]created[/]: ").AppendLine(Markup.Escape(view.Created));
        body.Append("[bold]updated[/]: ").AppendLine(Markup.Escape(view.Updated));
        body.Append("[bold]summary[/]: ").AppendLine(Markup.Escape(view.Summary));
        body.Append("[bold]sources[/]: ").AppendLine(Markup.Escape(string.Join(", ", view.Sources)));
        body.Append("[bold]tags[/]: ").AppendLine(Markup.Escape(string.Join(", ", view.Tags)));
        if (view.Body is not null)
        {
            body.AppendLine().Append(Markup.Escape(view.Body));
        }

        var panel = new Panel(body.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader($"[[{view.Slug}]]"),
        };
        console.Write(panel);
    }

    private static void RenderListTable(TextWriter output, System.Collections.Generic.IReadOnlyList<PageSummary> pages)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Slug");
        table.AddColumn("Type");
        table.AddColumn("Status");
        table.AddColumn("Title");
        table.AddColumn("Summary");
        table.AddColumn("Sources");

        foreach (var p in pages)
        {
            table.AddRow(
                Markup.Escape(p.Slug),
                Markup.Escape(p.Type),
                Markup.Escape(p.Status),
                Markup.Escape(p.Title),
                Markup.Escape(p.Summary),
                p.SourcesCount.ToString());
        }

        console.Write(table);
        console.MarkupLine($"[grey]{pages.Count} page(s)[/]");
    }

    private static string ResolveBody(CommandContext ctx, string? bodyFile)
    {
        if (!string.IsNullOrEmpty(bodyFile))
        {
            if (!File.Exists(bodyFile))
                throw new ValidationException("body-file-not-found", $"--body-file '{bodyFile}' does not exist", bodyFile);
            return File.ReadAllText(bodyFile);
        }
        return ctx.In.ReadToEnd();
    }

    private static string[] SplitCsv(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
