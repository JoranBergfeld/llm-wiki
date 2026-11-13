using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wiki.Mcp.Tools;

// Maintain: `check_health` (spec tool catalogue: `lint` incl. --fix-links,
// `links check`, `eval` without --fail-under). Steps run in that order and
// stop at the first failure.
//
// --fail-under is deliberately not an argument (spec, Error contract: exit 4
// cannot happen over MCP). The agent reads data.eval.score and judges it.
//
// eval is opt-in: eval.yaml is human-owned and absent from a fresh vault, and
// a missing file would stop the intent after lint and links had already run.
[McpServerToolType]
public sealed class CheckHealthTool(CliAdapter cli)
{
    [McpServerTool(Name = "check_health", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("Run the vault's health checks: lint (data.lint; files or refreshes issues, see work_issues), " +
                 "then the external-link inventory (data.links), then optionally the retrieval eval against eval.yaml " +
                 "(data.eval: recall@k score per golden question). Stops at the first check that fails.")]
    public CallToolResult CheckHealth(
        [Description("lint: also repair wikilink targets and idmap entries broken by a rename done outside the CLI")]
        bool fixLinks = false,
        [Description("Run `links check` (default true)")]
        bool links = true,
        [Description("links: actually probe each URL over the network and file broken-external-link issues")]
        bool external = false,
        [Description("links with external: per-request timeout in milliseconds")]
        int? timeout = null,
        [Description("links with external: how many URLs to probe at once")]
        int? concurrency = null,
        [Description("Also run the retrieval eval (needs eval.yaml at the vault root)")]
        bool eval = false,
        [Description("eval: how many candidate pages the router may surface (default 10)")]
        int? k = null,
        CancellationToken cancellationToken = default)
    {
        var run = cli.Begin(cancellationToken);
        var ok = run.Step(new CliStep("lint", "lint")
            .Flag("--fix-links", fixLinks));
        if (ok && links)
        {
            ok = run.Step(new CliStep("links", "links", "check")
                .Flag("--external", external)
                .Option("--timeout", timeout)
                .Option("--concurrency", concurrency));
        }
        if (ok && eval)
        {
            run.Step(new CliStep("eval", "eval")
                .Option("--k", k));
        }
        return run.Result();
    }
}
