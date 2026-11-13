using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Maintain: `work_issues` (spec tool catalogue: `issues list/show/resolve`).
// `action` picks the command. Whether an issue is fixed is the agent's call;
// the tool only records it.
[McpServerToolType]
public sealed class WorkIssuesTool(CliAdapter cli)
{
    private const string Name = "work_issues";

    [McpServerTool(Name = Name, ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Work the issues lint and audits file. action=list (default): issues (data.issues), filtered by kind/status. " +
                 "action=show: one issue (data.issue); needs `issue`. " +
                 "action=resolve: mark an issue resolved, with an optional note (data.issue); needs `issue`. " +
                 "Arguments an action does not use are ignored.")]
    public CallToolResult WorkIssues(
        [Description("list | show | resolve")]
        [AllowedValues("list", "show", "resolve")]
        string action = "list",
        [Description("action=list: only this issue kind, e.g. orphan | dangling-link | stale | rename-drift | unsupported-claim")]
        string? kind = null,
        [Description("action=list: open | resolved")]
        string? status = null,
        [Description("action=show/resolve: the issue id (ULID)")]
        string? issue = null,
        [Description("action=resolve: free-text resolution note")]
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var issueId = action is "show" or "resolve"
            ? ToolArgumentGuard.RequireFor(Name, issue, "issue", "action=" + action)
            : null;

        var run = cli.Begin(cancellationToken);
        switch (action)
        {
            case "show":
                run.Step(new CliStep("issue", "issues", "show")
                    .Positional(issueId!));
                break;
            case "resolve":
                run.Step(new CliStep("issue", "issues", "resolve")
                    .Option("--note", note)
                    .Positional(issueId!));
                break;
            default:
                run.Step(new CliStep("issues", "issues", "list")
                    .Option("--kind", kind)
                    .Option("--status", status));
                break;
        }
        return run.Result();
    }
}
