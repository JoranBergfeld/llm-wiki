using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Json;
using Wiki.State;

namespace Wiki.Services;

// `wiki persona read` result: the persona as four line arrays plus the
// three frontmatter fields. `Exists` separates "no PERSONA.md yet" from "a
// PERSONA.md with empty sections" for a caller that cares; both read as an
// empty persona. Frontmatter fields are null when absent or unparseable -
// `wiki lint` is where a malformed file gets reported, not here.
public sealed record PersonaView(
    bool Exists,
    string Path,
    string? SchemaVersion,
    string? LastDistilled,
    string? AuditEntry,
    string[] Values,
    string[] Principles,
    string[] ToneOfVoice,
    string[] Boundaries) : IHumanRenderable
{
    public string HumanSummary()
    {
        if (!Exists)
            return "No persona yet (PERSONA.md does not exist)";

        var sb = new StringBuilder();
        sb.Append("Persona");
        if (LastDistilled is not null) sb.Append($" (last distilled {LastDistilled})");
        AppendSection(sb, PersonaSection.Values, Values);
        AppendSection(sb, PersonaSection.Principles, Principles);
        AppendSection(sb, PersonaSection.ToneOfVoice, ToneOfVoice);
        AppendSection(sb, PersonaSection.Boundaries, Boundaries);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, PersonaSection s, string[] lines)
    {
        sb.Append("\n\n").Append(PersonaSectionX.Heading(s));
        if (lines.Length == 0)
        {
            sb.Append("\n  (none)");
            return;
        }
        foreach (var line in lines)
            sb.Append("\n  - ").Append(line);
    }
}

// One page a distillation pass should consider: an endorsed note still
// flagged `persona: candidate`. Enough to route; the agent reads the body
// with `wiki page show`.
public sealed record PersonaCandidateView(string Id, string Slug, string Path, string Type, string Title, string Summary, string[] Sources);

// `wiki persona distill`: the read side of a pass. `Pending` is the open
// changeset (null when none), so a pass knows it is about to supersede one.
// Candidate interview transcripts join this view with issue #53.
// `Recheck` (issue #52) lists the persona lines lint has filed an open
// `persona-stale-evidence` issue for and that are still stale: the re-check
// proposals this pass should settle by re-confirming the line with current
// evidence (a `change` whose text equals `from`), rewording it or removing it.
public sealed record PersonaDistillView(
    PersonaView Persona,
    PersonaCandidateView[] Candidates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PersonaChangesetData? Pending,
    PersonaRecheckView[] Recheck) : IHumanRenderable
{
    public string HumanSummary()
        => $"{Candidates.Length} candidate page(s); {Recheck.Length} line(s) to re-check; "
           + (Pending is null ? "no pending changeset" : "pending changeset " + Pending.Id);
}

// One re-check proposal: the open issue that raised it, the line, and each
// piece of its evidence that no longer holds.
public sealed record PersonaRecheckView(string Issue, string Section, string Text, PersonaStaleEvidence[] Evidence);

// `wiki persona propose`: the queued changeset (null when the pass proposed
// no line changes and only recorded what it considered), the id of the
// changeset it superseded, and the pages whose candidate flag it cleared.
public sealed record PersonaProposeResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PersonaChangesetData? Changeset,
    string? Superseded,
    string[] Cleared,
    string? PendingPath) : IHumanRenderable
{
    public string HumanSummary()
        => (Changeset is null ? "nothing queued" : $"queued persona changeset {Changeset.Id}")
           + (Superseded is null ? "" : $", superseding {Superseded}")
           + $"; cleared {Cleared.Length} candidate flag(s)";
}

// `wiki review approve|reject` on a persona changeset.
public sealed record PersonaDecisionResult(PersonaChangesetData Changeset, int Added, int Changed, int Removed) : IHumanRenderable
{
    public string HumanSummary() => Changeset.Status == "approved"
        ? $"accepted persona changeset {Changeset.Id} (+{Added} ~{Changed} -{Removed}) -> PERSONA.md"
        : $"rejected persona changeset {Changeset.Id}";
}

// The `wiki persona` command group's service, and the persona side of
// `wiki review`. Only this class ever writes PERSONA.md - ingest has no path
// to it.
//
// Changesets follow the reviewed-amendment pattern of `schema propose`
// (SchemaService): propose validates and stores, review decides, and only an
// accept writes the target file. Validation runs at both ends because
// PERSONA.md may have changed between the two.
public sealed class PersonaService
{
    private readonly Func<long> _nowUnixMs;
    private readonly Func<byte[]> _randomBytes;

    public PersonaService(Func<long>? nowUnixMs = null, Func<byte[]>? randomBytes = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _randomBytes = randomBytes ?? DefaultRandomBytes;
    }

    // Vault-relative path of the Obsidian-readable copy of the open changeset.
    public const string PendingRelPath = "persona/changeset.md";

    public static PersonaDoc Load(Vault v)
        => File.Exists(v.PersonaPath) ? PersonaDoc.Parse(File.ReadAllText(v.PersonaPath)) : PersonaDoc.Empty();

    public PersonaView Read(Vault v)
    {
        var exists = File.Exists(v.PersonaPath);
        var doc = Load(v);
        return new PersonaView(
            exists,
            PersonaDoc.FileName,
            doc.SchemaVersion,
            doc.LastDistilled,
            doc.AuditEntry,
            doc.Section(PersonaSection.Values).ToArray(),
            doc.Section(PersonaSection.Principles).ToArray(),
            doc.Section(PersonaSection.ToneOfVoice).ToArray(),
            doc.Section(PersonaSection.Boundaries).ToArray());
    }

    public PersonaDistillView Distill(Vault v)
    {
        var candidates = Pages(v).Values
            .Where(p => p.Front.PersonaCandidate)
            .OrderBy(p => p.RelPath, StringComparer.Ordinal)
            .Select(p => new PersonaCandidateView(p.Front.Id, p.Slug, p.RelPath, PageTypeX.ToWire(p.Front.Type),
                p.Front.Title, p.Front.Summary, p.Front.Sources))
            .ToArray();
        var store = new PersonaChangesets();
        store.Load(v);
        return new PersonaDistillView(Read(v), candidates, store.Open(), Recheck(v));
    }

    // Lint files the proposal (the open issue); the live check keeps a line
    // that stopped being stale since that lint run out of the pass.
    private static PersonaRecheckView[] Recheck(Vault v)
    {
        var issues = new Issues();
        issues.Load(v);
        var open = issues.List(IssueKind.PersonaStaleEvidence, "open")
            .GroupBy(i => i.Subject, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        if (open.Count == 0)
            return Array.Empty<PersonaRecheckView>();

        return PersonaEvidence.StaleLines(v, VaultConfig.Load(v.ConfigPath), Load(v))
            .Where(l => open.ContainsKey(l.Subject))
            .Select(l => new PersonaRecheckView(open[l.Subject], l.Section, l.Text, l.Evidence))
            .ToArray();
    }

    public PersonaProposeResult Propose(Vault v, string input)
    {
        PersonaChangesetData? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(input, WikiJsonContext.Default.PersonaChangesetData);
        }
        catch (JsonException ex)
        {
            throw new ValidationException("invalid-changeset", $"the changeset is not valid JSON: {ex.Message}");
        }
        if (parsed is null)
            throw new ValidationException("invalid-changeset", "the changeset is empty");

        var reason = (parsed.Reason ?? "").Trim();
        if (reason.Length == 0)
            throw new ValidationException("invalid-changeset", "'reason' is required: say why the pass proposes this");
        var changes = parsed.Changes ?? Array.Empty<PersonaChangeData>();
        var consideredInput = parsed.Considered ?? Array.Empty<string>();
        if (changes.Length == 0 && consideredInput.Length == 0)
            throw new ValidationException("invalid-changeset",
                "the changeset has no 'changes' and no 'considered' pages; nothing to propose");

        var cfg = VaultConfig.Load(v.ConfigPath);
        var idmap = new IdMap();
        idmap.Load(v);
        var pages = Pages(v);

        var considered = consideredInput.Select(r => ResolvePage(pages, r).Front.Id).ToList();

        var entries = Mutable(Load(v));
        var resolved = new List<PersonaChangeData>();
        for (var i = 0; i < changes.Length; i++)
        {
            var change = Apply(entries, changes[i], i);
            if (change.Op != "remove")
            {
                var evidence = ResolveEvidence(v, cfg, idmap, pages, changes[i].Evidence, i);
                var section = PersonaSectionX.FromWire(change.Section)!.Value;
                var problem = PersonaEvidence.Problem(section, evidence);
                if (problem is not null)
                    throw new ValidationException("persona-evidence",
                        $"change {i + 1} ({change.Section} '{change.Text}'): {problem}");
                change.Evidence = evidence;
                foreach (var e in evidence.Where(e => e.Kind == PersonaEvidenceKind.Page))
                    considered.Add(e.Page!);
            }
            resolved.Add(change);
        }

        if (resolved.Count > 0)
            EnsureLintClean(entries);

        // --- Validation complete ---

        var nowMs = _nowUnixMs();
        var consideredIds = considered.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        PersonaChangesetData? created = null;
        string? superseded = null;
        if (resolved.Count > 0)
        {
            created = new PersonaChangesetData
            {
                Id = WikiUlid.New(nowMs, _randomBytes()),
                Status = "open",
                CreatedAt = Iso(nowMs),
                Reason = reason,
                Considered = consideredIds,
                Changes = resolved.ToArray(),
            };

            var store = new PersonaChangesets();
            store.Load(v);
            var open = store.Open();
            if (open is not null)
            {
                open.Status = "superseded";
                open.SupersededBy = created.Id;
                superseded = open.Id;
            }
            store.Put(created);
            store.Save(v);
            WritePending(v, created, pages);
        }

        var cleared = ClearCandidates(v, pages, consideredIds);
        return new PersonaProposeResult(created, superseded, cleared, created is null ? null : PendingRelPath);
    }

    // Applies an open changeset to the CURRENT PERSONA.md and writes it.
    // Returns the decision plus the full evidence ledger of the new file,
    // which the caller records on the audit line.
    // `Retired` is every evidence reference the previous ledger held that
    // the new one no longer does: the evidence of removed and reworded
    // lines, and the old hashes of re-confirmed ones (issue #52).
    public (PersonaDecisionResult Result, PersonaLedgerLine[] Ledger, PersonaEvidenceData[] Retired) Accept(Vault v, string id)
    {
        var store = new PersonaChangesets();
        store.Load(v);
        var changeset = RequireOpen(store, id);

        var current = Load(v);
        var entries = Mutable(current);
        for (var i = 0; i < changeset.Changes.Length; i++)
            Apply(entries, changeset.Changes[i], i, stale: true);
        EnsureLintClean(entries);

        // Every line of the new file with its evidence: what this changeset
        // brought for the lines it adds or rewrites, what the previous accept
        // recorded for the lines it leaves alone.
        var previous = PersonaEvidence.LoadLedger(v, current.AuditEntry) ?? Array.Empty<PersonaLedgerLine>();
        var ledger = new List<PersonaLedgerLine>();
        foreach (var s in PersonaSectionX.InOrder)
        {
            var wire = PersonaSectionX.ToWire(s);
            foreach (var line in entries[s])
            {
                var introduced = changeset.Changes.LastOrDefault(c => c.Op != "remove" && c.Section == wire && c.Text == line);
                var evidence = introduced?.Evidence
                    ?? previous.FirstOrDefault(l => l.Section == wire && l.Text == line)?.Evidence
                    ?? Array.Empty<PersonaEvidenceData>();
                ledger.Add(new PersonaLedgerLine { Section = wire, Text = line, Evidence = evidence });
            }
        }

        var kept = new HashSet<string>(ledger.SelectMany(l => l.Evidence).Select(EvidenceKey), StringComparer.Ordinal);
        var retired = previous.SelectMany(l => l.Evidence)
            .Where(e => !kept.Contains(EvidenceKey(e)))
            .GroupBy(EvidenceKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();

        // --- Validation complete ---

        // last_distilled is the date of the pass that produced the lines,
        // which is when the changeset was proposed, not when it was reviewed.
        var lastDistilled = changeset.CreatedAt[..10];
        AtomicFile.Write(v.PersonaPath, PersonaDoc.Serialize(lastDistilled, changeset.Id, Freeze(entries)));
        changeset.Status = "approved";
        store.Save(v);
        DeletePending(v);

        return (Decision(changeset), ledger.ToArray(), retired);
    }

    private static string EvidenceKey(PersonaEvidenceData e) => $"{e.Kind}|{e.Page}|{e.Path}|{e.Anchor}|{e.Hash}";

    public PersonaDecisionResult Reject(Vault v, string id, string? reason)
    {
        var store = new PersonaChangesets();
        store.Load(v);
        var changeset = RequireOpen(store, id);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ValidationException("reason-required",
                "rejecting a persona changeset needs a reason (--note) so the next pass knows what to fix");

        // --- Validation complete ---

        changeset.Status = "rejected";
        changeset.Note = reason.Trim();
        store.Save(v);
        DeletePending(v);
        return Decision(changeset);
    }

    public static bool IsChangeset(Vault v, string id)
    {
        var store = new PersonaChangesets();
        store.Load(v);
        return store.Get(id) is not null;
    }

    // The open changeset as `wiki review list` shows it: a line diff of
    // PERSONA.md as it is against PERSONA.md as it would be.
    public static (PersonaChangesetData Changeset, bool PersonaExists, string Diff)? Pending(Vault v)
    {
        var store = new PersonaChangesets();
        store.Load(v);
        var open = store.Open();
        if (open is null)
            return null;

        var entries = Mutable(Load(v));
        try
        {
            for (var i = 0; i < open.Changes.Length; i++)
                Apply(entries, open.Changes[i], i, stale: true);
        }
        catch (ValidationException)
        {
            // Listed anyway; approving it reports why it no longer applies.
        }
        var exists = File.Exists(v.PersonaPath);
        var before = exists ? File.ReadAllText(v.PersonaPath).Replace("\r\n", "\n") : "";
        var after = PersonaDoc.Serialize(open.CreatedAt[..10], open.Id, Freeze(entries));
        return (open, exists, ReviewService.LineDiff(before, after));
    }

    public static (int Added, int Changed, int Removed) Counts(PersonaChangesetData c)
        => (c.Changes.Count(x => x.Op == "add"), c.Changes.Count(x => x.Op == "change"), c.Changes.Count(x => x.Op == "remove"));

    private static PersonaDecisionResult Decision(PersonaChangesetData c)
    {
        var (a, ch, r) = Counts(c);
        return new PersonaDecisionResult(c, a, ch, r);
    }

    private static PersonaChangesetData RequireOpen(PersonaChangesets store, string id)
    {
        var changeset = store.Get(id)
            ?? throw new ValidationException("not-found", $"no persona changeset found for id '{id}'");
        if (changeset.Status == "superseded")
            throw new StateConflictException("state-conflict",
                $"persona changeset '{id}' was superseded by '{changeset.SupersededBy}'; review that one instead");
        if (changeset.Status != "open")
            throw new StateConflictException("state-conflict",
                $"persona changeset '{id}' is already '{changeset.Status}'; nothing to do");
        return changeset;
    }

    // Validates one change against the persona as it stands and applies it.
    // Returns the normalised change (trimmed text; evidence as given, the
    // caller resolves it). With `stale`, a mismatch means PERSONA.md moved on
    // since the proposal was validated.
    private static PersonaChangeData Apply(Dictionary<PersonaSection, List<string>> entries, PersonaChangeData change, int index, bool stale = false)
    {
        var where = $"change {index + 1}";
        var section = PersonaSectionX.FromWire(change.Section)
            ?? throw new ValidationException("invalid-persona-section",
                $"{where}: unknown section '{change.Section}'; expected values, principles, tone-of-voice or boundaries");
        var heading = PersonaSectionX.Heading(section);
        var lines = entries[section];
        var from = change.From?.Trim();
        var text = change.Text?.Trim();

        // `keep` is the index of the line a `change` replaces: re-confirming a
        // line with fresh evidence (text equal to from) is not a duplicate.
        string RequireText(int keep = -1)
        {
            if (string.IsNullOrEmpty(text))
                throw new ValidationException("invalid-changeset", $"{where}: '{change.Op}' needs a non-empty 'text'");
            if (text.Contains('\n') || text.Contains('\r'))
                throw new ValidationException("invalid-changeset", $"{where}: a persona line is one line; 'text' holds a line break");
            var existing = lines.IndexOf(text);
            if (existing >= 0 && existing != keep)
                throw new ValidationException(stale ? "changeset-stale" : "duplicate-persona-line",
                    $"{where}: {heading} already holds '{text}'");
            return text;
        }

        int RequireFrom()
        {
            if (string.IsNullOrEmpty(from))
                throw new ValidationException("invalid-changeset",
                    $"{where}: '{change.Op}' needs 'from', the current line it replaces or removes");
            var at = lines.IndexOf(from);
            if (at < 0)
                throw new ValidationException(stale ? "changeset-stale" : "unknown-persona-line",
                    $"{where}: {heading} has no line '{from}'");
            return at;
        }

        switch (change.Op)
        {
            case "add":
                lines.Add(RequireText());
                return new PersonaChangeData { Op = "add", Section = change.Section, Text = text, Evidence = change.Evidence };
            case "change":
            {
                var at = RequireFrom();
                lines[at] = RequireText(keep: at);
                return new PersonaChangeData { Op = "change", Section = change.Section, From = from, Text = text, Evidence = change.Evidence };
            }
            case "remove":
                lines.RemoveAt(RequireFrom());
                return new PersonaChangeData { Op = "remove", Section = change.Section, From = from };
            default:
                throw new ValidationException("invalid-changeset",
                    $"{where}: unknown op '{change.Op}'; expected add, change or remove");
        }
    }

    // Resolves the agent's evidence references to stored ones: the CLI, not
    // the agent, fixes which file and which content each reference means.
    // EXTENSION POINT (issue #53): the owner-stated branch resolves a
    // transcript file + answer anchor and hashes that one answer, producing
    // { kind: owner-stated, path, anchor, hash }.
    private static PersonaEvidenceData[] ResolveEvidence(Vault v, VaultConfig cfg, IdMap idmap,
        Dictionary<string, PageEntry> pages, PersonaEvidenceData[]? input, int index)
    {
        var result = new List<PersonaEvidenceData>();
        foreach (var e in input ?? Array.Empty<PersonaEvidenceData>())
        {
            var kind = string.IsNullOrEmpty(e.Kind) ? PersonaEvidenceKind.Page : e.Kind;
            switch (kind)
            {
                case PersonaEvidenceKind.Page:
                {
                    if (string.IsNullOrWhiteSpace(e.Page))
                        throw new ValidationException("invalid-changeset",
                            $"change {index + 1}: page evidence needs 'page' (an id or slug)");
                    var page = ResolvePage(pages, e.Page.Trim());
                    if (!PageService.CitesPersonaSource(v, cfg, idmap, page.Front.Sources))
                        throw new ValidationException("evidence-not-endorsed",
                            $"change {index + 1}: [[{page.Slug}]] cites no source in a persona-source category, so it is not an endorsed note");
                    if (result.Any(r => r.Page == page.Front.Id))
                        continue;
                    result.Add(new PersonaEvidenceData
                    {
                        Kind = PersonaEvidenceKind.Page,
                        Page = page.Front.Id,
                        Path = page.RelPath,
                        Hash = PersonaEvidence.PageHash(page.Body),
                    });
                    break;
                }
                case PersonaEvidenceKind.OwnerStated:
                    throw new ValidationException("unsupported-evidence-kind",
                        $"change {index + 1}: owner-stated evidence is recorded through the onboarding interview, which this build does not have yet");
                default:
                    throw new ValidationException("invalid-evidence-kind",
                        $"change {index + 1}: unknown evidence kind '{kind}'; expected page or owner-stated");
            }
        }
        return result.ToArray();
    }

    // The persona lint rules (PersonaDoc.Validate, defined once) applied to
    // the file the changeset would produce; any error refuses it.
    private void EnsureLintClean(Dictionary<PersonaSection, List<string>> entries)
    {
        var text = PersonaDoc.Serialize(Iso(_nowUnixMs())[..10], "pending", Freeze(entries));
        var errors = PersonaDoc.Parse(text).Validate()
            .Where(f => IssueKindX.Severity(f.Kind) == "error")
            .Select(f => $"{IssueKindX.ToWire(f.Kind)}: {f.Detail}")
            .ToArray();
        if (errors.Length > 0)
            throw new ValidationException("persona-lint",
                "the persona this changeset produces fails lint: " + string.Join("; ", errors), PersonaDoc.FileName);
    }

    private static string[] ClearCandidates(Vault v, Dictionary<string, PageEntry> pages, IEnumerable<string> ids)
    {
        var cleared = new List<string>();
        foreach (var id in ids)
        {
            var page = pages[id];
            if (!page.Front.PersonaCandidate)
                continue;
            // Only the flag changes: not `updated` (the content did not
            // change), nothing else in the frontmatter, not the body - so the
            // evidence hash (of the body) stays valid.
            var front = new PageFrontmatter
            {
                Id = page.Front.Id,
                Type = page.Front.Type,
                Title = page.Front.Title,
                Status = page.Front.Status,
                Created = page.Front.Created,
                Updated = page.Front.Updated,
                Summary = page.Front.Summary,
                Sources = page.Front.Sources,
                Tags = page.Front.Tags,
                PersonaCandidate = false,
            };
            AtomicFile.Write(PagePaths.Full(v, page.Slug, page.Front), new PageDoc(front, page.Body).Serialize());
            cleared.Add(id);
        }
        return cleared.ToArray();
    }

    // The open changeset as a Markdown file the owner can read in Obsidian.
    // A rendering only: review reads the store, so editing this file changes
    // nothing that gets accepted.
    private static void WritePending(Vault v, PersonaChangesetData c, Dictionary<string, PageEntry> pages)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append(Frontmatter.WriteScalar("changeset", c.Id, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("created", c.CreatedAt, quoted: false)).Append('\n');
        sb.Append("---\n");
        sb.Append("# Pending persona changeset\n\n");
        sb.Append("> Written by `wiki persona propose`; editing this file changes nothing. Decide it with\n");
        sb.Append($"> `wiki review approve {c.Id}` or `wiki review reject {c.Id} --note \"<reason>\"`.\n\n");
        sb.Append("**Reason:** ").Append(c.Reason).Append('\n');
        foreach (var s in PersonaSectionX.InOrder)
        {
            var wire = PersonaSectionX.ToWire(s);
            var mine = c.Changes.Where(x => x.Section == wire).ToArray();
            if (mine.Length == 0)
                continue;
            sb.Append("\n## ").Append(PersonaSectionX.Heading(s)).Append("\n\n");
            foreach (var x in mine)
            {
                sb.Append(x.Op switch
                {
                    "add" => $"- **add:** {x.Text}\n",
                    "change" => $"- **change:** {x.From}\n  **to:** {x.Text}\n",
                    _ => $"- **remove:** {x.From}\n",
                });
                foreach (var e in x.Evidence ?? Array.Empty<PersonaEvidenceData>())
                {
                    var label = e.Kind == PersonaEvidenceKind.Page && e.Page is not null && pages.TryGetValue(e.Page, out var p)
                        ? $"[[{p.Slug}]]"
                        : e.Path;
                    sb.Append($"  - evidence: {label} (sha256 {e.Hash?[..12]})\n");
                }
            }
        }
        var path = Path.Combine(v.Root, PendingRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, sb.ToString());
    }

    private static void DeletePending(Vault v)
    {
        AtomicFile.Delete(Path.Combine(v.Root, PendingRelPath));
    }

    private sealed record PageEntry(string Slug, PageFrontmatter Front, string Body, string RelPath);

    // Every page keyed by id; slugs resolve through ResolvePage.
    private static Dictionary<string, PageEntry> Pages(Vault v)
    {
        var result = new Dictionary<string, PageEntry>(StringComparer.Ordinal);
        foreach (var (slug, front, body) in PageStore.EnumerateWithBody(v))
            result[front.Id] = new PageEntry(slug, front, body, PagePaths.Relative(v, slug, front));
        return result;
    }

    private static PageEntry ResolvePage(Dictionary<string, PageEntry> pages, string idOrSlug)
    {
        if (pages.TryGetValue(idOrSlug, out var byId))
            return byId;
        return pages.Values.FirstOrDefault(p => p.Slug == idOrSlug)
            ?? throw new ValidationException("not-found", $"no page found for '{idOrSlug}'");
    }

    private static Dictionary<PersonaSection, List<string>> Mutable(PersonaDoc doc)
        => PersonaSectionX.InOrder.ToDictionary(s => s, s => doc.Section(s).ToList());

    private static Dictionary<PersonaSection, IReadOnlyList<string>> Freeze(Dictionary<PersonaSection, List<string>> entries)
        => entries.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);

    private static string Iso(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static byte[] DefaultRandomBytes()
    {
        var bytes = new byte[10];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
