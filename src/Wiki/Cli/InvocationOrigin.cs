using System.CommandLine;

namespace Wiki.Cli;

// How a command invocation reached the CLI (issue #44, settled in #34/#35).
// Only the transport is known, never a client identity: the MCP server is
// single-user, so "which client" has no answer.
public enum Transport
{
    // A human or agent ran the `wiki` binary.
    Cli,
    // The MCP server over stdio (`wiki mcp`).
    Stdio,
    // The MCP server over streamable HTTP (`wiki mcp --http`).
    Http,
}

// The transport plus whether a bearer token was presented. Recorded on every
// audit line as `via=cli`, `via=stdio`, `via=http` or `via=http+token`.
//
// It is passed IN, never read from the environment or argv: the MCP adapter
// calls App.Main(argv, stdout, stdin, origin) with a typed value (#37: the
// adapter never sets environment variables, and argv is built from remote tool
// arguments, so a flag would let a remote client claim to be the CLI).
public sealed record InvocationOrigin(Transport Transport, bool TokenUsed)
{
    public static readonly InvocationOrigin Cli = new(Transport.Cli, false);

    public string Via => Transport switch
    {
        Transport.Cli => "cli",
        Transport.Stdio => "stdio",
        _ => TokenUsed ? "http+token" : "http",
    };
}

// The root of the command tree, carrying the origin of this one parse. This is
// how the origin reaches CommandBinding.Bind without threading a new parameter
// through every command's Build(...): Bind reads it off
// parseResult.RootCommandResult. The tree is rebuilt per App.Main call, so the
// value is per invocation and safe under parallel in-process callers.
public sealed class WikiRootCommand : RootCommand
{
    public InvocationOrigin Origin { get; }

    public WikiRootCommand(string description, InvocationOrigin origin) : base(description)
    {
        Origin = origin;
    }
}
