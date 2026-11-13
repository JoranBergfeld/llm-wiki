using System;
using System.ComponentModel;
using System.Threading;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Admin: `repair_vault` (spec tool catalogue: `reindex`, `page set-status`).
// A multi-step intent: `reindex` first (so ids resolve against a rebuilt
// idmap), then one `page set-status` per page in the order given. It stops
// at the first failing step and keeps every step before it applied.
[McpServerToolType]
public sealed class RepairVaultTool(CliAdapter cli)
{
    [McpServerTool(Name = "repair_vault", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Repair the vault: rebuild idmap.json and index.md from the files on disk (wiki reindex, data.reindex), " +
                 "then set the status of each listed page (wiki page set-status, data.statuses, one entry per page). " +
                 "Stops at the first failing step; steps already done stay done (completedSteps).")]
    public CallToolResult RepairVault(
        [Description("Rebuild idmap.json and index.md first (default true)")]
        bool reindex = true,
        [Description("Comma-separated page ids (ULIDs) whose status to set; requires `status`")]
        string? pages = null,
        [Description("Status to give every listed page: active | pending-review | needs-review | archived; requires `pages`")]
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var ids = (pages ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length > 0 != status is not null)
            throw new McpProtocolException("repair_vault: `pages` and `status` must be given together", McpErrorCode.InvalidParams);

        var run = cli.Begin(cancellationToken);
        if (reindex)
            run.Step(new CliStep("reindex", "reindex"));
        foreach (var id in ids)
        {
            if (run.Stopped)
                break;
            run.Step(new CliStep("statuses", "page", "set-status")
                .Collect()
                .Positional(id)
                .Positional(status!));
        }
        return run.Result();
    }
}
