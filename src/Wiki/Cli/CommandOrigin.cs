namespace Wiki.Cli;

// How a command invocation reached the CLI (spec, Audit log: "each line
// records the transport (stdio/HTTP/CLI) and whether a token was used").
public enum Transport
{
    // A human or agent ran `wiki` directly.
    Cli,
    // An MCP tool call over `wiki mcp` (stdio).
    Stdio,
    // An MCP tool call over `wiki mcp --http`.
    Http,
}

// The transport and token flag of one invocation, carried as a value from
// the caller into CommandContext.Origin (issue #54), so the audit log (#44)
// can record it from the CommandInvocation it is handed. It travels as an
// App.Main argument, never through an environment variable or other process
// state: the MCP server runs many invocations in one process, and a tool
// call must not be able to influence how the next one is recorded.
public sealed record CommandOrigin(Transport Transport, bool TokenUsed)
{
    public static readonly CommandOrigin Cli = new(Transport.Cli, TokenUsed: false);
    public static readonly CommandOrigin Stdio = new(Transport.Stdio, TokenUsed: false);
}
