using System;
using System.Collections.Generic;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/audits.json store: the last faithfulness verdict recorded for
// each page (issue #12).
//
// Keyed by PAGE ID, not slug. A rename changes the slug and keeps the id, and
// an audit is a statement about the page, not about its filename - keying on
// the slug would silently reset a page's audit history every time it was
// renamed.
//
// One record per page, overwritten on each audit rather than appended. What
// selection needs is "when was this last looked at and what did we conclude";
// a full history would be a second, larger thing to reason about for no
// decision it would change. The durable record of a BAD verdict is the
// `unsupported-claim` issue, which does accumulate occurrences the way every
// other finding does - that is where the trend lives.
//
// This is historical state in amendment A's sense: markdown does not contain
// it, so `wiki reindex` cannot rebuild it. Reindex leaves the file alone (it
// only ever rewrites idmap.json and ledger.json), which is the same treatment
// proposals.json and issues.json get.
//
// Load/Save contract mirrors Issues/Proposals: Save rebuilds a fresh snapshot
// sorted by page id (ordinal) before serializing, so byte order is
// deterministic regardless of insertion order.
public sealed class Audits
{
    private readonly Dictionary<string, AuditRecord> _byPageId = new();

    public AuditRecord? Get(string pageId) => _byPageId.TryGetValue(pageId, out var a) ? a : null;

    public AuditRecord Record(string pageId, string slug, string verdict, string? note, string auditedAt)
    {
        var record = new AuditRecord
        {
            PageId = pageId,
            Slug = slug,
            Verdict = verdict,
            Note = note,
            AuditedAt = auditedAt,
            Audits = (_byPageId.TryGetValue(pageId, out var prior) ? prior.Audits : 0) + 1,
        };
        _byPageId[pageId] = record;
        return record;
    }

    public IReadOnlyList<AuditRecord> List(string? verdict)
    {
        var keys = new List<string>(_byPageId.Keys);
        keys.Sort(StringComparer.Ordinal);

        var result = new List<AuditRecord>(keys.Count);
        foreach (var key in keys)
        {
            var a = _byPageId[key];
            if (verdict is not null && a.Verdict != verdict) continue;
            result.Add(a);
        }
        return result;
    }

    public void Load(Vault v)
    {
        _byPageId.Clear();
        var path = PathOf(v);
        if (!System.IO.File.Exists(path))
            return;

        var loaded = JsonSerializer.Deserialize(System.IO.File.ReadAllText(path), WikiJsonContext.Default.AuditRecordDataArray);
        if (loaded is null)
            return;

        foreach (var d in loaded)
        {
            _byPageId[d.PageId] = new AuditRecord
            {
                PageId = d.PageId,
                Slug = d.Slug,
                Verdict = d.Verdict,
                Note = d.Note,
                AuditedAt = d.AuditedAt,
                Audits = d.Audits,
            };
        }
    }

    public void Save(Vault v)
    {
        var all = List(null);
        var data = new AuditRecordData[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            var a = all[i];
            data[i] = new AuditRecordData
            {
                PageId = a.PageId,
                Slug = a.Slug,
                Verdict = a.Verdict,
                Note = a.Note,
                AuditedAt = a.AuditedAt,
                Audits = a.Audits,
            };
        }

        AtomicFile.Write(PathOf(v), JsonSerializer.Serialize(data, WikiJsonContext.Default.AuditRecordDataArray));
    }

    private static string PathOf(Vault v) => System.IO.Path.Combine(v.StateDir, "audits.json");
}

// `Verdict` is a plain "supported"/"unsupported" string rather than an enum -
// same call as Issue.Status and Proposal.Status, for the same reason: two
// values used in one place do not earn a wire-mapper table.
public sealed class AuditRecord
{
    public required string PageId { get; init; }
    public required string Slug { get; init; }
    public required string Verdict { get; init; }
    public required string AuditedAt { get; init; }
    public required int Audits { get; init; }
    public string? Note { get; init; }
}

public sealed class AuditRecordData
{
    public required string PageId { get; set; }
    public required string Slug { get; set; }
    public required string Verdict { get; set; }
    public required string AuditedAt { get; set; }
    public required int Audits { get; set; }
    public string? Note { get; set; }
}
