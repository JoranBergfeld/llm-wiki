using System.CommandLine;
using System.IO;
using System.Linq;
using Spectre.Console;
using Wiki.Core;
using Wiki.Services;
using Wiki.State;

namespace Wiki.Cli.Commands;

// `wiki schema propose/proposals/approve/reject` (spec §13, amendment C):
// the reflect loop's amendment surface. `propose` is the only verb the LLM
// is expected to drive; `approve`/`reject` are human actions (design
// principle 4, "the LLM may propose; it may never apply") - nothing here
// stops an agent from *invoking* approve/reject, but the workflow they exist
// to serve is a human reviewing `wiki schema proposals` and deciding.
public static class SchemaCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var schema = new Command("schema", "Propose and apply full-section replacements to AGENTS.md (reflect loop, spec §13)");
        schema.Add(BuildPropose(vaultOption, jsonOption, stdout, stdin));
        schema.Add(BuildProposals(vaultOption, jsonOption, stdout, stdin));
        schema.Add(BuildApprove(vaultOption, jsonOption, stdout, stdin));
        schema.Add(BuildReject(vaultOption, jsonOption, stdout, stdin));
        return schema;
    }

    private static Command BuildPropose(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var sectionOption = new Option<string>("--section")
        {
            Required = true,
            Description = "Heading text (exact, trimmed) of the '##' or '###' AGENTS.md section to replace",
        };
        var rationaleOption = new Option<string?>("--rationale")
        {
            Description = "Free-text rationale for the amendment, ideally citing issue IDs",
        };
        // Same stdin/--body-file split as `page upsert`: --stdin is the
        // documented default source, --body-file overrides it. Neither flag
        // is strictly required for stdin to be read - ResolveBody falls back
        // to stdin whenever --body-file is omitted.
        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read the section's new full body text from stdin (default body source when --body-file is omitted)",
        };
        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Read the section's new full body text from this file instead of stdin",
        };

        var propose = new Command("propose", "Submit the new full text of one AGENTS.md section as an open proposal")
        {
            sectionOption,
            rationaleOption,
            stdinOption,
            bodyFileOption,
        };

        propose.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var section = parseResult.GetRequiredValue(sectionOption);
            var rationale = parseResult.GetValue(rationaleOption) ?? "";
            var bodyFile = parseResult.GetValue(bodyFileOption);
            var newText = ResolveBody(ctx, bodyFile);

            var vault = ctx.ResolveVault();
            var service = new SchemaService();
            var created = service.Propose(vault, section, newText, rationale);

            if (ctx.Json)
            {
                ctx.EmitOk(ToData(created));
            }
            else
            {
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(ctx.Out) });
                console.MarkupLine($"[green]OK[/] proposed {Markup.Escape(created.Id)} for section '{Markup.Escape(created.Section)}'");
            }
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

        var proposals = new Command("proposals", "List AGENTS.md amendment proposals, optionally filtered by --status")
        {
            statusOption,
        };

        proposals.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var status = ValidateStatus(parseResult.GetValue(statusOption));

            var vault = ctx.ResolveVault();
            var service = new SchemaService();
            var results = service.List(vault, status).Select(ToData).ToArray();

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

        return proposals;
    }

    private static Command BuildApprove(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Proposal id (ULID) to approve",
        };

        var approve = new Command("approve", "Apply an open proposal's full-section replacement to AGENTS.md")
        {
            idArgument,
        };

        approve.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);

            var vault = ctx.ResolveVault();
            var service = new SchemaService();
            service.Approve(vault, id);

            var updated = Reload(vault, id);
            if (ctx.Json)
            {
                ctx.EmitOk(ToData(updated));
            }
            else
            {
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(ctx.Out) });
                console.MarkupLine($"[green]OK[/] approved {Markup.Escape(updated.Id)} -> AGENTS.md section '{Markup.Escape(updated.Section)}' replaced");
            }
            return 0;
        }));

        return approve;
    }

    private static Command BuildReject(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Proposal id (ULID) to reject",
        };
        var noteOption = new Option<string?>("--note")
        {
            Description = "Optional free-text rejection note",
        };

        var reject = new Command("reject", "Reject an open proposal; AGENTS.md is left untouched")
        {
            idArgument,
            noteOption,
        };

        reject.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var id = parseResult.GetRequiredValue(idArgument);
            var note = parseResult.GetValue(noteOption);

            var vault = ctx.ResolveVault();
            var service = new SchemaService();
            service.Reject(vault, id, note);

            var updated = Reload(vault, id);
            if (ctx.Json)
            {
                ctx.EmitOk(ToData(updated));
            }
            else
            {
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(ctx.Out) });
                console.MarkupLine($"[yellow]OK[/] rejected {Markup.Escape(updated.Id)}");
            }
            return 0;
        }));

        return reject;
    }

    // Approve/Reject are `void` on SchemaService (task interface) - re-read
    // the proposal through the store afterward, same "re-fetch for the
    // response DTO" pattern ReviewCommand uses around ReviewService.
    private static Proposal Reload(Vault v, string proposalId)
    {
        var store = new Proposals();
        store.Load(v);
        return store.Get(proposalId)!;
    }

    // `--status` is a raw string (Proposal.Status isn't an enum, same call as
    // Issue.Status) - validate against the closed lifecycle set before it
    // reaches Proposals.List's `!=` filter so a typo doesn't silently return
    // an empty list at exit 0.
    private static string? ValidateStatus(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        if (raw != "open" && raw != "approved" && raw != "rejected")
            throw new ValidationException("invalid-proposal-status", $"unknown proposal status '{raw}'; expected 'open', 'approved', or 'rejected'");
        return raw;
    }

    private static ProposalData ToData(Proposal p) => new()
    {
        Id = p.Id,
        Section = p.Section,
        NewText = p.NewText,
        Rationale = p.Rationale,
        Status = p.Status,
        CreatedAt = p.CreatedAt,
        Note = p.Note,
    };

    private static void RenderListTable(TextWriter output, System.Collections.Generic.IReadOnlyList<ProposalData> rows)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });

        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Section");
        table.AddColumn("Status");
        table.AddColumn("Rationale");
        table.AddColumn("CreatedAt");

        foreach (var r in rows)
        {
            table.AddRow(
                Markup.Escape(r.Id),
                Markup.Escape(r.Section),
                Markup.Escape(r.Status),
                Markup.Escape(r.Rationale),
                Markup.Escape(r.CreatedAt));
        }

        console.Write(table);
        console.MarkupLine($"[grey]{rows.Count} proposal(s)[/]");
    }

    private static string ResolveBody(CommandContext ctx, string? bodyFile)
    {
        if (!string.IsNullOrEmpty(bodyFile))
        {
            if (!File.Exists(bodyFile))
                throw new ValidationException("body-file-not-found", $"--body-file '{bodyFile}' does not exist", bodyFile);
            return File.ReadAllText(bodyFile);
        }
        return ctx.In.ReadToEnd();
    }
}
