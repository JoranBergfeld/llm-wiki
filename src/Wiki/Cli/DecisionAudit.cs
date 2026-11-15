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

    // Withdrawing a pending proposal (amendment AX, spec story 88): the
    // proposer takes it back, so the history keeps why. One option shape
    // for every group (`--reason`, alias `--note` like `review reject`), and
    // the reason is required: a withdrawal without one says nothing.
    public static System.CommandLine.Option<string?> WithdrawReasonOption() => new("--reason", "--note")
    {
        Description = "Why the proposal is withdrawn (required; recorded on the proposal and the audit line)",
    };

    public static string RequireWithdrawReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? throw new Core.ValidationException("reason-required",
                "withdrawing a proposal needs a reason (--reason) so the history says why it was taken back")
            : reason.Trim();

    // The audit fields of a withdrawal: who withdrew it and why.
    public static void RecordWithdraw(CommandContext ctx, string reason)
    {
        Record(ctx);
        ctx.Audit.Field("reason", reason);
    }
}
