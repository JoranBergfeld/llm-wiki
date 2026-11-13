using System.CommandLine;

namespace Wiki.Cli;

// The root of the command tree App.Main builds afresh for every invocation.
// It carries that invocation's CommandOrigin so CommandBinding can hand it to
// the command's CommandContext (and from there to the boundary steps). A
// fresh tree per call keeps the value per invocation: concurrent in-process
// calls (tests, MCP tool calls) never see each other's origin.
public sealed class WikiRootCommand(string description, CommandOrigin origin) : RootCommand(description)
{
    public CommandOrigin Origin { get; } = origin;
}
