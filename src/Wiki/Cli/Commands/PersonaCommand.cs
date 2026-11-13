using System.CommandLine;
using System.IO;
using System.Linq;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki persona ...` (spec §8, amendments AC and AG): the owner's persona in
// PERSONA.md. `read` serves it, `distill` is the read side of a distillation
// pass, `propose` queues the pass's changeset for `wiki review`. The
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
            var input = BodyInput.Resolve(ctx, parseResult.GetValue(bodyFileOption), parseResult.GetValue(stdinOption));
            var vault = ctx.ResolveVault();
            var result = new PersonaService().Propose(vault, input);

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

    // `cli` or `mcp` (issue #32); auto-accept (#51) sets its own value.
    public static string DecidedVia(CommandContext ctx) => DecisionAudit.DecidedVia(ctx);

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
