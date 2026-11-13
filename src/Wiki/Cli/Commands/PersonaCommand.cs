using System.CommandLine;
using System.IO;
using System.Linq;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki persona ...` (spec §8, amendments AC, AH and AM): the owner's persona in
// PERSONA.md. `read` serves it, `distill` is the read side of a distillation
// pass, `propose` queues the pass's changeset for `wiki review`, or accepts it
// at once when WIKI_PERSONA_AUTO_ACCEPT is on. The
// onboarding interview (`persona interview guide|record`) joins as a nested
// command in a later ticket.
public static class PersonaCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var persona = new Command("persona", "The owner's persona (PERSONA.md: values, principles, tone of voice, boundaries)");
        persona.Add(BuildRead(vaultOption, jsonOption, stdout, stdin));
        persona.Add(BuildDistill(vaultOption, jsonOption, stdout, stdin));
        persona.Add(BuildPropose(vaultOption, jsonOption, stdout, stdin));
        return persona;
    }

    private static Command BuildRead(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var read = new Command("read", "Print the persona; a vault without PERSONA.md has an empty persona");

        read.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            var view = new PersonaService().Read(vault);
            ctx.EmitOk(view);
            return 0;
        }));

        return read;
    }

    private static Command BuildDistill(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var distill = new Command("distill",
            "Start a distillation pass: candidate pages (endorsed notes not yet considered), the current persona and any pending changeset");

        distill.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();
            ctx.EmitOk(new PersonaService().Distill(vault));
            return 0;
        }));

        return distill;
    }

    private static Command BuildPropose(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read the changeset JSON from stdin (default when --body-file is omitted)",
        };
        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Read the changeset JSON from this file instead of stdin",
        };

        var propose = new Command("propose",
            "Queue one persona changeset for `wiki review` (validated against persona lint first) and clear the candidate flags the pass considered")
        {
            stdinOption,
            bodyFileOption,
        };

        propose.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            // Read first: a malformed value fails before anything is written.
            var autoAccept = PersonaAutoAccept.Enabled();
            var input = BodyInput.Resolve(ctx, parseResult.GetValue(bodyFileOption), parseResult.GetValue(stdinOption));
            var vault = ctx.ResolveVault();
            var service = new PersonaService();
            var result = service.Propose(vault, input);

            if (autoAccept && result.Changeset is not null)
            {
                // Auto-accept skips only the human: Propose has validated the
                // changeset against persona lint, and Accept validates it
                // again against PERSONA.md, exactly as `review approve` does.
                // The step gets one audit line, the accept, carrying what the
                // propose line would have said about supersession and flags.
                var (decision, ledger) = service.Accept(vault, result.Changeset.Id);
                result = result with { Changeset = decision.Changeset, PendingPath = null, AutoAccepted = true };
                PersonaAudit.Accept(ctx, decision, ledger, PersonaAutoAccept.DecidedVia);
                if (result.Superseded is not null)
                    ctx.Audit.Field("supersedes", result.Superseded);
                if (result.Cleared.Length > 0)
                    ctx.Audit.Field("cleared", string.Join(",", result.Cleared));
                ctx.EmitOk(result);
                return 0;
            }

            ctx.Audit.Action = "persona.propose";
            ctx.Audit.Subject = result.Changeset?.Id ?? "-";
            if (result.Changeset is not null)
            {
                var (a, c, r) = PersonaService.Counts(result.Changeset);
                ctx.Audit.Field("lines", $"+{a} ~{c} -{r}");
            }
            if (result.Superseded is not null)
                ctx.Audit.Field("supersedes", result.Superseded);
            if (result.Cleared.Length > 0)
                ctx.Audit.Field("cleared", string.Join(",", result.Cleared));
            ctx.Audit.Field("reason", result.Changeset?.Reason ?? "considered pages only");
            if (result.Changeset is not null)
                ctx.Audit.Detail("changeset-detail", PersonaAudit.ChangesetJson(result.Changeset));

            ctx.EmitOk(result);
            return 0;
        }));

        return propose;
    }
}

// Shared by `persona propose` and `review approve|reject` for their audit
// lines.
internal static class PersonaAudit
{
    public static string ChangesetJson(PersonaChangesetData c)
        => System.Text.Json.JsonSerializer.Serialize(c, Json.WikiJsonContext.Default.PersonaChangesetData);

    public static string LedgerJson(PersonaLedgerLine[] ledger)
        => System.Text.Json.JsonSerializer.Serialize(ledger, Json.WikiJsonContext.Default.PersonaLedgerLineArray);

    // `cli` for a person or agent at the terminal, `mcp` for either MCP
    // transport (issue #32); auto-accept (#51) records
    // PersonaAutoAccept.DecidedVia instead.
    public static string DecidedVia(CommandContext ctx) => ctx.Origin.Transport == Transport.Cli ? "cli" : "mcp";

    // The `persona.accept` audit line, for `review approve` and for
    // auto-accept alike: who decided, every evidence reference with its hash,
    // and the full evidence ledger of the new PERSONA.md, which lint reads
    // back by `changeset=<audit_entry>`.
    public static void Accept(CommandContext ctx, PersonaDecisionResult result, PersonaLedgerLine[] ledger, string decidedVia)
    {
        var c = result.Changeset;
        ctx.Audit.Action = PersonaEvidence.AcceptAction;
        ctx.Audit.Subject = PersonaDoc.FileName;
        ctx.Audit.Field(PersonaEvidence.ChangesetField, c.Id)
                 .Field("lines", $"+{result.Added} ~{result.Changed} -{result.Removed}")
                 .Field("evidence", EvidenceList(c))
                 .Field("decidedVia", decidedVia)
                 .Field("reason", c.Reason)
                 .Detail("changeset-detail", ChangesetJson(c))
                 .Detail(PersonaEvidence.LedgerField, LedgerJson(ledger));
    }

    // Every evidence reference of the lines the changeset introduces, as
    // `<path>@<sha256>` (or `<path>#<anchor>@<sha256>` for owner-stated).
    public static string EvidenceList(PersonaChangesetData c)
    {
        var refs = c.Changes
            .SelectMany(x => x.Evidence ?? System.Array.Empty<PersonaEvidenceData>())
            .Select(e => (e.Anchor is null ? e.Path : e.Path + "#" + e.Anchor) + "@" + e.Hash)
            .Distinct(System.StringComparer.Ordinal)
            .ToArray();
        return refs.Length == 0 ? "-" : string.Join(",", refs);
    }
}
