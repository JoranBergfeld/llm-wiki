namespace Wiki.Core;

// Thrown when a command refuses to run because the vault is already in some
// state that conflicts with the requested operation (e.g. `wiki init` against
// a directory that already has a wiki.yaml). Distinct from ValidationException
// (bad input, exit 1) - this is "your input is fine, but doing this now would
// clobber or duplicate existing state" (exit 3).
public sealed class StateConflictException : System.Exception
{
    public string Code { get; }
    public string? Path { get; }

    public StateConflictException(string code, string message, string? path = null)
        : base(message)
    {
        Code = code;
        Path = path;
    }
}
