using System.CommandLine;
using System.IO;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki reindex`: rebuilds .wiki/idmap.json and wiki/index.md from raw/ and
// wiki/ frontmatter alone. M1 scope (Task 15) - ledger/issues state rebuild
// is a later task (27), once ledger state exists at all. No flags/arguments:
// reindex always does a full rescan of the whole vault.
public static class ReindexCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var command = new Command("reindex", "Rebuild idmap.json and index.md from raw/ and wiki/ frontmatter alone");

        command.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            var service = new ReindexService();
            var result = service.Rebuild(vault);
            ctx.EmitOk(result);
            return 0;
        }));

        return command;
    }
}
