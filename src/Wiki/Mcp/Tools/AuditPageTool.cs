using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Maintain: `audit_page` (spec tool catalogue: `audit next/record/list`).
// The CLI selects and records; the verdict is the agent's judgement and
// comes in as an argument. Every action's data is under data.audit.
[McpServerToolType]
public sealed class AuditPageTool(CliAdapter cli)
{
    private const string Name = "audit_page";

    [McpServerTool(Name = Name, ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Faithfulness auditing. action=next (default): the page most worth auditing, with the ids of the sources " +
                 "it cites (data.audit; hasTarget false when there is nothing to audit) - read those with browse_wiki view=source. " +
                 "action=record: record your verdict on a page (data.audit); needs `page` and `verdict`; 'unsupported' needs a " +
                 "`note` and files an unsupported-claim issue. action=list: the last verdict per audited page (data.audit), " +
                 "optionally only one verdict. Arguments an action does not use are ignored.")]
    public CallToolResult AuditPage(
        [Description("next | record | list")]
        [AllowedValues("next", "record", "list")]
        string action = "next",
        [Description("action=record: the page id (ULID) the verdict applies to")]
        string? page = null,
        [Description("action=record: supported | unsupported. action=list: only this verdict")]
        string? verdict = null,
        [Description("action=record: what was found; for 'unsupported' name the claim and the source that fails to support it")]
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        string? pageId = null, recorded = null;
        if (action == "record")
        {
            pageId = ToolArgumentGuard.RequireFor(Name, page, "page", "action=record");
            recorded = ToolArgumentGuard.RequireFor(Name, verdict, "verdict", "action=record");
        }

        var run = cli.Begin(cancellationToken);
        switch (action)
        {
            case "record":
                run.Step(new CliStep("audit", "audit", "record")
                    .Option("--verdict", recorded)
                    .Option("--note", note)
                    .Positional(pageId!));
                break;
            case "list":
                run.Step(new CliStep("audit", "audit", "list")
                    .Option("--verdict", verdict));
                break;
            default:
                run.Step(new CliStep("audit", "audit", "next"));
                break;
        }
        return run.Result();
    }
}
