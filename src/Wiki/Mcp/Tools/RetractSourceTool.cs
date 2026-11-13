using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Admin: `retract_source` (spec tool catalogue: `source retract`). Its own
// tool so a server instance can exclude it on its own.
[McpServerToolType]
public sealed class RetractSourceTool(CliAdapter cli)
{
    [McpServerTool(Name = "retract_source", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
    [Description("Retract a source (wiki source retract): the source becomes retracted, its summary page archived, and " +
                 "every other page citing it needs-review with a filed retraction issue. Returns what changed " +
                 "(data.retract). Retracting an already retracted source is exit 3, not an error.")]
    public CallToolResult RetractSource(
        [Description("Source id (ULID) to retract")]
        string source,
        [Description("Why the source is retracted; recorded in the log and on every filed issue")]
        string reason,
        [Description("Also strip the raw file's body, keeping a metadata stub so the id still resolves")]
        bool purge = false,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        run.Step(new CliStep("retract", "source", "retract")
            .Option("--reason", reason)
            .Flag("--purge", purge)
            .Positional(source));
        return run.Result();
    }
}
