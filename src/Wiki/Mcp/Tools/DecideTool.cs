using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Decide: `decide` (spec tool catalogue: approve/reject for every kind
// `review_queue` lists; withdraw for the pending proposals, amendment AX). `kind` picks the command group: pages and persona
// changesets go through `wiki review`, categories through `wiki category`,
// schema proposals through `wiki schema`. Who decided is not an argument:
// the step's audit line records `decidedVia=mcp` from the server's
// InvocationOrigin, next to the transport (`via=`).
[McpServerToolType]
public sealed class DecideTool(CliAdapter cli)
{
    [McpServerTool(Name = "decide", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Approve or reject one item from review_queue (data.decision). kind=page or persona: wiki review " +
                 "approve|reject (approving a page makes it active; rejecting restores or archives it; approving a " +
                 "persona changeset rewrites PERSONA.md). kind=category: wiki category approve|reject (approving adds " +
                 "the category to wiki.yaml). kind=schema: wiki schema approve|reject (approving replaces the AGENTS.md " +
                 "section). decision=withdraw takes a pending persona changeset, category or schema proposal back (wiki review|" +
                 "category|schema withdraw); a page under review is rejected, not withdrawn. Rejecting a persona changeset " +
                 "and every withdraw require `reason`. An item already decided is exit 3, not an error. " +
                 "`reason` is ignored on approve.")]
    public CallToolResult Decide(
        [Description("What is decided: page | category | schema | persona")]
        [AllowedValues("page", "category", "schema", "persona")]
        string kind,
        [Description("The item id (ULID) from review_queue: page id, proposal id or persona changeset id")]
        string id,
        [Description("approve | reject | withdraw")]
        [AllowedValues("approve", "reject", "withdraw")]
        string decision,
        [Description("Why it was rejected or withdrawn; recorded on the item and the audit line. Required to reject a persona changeset and to withdraw.")]
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var group = kind switch
        {
            "category" => "category",
            "schema" => "schema",
            _ => "review",
        };
        var step = new CliStep("decision", group, decision);
        if (decision != "approve")
            step.Option("--note", reason);
        step.Positional(id);

        var run = cli.Begin(cancellationToken);
        run.Step(step);
        return run.Result();
    }
}
