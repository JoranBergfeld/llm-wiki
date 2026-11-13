using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;

namespace Wiki.Cli;

// Wraps a (ParseResult, CommandContext) handler into the Func<ParseResult,int>
// shape Command.SetAction wants, building the CommandContext from the
// recursive global options plus the in/out streams App.Main was called with.
// Every command's Build(...) closes over the same stdout/stdin App.Main
// received, so no command ever touches System.Console - the whole tree stays
// in-proc testable. Shared here so later command groups (Task 9+) don't each
// re-derive this wiring.
//
// It is also the single entry into VaultBoundary (issue #42): every handler
// bound here runs inside the read or the mutation boundary, chosen by the
// command's CommandCatalog entry.
public static class CommandBinding
{
    public static Func<ParseResult, int> Bind(
        Option<string?> vaultOption,
        Option<bool> jsonOption,
        TextWriter stdout,
        TextReader stdin,
        Func<ParseResult, CommandContext, int> handler)
    {
        return parseResult =>
        {
            var ctx = new CommandContext
            {
                VaultFlag = parseResult.GetValue(vaultOption),
                Json = parseResult.GetValue(jsonOption),
                Out = stdout,
                In = stdin,
            };

            var name = CommandPath(parseResult.CommandResult);
            // Fail closed: an unclassified command is a programming error the
            // CommandClassificationTests exist to catch. Running it unguarded
            // would let a writer past the lock and the audit log.
            var kind = CommandCatalog.Classify(name)
                ?? throw new InvalidOperationException($"command '{name}' is not classified in CommandCatalog");

            var invocation = new CommandInvocation
            {
                Name = name,
                Kind = kind,
                Context = ctx,
                ParseResult = parseResult,
            };
            return VaultBoundary.Run(invocation, () => handler(parseResult, ctx));
        };
    }

    // "page upsert" for `wiki page upsert ...`: the invoked command's name
    // and its parents', root excluded. Walks the parse result rather than
    // Command.Parents because that is the path the user actually took.
    public static string CommandPath(CommandResult result)
    {
        var path = result.Command.Name;
        for (var parent = result.Parent as CommandResult; parent?.Parent is not null; parent = parent.Parent as CommandResult)
            path = parent.Command.Name + " " + path;
        return path;
    }
}
