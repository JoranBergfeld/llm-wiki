using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Wiki.Audit;
using Wiki.Core;
using Wiki.Json;
using Wiki.State;

namespace Wiki.Services;

// The persona evidence rules and the audit-log lookup behind them (issue
// #50, ADR 0001). Evidence lives only in the audit log: each persona.accept
// line carries a `ledger=@<sha256>` detail listing every line of the
// resulting PERSONA.md with the evidence it rests on, and PERSONA.md's
// `audit_entry` is that line's hash (amendment AY). Files written before AY
// hold the changeset id instead, which the line records as `changeset=<id>`;
// the lookup accepts either, since a line hash and a ULID never collide.
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

    // The evidence ledger recorded for the accept whose line hash (or, for a
    // pre-AY file, changeset id) is `auditEntry`, or null when there is none to read: no audit_entry, no
    // log, no such accept (a PERSONA.md written by hand or before this
    // ticket), or its detail file missing. Searches newest first across the
    // live segment and, once rotated away (#45), the archived segments, so
    // an old accept costs a decompress only when it is not in the live log.
    public static IReadOnlyList<PersonaLedgerLine>? LoadLedger(Vault v, string? auditEntry)
    {
        if (string.IsNullOrWhiteSpace(auditEntry))
            return null;

        var log = AuditLog.For(v);
        foreach (var raw in log.LinesNewestFirst())
        {
            var parsed = AuditLine.Parse(raw);
            if (parsed is null || parsed.Segments.Length < 3 || parsed.Segments[1] != AcceptAction)
                continue;
            if (parsed.Hash != auditEntry && Field(parsed, ChangesetField) != auditEntry)
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

    // Why a recorded piece of page evidence no longer holds (issue #52).
    public const string StaleChanged = "changed";
    public const string StaleGone = "gone";
    public const string StaleNotEndorsed = "not-endorsed";

    // Owner-stated evidence (issue #53): `changed` and `gone` apply to the
    // one answer it names; a deleted transcript is `retracted`, the owner
    // taking back everything said in it.
    public const string StaleRetracted = "retracted";

    // The issue subject of one persona line: lines have no ids (issue #31),
    // so section plus text is the line's identity. A reworded line is a new
    // line, which is exactly when its old stale warning should retire.
    public static string LineSubject(string sectionWire, string text) => $"{PersonaDoc.FileName}#{sectionWire}: {text}";

    // Every line of PERSONA.md resting on at least one piece of evidence that
    // no longer holds, judged against the ledger of the accept that wrote the
    // file. Page evidence is stale when the page (looked up by id, so a
    // rename is not staleness) is gone, its body no longer hashes to the
    // recorded value, or it is no longer an endorsed note (amendment AV: every cited source in a
    // persona-source category). Owner-stated evidence (issue #53) is stale when its answer
    // no longer hashes to the recorded value (changed), its anchor is no
    // longer in the transcript (gone) or the transcript was deleted
    // (retracted); only the lines resting on that one answer are affected.
    // A line the ledger does not know is the persona-evidence error's
    // business, not this check's.
    public static IReadOnlyList<PersonaStaleLine> StaleLines(Vault v, VaultConfig cfg, PersonaDoc doc)
    {
        var ledger = LoadLedger(v, doc.AuditEntry);
        if (ledger is null)
            return Array.Empty<PersonaStaleLine>();

        var idmap = new IdMap();
        idmap.Load(v);
        var pages = new Dictionary<string, (string Slug, PageFrontmatter Front, string Body)>(StringComparer.Ordinal);
        foreach (var p in PageStore.EnumerateWithBody(v))
            pages[p.Front.Id] = p;

        var transcripts = new Dictionary<string, InterviewTranscript?>(StringComparer.Ordinal);

        var result = new List<PersonaStaleLine>();
        foreach (var s in PersonaSectionX.InOrder)
        {
            var wire = PersonaSectionX.ToWire(s);
            foreach (var line in doc.Section(s))
            {
                var recorded = ledger.FirstOrDefault(l => l.Section == wire && l.Text == line);
                if (recorded is null)
                    continue;
                var stale = new List<PersonaStaleEvidence>();
                foreach (var e in recorded.Evidence.Where(e => e.Kind == PersonaEvidenceKind.Page && e.Page is not null))
                {
                    if (!pages.TryGetValue(e.Page!, out var page))
                    {
                        stale.Add(PageStale(e.Page!, e.Path, null, StaleGone, e.Hash, null));
                        continue;
                    }
                    var path = PagePaths.Relative(v, page.Slug, page.Front);
                    var hash = PageHash(page.Body);
                    if (hash != e.Hash)
                        stale.Add(PageStale(e.Page!, path, page.Slug, StaleChanged, e.Hash, hash));
                    else if (!PageService.IsEndorsedNote(v, cfg, idmap, page.Front.Sources))
                        stale.Add(PageStale(e.Page!, path, page.Slug, StaleNotEndorsed, e.Hash, hash));
                }
                foreach (var e in recorded.Evidence.Where(e => e.Kind == PersonaEvidenceKind.OwnerStated && e.Path is not null))
                {
                    var problem = OwnerStatedStale(v, transcripts, e);
                    if (problem is not null)
                        stale.Add(problem);
                }
                if (stale.Count > 0)
                    result.Add(new PersonaStaleLine(wire, line, LineSubject(wire, line), stale.ToArray()));
            }
        }
        return result;
    }

    private static PersonaStaleEvidence PageStale(string page, string? path, string? slug, string reason, string? recorded, string? current)
        => new(PersonaEvidenceKind.Page, page, path, slug, null, reason, recorded, current);

    private static PersonaStaleEvidence? OwnerStatedStale(Vault v, Dictionary<string, InterviewTranscript?> cache, PersonaEvidenceData e)
    {
        PersonaStaleEvidence Stale(string reason, string? current)
            => new(PersonaEvidenceKind.OwnerStated, null, e.Path, null, e.Anchor, reason, e.Hash, current);

        var file = System.IO.Path.GetFileNameWithoutExtension(e.Path!);
        if (!cache.TryGetValue(file, out var transcript))
            cache[file] = transcript = InterviewTranscript.IsId(file) ? InterviewService.Load(v, file) : null;
        if (transcript is null)
            return Stale(StaleRetracted, null);
        var entry = transcript.Entries.FirstOrDefault(x => x.Anchor == e.Anchor);
        if (entry is null)
            return Stale(StaleGone, null);
        var hash = InterviewTranscript.AnswerHash(entry.Answer);
        return hash == e.Hash ? null : Stale(StaleChanged, hash);
    }

    // The lint detail for one stale line: every stale piece, then what to do.
    public static string StaleDetail(PersonaStaleLine line)
    {
        var parts = line.Evidence.Select(e =>
        {
            if (e.Kind == PersonaEvidenceKind.OwnerStated)
            {
                var answer = $"owner-stated answer {e.Path}#{e.Anchor}";
                return e.Reason switch
                {
                    StaleChanged => $"{answer} changed since the line was accepted (edited by hand) (recorded sha256 {Short(e.RecordedHash)}, now {Short(e.CurrentHash)})",
                    StaleGone => $"{answer} is no longer in the transcript",
                    _ => $"{answer} was retracted: the interview transcript was deleted",
                };
            }
            var label = e.Slug is null ? $"page {e.Page} ({e.Path})" : $"[[{e.Slug}]] ({e.Path})";
            return e.Reason switch
            {
                StaleChanged => $"{label} changed since the line was accepted (recorded sha256 {Short(e.RecordedHash)}, now {Short(e.CurrentHash)})",
                StaleGone => $"{label} is gone",
                _ => $"{label} is no longer an endorsed note (a cited source is outside the persona-source categories)",
            };
        });
        return string.Join("; ", parts)
               + ". Re-check the line in a distillation pass (`wiki persona distill` lists it under `recheck`): "
               + "re-confirm it with current evidence, reword it or remove it.";
    }

    private static string Short(string? hash) => hash is null ? "?" : hash.Length > 12 ? hash[..12] : hash;

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

// One stale piece of evidence behind a persona line (issue #52). `Kind` is
// page or owner-stated. For a page, `Reason` is changed | gone |
// not-endorsed; `Slug` is null when the page is gone, `Path` is then the
// recorded path, else the page's current one. For owner-stated evidence
// (issue #53) `Page` and `Slug` are null, `Path` + `Anchor` name the answer
// and `Reason` is changed | gone | retracted.
public sealed record PersonaStaleEvidence(string Kind, string? Page, string? Path, string? Slug, string? Anchor, string Reason,
    string? RecordedHash, string? CurrentHash);

// A persona line resting on stale evidence: the re-check a distillation pass
// picks up. `Subject` is the line's `persona-stale-evidence` issue subject.
public sealed record PersonaStaleLine(string Section, string Text, string Subject, PersonaStaleEvidence[] Evidence);
