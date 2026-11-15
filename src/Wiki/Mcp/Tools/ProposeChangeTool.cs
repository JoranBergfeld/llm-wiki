using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Propose: `propose_change` (spec tool catalogue: `category propose`,
// `schema propose`, `persona propose`). `kind` picks the command. Proposing
// never applies anything: the result is an open item in `review_queue`.
// Whatever the CLI returns passes through unchanged (persona auto-accept,
// #51, reports itself in the `persona propose` data).
[McpServerToolType]
public sealed class ProposeChangeTool(CliAdapter cli)
{
    private const string Name = "propose_change";

    [McpServerTool(Name = Name, ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("File a proposal for review. kind=category: a new source category (wiki category propose, data.category); " +
                 "needs `category` and `description`. kind=schema: the new full text of one AGENTS.md section " +
                 "(wiki schema propose, data.schema); needs `section` and `text`. kind=persona: one persona changeset " +
                 "as JSON (wiki persona propose, data.persona); needs `changeset`. Nothing is applied until `decide` " +
                 "approves it. Arguments a kind does not use are ignored.")]
    public CallToolResult ProposeChange(
        [Description("What to propose: category | schema | persona")]
        [AllowedValues("category", "schema", "persona")]
        string kind,
        [Description("kind=category: the proposed category id (lowercase kebab-case, must not exist yet)")]
        string? category = null,
        [Description("kind=category: one-line description of what belongs in the category")]
        string? description = null,
        [Description("kind=category: comma-separated ids of the sources that fit no existing category")]
        string? sources = null,
        [Description("kind=schema: heading text of the '##' or '###' AGENTS.md section to replace")]
        string? section = null,
        [Description("kind=schema: the section's new full body text")]
        string? text = null,
        [Description("kind=category/schema: why the change is needed")]
        string? rationale = null,
        [Description("kind=persona: the changeset JSON: {reason, considered[], changes[{op, section, from, text, evidence[]}]} " +
                     "(see wiki persona propose)")]
        string? changeset = null,
        CancellationToken cancellationToken = default)
    {
        // Checked before any step runs: a missing argument is a protocol error, not a CLI failure.
        var selector = "kind=" + kind;
        CliStep step = kind switch
        {
            "category" => new CliStep("category", "category", "propose")
                .Option("--description", ToolArgumentGuard.RequireFor(Name, description, "description", selector))
                .Option("--rationale", rationale)
                .Option("--sources", sources)
                .Positional(ToolArgumentGuard.RequireFor(Name, category, "category", selector)),
            "schema" => new CliStep("schema", "schema", "propose")
                .Option("--section", ToolArgumentGuard.RequireFor(Name, section, "section", selector))
                .Option("--rationale", rationale)
                .Flag("--stdin", true)
                .Stdin(ToolArgumentGuard.RequireFor(Name, text, "text", selector)),
            _ => new CliStep("persona", "persona", "propose")
                .Flag("--stdin", true)
                .Stdin(ToolArgumentGuard.RequireFor(Name, changeset, "changeset", selector)),
        };

        var run = cli.Begin(cancellationToken);
        run.Step(step);
        return run.Result();
    }
}
