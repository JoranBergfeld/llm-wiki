namespace Wiki.Core;

// Thrown when a mutation is refused because the audit log's live chain is
// broken (issues #44, #45). Nothing was read past the check or written. It
// maps to exit 6, deliberately not 3: exit 3 means "already done, move on" and
// comes back over MCP as a success, while a refused write means "not done, and
// will not be until `wiki audit reanchor`", which must come back as an error.
// Not retryable as-is, unlike vault-busy (exit 5).
public sealed class AuditChainBrokenException : System.Exception
{
    public const string Code = "audit-chain-broken";
    public const int ExitCode = 6;

    public string? Path { get; }

    public AuditChainBrokenException(string message, string? path = null)
        : base(message)
    {
        Path = path;
    }
}
