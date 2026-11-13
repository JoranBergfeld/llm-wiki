using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Wiki.Audit;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.Services;

// The persona evidence rules and the audit-log lookup behind them (issue
// #50, ADR 0001). Evidence lives only in the audit log: each persona.accept
// line carries a `ledger=@<sha256>` detail listing every line of the
// resulting PERSONA.md with the evidence it rests on, and PERSONA.md's
// `audit_entry` is the changeset id that line records as `changeset=<id>`.
//
// The line rule is defined here once and used both by `persona propose`
// (reject before queueing) and by `wiki lint` (the persona-evidence error).
public static class PersonaEvidence
{
    public const string AcceptAction = "persona.accept";
    public const string ChangesetField = "changeset";
    public const string LedgerField = "ledger";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // The content hash recorded for page evidence: SHA-256 of the page body
    // (LF line endings, UTF-8), not of the whole file. Frontmatter is
    // metadata the CLI itself rewrites - clearing the candidate flag, a status
    // flip, a rename - and none of that changes what the owner endorsed, so
    // it must not make evidence look stale (issue #52 compares this hash).
    public static string PageHash(string body) => AuditLine.Sha256Hex(body.Replace("\r\n", "\n"));

    // Why `evidence` is not enough to support a line in `section`, or null
    // when it is. Owner-stated text supports any line on its own; endorsed
    // notes need at least two distinct pages and never support tone of voice.
    public static string? Problem(PersonaSection section, IReadOnlyList<PersonaEvidenceData> evidence)
    {
        if (evidence.Any(e => e.Kind == PersonaEvidenceKind.OwnerStated))
            return null;
        if (section == PersonaSection.ToneOfVoice)
            return "tone of voice rests only on owner-stated text, never on notes";
        var pages = evidence.Where(e => e.Kind == PersonaEvidenceKind.Page)
            .Select(e => e.Page).Distinct(StringComparer.Ordinal).Count();
        return pages >= 2 ? null : $"a line distilled from endorsed notes needs at least two supporting pages (has {pages})";
    }

    // The evidence ledger recorded for the accept whose changeset id is
    // `auditEntry`, or null when there is none to read: no audit_entry, no
    // log, no such accept (a PERSONA.md written by hand or before this
    // ticket), or its detail file missing. Reads the live segment only;
    // rotation (#45) will need to look into older segments too.
    public static IReadOnlyList<PersonaLedgerLine>? LoadLedger(Vault v, string? auditEntry)
    {
        if (string.IsNullOrWhiteSpace(auditEntry))
            return null;

        var log = AuditLog.For(v);
        if (!File.Exists(log.LogPath))
            return null;

        string text;
        // ReadWrite share: a mutating command (lint) reads while its own
        // audit writer holds the file open.
        using (var s = new FileStream(log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(s, Utf8NoBom))
            text = r.ReadToEnd();

        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var parsed = AuditLine.Parse(lines[i]);
            if (parsed is null || parsed.Segments.Length < 3 || parsed.Segments[1] != AcceptAction)
                continue;
            if (Field(parsed, ChangesetField) != auditEntry)
                continue;

            var reference = Field(parsed, LedgerField);
            if (reference is null || reference.Length != 65 || reference[0] != '@')
                return null;
            var path = log.DetailPath(reference[1..]);
            if (!File.Exists(path))
                return null;
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path, Utf8NoBom), WikiJsonContext.Default.PersonaLedgerLineArray);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    private static string? Field(AuditLine.Parsed parsed, string key)
    {
        var prefix = key + "=";
        for (var i = 3; i < parsed.Segments.Length; i++)
            if (parsed.Segments[i].StartsWith(prefix, StringComparison.Ordinal))
                return parsed.Segments[i][prefix.Length..];
        return null;
    }

    // Lint: every line of PERSONA.md checked against the ledger of the accept
    // that produced it. A line the ledger does not know (added or reworded by
    // hand since) has no evidence at all. A file whose audit_entry resolves to
    // no recorded accept is not judged here: there is no evidence record to
    // check it against, and saying so is the stale-evidence check's job (#52).
    public static IEnumerable<string> LintProblems(Vault v, PersonaDoc doc)
    {
        var ledger = LoadLedger(v, doc.AuditEntry);
        if (ledger is null)
            yield break;

        foreach (var s in PersonaSectionX.InOrder)
        {
            var wire = PersonaSectionX.ToWire(s);
            foreach (var line in doc.Section(s))
            {
                var recorded = ledger.FirstOrDefault(l => l.Section == wire && l.Text == line);
                var problem = Problem(s, recorded?.Evidence ?? Array.Empty<PersonaEvidenceData>());
                if (problem is not null)
                    yield return $"{PersonaSectionX.Heading(s)} '{line}': {problem}";
            }
        }
    }
}
