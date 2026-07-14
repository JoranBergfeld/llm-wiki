using System.CommandLine;
using System.IO;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki lint [--fix-links]` (spec §11/§12, Task 22): runs every advisory
// check, files/refreshes findings in .wiki/issues.json, writes
// .wiki/lint.json. `--fix-links` additionally repairs mechanical wikilink
// targets/idmap entries broken by a detected rename-drift - see
// LintService.ApplyFixLinks for exactly what that does and doesn't touch.
public static class LintCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var fixLinksOption = new Option<bool>("--fix-links")
        {
            Description = "Repair mechanical wikilink targets and idmap entries broken by a detected rename-drift (Obsidian-side rename)",
        };

        var command = new Command("lint", "Run advisory checks (spec §11), file/refresh issues, write .wiki/lint.json")
        {
            fixLinksOption,
        };

        command.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var fixLinks = parseResult.GetValue(fixLinksOption);

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfig();
            var service = new LintService();
            var report = service.Run(vault, cfg, fixLinks);

            ctx.EmitOk(report);
            return 0;
        }));

        return command;
    }
}
