using System;
using System.CommandLine;
using System.IO;

namespace Wiki.Cli;

// Wraps a (ParseResult, CommandContext) handler into the Func<ParseResult,int>
// shape Command.SetAction wants, building the CommandContext from the
// recursive global options plus the in/out streams App.Main was called with.
// Every command's Build(...) closes over the same stdout/stdin App.Main
// received, so no command ever touches System.Console - the whole tree stays
// in-proc testable. Shared here so later command groups (Task 9+) don't each
// re-derive this wiring.
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
            return handler(parseResult, ctx);
        };
    }
}
