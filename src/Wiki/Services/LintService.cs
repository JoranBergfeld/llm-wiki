using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// One `wiki lint` finding, before it becomes an Issues.Upsert call. Kept as
// a tuple-shaped record (not wired into Issues directly) so each check
// method can be a small, independently testable pure function per the task
// brief ("small private method returning (kind,subject,detail) findings").
public sealed record LintFinding(IssueKind Kind, string Subject, string Detail);

// `wiki lint`'s per-kind tally in the report - an array of (wireKind, count)
// pairs rather than Dictionary<string,int> (not registered in
// WikiJsonContext, and every other list-shaped result in this codebase is
// already an array of a small record, e.g. PageSummary[]/Hit[]).
public sealed record LintKindCount(string Kind, int Count);

// `wiki lint` result. Filed/Refreshed split is Issues.Upsert's merge
// semantics surfaced to the caller: a finding whose (kind,subject) already
// had an OPEN issue before this run bumps that issue's occurrences
// (Refreshed); anything new is Filed. IndexRegenerated / FixLinks* report
// what lint changed on disk beyond issues.json/lint.json - index-drift's
// auto-fix always runs when triggered; the FixLinks* counts stay zero
// unless `--fix-links` was passed.
public sealed record LintReport(
    int Filed,
    int Refreshed,
    LintKindCount[] Counts,
    bool IndexRegenerated,
    int FixLinksIdmapRepaired,
    int FixLinksBodiesRewritten) : IHumanRenderable
{
    public string HumanSummary()
    {
        var msg = $"Lint: {Filed} issue(s) filed, {Refreshed} refreshed across {Counts.Length} kind(s)";
        if (IndexRegenerated)
            msg += "; index.md regenerated (drift)";
        if (FixLinksIdmapRepaired > 0 || FixLinksBodiesRewritten > 0)
            msg += $"; --fix-links repaired {FixLinksIdmapRepaired} idmap entrie(s), rewrote {FixLinksBodiesRewritten} page body/bodies";
        return msg;
    }
}

// Runs every advisory check in spec §11's table, files/refreshes each
// finding via Issues.Upsert (spec §12), and writes `.wiki/lint.json`
// (amendment D) on every run - the `linted` ledger precondition
// (IngestService.CheckLintPrecondition) reads that file back.
//
// Content discipline (Global Constraints): lint NEVER edits page content.
// The two narrow exceptions, both mechanical:
//   1. index-drift is ALWAYS auto-fixed (index.md is CLI-generated output,
//      not authored page content) - regenerated whenever it doesn't match a
//      fresh render, regardless of `--fix-links`.
//   2. `--fix-links` (opt-in) repairs ONLY wikilink *targets* that broke
//      because of a detected rename-drift (an Obsidian-side file rename that
//      left idmap/inbound links pointing at the old slug) - see
//      ApplyFixLinks. It never touches any other page text.
//
// Fuzzy checks (amendment F: coverage-gap, stale, oversize) use the
// deliberately dumb heuristics documented on each check method below - no
// NLP, exact rule spelled out in each doc comment and in the task report.
//
// Clock seam: mirrors PageService/IngestService - defaults to the real
// clock so production code (LintCommand) just does `new LintService()`;
// tests inject a fixed function for deterministic "today"/age-in-days math
// and Issues/lint.json timestamps.
public sealed class LintService
{
    private readonly Func<long> _nowUnixMs;

    public LintService(Func<long>? nowUnixMs = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public LintReport Run(Vault v, VaultConfig cfg, bool fixLinks)
    {
        var nowMs = _nowUnixMs();
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime;
        var utcIso = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(nowUtc);

        var idmap = new IdMap();
        idmap.Load(v);

        // Snapshot every page ONCE, before any --fix-links mutation, so every
        // check (and the finding text it files) reflects the vault as it
        // actually was at the start of this run.
        var pages = PageStore.EnumerateWithBody(v);
        var existingSlugs = new HashSet<string>(pages.Select(p => p.Slug), StringComparer.Ordinal);

        var findings = new List<LintFinding>();
        findings.AddRange(CheckOrphans(v, pages));
        findings.AddRange(CheckDanglingLinks(pages, existingSlugs));
        findings.AddRange(CheckStale(v, pages, idmap, cfg, today));
        findings.AddRange(CheckCoverageGap(pages));
        findings.AddRange(CheckOversize(pages, cfg));
        findings.AddRange(CheckBacklog(pages, PageStatus.NeedsReview, IssueKind.NeedsReviewBacklog, today));
        findings.AddRange(CheckBacklog(pages, PageStatus.PendingReview, IssueKind.PendingBacklog, today));

        var renameDrifts = CheckRenameDrift(v, pages, idmap);
        foreach (var d in renameDrifts)
            findings.Add(d.Finding);

        var idmapRepaired = 0;
        var bodiesRewritten = 0;
        if (fixLinks)
        {
            (idmapRepaired, bodiesRewritten) = ApplyFixLinks(v, idmap, renameDrifts);
        }

        // index-drift: unconditional auto-fix (spec §11), evaluated against
        // whatever is on disk AFTER any --fix-links body rewrites above, so
        // the finding (and the regenerated file) both reflect the current
        // page set. Still filed even though it's auto-fixed, "so drift cause
        // is investigated" (task brief).
        var freshPages = PageStore.Enumerate(v);
        var expectedIndex = IndexFile.Render(freshPages);
        var actualIndex = File.Exists(v.IndexPath) ? File.ReadAllText(v.IndexPath) : "";
        var indexRegenerated = false;
        if (!string.Equals(expectedIndex, actualIndex, StringComparison.Ordinal))
        {
            findings.Add(new LintFinding(IssueKind.IndexDrift, "index.md",
                "wiki/index.md does not match a freshly rendered index (entry set or content drift); regenerated automatically"));
            IndexFile.Regenerate(v, freshPages);
            indexRegenerated = true;
        }

        // --- Findings complete. File/refresh each one, then persist state. ---

        var issues = new Issues();
        issues.Load(v);

        var openBefore = new HashSet<(IssueKind Kind, string Subject)>();
        foreach (var issue in issues.List(null, "open"))
            openBefore.Add((issue.Kind, issue.Subject));

        var filed = 0;
        var refreshed = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            issues.Upsert(f.Kind, f.Subject, f.Detail, utcIso);
            if (openBefore.Contains((f.Kind, f.Subject))) refreshed++; else filed++;

            var wireKind = IssueKindX.ToWire(f.Kind);
            counts[wireKind] = counts.TryGetValue(wireKind, out var c) ? c + 1 : 1;
        }
        issues.Save(v);

        var lintState = new LintState();
        lintState.Save(v, utcIso);

        LogFile.Append(v, utcIso, "lint", "vault",
            $"filed={filed} refreshed={refreshed} fix_links={(fixLinks ? "true" : "false")}");

        var countArray = counts
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new LintKindCount(kv.Key, kv.Value))
            .ToArray();

        return new LintReport(filed, refreshed, countArray, indexRegenerated, idmapRepaired, bodiesRewritten);
    }

    // `orphan` (spec §11): active page (excluding overview, and pending-review
    // by construction - Active/PendingReview are mutually exclusive
    // PageStatus values) with zero inbound wikilinks. Reuses
    // PageQuery.BuildInboundMap verbatim rather than
    // re-implementing the inbound-link scan - same map `page list --orphans`
    // and `page backlinks` already build.
    private static IEnumerable<LintFinding> CheckOrphans(Vault v, IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> pages)
    {
        var inbound = PageQuery.BuildInboundMap(v);
        foreach (var (slug, front, _) in pages)
        {
            if (front.Status != PageStatus.Active || front.Type == PageType.Overview)
                continue;
            if (inbound.TryGetValue(slug, out var sources) && sources.Count > 0)
                continue;
            yield return new LintFinding(IssueKind.Orphan, slug, "active page has zero inbound wikilinks");
        }
    }

    // `dangling-link` (spec §11): any `[[wikilink]]` in a page's CURRENT body
    // whose target does not resolve to an existing page slug - whether it
    // got there via `--allow-dangling` on upsert or an Obsidian-side edit
    // (the CLI's own blocking check only catches submitted-at-write-time
    // dangling links; this catches everything else already on disk).
    // Subject is the page CONTAINING the link, per spec's table; detail
    // lists every distinct dangling target in that page.
    private static IEnumerable<LintFinding> CheckDanglingLinks(
        IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> pages, HashSet<string> existingSlugs)
    {
        foreach (var (slug, _, body) in pages)
        {
            var dangling = Wikilinks.Extract(body)
                .Select(l => l.Target)
                .Where(t => t != slug && !existingSlugs.Contains(t))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (dangling.Length == 0) continue;

            yield return new LintFinding(IssueKind.DanglingLink, slug,
                $"dangling wikilink target(s): {string.Join(", ", dangling)}");
        }
    }

    // `stale` (spec §11, amendment F - deliberately dumb): a `summary` page
    // whose `updated` is more than `staleness_days` old AND at least one
    // source it directly cites (its `sources` list) has a frontmatter
    // `added` date newer than that `updated` date. This is the SIMPLER of
    // the two signals the task brief sketches ("summary older... AND a
    // source it cites... has a newer added/updated" vs. "a newer source
    // sharing an entity/concept") - the "shared-source page" variant would
    // need a second graph pass over every OTHER page's sources to find
    // overlap, which is real complexity for a check amendment F says must
    // stay dumb. Direct-citation timestamp comparison is the simple,
    // deterministic choice; summaries with an empty `sources` list are
    // skipped entirely (nothing to compare against).
    private static IEnumerable<LintFinding> CheckStale(
        Vault v, IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> pages, IdMap idmap, VaultConfig cfg, DateOnly today)
    {
        foreach (var (slug, front, _) in pages)
        {
            if (front.Type != PageType.Summary) continue;
            if (front.Sources.Length == 0) continue;
            if (!DateOnly.TryParse(front.Updated, CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated)) continue;

            var ageDays = today.DayNumber - updated.DayNumber;
            if (ageDays <= cfg.StalenessDays) continue;

            foreach (var sourceId in front.Sources)
            {
                var added = ReadSourceAdded(v, idmap, sourceId);
                if (added is null || added.Value.DayNumber <= updated.DayNumber) continue;

                yield return new LintFinding(IssueKind.Stale, slug,
                    $"summary 'updated' {front.Updated} is {ageDays}d old (> staleness_days {cfg.StalenessDays}); " +
                    $"cited source '{sourceId}' was added {added.Value:yyyy-MM-dd}, newer than the summary");
                break; // one finding per stale summary is enough
            }
        }
    }

    private static DateOnly? ReadSourceAdded(Vault v, IdMap idmap, string sourceId)
    {
        var relPath = idmap.PathFor(sourceId);
        if (relPath is null || !relPath.StartsWith("raw/", StringComparison.Ordinal)) return null;

        var fullPath = Path.Combine(v.Root, relPath);
        if (!File.Exists(fullPath)) return null;

        var (scalars, lists, _) = Frontmatter.ReadBlock(File.ReadAllText(fullPath));
        var front = SourceFrontmatter.FromRaw(scalars, lists);
        return DateOnly.TryParse(front.Added, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    // `coverage-gap` (spec §11, amendment F - deliberately dumb): a
    // capitalized multi-word token (2-4 Title-Case words, regex below)
    // appearing as plain text - NOT inside a `[[wikilink]]` - in at least 3
    // DISTINCT page bodies, where no existing page's title matches it
    // (case-insensitive). No NLP/entity-recognition: this is pure
    // token-frequency counting, exactly as amendment F specifies. Code
    // fences are skipped (same discipline as Wikilinks.Extract) so example
    // code/config text isn't scanned. Subject is the term text itself (the
    // table's "subject(page/source id)" doesn't apply here - there IS no
    // page/source for a coverage gap, that's the whole finding).
    private static readonly Regex WikilinkSpan = new(@"\[\[[^\]]*\]\]", RegexOptions.Compiled);
    private static readonly Regex ProperNounToken = new(@"\b[A-Z][a-zA-Z]*(?:\s[A-Z][a-zA-Z]*){1,3}\b", RegexOptions.Compiled);

    private static IEnumerable<LintFinding> CheckCoverageGap(IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> pages)
    {
        var mentionedBy = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var existingTitles = new HashSet<string>(pages.Select(p => p.Front.Title.Trim()), StringComparer.OrdinalIgnoreCase);

        foreach (var (slug, _, body) in pages)
        {
            var inFence = false;
            foreach (var rawLine in body.Split('\n'))
            {
                if (rawLine.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
                if (inFence) continue;

                var strippedOfLinks = WikilinkSpan.Replace(rawLine, " ");
                foreach (Match m in ProperNounToken.Matches(strippedOfLinks))
                {
                    var term = m.Value.Trim();
                    if (!mentionedBy.TryGetValue(term, out var slugs))
                    {
                        slugs = new SortedSet<string>(StringComparer.Ordinal);
                        mentionedBy[term] = slugs;
                    }
                    slugs.Add(slug);
                }
            }
        }

        foreach (var term in mentionedBy.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            var slugs = mentionedBy[term];
            if (slugs.Count < 3) continue;
            if (existingTitles.Contains(term)) continue;

            yield return new LintFinding(IssueKind.CoverageGap, term,
                $"mentioned as plain (non-wikilink) text in {slugs.Count} page(s) ({string.Join(", ", slugs)}); no page of its own exists");
        }
    }

    // `oversize` (spec §11, amendment F - deliberately dumb): page BODY line
    // count (frontmatter excluded) exceeds `max_page_lines`. Plain
    // `body.Split('\n').Length` - no attempt to collapse blank lines or
    // count "meaningful" lines differently; a trailing newline contributes
    // one extra (empty) element, which is fine for a threshold comparison.
    private static IEnumerable<LintFinding> CheckOversize(
        IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> pages, VaultConfig cfg)
    {
        foreach (var (slug, _, body) in pages)
        {
            var lineCount = body.Split('\n').Length;
            if (lineCount <= cfg.MaxPageLines) continue;

            yield return new LintFinding(IssueKind.Oversize, slug,
                $"page body has {lineCount} line(s), exceeding max_page_lines ({cfg.MaxPageLines})");
        }
    }

    // `needs-review-backlog` / `pending-backlog` (spec §11): pages sitting in
    // the given status for more than 14 days. PageFrontmatter has no
    // separate "entered this status at" timestamp, so `updated` is used as
    // the proxy - PageService.SetStatus always stamps `updated` to today
    // when a status changes, so for a page whose status was set via the CLI
    // (the only way status changes today) `updated` IS the status-entry
    // date. Documented judgment call, not a spec requirement.
    private static IEnumerable<LintFinding> CheckBacklog(
        IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> pages, PageStatus status, IssueKind kind, DateOnly today)
    {
        foreach (var (slug, front, _) in pages)
        {
            if (front.Status != status) continue;
            if (!DateOnly.TryParse(front.Updated, CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated)) continue;

            var ageDays = today.DayNumber - updated.DayNumber;
            if (ageDays <= 14) continue;

            yield return new LintFinding(kind, slug,
                $"page has been '{PageStatusX.ToWire(status)}' for {ageDays}d (based on 'updated'; threshold 14d)");
        }
    }

    // `rename-drift` (spec §11): a page file that actually exists at path P
    // whose frontmatter `id` idmap maps to a DIFFERENT path (or no entry at
    // all) - the signature of an Obsidian-side rename/creation that bypassed
    // `wiki page rename`/`wiki reindex`. Subject is the page's stable `id`
    // (not its current slug): the id survives the rename, so using it keeps
    // recurring drift on the SAME id merged into one issue across lints,
    // instead of spawning a fresh issue every time the slug changes again.
    // Carries enough (OldSlug/NewSlug/ActualRelPath) for ApplyFixLinks to
    // repair idmap + inbound links without a second disk scan.
    private sealed record RenameDrift(LintFinding Finding, string Id, string? OldSlug, string NewSlug, string ActualRelPath);

    private static List<RenameDrift> CheckRenameDrift(
        Vault v, IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> pages, IdMap idmap)
    {
        var result = new List<RenameDrift>();
        foreach (var (slug, front, _) in pages)
        {
            var actualRelPath = PagePaths.Relative(v, slug, front);
            var idmapPath = idmap.PathFor(front.Id);
            if (idmapPath == actualRelPath) continue;

            var oldSlug = idmapPath is null ? null : Path.GetFileNameWithoutExtension(idmapPath);
            var detail = idmapPath is null
                ? $"idmap has no entry for id '{front.Id}'; actual file is at '{actualRelPath}'"
                : $"idmap maps id '{front.Id}' to '{idmapPath}', but the file actually lives at '{actualRelPath}' (Obsidian-side rename?)";

            var finding = new LintFinding(IssueKind.RenameDrift, front.Id, detail);
            result.Add(new RenameDrift(finding, front.Id, oldSlug, slug, actualRelPath));
        }
        return result;
    }

    // `--fix-links`: repairs ONLY what spec §11 allows - mechanical link
    // targets/idmap entries broken by a detected rename-drift. Two steps:
    //   1. idmap repair: Put(id, actualRelPath) for every drifted id (a
    //      scoped, drift-only version of `wiki reindex`'s idmap rebuild).
    //   2. inbound-link repair: for every drift that HAD a resolvable old
    //      slug (idmapPath was present, just stale), rewrite `[[oldSlug...]]`
    //      wikilinks across every page body to `[[newSlug...]]` - the exact
    //      same Wikilinks.Rewrite PageService.Rename uses, just triggered by
    //      lint discovering the rename instead of `wiki page rename` doing
    //      it live. A drift with NO old slug (idmap had no entry at all -
    //      e.g. a page file dropped in Obsidian, never indexed) has nothing
    //      to rewrite: nothing could have linked to a slug that never
    //      existed in idmap, so only the idmap Put applies.
    // Does NOT touch dangling links that aren't rename-related (no known
    // correct target to repair them to) - those stay filed as `dangling-link`
    // issues for a human/agent to resolve.
    private static (int IdmapRepaired, int BodiesRewritten) ApplyFixLinks(Vault v, IdMap idmap, List<RenameDrift> drifts)
    {
        if (drifts.Count == 0) return (0, 0);

        foreach (var d in drifts)
            idmap.Put(d.Id, d.ActualRelPath);
        idmap.Save(v);

        var slugRewrites = drifts
            .Where(d => d.OldSlug is not null && d.OldSlug != d.NewSlug)
            .Select(d => (Old: d.OldSlug!, New: d.NewSlug))
            .ToList();

        var bodiesRewritten = 0;
        if (slugRewrites.Count > 0)
        {
            foreach (var (slug, front, body) in PageStore.EnumerateWithBody(v))
            {
                var rewritten = body;
                foreach (var (oldSlug, newSlug) in slugRewrites)
                    rewritten = Wikilinks.Rewrite(rewritten, oldSlug, newSlug);

                if (string.Equals(rewritten, body, StringComparison.Ordinal)) continue;

                AtomicFile.Write(PagePaths.Full(v, slug, front), new PageDoc(front, rewritten).Serialize());
                bodiesRewritten++;
            }
        }

        return (drifts.Count, bodiesRewritten);
    }

}
