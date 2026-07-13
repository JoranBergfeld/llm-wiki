namespace Wiki.Core;

// The single exception type all blocking validation throws. A top-level handler
// in App.Main maps Code/Message/Path onto a WikiError in the failure envelope.
public sealed class ValidationException : System.Exception
{
    public string Code { get; }
    public string? Path { get; }

    public ValidationException(string code, string message, string? path = null)
        : base(message)
    {
        Code = code;
        Path = path;
    }
}
