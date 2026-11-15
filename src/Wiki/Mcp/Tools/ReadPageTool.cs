using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Read: `read_page` (spec tool catalogue: `page show` + backlinks option).
// Two steps when backlinks are asked for; the second only runs if the page
// was found.
[McpServerToolType]
public sealed class ReadPageTool(CliAdapter cli)
{
    [McpServerTool(Name = "read_page", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Read one page: its frontmatter and body (data.page), and optionally the slugs of pages linking to it " +
                 "(data.backlinks).")]
    public CallToolResult ReadPage(
        [Description("Page id (ULID) or slug")]
        string page,
        [Description("Also return the slugs of pages whose body links to this page")]
        bool backlinks = false,
        [Description("Omit the body; return only the frontmatter fields")]
        bool frontmatterOnly = false,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        if (run.Step(new CliStep("page", "page", "show")
                .Flag("--frontmatter-only", frontmatterOnly)
                .Positional(page))
            && backlinks)
        {
            run.Step(new CliStep("backlinks", "page", "backlinks")
                .Positional(page));
        }
        return run.Result();
    }
}
