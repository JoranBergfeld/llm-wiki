using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Admin: `add_category` (spec tool catalogue: `category add`). Adds the
// category directly; `propose_change` is the reviewed route.
[McpServerToolType]
public sealed class AddCategoryTool(CliAdapter cli)
{
    [McpServerTool(Name = "add_category", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Add a source category to wiki.yaml directly, without a proposal (wiki category add). Returns the " +
                 "added category (data.category).")]
    public CallToolResult AddCategory(
        [Description("New category id: lowercase kebab-case, not already declared")]
        string id,
        [Description("One-line description of what belongs in this category")]
        string description,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        run.Step(new CliStep("category", "category", "add")
            .Option("--description", description)
            .Positional(id));
        return run.Result();
    }
}
