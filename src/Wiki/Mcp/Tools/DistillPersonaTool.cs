using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Persona: `distill_persona` (spec tool catalogue: `persona distill`, the
// read side of a distillation pass). The agent judges what it returns and
// files the result with propose_change kind=persona; this tool clears no
// candidate flag (propose does, for what the changeset considered).
[McpServerToolType]
public sealed class DistillPersonaTool(CliAdapter cli)
{
    [McpServerTool(Name = "distill_persona", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Start a persona distillation pass (wiki persona distill, data.distill): the current persona, candidate " +
                 "pages (endorsed notes not yet considered), candidate interview transcripts with each answer's anchor, " +
                 "lines whose evidence went stale (recheck) and any pending changeset. File the pass's single changeset " +
                 "with propose_change kind=persona; cite a transcript answer as evidence " +
                 "{kind:\"owner-stated\", path:<transcript id>, anchor:\"a1\"}.")]
    public CallToolResult DistillPersona(CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        run.Step(new CliStep("distill", "persona", "distill"));
        return run.Result();
    }
}
