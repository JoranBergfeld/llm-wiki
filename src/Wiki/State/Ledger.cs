using System.Collections.Generic;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/ledger.json cache: the ingest state machine for every registered
// source (spec §10), keyed by source id. Same shape of contract as IdMap:
// Load/Save round-trip through disk, Save always serializes a freshly-built,
// sourceId-sorted snapshot so the on-disk bytes are deterministic regardless
// of insertion order (needed for reindex byte-identity of *structural* ledger
// state per amendment A - Task 27's job to wire up, but the sort-on-save
// discipline is established here so that later rebuild is a drop-in Save()).
public sealed class Ledger
{
    private readonly Dictionary<string, LedgerEntry> _byId = new();

    public LedgerEntry? Get(string sourceId) => _byId.TryGetValue(sourceId, out var e) ? e : null;

    // `registered` is always the first state for a source - Register creates
    // (or overwrites) the entry outright rather than going through Advance's
    // precondition machinery. utcIso is caller-supplied (mirrors
    // LogFile.Append/PageService's captured-`nowMs` seam) so registration
    // timestamps are deterministic under test.
    public void Register(string sourceId, string utcIso)
    {
        _byId[sourceId] = new LedgerEntry
        {
            SourceId = sourceId,
            State = LedgerState.Registered,
            Touched = System.Array.Empty<string>(),
            IntegratedAt = null,
            RegisteredAt = utcIso,
        };
    }

    // Task 17 wires the CLI precondition checks (`ingest advance`) around
    // this; here it's the raw state transition. IntegratedAt is stamped only
    // when transitioning *into* Integrated, and preserved (not cleared) on
    // every other transition, including the later move to Linted - it always
    // records the timestamp of the most recent `integrated` transition, which
    // is exactly what the `linted` precondition (spec §10) compares against
    // `.wiki/lint.json`.
    //
    // Error code reconciliation (Task 17): "unknown-source" - not
    // "unknown-source-id" - matching PageService's page-sources check
    // (Create/Update's `unknown-source` when a `--sources` id isn't
    // registered). Both are the same underlying condition, "a source id that
    // doesn't exist", so they share one code.
    public void Advance(string sourceId, LedgerState to, string[] touched, string utcIso)
    {
        var existing = Get(sourceId)
            ?? throw new ValidationException("unknown-source", $"no ledger entry for source id '{sourceId}'; register it first with 'wiki source add'");

        _byId[sourceId] = new LedgerEntry
        {
            SourceId = sourceId,
            State = to,
            Touched = touched,
            IntegratedAt = to == LedgerState.Integrated ? utcIso : existing.IntegratedAt,
            RegisteredAt = existing.RegisteredAt,
        };
    }

    public IReadOnlyList<LedgerEntry> All()
    {
        var keys = new List<string>(_byId.Keys);
        keys.Sort(System.StringComparer.Ordinal);

        var result = new List<LedgerEntry>(keys.Count);
        foreach (var key in keys)
            result.Add(_byId[key]);
        return result;
    }

    public void Load(Vault v)
    {
        _byId.Clear();
        var path = PathOf(v);
        if (!System.IO.File.Exists(path))
            return;

        var text = System.IO.File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize(text, WikiJsonContext.Default.LedgerEntryDataArray);
        if (loaded is null)
            return;

        foreach (var d in loaded)
        {
            _byId[d.SourceId] = new LedgerEntry
            {
                SourceId = d.SourceId,
                State = LedgerStateX.Parse(d.State),
                Touched = d.Touched,
                IntegratedAt = d.IntegratedAt,
                RegisteredAt = d.RegisteredAt,
            };
        }
    }

    public void Save(Vault v)
    {
        var entries = All();
        var data = new LedgerEntryData[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            data[i] = new LedgerEntryData
            {
                SourceId = e.SourceId,
                State = LedgerStateX.ToWire(e.State),
                Touched = e.Touched,
                IntegratedAt = e.IntegratedAt,
                RegisteredAt = e.RegisteredAt,
            };
        }

        var json = JsonSerializer.Serialize(data, WikiJsonContext.Default.LedgerEntryDataArray);
        AtomicFile.Write(PathOf(v), json);
    }

    private static string PathOf(Vault v) => System.IO.Path.Combine(v.StateDir, "ledger.json");
}

// In-memory ledger entry. `State` is the LedgerState enum (closed vocabulary,
// spec §10) - callers work with this shape; Ledger.Save/Load convert to/from
// LedgerEntryData (below) at the JSON boundary only, matching every other
// enum in this codebase (PageStatus, PageType, ...) which is always
// represented as its wire string once it crosses a serialization boundary,
// never as a raw numeric enum value.
public sealed class LedgerEntry
{
    public required string SourceId { get; init; }
    public required LedgerState State { get; init; }
    public required string[] Touched { get; init; }
    public string? IntegratedAt { get; init; }
    public string? RegisteredAt { get; init; }
}

// Wire shape for one ledger.json array entry. Plain mutable-property class
// (not the LedgerEntry record above) so System.Text.Json source-gen can
// (de)serialize it directly with zero reflection/converters - State is
// already a plain string here, so no enum converter is needed either.
public sealed class LedgerEntryData
{
    public required string SourceId { get; set; }
    public required string State { get; set; }
    public required string[] Touched { get; set; }
    public string? IntegratedAt { get; set; }
    public string? RegisteredAt { get; set; }
}
