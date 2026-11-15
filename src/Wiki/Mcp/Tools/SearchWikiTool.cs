using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Find: `search_wiki` (spec tool catalogue: `search`, `index show`). With a
// query it runs `wiki search`; without one it returns the routing index
// (`wiki index show`), which is what an agent orients on before it knows a
// term to search for.
[McpServerToolType]
public sealed class SearchWikiTool(CliAdapter cli)
{
    [McpServerTool(Name = "search_wiki", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Search the vault's pages and sources for matching lines (data.search: hits, truncated, scanned), " +
                 "or, without a query, return the routing index of every page (data.index). Never returns full bodies; " +
                 "use read_page for that.")]
    public CallToolResult SearchWiki(
        [Description("Plain-text substring (case-insensitive), or a regex when `regex` is true. Omit to get the routing index.")]
        string? query = null,
        [Description("Only pages of this type: summary | entity | concept | overview")]
        string? type = null,
        [Description("Only wiki pages or only raw sources: page | source (default both). Ignored without a query.")]
        string? kind = null,
        [Description("Cap on hits returned (default 50). Ignored without a query.")]
        int? limit = null,
        [Description("Treat `query` as a case-insensitive regex. Ignored without a query.")]
        bool regex = false,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        if (query is null)
        {
            run.Step(new CliStep("index", "index", "show")
                .Option("--type", type));
        }
        else
        {
            run.Step(new CliStep("search", "search")
                .Option("--type", type)
                .Option("--kind", kind)
                .Option("--limit", limit)
                .Flag("--regex", regex)
                .Positional(query));
        }
        return run.Result();
    }
}
