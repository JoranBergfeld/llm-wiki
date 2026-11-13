using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Browse: `browse_wiki` (spec tool catalogue: `page list`, `source
// list/show/impact`, `category list`). `view` picks the command; the
// `source` view is two steps - the source itself, then the pages citing it -
// and the second only runs if the source was found.
[McpServerToolType]
public sealed class BrowseWikiTool(CliAdapter cli)
{
    private const string Name = "browse_wiki";

    [McpServerTool(Name = Name, ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Orient in the vault. view=pages (default): list pages (data.pages), filtered by type/status/orphans. " +
                 "view=sources: list registered raw sources (data.sources), filtered by status/category. " +
                 "view=source: one source's frontmatter and raw body (data.source) plus the pages citing it (data.impact); needs `source`. " +
                 "view=categories: the source categories declared in wiki.yaml (data.categories). " +
                 "Arguments a view does not use are ignored.")]
    public CallToolResult BrowseWiki(
        [Description("What to browse: pages | sources | source | categories")]
        [AllowedValues("pages", "sources", "source", "categories")]
        string view = "pages",
        [Description("view=pages: only pages of this type: summary | entity | concept | overview")]
        string? type = null,
        [Description("view=pages: active | pending-review | needs-review | archived. view=sources: active | retracted")]
        string? status = null,
        [Description("view=pages: only active pages with zero inbound wikilinks")]
        bool orphans = false,
        [Description("view=sources: only sources in this category id")]
        string? category = null,
        [Description("view=source: the source id (ULID)")]
        string? source = null,
        [Description("view=source: omit the raw body; return only the frontmatter")]
        bool frontmatterOnly = false,
        CancellationToken cancellationToken = default)
    {
        // Checked before any step runs: a missing argument is a protocol error, not a CLI failure.
        var sourceId = view == "source" ? ToolArgumentGuard.RequireFor(Name, source, "source", "view=source") : null;

        var run = cli.Begin(cancellationToken);
        switch (view)
        {
            case "sources":
                run.Step(new CliStep("sources", "source", "list")
                    .Option("--status", status)
                    .Option("--category", category));
                break;
            case "source":
                if (run.Step(new CliStep("source", "source", "show")
                        .Flag("--frontmatter-only", frontmatterOnly)
                        .Positional(sourceId!)))
                {
                    run.Step(new CliStep("impact", "source", "impact")
                        .Positional(sourceId!));
                }
                break;
            case "categories":
                run.Step(new CliStep("categories", "category", "list"));
                break;
            default:
                run.Step(new CliStep("pages", "page", "list")
                    .Option("--type", type)
                    .Option("--status", status)
                    .Flag("--orphans", orphans));
                break;
        }
        return run.Result();
    }
}
