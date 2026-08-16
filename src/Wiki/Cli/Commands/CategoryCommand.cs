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
        var category = new Command("category", "Add, list, and propose source categories declared in wiki.yaml");
        category.Add(BuildAdd(vaultOption, jsonOption, stdout, stdin));
        category.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        category.Add(BuildPropose(vaultOption, jsonOption, stdout, stdin));
        category.Add(BuildProposals(vaultOption, jsonOption, stdout, stdin));
        category.Add(BuildApprove(vaultOption, jsonOption, stdout, stdin));
        category.Add(BuildReject(vaultOption, jsonOption, stdout, stdin));
        return category;
    }

    // `wiki category propose <id> --description "…" [--rationale "…"]
    // [--sources id1,id2]` (issue #9): the agent's channel for "nothing in the
    // taxonomy fits these sources". Mirrors `wiki schema propose` - same
    // lifecycle, same listing, same approve/reject verbs, same "the LLM may
    // propose; it may never apply" rule. Approving performs the `category add`
    // the human would otherwise have typed.
    private static Command BuildPropose(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Proposed category id - lowercase kebab-case, must not already exist",
        };
        var descriptionOption = new Option<string>("--description")
        {
            Required = true,
            Description = "One-line description of what would belong in this category",
        };
        var rationaleOption = new Option<string?>("--rationale")
        {
            Description = "Why the existing categories do not fit",
        };
        var sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated ids of the sources that fit no existing category (the evidence for the decision)",
        };

        var propose = new Command("propose", "Propose a new category for the human to approve or reject")
        {
            idArgument,
            descriptionOption,
            rationaleOption,
            sourcesOption,
        };

        propose.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var description = parseResult.GetRequiredValue(descriptionOption);
            var rationale = parseResult.GetValue(rationaleOption) ?? "";
            var sources = SplitCsv(parseResult.GetValue(sourcesOption));

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfigWithoutCategoryCrossCheck();

            var service = new CategoryService();
            var created = service.Propose(vault, cfg, id, description, rationale, sources);
            ctx.EmitOk(created);
            return 0;
        }));

        return propose;
    }

    private static Command BuildProposals(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var statusOption = new Option<string?>("--status")
        {
            Description = "Filter to a single status: open | approved | rejected",
        };

        var proposals = new Command("proposals", "List category proposals, optionally filtered by --status")
        {
            statusOption,
        };

        proposals.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var status = ValidateStatus(parseResult.GetValue(statusOption));

            var vault = ctx.ResolveVault();
            var service = new CategoryService();
            var results = service.ListProposals(vault, status);

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderProposalTable(ctx.Out, results);
            }
            return 0;
        }));

        return proposals;
    }

    private static Command BuildApprove(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Category proposal id (ULID) to approve",
        };

        var approve = new Command("approve", "Approve a category proposal: performs the equivalent 'category add'")
        {
            idArgument,
        };

        approve.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);

            var vault = ctx.ResolveVault();
            var cfg = ctx.LoadConfigWithoutCategoryCrossCheck();

            var service = new CategoryService();
            var updated = service.Approve(vault, cfg, id);
            ctx.EmitOk(updated);
            return 0;
        }));

        return approve;
    }

    private static Command BuildReject(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Category proposal id (ULID) to reject",
        };
        var noteOption = new Option<string?>("--note")
        {
            Description = "Optional free-text rejection note",
        };

        var reject = new Command("reject", "Reject a category proposal; wiki.yaml is left untouched")
        {
            idArgument,
            noteOption,
        };

        reject.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var note = parseResult.GetValue(noteOption);

            var vault = ctx.ResolveVault();
            var service = new CategoryService();
            var updated = service.Reject(vault, id, note);
            ctx.EmitOk(updated);
            return 0;
        }));

        return reject;
    }

    // Same closed lifecycle set (and same rationale for validating it before
    // it reaches a `!=` filter) as SchemaCommand.ValidateStatus.
    private static string? ValidateStatus(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        if (raw != "open" && raw != "approved" && raw != "rejected")
            throw new ValidationException("invalid-proposal-status",
                $"unknown proposal status '{raw}'; expected 'open', 'approved', or 'rejected'");
        return raw;
    }

    private static string[] SplitCsv(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? System.Array.Empty<string>()
            : raw.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

    private static void RenderProposalTable(TextWriter output, IReadOnlyList<CategoryProposalView> rows)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Category");
        table.AddColumn("Description");
        table.AddColumn("Status");
        table.AddColumn("Sources");
        table.AddColumn("Rationale");

        foreach (var r in rows)
        {
            table.AddRow(
                Markup.Escape(r.Id),
                Markup.Escape(r.CategoryId),
                Markup.Escape(r.Description),
                Markup.Escape(r.Status),
                Markup.Escape(string.Join(", ", r.Sources)),
                Markup.Escape(r.Rationale));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{rows.Count} proposal(s)[/]");
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
