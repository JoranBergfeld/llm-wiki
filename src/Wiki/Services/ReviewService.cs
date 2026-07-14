using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// `wiki review list` row: one pending-review page. For a create (no shadow),
// `IsUpdate` is false and `Diff` is null - there's nothing to diff a brand
// new page against. For an update (a shadow exists), `Diff` is a line-based
// diff between the shadow's previous body and the page's current body, so
// the human reviewing doesn't have to `page show` twice and eyeball it.
public sealed record PendingView(
    string Id,
    string Slug,
    string Title,
    string Type,
    bool IsUpdate,
    string? Diff) : IHumanRenderable
{
    public string HumanSummary() => $"[[{Slug}]] ({Type}, {(IsUpdate ? "update" : "create")})";
}

// The review gate's workflow surface (spec §15): `wiki review list/approve/
// reject`. Nothing here decides WHETHER a page lands pending-review in the
// first place - that's PageService.Upsert reading cfg.ReviewGate - this
// class only drives pending pages the rest of the way to active/archived.
//
// Clock seam mirrors every other service in this codebase (PageService,
// LintService, IngestService): defaults to the real clock so production code
// just does `new ReviewService()`; tests inject a fixed function.
public sealed class ReviewService
{
    private readonly Func<long> _nowUnixMs;

    public ReviewService(Func<long>? nowUnixMs = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // Every pending-review page in the vault, in PageStore's deterministic
    // (sorted) enumeration order. Read-only - no idmap/index/log write.
    public IReadOnlyList<PendingView> List(Vault v)
    {
        var result = new List<PendingView>();
        foreach (var (slug, front, body) in PageStore.EnumerateWithBody(v))
        {
            if (front.Status != PageStatus.PendingReview)
                continue;

            var prevBody = ReviewShadow.Load(v, front.Id);
            var isUpdate = prevBody is not null;
            var diff = isUpdate ? UnifiedDiff(prevBody!, body) : null;

            result.Add(new PendingView(front.Id, slug, front.Title, PageTypeX.ToWire(front.Type), isUpdate, diff));
        }
        return result.ToArray();
    }

    // pending-review -> active. Delegates the actual write to
    // PageService.SetStatus (same status-flip primitive `page set-status`
    // uses - handles the idmap lookup, updated-timestamp, index regenerate,
    // and log-append) once ResolvePending has confirmed the page exists AND
    // is actually pending - SetStatus alone would happily flip ANY status to
    // active, which would let `approve` silently "succeed" on an
    // already-active or needs-review page.
    public void Approve(Vault v, string pageId)
    {
        ResolvePending(v, pageId);

        new PageService(_nowUnixMs).SetStatus(v, pageId, PageStatus.Active);
        ReviewShadow.Clear(v, pageId);
    }

    // pending-review -> active (update: shadow restored) or archived (create:
    // no shadow, nothing to restore). Either way a `pending-backlog` issue is
    // filed with the rejection reason - see the class doc below for why that
    // kind, of the closed nine, is the rejection signal.
    //
    // The update-restore path can't reuse SetStatus (that only ever touches
    // status/updated, never the body), so it re-implements the same
    // validate-then-write shape PageService.Update uses: build the full
    // frontmatter+body, round-trip it through PageDoc.Parse as a schema gate,
    // THEN write + regenerate the index - all after ResolvePending's
    // not-found/not-pending checks have already run.
    public void Reject(Vault v, string pageId, string? note)
    {
        var (fullPath, doc, slug) = ResolvePending(v, pageId);
        var prevBody = ReviewShadow.Load(v, pageId);
        var isUpdate = prevBody is not null;

        var nowMs = _nowUnixMs();
        var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime;
        var today = nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var utcIso = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

        if (isUpdate)
        {
            var restoredFront = new PageFrontmatter
            {
                Id = doc.Front.Id,
                Type = doc.Front.Type,
                Title = doc.Front.Title,
                Status = PageStatus.Active,
                Created = doc.Front.Created,
                Updated = today,
                Summary = doc.Front.Summary,
                Sources = doc.Front.Sources,
                Tags = doc.Front.Tags,
            };
            var serialized = new PageDoc(restoredFront, prevBody!).Serialize();
            // Frontmatter schema gate proper: must round-trip through the
            // same closed-schema parser real page files are read back with.
            PageDoc.Parse(serialized);

            AtomicFile.Write(fullPath, serialized);

            var freshPages = PageStore.Enumerate(v);
            IndexFile.Regenerate(v, freshPages);
        }
        else
        {
            new PageService(_nowUnixMs).SetStatus(v, pageId, PageStatus.Archived);
        }

        ReviewShadow.Clear(v, pageId);

        // Issue filed on every reject (task brief): none of the 9 closed
        // IssueKind values is a dedicated "rejection" kind (spec §15's own
        // command reference names a `retraction` kind that doesn't exist in
        // the enum either - out of scope here). Of the nine, `pending-backlog`
        // is the closest fit: the page WAS in the review-gate's
        // pending-review lifecycle right up until this call, so a reject is
        // a review-gate-lifecycle event on that same subject, same as a
        // pending page aging out. It's an imperfect match (this fires
        // immediately, not after a 14-day backlog), but it's the only kind of
        // the nine tied to the pending-review status at all - see the task
        // report for the alternatives considered and why they fit worse.
        var issues = new Issues();
        issues.Load(v);
        var detail = isUpdate
            ? $"rejected update to '{slug}' (id={pageId}); previous body restored, status set to active"
            : $"rejected create of '{slug}' (id={pageId}); no prior version to restore, status set to archived";
        if (!string.IsNullOrWhiteSpace(note))
            detail += $"; note: {note}";
        issues.Upsert(IssueKind.PendingBacklog, slug, detail, utcIso);
        issues.Save(v);

        var noteDetail = note is null ? "" : $" note=\"{note}\"";
        LogFile.Append(v, utcIso, "review-reject", slug, $"id={pageId} kind={(isUpdate ? "update" : "create")}{noteDetail}");
    }

    // Resolves a page id to (fullPath, parsed doc, slug), throwing
    // `not-found` for anything Upsert's own unknown-id guard would also
    // reject (no idmap entry, a raw/ source id, a stale entry whose file is
    // gone) and `not-pending` if the page exists but isn't currently
    // pending-review - approve/reject must not silently no-op or misfire on
    // an already-decided page.
    private static (string FullPath, PageDoc Doc, string Slug) ResolvePending(Vault v, string pageId)
    {
        var idmap = new IdMap();
        idmap.Load(v);

        var relPath = idmap.PathFor(pageId);
        var fullPath = relPath is null ? null : Path.Combine(v.Root, relPath);
        if (relPath is null || !relPath.StartsWith("wiki/", StringComparison.Ordinal) || !File.Exists(fullPath))
            throw new ValidationException("not-found", $"no page found for id '{pageId}'");

        var doc = PageDoc.Parse(File.ReadAllText(fullPath));
        if (doc.Front.Status != PageStatus.PendingReview)
            throw new ValidationException("not-pending",
                $"page '{pageId}' is not pending-review (status: {PageStatusX.ToWire(doc.Front.Status)})");

        var slug = Path.GetFileNameWithoutExtension(fullPath);
        return (fullPath!, doc, slug);
    }

    // Minimal line-based diff for `review list`'s update view: classic
    // O(n*m) LCS dynamic program, hand-rolled rather than pulling in a diff
    // library (AOT/dependency discipline, same spirit as lint's amendment F
    // "deliberately dumb" heuristics). Pages are small (bounded by
    // max_page_lines in practice), so the DP cost is negligible. Output is a
    // simple `- removed` / `+ added` / `  unchanged` prefix per line, good
    // enough for a human review pass; not a patch-applicable unified diff.
    private static string UnifiedDiff(string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');

        var n = oldLines.Length;
        var m = newLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var sb = new StringBuilder();
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b])
            {
                sb.Append("  ").Append(oldLines[a]).Append('\n');
                a++; b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                sb.Append("- ").Append(oldLines[a]).Append('\n');
                a++;
            }
            else
            {
                sb.Append("+ ").Append(newLines[b]).Append('\n');
                b++;
            }
        }
        while (a < n) { sb.Append("- ").Append(oldLines[a]).Append('\n'); a++; }
        while (b < m) { sb.Append("+ ").Append(newLines[b]).Append('\n'); b++; }

        return sb.ToString();
    }
}
