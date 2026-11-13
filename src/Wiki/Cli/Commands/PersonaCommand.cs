using System.CommandLine;
using System.IO;
using System.Linq;
using Wiki.Core;
using Wiki.Services;

namespace Wiki.Cli.Commands;

// `wiki persona ...` (spec §8, amendments AC, AH and AM): the owner's persona in
// PERSONA.md. `read` serves it, `distill` is the read side of a distillation
// pass, `propose` queues the pass's changeset for `wiki review`, or accepts it
// at once when WIKI_PERSONA_AUTO_ACCEPT is on. `interview guide|record` is the
// onboarding interview (issue #53, amendment AO).
public static class PersonaCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var persona = new Command("persona", "The owner's persona (PERSONA.md: values, principles, tone of voice, boundaries)");
        persona.Add(BuildRead(vaultOption, jsonOption, stdout, stdin));
        persona.Add(BuildDistill(vaultOption, jsonOption, stdout, stdin));
        persona.Add(BuildPropose(vaultOption, jsonOption, stdout, stdin));
        persona.Add(BuildInterview(vaultOption, jsonOption, stdout, stdin));
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

    private static Command BuildInterview(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin)
    {
        var interview = new Command("interview",
            "The onboarding interview: an agent asks the owner about values, principles, tone of voice and boundaries, and records each answer word for word");

        var guide = new Command("guide", "Print the interview guide: the vault's persona/interview-guide.md when present, else the shipped one");
        guide.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            ctx.EmitOk(InterviewService.Guide(ctx.ResolveVault()));
            return 0;
        }));
        interview.Add(guide);

        var sectionOption = new Option<string>("--section")
        {
            Description = "The persona section the answer speaks to: values, principles, tone-of-voice or boundaries",
            Required = true,
        };
        var questionOption = new Option<string?>("--question")
        {
            Description = "The question as asked; omit for a line the owner dictated",
        };
        var answerOption = new Option<string?>("--answer")
        {
            Description = "The owner's answer, word for word; omit to read it from stdin or --body-file",
        };
        var transcriptOption = new Option<string?>("--transcript")
        {
            Description = "Append to this transcript (its id, e.g. 2026-11-13-v2); omit to start a new one",
        };
        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read the answer from stdin (default when neither --answer nor --body-file is given)",
        };
        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Read the answer from this file",
        };

        var record = new Command("record",
            "Append one question and answer, word for word, to an interview transcript; without --transcript a new transcript is started and its id returned")
        {
            sectionOption,
            questionOption,
            answerOption,
            transcriptOption,
            stdinOption,
            bodyFileOption,
        };
        record.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var answer = parseResult.GetValue(answerOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);
            var stdinFlag = parseResult.GetValue(stdinOption);
            if (answer is not null && (stdinFlag || !string.IsNullOrEmpty(bodyFile)))
                throw new ValidationException("body-source-conflict",
                    "--answer, --stdin and --body-file are mutually exclusive; pass exactly one");
            answer ??= BodyInput.Resolve(ctx, bodyFile, stdinFlag);

            var vault = ctx.ResolveVault();
            var result = new InterviewService().Record(vault, ctx.Origin, parseResult.GetValue(sectionOption)!,
                parseResult.GetValue(questionOption), answer, parseResult.GetValue(transcriptOption));

            ctx.Audit.Action = "persona.interview.record";
            ctx.Audit.Subject = result.Path + "#" + result.Anchor;
            ctx.Audit.Field("section", result.Section)
                     .Field("guide", result.Guide)
                     .Field("recordedVia", result.RecordedVia)
                     .Field("answer-hash", result.AnswerHash);
            if (result.Created)
                ctx.Audit.Field("created", "true");

            ctx.EmitOk(result);
            return 0;
        }));
        interview.Add(record);

        return interview;
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
                var acceptance = service.Accept(vault, result.Changeset.Id);
                result = result with { Changeset = acceptance.Result.Changeset, PendingPath = null, AutoAccepted = true };
                PersonaAudit.Accept(ctx, acceptance, PersonaAutoAccept.DecidedVia);
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

    // `cli` or `mcp` (issue #32; shared with every decision, DecisionAudit);
    // auto-accept (#51) records PersonaAutoAccept.DecidedVia instead.
    public static string DecidedVia(CommandContext ctx) => DecisionAudit.DecidedVia(ctx);

    // The `persona.accept` audit line, for `review approve` and for
    // auto-accept alike: who decided, every evidence reference with its hash,
    // and the full evidence ledger of the new PERSONA.md, which lint reads
    // back by the line's hash, PERSONA.md's `audit_entry` (amendment AY): the
    // file itself is written once the line is built, through WriteOnSeal.
    // `retired` is the evidence the
    // previous ledger held and the new one does not (issue #52): removing or
    // rewording a line retires what supported it, on the record; the field
    // is omitted when nothing is retired.
    public static void Accept(CommandContext ctx, PersonaAcceptance acceptance, string decidedVia)
    {
        var (result, ledger, retired, writePersona) = acceptance;
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
        if (retired.Length > 0)
            ctx.Audit.Field("retired", EvidenceRefs(retired));
        ctx.Audit.WriteOnSeal(writePersona);
    }

    // Every evidence reference of the lines the changeset introduces, as
    // `<path>@<sha256>` (or `<path>#<anchor>@<sha256>` for owner-stated).
    public static string EvidenceList(PersonaChangesetData c)
        => EvidenceRefs(c.Changes.SelectMany(x => x.Evidence ?? System.Array.Empty<PersonaEvidenceData>()));

    public static string EvidenceRefs(System.Collections.Generic.IEnumerable<PersonaEvidenceData> evidence)
    {
        var refs = evidence
            .Select(e => (e.Anchor is null ? e.Path : e.Path + "#" + e.Anchor) + "@" + e.Hash)
            .Distinct(System.StringComparer.Ordinal)
            .ToArray();
        return refs.Length == 0 ? "-" : string.Join(",", refs);
    }
}
