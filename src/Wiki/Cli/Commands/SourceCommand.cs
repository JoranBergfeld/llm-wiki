using System.CommandLine;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki source ...` command group. Task 16 wired up `add`; Task 20 added the
// read-only query trio `list`/`show`/`impact` - raw/ is immutable, so none
// of those three ever write anything. Task 24 adds `retract`, the one
// command in this group that DOES mutate raw/ (spec §14 cascade).
public static class SourceCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var source = new Command("source", "Register and query immutable raw sources");
        source.Add(BuildAdd(vaultOption, jsonOption, stdout, stdin));
        source.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        source.Add(BuildShow(vaultOption, jsonOption, stdout, stdin));
        source.Add(BuildImpact(vaultOption, jsonOption, stdout, stdin));
        source.Add(BuildRetract(vaultOption, jsonOption, stdout, stdin));
        return source;
    }

    private static Command BuildAdd(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var fileArgument = new Argument<string>("file")
        {
            Description = "Path to the raw source file to register",
        };
        var categoryOption = new Option<string>("--category")
        {
            Required = true,
            Description = "Source category id (must already exist in wiki.yaml)",
        };
        var titleOption = new Option<string>("--title")
        {
            Required = true,
            Description = "Source title",
        };
        var originOption = new Option<string?>("--origin")
        {
            Description = "Free-text provenance note, e.g. a URL (default: 'manual')",
        };

        var add = new Command("add", "Copy a file into raw/ under a new ULID, hash it, dedup, register the ledger entry")
        {
            fileArgument,
            categoryOption,
            titleOption,
            originOption,
        };

        add.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var file = parseResult.GetRequiredValue(fileArgument);
            var category = parseResult.GetRequiredValue(categoryOption);
            var title = parseResult.GetRequiredValue(titleOption);
            var origin = parseResult.GetValue(originOption);

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfig();

            var service = new SourceService();
            var result = service.Add(vault, cfg, file, category, title, origin);
            ctx.EmitOk(result);
            return 0;
        }));

        return add;
    }

    private static Command BuildList(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var statusOption = new Option<string?>("--status")
        {
            Description = "Filter to a single source status: active | retracted",
        };
        var categoryOption = new Option<string?>("--category")
        {
            Description = "Filter to a single category id",
        };

        var list = new Command("list", "List registered sources, optionally filtered by --status and/or --category")
        {
            statusOption,
            categoryOption,
        };

        list.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var statusRaw = parseResult.GetValue(statusOption);
            var status = string.IsNullOrEmpty(statusRaw) ? (SourceStatus?)null : SourceStatusX.Parse(statusRaw);
            var category = parseResult.GetValue(categoryOption);

            var vault = ctx.ResolveVault();
            var service = new SourceService();
            var results = service.List(vault, status, category);

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

    private static Command BuildShow(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Source id (ULID) to look up",
        };
        var frontmatterOnlyOption = new Option<bool>("--frontmatter-only")
        {
            Description = "Omit the raw body; return only frontmatter fields",
        };

        var show = new Command("show", "Show a single source's frontmatter (and raw body)")
        {
            idArgument,
            frontmatterOnlyOption,
        };

        show.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var frontmatterOnly = parseResult.GetValue(frontmatterOnlyOption);

            var vault = ctx.ResolveVault();
            var service = new SourceService();
            var view = service.Show(vault, id, frontmatterOnly);

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

    private static Command BuildImpact(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Source id (ULID) to check provenance impact for",
        };

        var impact = new Command("impact", "List pages whose frontmatter 'sources' cites this source id")
        {
            idArgument,
        };

        impact.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);

            var vault = ctx.ResolveVault();
            var service = new SourceService();
            var results = service.Impact(vault, id);

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderImpactList(ctx.Out, results);
            }
            return 0;
        }));

        return impact;
    }

    private static Command BuildRetract(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Source id (ULID) to retract",
        };
        var reasonOption = new Option<string>("--reason")
        {
            Required = true,
            Description = "Why the source is being retracted (recorded on the log line and every filed retraction issue)",
        };
        var purgeOption = new Option<bool>("--purge")
        {
            Description = "Also strip the raw file's body content (compliance/deletion case), keeping a metadata stub so the id still resolves",
        };

        var retract = new Command("retract",
            "Retract a source: source -> retracted; its summary page -> archived; every other citing page -> needs-review + a filed 'retraction' issue (spec §14)")
        {
            idArgument,
            reasonOption,
            purgeOption,
        };

        retract.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var reason = parseResult.GetRequiredValue(reasonOption);
            var purge = parseResult.GetValue(purgeOption);

            var vault = ctx.ResolveVault();
            var service = new SourceService();
            var result = service.Retract(vault, id, reason, purge);
            ctx.EmitOk(result);
            return 0;
        }));

        return retract;
    }

    private static void RenderListTable(TextWriter output, System.Collections.Generic.IReadOnlyList<SourceSummary> sources)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Title");
        table.AddColumn("Category");
        table.AddColumn("Status");
        table.AddColumn("Added");
        table.AddColumn("Sha256");

        foreach (var s in sources)
        {
            table.AddRow(
                Markup.Escape(s.Id),
                Markup.Escape(s.Title),
                Markup.Escape(s.Category),
                Markup.Escape(s.Status),
                Markup.Escape(s.Added),
                Markup.Escape(s.Sha256));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{sources.Count} source(s)[/]");
    }

    private static void RenderShowPanel(TextWriter output, SourceView view)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var body = new System.Text.StringBuilder();
        body.Append("[bold]id[/]: ").AppendLine(Markup.Escape(view.Id));
        body.Append("[bold]category[/]: ").AppendLine(Markup.Escape(view.Category));
        body.Append("[bold]status[/]: ").AppendLine(Markup.Escape(view.Status));
        body.Append("[bold]added[/]: ").AppendLine(Markup.Escape(view.Added));
        body.Append("[bold]sha256[/]: ").AppendLine(Markup.Escape(view.Sha256));
        body.Append("[bold]origin[/]: ").AppendLine(Markup.Escape(view.Origin));
        if (view.Body is not null)
        {
            body.AppendLine().Append(Markup.Escape(view.Body));
        }

        var panel = new Panel(body.ToString().TrimEnd('\n'))
        {
            Header = new PanelHeader(Markup.Escape(view.Title)),
        };
        console.Write(panel);
    }

    private static void RenderImpactList(TextWriter output, System.Collections.Generic.IReadOnlyList<SourceImpactEntry> entries)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        if (entries.Count == 0)
        {
            console.MarkupLine("[grey]no citing pages[/]");
            return;
        }

        foreach (var e in entries)
            console.MarkupLine(Markup.Escape($"[[{e.Slug}]] ({e.Type}, {e.Status})"));
    }
}
