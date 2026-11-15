namespace Wiki.Core;

// An interrupted write was found that recovery cannot finish or undo without
// guessing (issue #47): a file it touched has changed since, the audit log has
// moved on without its line, or the journal itself is damaged. Every write is
// refused until someone repairs the vault, because writing on top would bury
// the interrupted change; reads keep working. Exit 2 (environment problem),
// not 3: exit 3 means "already done, move on" and comes back over MCP as a
// success, and not 5: retrying will not help.
public sealed class RepairNeededException : System.Exception
{
    public const string Code = "repair-needed";
    public const int ExitCode = 2;

    public string? Path { get; }

    public RepairNeededException(string message, string? path = null)
        : base(message)
    {
        Path = path;
    }
}
