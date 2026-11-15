using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Write: `write_page` (spec tool catalogue: `page upsert`). The body arrives
// as a string argument and reaches the command on stdin with `--stdin`, so
// it is never part of argv.
[McpServerToolType]
public sealed class WritePageTool(CliAdapter cli)
{
    [McpServerTool(Name = "write_page", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Create a wiki page, or replace an existing page in full when `id` is given (wiki page upsert). " +
                 "Returns the written page's id, slug and path (data.page). Every [[wikilink]] in the body must resolve " +
                 "unless allowDangling is true.")]
    public CallToolResult WritePage(
        [Description("Page type: summary | entity | concept | overview")]
        string type,
        [Description("Page title; a new page's slug is derived from it")]
        string title,
        [Description("One-line routing description (frontmatter 'summary')")]
        string summary,
        [Description("The full Markdown body. On an update it replaces the current body entirely.")]
        string body,
        [Description("Existing page id (ULID) to update. Omit to create a new page.")]
        string? id = null,
        [Description("Comma-separated source ids this page cites")]
        string? sources = null,
        [Description("Comma-separated free-form tags")]
        string? tags = null,
        [Description("Permit wikilinks whose target does not exist yet")]
        bool allowDangling = false,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        run.Step(new CliStep("page", "page", "upsert")
            .Option("--type", type)
            .Option("--title", title)
            .Option("--summary", summary)
            .Option("--id", id)
            .Option("--sources", sources)
            .Option("--tags", tags)
            .Flag("--allow-dangling", allowDangling)
            .Flag("--stdin", true)
            .Stdin(body));
        return run.Result();
    }
}
