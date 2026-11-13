using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Ingest: `continue_ingest` (spec tool catalogue: `ingest status/resume/
// advance`). The ledger state machine an ingest_source call starts:
// status lists the open ingests, resume gives one source's plan, advance
// records the next transition (the CLI checks its precondition) and then
// returns the plan from the new state, so the agent always holds the next
// step.
[McpServerToolType]
public sealed class ContinueIngestTool(CliAdapter cli)
{
    private const string Name = "continue_ingest";

    [McpServerTool(Name = Name, ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Drive a source through the ingest ledger (registered -> summarized -> integrated -> linted). " +
                 "action=status (default): every source not yet linted, or one source with sourceId (data.status). " +
                 "action=resume: what remains for a source - states and expected artifacts (data.plan); needs sourceId. " +
                 "action=advance: record the next state after checking its precondition (data.advance), then the plan " +
                 "from there (data.plan); needs sourceId and `to`; `touched` is required for to=integrated (may be empty). " +
                 "Arguments an action does not use are ignored.")]
    public CallToolResult ContinueIngest(
        [Description("status | resume | advance")]
        [AllowedValues("status", "resume", "advance")]
        string action = "status",
        [Description("Source id (ULID). action=status: optional; resume/advance: required")]
        string? sourceId = null,
        [Description("action=advance: target state, summarized | integrated | linted")]
        string? to = null,
        [Description("action=advance to integrated: comma-separated ids of the entity/concept pages touched (may be empty)")]
        string? touched = null,
        CancellationToken cancellationToken = default)
    {
        string? id = sourceId, target = null;
        if (action is "resume" or "advance")
            id = ToolArgumentGuard.RequireFor(Name, sourceId, "sourceId", "action=" + action);
        if (action == "advance")
            target = ToolArgumentGuard.RequireFor(Name, to, "to", "action=advance");

        var run = cli.Begin(cancellationToken);
        switch (action)
        {
            case "resume":
                run.Step(new CliStep("plan", "ingest", "resume").Positional(id!));
                break;
            case "advance":
                if (run.Step(new CliStep("advance", "ingest", "advance")
                        .Option("--to", target)
                        // `--touched=` (empty) does not parse; the CLI reads an
                        // absent option as the empty list.
                        .Option("--touched", string.IsNullOrWhiteSpace(touched) ? null : touched)
                        .Positional(id!)))
                    run.Step(new CliStep("plan", "ingest", "resume").Positional(id!));
                break;
            default:
                var status = new CliStep("status", "ingest", "status");
                if (id is not null)
                    status.Positional(id);
                run.Step(status);
                break;
        }
        return run.Result();
    }
}
