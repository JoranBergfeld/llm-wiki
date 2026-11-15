using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Decide: `review_queue` (spec tool catalogue: every pending item - pages,
// categories, schema, persona). Three read steps: `review list` (pending
// pages and the open persona changeset, each row with its `kind`), then the
// open category and schema proposals.
[McpServerToolType]
public sealed class ReviewQueueTool(CliAdapter cli)
{
    [McpServerTool(Name = "review_queue", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List everything waiting for a decision: pending-review pages and the open persona changeset " +
                 "(wiki review list, data.review; each row's `kind` is page or persona, with a diff), open category " +
                 "proposals (wiki category proposals --status open, data.categories) and open AGENTS.md schema " +
                 "proposals (wiki schema proposals --status open, data.schema). Decide each with `decide`.")]
    public CallToolResult ReviewQueue(CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        if (run.Step(new CliStep("review", "review", "list")))
        {
            if (run.Step(new CliStep("categories", "category", "proposals").Option("--status", "open")))
                run.Step(new CliStep("schema", "schema", "proposals").Option("--status", "open"));
        }
        return run.Result();
    }
}
