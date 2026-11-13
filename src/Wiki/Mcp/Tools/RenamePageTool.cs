using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Write: `rename_page` (spec tool catalogue: `page rename`).
[McpServerToolType]
public sealed class RenamePageTool(CliAdapter cli)
{
    [McpServerTool(Name = "rename_page", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Rename a page's slug and rewrite every inbound [[wikilink]] to it (wiki page rename). Returns the id, " +
                 "old and new slug and the number of links rewritten (data.rename).")]
    public CallToolResult RenamePage(
        [Description("Page id (ULID) to rename")]
        string id,
        [Description("New slug, already normalized kebab-case (e.g. 'acme-corp')")]
        string newSlug,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        run.Step(new CliStep("rename", "page", "rename")
            .Positional(id)
            .Positional(newSlug));
        return run.Result();
    }
}
