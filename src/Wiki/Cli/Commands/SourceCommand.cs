using System.CommandLine;
using System.IO;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki source ...` command group. Task 16 wires up `add` only; `list` /
// `show` / `impact` / `retract` land in later M2/M3 tasks as further
// siblings under the same `source` group.
public static class SourceCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var source = new Command("source", "Register and query immutable raw sources");
        source.Add(BuildAdd(vaultOption, jsonOption, stdout, stdin));
        return source;
    }

    private static Command BuildAdd(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var fileArgument = new Argument<string>("file")
        {
            Description = "Path to the raw source file to register",
        };
        var categoryOption = new Option<string>("--category")
        {
            Required = true,
            Description = "Source category id (must already exist in wiki.yaml)",
        };
        var titleOption = new Option<string>("--title")
        {
            Required = true,
            Description = "Source title",
        };
        var originOption = new Option<string?>("--origin")
        {
            Description = "Free-text provenance note, e.g. a URL (default: 'manual')",
        };

        var add = new Command("add", "Copy a file into raw/ under a new ULID, hash it, dedup, register the ledger entry")
        {
            fileArgument,
            categoryOption,
            titleOption,
            originOption,
        };

        add.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var file = parseResult.GetRequiredValue(fileArgument);
            var category = parseResult.GetRequiredValue(categoryOption);
            var title = parseResult.GetRequiredValue(titleOption);
            var origin = parseResult.GetValue(originOption);

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfig();

            var service = new SourceService();
            var result = service.Add(vault, cfg, file, category, title, origin);
            ctx.EmitOk(result);
            return 0;
        }));

        return add;
    }
}
