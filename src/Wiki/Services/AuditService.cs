using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// One cited source on an audit target: enough to identify it, not its
// content. The auditor fetches bodies with `wiki source show` - inlining them
// would make a single `audit next` payload unbounded (a page can cite a dozen
// transcripts) and would duplicate a command that already exists.
public sealed record AuditSource(string Id, string Title, string Category, string Status);

// `wiki audit next` result. "Nothing to audit" is a legitimate, common answer
// and must not be an error the agent has to catch, so it comes back as exit 0
// with `HasTarget: false` and a `Reason`.
//
// HasTarget exists rather than leaving the caller to notice a missing
// `pageId`: the envelope omits null fields (WhenWritingNull), so every
// nullable field below simply vanishes in the no-work case, and a contract
// that asks the agent to branch on the ABSENCE of a key is a contract it will
// eventually get wrong. One always-present boolean is the whole answer.
//
// `Why` explains the SELECTION, so a human reading the JSON can see which
// heuristic put this page at the front rather than having to re-derive it.
public sealed record AuditTarget(
    bool HasTarget,
    string? PageId,
    string? Slug,
    string? Title,
    string? Summary,
    string? Body,
    AuditSource[] Sources,
    string? LastAuditedAt,
    int PriorAudits,
    string Why,
    string? Reason) : IHumanRenderable
{
    public string HumanSummary()
        => !HasTarget ? $"Nothing to audit: {Reason}" : $"Audit [[{Slug}]] against {Sources.Length} cited source(s) — {Why}";
}

// `wiki audit record` result.
public sealed record AuditRecordResult(
    string PageId,
    string Slug,
    string Verdict,
    string? Note,
    string AuditedAt,
    int Audits,
    bool IssueFiled) : IHumanRenderable
{
    public string HumanSummary()
        => $"Recorded '{Verdict}' for [[{Slug}]]" + (IssueFiled ? "; filed an unsupported-claim issue" : "");
}

// `wiki audit next|record|list` (issue #12): faithfulness auditing, split the
// same way every other part of this system is split.
//
// THE CLI HAS NO LLM ACCESS, BY DESIGN, and that is the shape of the feature
// rather than a blocker. The CLI does SELECTION and BOOKKEEPING; the agent
// does the JUDGEMENT; the human disposes. No model in the binary, no network
// call, nothing nondeterministic behind the wall.
//
// What this covers that `wiki eval` (amendment W) cannot: eval measures
// RETRIEVAL - does the router surface the right pages - and content-loss
// (amendment V) detects removal STRUCTURALLY. Neither can tell you whether a
// page's claims are actually supported by its cited sources. That needs
// semantic judgement, and it is the failure that matters most in a
// retrieval-first vault: a confidently wrong page gets cited later without
// rechecking, which is the entire reason for having built the wiki.
//
// RETRIEVAL-FIRST MAKES THIS CHEAP. Faithfulness only needs to hold for pages
// that actually get retrieved, so this is never a whole-vault sweep - it is a
// targeted check on the handful of pages that carry real answers. That makes
// SELECTION the interesting design problem, not judgement.
//
// The best selection signal would be actual retrieval frequency, which
// requires logging what gets retrieved. That does not exist, and it is
// deliberately not built here: it would have to earn its keep on its own
// merits (it would also let amendment W's golden questions be harvested from
// real usage rather than hand-authored), and building it *for* this feature
// would be the tail wagging the dog. So selection falls back to heuristics
// the vault already has, in this priority order:
//
//   1. Pages carrying an open `content-loss` issue. Amendment V already
//      flagged these as having lost references in a rewrite, which is the
//      cheap structural signal that something may also have gone wrong
//      semantically. Spending the expensive check where the free one already
//      pointed is the whole reason C shipped before this.
//   2. Never audited before, then least-recently audited. Coverage first,
//      re-checking second.
//   3. Most cited sources. More sources means more chances for a claim to
//      have drifted away from what any of them says, and it is the closest
//      proxy the vault has for "this page carries real answers".
//   4. Slug, ordinal - so the selection is a pure function of vault state and
//      two runs on an unchanged vault pick the same page.
//
// Pages with NO cited sources are not candidates at all: faithfulness is a
// statement about claims against sources, and there is nothing to check a
// page against if it cites nothing. Archived pages are excluded (dead
// history, §7) and so are pending-review ones (not citable yet, and a human
// is already looking at them).
public sealed class AuditService
{
    private readonly Func<long> _nowUnixMs;

    public AuditService(Func<long>? nowUnixMs = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public AuditTarget Next(Vault v)
    {
        var audits = new Audits();
        audits.Load(v);

        var issues = new Issues();
        issues.Load(v);
        var lossFlagged = new HashSet<string>(
            issues.List(IssueKind.ContentLoss, "open").Select(i => i.Subject), StringComparer.Ordinal);

        var idmap = new IdMap();
        idmap.Load(v);

        var candidates = PageStore.EnumerateWithBody(v)
            .Where(p => p.Front.Status == PageStatus.Active)
            .Where(p => p.Front.Sources.Length > 0)
            .ToArray();

        if (candidates.Length == 0)
            return new AuditTarget(false, null, null, null, null, null, Array.Empty<AuditSource>(), null, 0, "",
                "no active page cites any source, so there is nothing to check claims against");

        var ranked = candidates
            .Select(p => (Page: p, Record: audits.Get(p.Front.Id), Flagged: lossFlagged.Contains(p.Slug)))
            .OrderByDescending(x => x.Flagged)
            // Never audited sorts first: "" is ordinally below every ISO
            // timestamp, so this is "never, then oldest" in one comparison.
            .ThenBy(x => x.Record?.AuditedAt ?? "", StringComparer.Ordinal)
            .ThenByDescending(x => x.Page.Front.Sources.Length)
            .ThenBy(x => x.Page.Slug, StringComparer.Ordinal)
            .ToArray();

        var pick = ranked[0];
        var why = pick.Flagged
            ? "carries an open content-loss issue"
            : pick.Record is null
                ? "never audited"
                : $"least recently audited (last {pick.Record.AuditedAt})";
        why += $"; cites {pick.Page.Front.Sources.Length} source(s)";

        var sources = pick.Page.Front.Sources
            .Select(id => DescribeSource(v, idmap, id))
            .ToArray();

        return new AuditTarget(
            true,
            pick.Page.Front.Id,
            pick.Page.Slug,
            pick.Page.Front.Title,
            pick.Page.Front.Summary,
            pick.Page.Body,
            sources,
            pick.Record?.AuditedAt,
            pick.Record?.Audits ?? 0,
            why,
            null);
    }

    // Records the agent's verdict. `unsupported` files an `unsupported-claim`
    // issue, which plugs straight into the occurrence counting that already
    // exists - and a semantic finding that keeps recurring is exactly the
    // evidence the reflect loop (`schema propose`) was built to act on.
    //
    // The note is REQUIRED for `unsupported` and optional for `supported`: a
    // finding nobody can act on is worse than no finding, and "this page
    // asserts X, no cited source says X" is the entire content of the report.
    public AuditRecordResult Record(Vault v, string pageId, string verdict, string? note)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        if (verdict != "supported" && verdict != "unsupported")
            throw new ValidationException("invalid-verdict",
                $"unknown verdict '{verdict}'; expected 'supported' or 'unsupported'");

        if (verdict == "unsupported" && string.IsNullOrWhiteSpace(note))
            throw new ValidationException("note-required",
                "--note is required for an 'unsupported' verdict: name the claim and say which cited source fails to support it");

        if (note is not null)
            Scalar.GuardSingleLineQuotable(note, "note", "invalid-note");

        var idmap = new IdMap();
        idmap.Load(v);
        var relPath = idmap.PathFor(pageId);
        var fullPath = relPath is null ? null : System.IO.Path.Combine(v.Root, relPath);
        if (relPath is null || !relPath.StartsWith("wiki/", StringComparison.Ordinal) || !System.IO.File.Exists(fullPath))
            throw new ValidationException("not-found", $"unknown page id '{pageId}'");

        var slug = System.IO.Path.GetFileNameWithoutExtension(fullPath)!;

        // --- Validation complete. Everything from here on is the write. ---

        var utcIso = DateTimeOffset.FromUnixTimeMilliseconds(_nowUnixMs()).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var audits = new Audits();
        audits.Load(v);
        var record = audits.Record(pageId, slug, verdict, note, utcIso);
        audits.Save(v);

        var issueFiled = false;
        if (verdict == "unsupported")
        {
            var issues = new Issues();
            issues.Load(v);
            issues.Upsert(IssueKind.UnsupportedClaim, slug,
                $"an adversarial re-read found claim(s) no cited source supports: {note}. " +
                "This is a finding to weigh, not a fact - revise the page, or resolve with a note explaining why it stands.",
                utcIso);
            issues.Save(v);
            issueFiled = true;
        }

        LogFile.Append(v, utcIso, "audit", slug, $"id={pageId} verdict={verdict}");

        return new AuditRecordResult(record.PageId, record.Slug, record.Verdict, record.Note, record.AuditedAt, record.Audits, issueFiled);
    }

    public IReadOnlyList<AuditRecord> List(Vault v, string? verdict)
    {
        if (verdict is not null && verdict != "supported" && verdict != "unsupported")
            throw new ValidationException("invalid-verdict",
                $"unknown verdict '{verdict}'; expected 'supported' or 'unsupported'");

        var audits = new Audits();
        audits.Load(v);
        return audits.List(verdict);
    }

    // A page's `sources` list is validated at upsert time, but a source can be
    // retracted afterwards - so a cited id that no longer resolves is a real
    // state the auditor must be told about rather than a reason to fail.
    private static AuditSource DescribeSource(Vault v, IdMap idmap, string id)
    {
        var relPath = idmap.PathFor(id);
        var fullPath = relPath is null ? null : System.IO.Path.Combine(v.Root, relPath);
        if (relPath is null || !relPath.StartsWith("raw/", StringComparison.Ordinal) || !System.IO.File.Exists(fullPath))
            return new AuditSource(id, "(unresolvable)", "", "missing");

        var (scalars, lists, _) = Frontmatter.ReadBlock(System.IO.File.ReadAllText(fullPath));
        var front = SourceFrontmatter.FromRaw(scalars, lists);
        return new AuditSource(front.Id, front.Title, front.Category, SourceStatusX.ToWire(front.Status));
    }
}
