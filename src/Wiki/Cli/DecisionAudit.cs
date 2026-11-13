namespace Wiki.Cli;

// Who decided an approve or reject, for its audit line (issue #30
// "Exposure": `decidedVia: cli | mcp | auto-accept`). Derived from the
// InvocationOrigin the host passed into App.Main, never from an argument, so
// an MCP client cannot claim to be the CLI. Every decision command - review,
// category and schema approve/reject - records it; persona auto-accept (#51)
// writes its own value.
internal static class DecisionAudit
{
    public const string Field = "decidedVia";

    // `cli` for a person or agent at the terminal, `mcp` for either MCP transport.
    public static string DecidedVia(CommandContext ctx) => ctx.Origin.Transport == Transport.Cli ? "cli" : "mcp";

    public static void Record(CommandContext ctx) => ctx.Audit.Field(Field, DecidedVia(ctx));
}
