using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/issues.json store: lint findings with an occurrence-merging
// lifecycle (spec §12). Upsert() is keyed on (kind, subject) - the reflect
// loop's whole premise is that `occurrences` surviving multiple lint passes
// signals an instructions deficiency rather than a one-off, so the SAME
// finding must accumulate on one record instead of spawning a new row every
// run.
//
// Merge scope - documented choice for the "resolved recurrence" case the
// task brief calls out: Upsert only merges into an OPEN issue with a
// matching (kind, subject). A finding whose prior issue was already
// `resolved` files a brand-new open issue instead of reopening the old one.
// Rationale: reopening would silently erase the resolution note/timestamp
// (the human's record of "I looked at this and fixed it"), and conflating
// "still open since forever" with "recurred after being fixed" would corrupt
// the occurrence count as a signal - the two are different situations for
// the agent to act on (one's backlog, the other is a regression). The
// resolved issue is left untouched as history; List(...) callers who only
// want live problems already pass `status: "open"`.
//
// Load/Save contract mirrors Ledger/IdMap exactly: Save always rebuilds a
// fresh snapshot sorted by Id (ordinal) immediately before serializing, so
// issues.json byte order is deterministic regardless of insertion/upsert
// order (same discipline IdMap uses for reindex byte-identity).
//
// Clock/RNG seam - narrower than PageService/SourceService's: Upsert takes
// `utcIso` as an explicit param (per this task's interface), so FirstSeen/
// LastSeen are already fully caller-controlled and need no clock seam of
// their own. The issue id's ULID still needs a millisecond timestamp and 80
// random bits; the timestamp is derived by parsing the SAME `utcIso` the
// caller passed (one source of truth for "now" per call, never a second
// independent clock), so only randomness needs injecting - defaulting to
// RandomNumberGenerator like every other ULID-minting service in this
// codebase, with a test seam for deterministic ids.
public sealed class Issues
{
    private readonly Dictionary<string, Issue> _byId = new();
    private readonly Func<byte[]> _randomBytes;

    public Issues(Func<byte[]>? randomBytes = null)
    {
        _randomBytes = randomBytes ?? DefaultRandomBytes;
    }

    public Issue? Get(string issueId) => _byId.TryGetValue(issueId, out var issue) ? issue : null;

    public Issue Upsert(IssueKind kind, string subject, string detail, string utcIso)
    {
        foreach (var issue in _byId.Values)
        {
            if (issue.Kind != kind || issue.Subject != subject || issue.Status != "open")
                continue;

            var bumped = new Issue
            {
                Id = issue.Id,
                Kind = issue.Kind,
                Subject = issue.Subject,
                Detail = detail,
                FirstSeen = issue.FirstSeen,
                LastSeen = utcIso,
                Occurrences = issue.Occurrences + 1,
                Status = issue.Status,
                ResolveNote = issue.ResolveNote,
            };
            _byId[issue.Id] = bumped;
            return bumped;
        }

        var unixMs = DateTimeOffset.Parse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
            .ToUnixTimeMilliseconds();
        var id = WikiUlid.New(unixMs, _randomBytes());

        var created = new Issue
        {
            Id = id,
            Kind = kind,
            Subject = subject,
            Detail = detail,
            FirstSeen = utcIso,
            LastSeen = utcIso,
            Occurrences = 1,
            Status = "open",
            ResolveNote = null,
        };
        _byId[id] = created;
        return created;
    }

    public void Resolve(string issueId, string? note)
    {
        if (!_byId.TryGetValue(issueId, out var issue))
            throw new ValidationException("not-found", $"no issue found for id '{issueId}'");

        _byId[issueId] = new Issue
        {
            Id = issue.Id,
            Kind = issue.Kind,
            Subject = issue.Subject,
            Detail = issue.Detail,
            FirstSeen = issue.FirstSeen,
            LastSeen = issue.LastSeen,
            Occurrences = issue.Occurrences,
            Status = "resolved",
            ResolveNote = note,
        };
    }

    // Sorted (Id, ordinal) so callers get a stable order for free - the same
    // order Save() writes to disk.
    public IReadOnlyList<Issue> List(IssueKind? kind, string? status)
    {
        var keys = new List<string>(_byId.Keys);
        keys.Sort(StringComparer.Ordinal);

        var result = new List<Issue>(keys.Count);
        foreach (var key in keys)
        {
            var issue = _byId[key];
            if (kind is not null && issue.Kind != kind)
                continue;
            if (status is not null && issue.Status != status)
                continue;
            result.Add(issue);
        }
        return result;
    }

    public void Load(Vault v)
    {
        _byId.Clear();
        var path = PathOf(v);
        if (!System.IO.File.Exists(path))
            return;

        var text = System.IO.File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize(text, WikiJsonContext.Default.IssueDataArray);
        if (loaded is null)
            return;

        foreach (var d in loaded)
        {
            _byId[d.Id] = new Issue
            {
                Id = d.Id,
                Kind = IssueKindX.Parse(d.Kind),
                Subject = d.Subject,
                Detail = d.Detail,
                FirstSeen = d.FirstSeen,
                LastSeen = d.LastSeen,
                Occurrences = d.Occurrences,
                Status = d.Status,
                ResolveNote = d.ResolveNote,
            };
        }
    }

    public void Save(Vault v)
    {
        var all = List(null, null);
        var data = new IssueData[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            data[i] = new IssueData
            {
                Id = e.Id,
                Kind = IssueKindX.ToWire(e.Kind),
                Subject = e.Subject,
                Detail = e.Detail,
                FirstSeen = e.FirstSeen,
                LastSeen = e.LastSeen,
                Occurrences = e.Occurrences,
                Status = e.Status,
                ResolveNote = e.ResolveNote,
            };
        }

        var json = JsonSerializer.Serialize(data, WikiJsonContext.Default.IssueDataArray);
        AtomicFile.Write(PathOf(v), json);
    }

    private static string PathOf(Vault v) => System.IO.Path.Combine(v.StateDir, "issues.json");

    private static byte[] DefaultRandomBytes()
    {
        var bytes = new byte[10];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}

// In-memory issue. `Status` is a plain "open"/"resolved" string rather than
// an enum - the task's own interface specifies `string Status`, and unlike
// IssueKind (a real closed vocabulary with 9 members reused across lint
// checks) two lifecycle states don't earn a dedicated enum + wire mapper.
public sealed class Issue
{
    public required string Id { get; init; }
    public required IssueKind Kind { get; init; }
    public required string Subject { get; init; }
    public required string Detail { get; init; }
    public required string FirstSeen { get; init; }
    public required string LastSeen { get; init; }
    public required int Occurrences { get; init; }
    public required string Status { get; init; }

    // Not in the task's minimal Issue field list, but Resolve(issueId, note)
    // needs somewhere to put the note - storing it on the issue itself
    // (rather than discarding it) is what makes `wiki issues show` actually
    // useful after a resolve.
    public string? ResolveNote { get; init; }
}

// Wire shape for one issues.json array entry - same split as
// LedgerEntry/LedgerEntryData: Kind is already a plain wire string here, so
// System.Text.Json source-gen (de)serializes this directly with zero
// reflection/converters.
public sealed class IssueData
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public required string Subject { get; set; }
    public required string Detail { get; set; }
    public required string FirstSeen { get; set; }
    public required string LastSeen { get; set; }
    public required int Occurrences { get; set; }
    public required string Status { get; set; }
    public string? ResolveNote { get; set; }
}
