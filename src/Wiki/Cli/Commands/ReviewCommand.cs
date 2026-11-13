using System.CommandLine;
using System.IO;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki review list/approve/reject` (spec §15): the review gate's workflow
// surface. `approve`/`reject` are `void` on ReviewService (task interface),
// so both re-read the page via PageService.Show afterward - same pattern
// PageCommand's `set-status` uses - to give the caller back the resulting
// frontmatter instead of a bespoke result DTO.
public static class ReviewCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var review = new Command("review", "Review gate: list/approve/reject pending-review pages (spec §15)");
        review.Add(BuildList(vaultOption, jsonOption, stdout, stdin));
        review.Add(BuildApprove(vaultOption, jsonOption, stdout, stdin));
        review.Add(BuildReject(vaultOption, jsonOption, stdout, stdin));
        return review;
    }

    private static Command BuildList(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var list = new Command("list", "List pending-review pages, with a diff against the shadow copy for updates");

        list.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            var service = new ReviewService();
            var results = service.List(vault);

            if (ctx.Json)
            {
                ctx.EmitOk(results);
            }
            else
            {
                RenderListView(ctx.Out, results);
            }
            return 0;
        }));

        return list;
    }

    private static Command BuildApprove(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Pending-review page id, or the persona changeset id (ULID), to approve",
        };

        var approve = new Command("approve", "Approve a pending-review page: pending-review -> active")
        {
            idArgument,
        };

        approve.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);

            var vault = ctx.ResolveVault();
            if (PersonaService.IsChangeset(vault, id))
                return ApprovePersona(ctx, vault, id);

            var service = new ReviewService();
            service.Approve(vault, id);
            DecisionAudit.Record(ctx);

            var view = PageQuery.Show(vault, id, frontmatterOnly: true);
            if (ctx.Json)
            {
                ctx.EmitOk(view);
            }
            else
            {
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(ctx.Out) });
                console.MarkupLine($"[green]OK[/] approved [[{Markup.Escape(view.Slug)}]] -> active");
            }
            return 0;
        }));

        return approve;
    }

    private static Command BuildReject(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Pending-review page id, or the persona changeset id (ULID), to reject",
        };
        var noteOption = new Option<string?>("--note", "--reason")
        {
            Description = "Rejection reason (recorded on the filed issue and the log); required for a persona changeset",
        };

        var reject = new Command("reject", "Reject a pending-review page: restores the previous body (update) or archives it (create); files an issue")
        {
            idArgument,
            noteOption,
        };

        reject.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var note = parseResult.GetValue(noteOption);

            var vault = ctx.ResolveVault();
            if (PersonaService.IsChangeset(vault, id))
                return RejectPersona(ctx, vault, id, note);

            var service = new ReviewService();
            service.Reject(vault, id, note);
            DecisionAudit.Record(ctx);

            var view = PageQuery.Show(vault, id, frontmatterOnly: true);
            if (ctx.Json)
            {
                ctx.EmitOk(view);
            }
            else
            {
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(ctx.Out) });
                console.MarkupLine($"[yellow]OK[/] rejected [[{Markup.Escape(view.Slug)}]] -> {Markup.Escape(view.Status)}");
            }
            return 0;
        }));

        return reject;
    }

    // A persona changeset is decided all-or-nothing (issue #32). Both
    // decisions record who decided (`decidedVia`) on their audit line; the
    // accept also records every evidence reference with its hash and the
    // full evidence ledger of the new PERSONA.md, which lint reads back.
    private static int ApprovePersona(CommandContext ctx, Vault vault, string id)
    {
        var (result, ledger, retired) = new PersonaService().Accept(vault, id);
        PersonaAudit.Accept(ctx, result, ledger, retired, PersonaAudit.DecidedVia(ctx));
        ctx.EmitOk(result);
        return 0;
    }

    private static int RejectPersona(CommandContext ctx, Vault vault, string id, string? note)
    {
        var result = new PersonaService().Reject(vault, id, note);

        ctx.Audit.Action = "persona.reject";
        ctx.Audit.Subject = id;
        ctx.Audit.Field("decidedVia", PersonaAudit.DecidedVia(ctx))
                 .Field("reason", result.Changeset.Note ?? "");

        ctx.EmitOk(result);
        return 0;
    }

    private static void RenderListView(TextWriter output, System.Collections.Generic.IReadOnlyList<PendingView> rows)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        if (rows.Count == 0)
        {
            console.MarkupLine("[grey]nothing pending review[/]");
            return;
        }

        foreach (var r in rows)
        {
            console.MarkupLine($"[bold]{Markup.Escape(r.Id)}[/] [[{Markup.Escape(r.Slug)}]] ({Markup.Escape(r.Type)}, {(r.IsUpdate ? "update" : "create")}) - {Markup.Escape(r.Title)}");
            if (r.Diff is not null)
            {
                foreach (var line in r.Diff.TrimEnd('\n').Split('\n'))
                    console.MarkupLine(Markup.Escape(line));
            }
        }
        console.MarkupLine($"[grey]{rows.Count} pending page(s)[/]");
    }
}
