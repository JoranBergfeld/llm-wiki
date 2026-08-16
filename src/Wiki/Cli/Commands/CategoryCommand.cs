using System.CommandLine;
using System.Collections.Generic;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki category add/list` (spec §5): the ONLY sanctioned way to add a
// category to wiki.yaml besides a human hand-editing the file. Nothing else
// in this codebase writes wiki.yaml - `wiki source add` only ever READS it
// (VaultConfig.HasCategory) and rejects an unknown category rather than
// creating one. See CategoryService's doc comment for the "no auto-create"
// guarantee this structurally enforces.
public static class CategoryCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var category = new Command("category", "Add and list source categories declared in wiki.yaml");
        category.Add(BuildAdd(vaultOption, jsonOption, stdout, stdin));
        category.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        return category;
    }

    private static Command BuildAdd(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "New category id - lowercase kebab-case, must not already exist",
        };
        var descriptionOption = new Option<string>("--description")
        {
            Required = true,
            Description = "One-line description of what belongs in this category",
        };

        var add = new Command("add", "Add a category to wiki.yaml, preserving the rest of the file")
        {
            idArgument,
            descriptionOption,
        };

        add.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var description = parseResult.GetRequiredValue(descriptionOption);

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfigWithoutCategoryCrossCheck();

            var service = new CategoryService();
            var result = service.Add(vault, cfg, id, description);
            ctx.EmitOk(result);
            return 0;
        }));

        return add;
    }

    private static Command BuildList(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var list = new Command("list", "List categories declared in wiki.yaml");

        list.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var cfg = ctx.LoadConfigWithoutCategoryCrossCheck();
            var service = new CategoryService();
            var results = service.List(cfg);

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderListTable(ctx.Out, results);
            }
            return 0;
        }));

        return list;
    }

    private static void RenderListTable(TextWriter output, IReadOnlyList<CategoryData> categories)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Description");

        foreach (var c in categories)
        {
            table.AddRow(Markup.Escape(c.Id), Markup.Escape(c.Description));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{categories.Count} categor{(categories.Count == 1 ? "y" : "ies")}[/]");
    }
}
