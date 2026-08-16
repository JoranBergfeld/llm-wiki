using System;
using System.CommandLine;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki page ...` command group. Task 12/13 wired up `upsert` (create +
// update); Task 14 added the read-only query pair `show`/`list`; this task
// (19) adds `backlinks` and `list --orphans`. `rename`/`set-status` land in
// later tasks as further siblings under the same `page` group.
public static class PageCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var page = new Command("page", "Create, update, and query wiki pages");
        page.Add(BuildUpsert(vaultOption, jsonOption, stdout, stdin));
        page.Add(BuildShow(vaultOption, jsonOption, stdout, stdin));
        page.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        page.Add(BuildBacklinks(vaultOption, jsonOption, stdout, stdin));
        page.Add(BuildRename(vaultOption, jsonOption, stdout, stdin));
        page.Add(BuildSetStatus(vaultOption, jsonOption, stdout, stdin));
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
            Description = "Existing page id to update; the supplied body replaces the current one in full",
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
            var useStdin = parseResult.GetValue(stdinOption);

            var body = BodyInput.Resolve(ctx, bodyFile, useStdin);

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
            var view = PageQuery.Show(vault, idOrName, frontmatterOnly);

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
        var orphansOption = new Option<bool>("--orphans")
        {
            Description = "Show only active pages with zero inbound wikilinks (excludes overview and pending-review)",
        };

        var list = new Command("list", "List pages, optionally filtered by --type, --status, and/or --orphans")
        {
            typeOption,
            statusOption,
            orphansOption,
        };

        list.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var typeRaw = parseResult.GetValue(typeOption);
            var statusRaw = parseResult.GetValue(statusOption);
            var type = string.IsNullOrEmpty(typeRaw) ? (PageType?)null : PageTypeX.Parse(typeRaw);
            var status = string.IsNullOrEmpty(statusRaw) ? (PageStatus?)null : PageStatusX.Parse(statusRaw);
            var orphans = parseResult.GetValue(orphansOption);

            var vault = ctx.ResolveVault();
            var results = PageQuery.List(vault, type, status, orphans);

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

    private static Command BuildBacklinks(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idOrNameArgument = new Argument<string>("id-or-name")
        {
            Description = "Page id (ULID) or slug/name to look up",
        };

        var backlinks = new Command("backlinks", "List the slugs of pages whose body links to this page")
        {
            idOrNameArgument,
        };

        backlinks.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var idOrName = parseResult.GetRequiredValue(idOrNameArgument);

            var vault = ctx.ResolveVault();
            var results = PageQuery.Backlinks(vault, idOrName);

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderBacklinksList(ctx.Out, results);
            }
            return 0;
        }));

        return backlinks;
    }

    private static Command BuildRename(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Page id (ULID) to rename",
        };
        var newSlugArgument = new Argument<string>("new-slug")
        {
            Description = "New slug - must already be normalized kebab-case (e.g. 'acme-corp')",
        };

        var rename = new Command("rename", "Rename a page's slug (filename), rewriting every inbound [[wikilink]]")
        {
            idArgument,
            newSlugArgument,
        };

        rename.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var newSlug = parseResult.GetRequiredValue(newSlugArgument);

            var vault = ctx.ResolveVault();
            var service = new PageService();
            var result = service.Rename(vault, id, newSlug);
            ctx.EmitOk(result);
            return 0;
        }));

        return rename;
    }

    private static Command BuildSetStatus(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Page id (ULID) to update",
        };
        var statusArgument = new Argument<string>("status")
        {
            Description = "New status: active | pending-review | needs-review | archived",
        };

        var setStatus = new Command("set-status", "Set a page's frontmatter status")
        {
            idArgument,
            statusArgument,
        };

        setStatus.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            // PageStatusX.Parse throws ValidationException(code="invalid-page-status")
            // on garbage - the same code `page list --status` already
            // surfaces for a bad --status filter, so garbage input here is
            // consistent with the rest of the CLI rather than inventing a
            // second "this status string is bad" code.
            var status = PageStatusX.Parse(parseResult.GetRequiredValue(statusArgument));

            var vault = ctx.ResolveVault();
            var service = new PageService();
            service.SetStatus(vault, id, status);

            // SetStatus's own contract is `void` (Task 20 brief); re-read the
            // page via the existing read-only Show query so callers still get
            // the resulting frontmatter back, without a bespoke DTO. Mirrors
            // BuildShow's json/human split exactly.
            var view = PageQuery.Show(vault, id, frontmatterOnly: true);
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

        return setStatus;
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

    private static void RenderBacklinksList(TextWriter output, System.Collections.Generic.IReadOnlyList<string> slugs)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        if (slugs.Count == 0)
        {
            console.MarkupLine("[grey]no backlinks[/]");
            return;
        }

        foreach (var slug in slugs)
            console.MarkupLine(Markup.Escape($"[[{slug}]]"));
    }

    private static string[] SplitCsv(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
