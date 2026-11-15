namespace Wiki.Core;

// Thrown when a command could not get the vault lock within the lock timeout
// (issue #46). Retryable: nothing was read or written, so running the same
// command again later is always safe. It maps to exit 5, deliberately not 3:
// exit 3 means "already done, move on" and comes back over MCP as a success,
// while a busy vault means "not done, try again" and must come back as an
// error the caller retries.
public sealed class VaultBusyException : System.Exception
{
    public const string Code = "vault-busy";
    public const int ExitCode = 5;

    public string? Path { get; }

    public VaultBusyException(string message, string? path = null)
        : base(message)
    {
        Path = path;
    }
}
