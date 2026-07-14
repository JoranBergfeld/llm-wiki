using System;
using System.Collections.Generic;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/proposals.json store: AGENTS.md amendment proposals under the
// reflect loop (spec §13, amendment C - full-section replacement, never a
// unified diff). Load/Save contract mirrors Ledger/Issues exactly - Save
// always rebuilds a fresh snapshot sorted by Id (ordinal) immediately before
// serializing, so proposals.json byte order is deterministic regardless of
// insertion order.
//
// This store only records state (open/approved/rejected) and the proposed
// text; it never touches AGENTS.md itself. SchemaService owns the
// "apply the full-section replacement to AGENTS.md" step - keeping that
// split mirrors Ledger (pure state transitions) vs IngestService
// (preconditions + the actual write orchestration).
public sealed class Proposals
{
    private readonly Dictionary<string, Proposal> _byId = new();

    public Proposal? Get(string proposalId) => _byId.TryGetValue(proposalId, out var p) ? p : null;

    public Proposal Add(string id, string section, string newText, string rationale, string createdAt)
    {
        var created = new Proposal
        {
            Id = id,
            Section = section,
            NewText = newText,
            Rationale = rationale,
            Status = "open",
            CreatedAt = createdAt,
            Note = null,
        };
        _byId[id] = created;
        return created;
    }

    // `status` is "approved" or "rejected" - the only two terminal
    // transitions out of "open" (spec §13: propose -> human approve/reject).
    // Callers (SchemaService) are responsible for the not-found/already-
    // decided precondition checks; this just records the transition.
    public Proposal SetStatus(string proposalId, string status, string? note)
    {
        var existing = _byId[proposalId];
        var updated = new Proposal
        {
            Id = existing.Id,
            Section = existing.Section,
            NewText = existing.NewText,
            Rationale = existing.Rationale,
            Status = status,
            CreatedAt = existing.CreatedAt,
            Note = note,
        };
        _byId[proposalId] = updated;
        return updated;
    }

    // Sorted (Id, ordinal) so callers get a stable order for free - the same
    // order Save() writes to disk.
    public IReadOnlyList<Proposal> List(string? status)
    {
        var keys = new List<string>(_byId.Keys);
        keys.Sort(StringComparer.Ordinal);

        var result = new List<Proposal>(keys.Count);
        foreach (var key in keys)
        {
            var p = _byId[key];
            if (status is not null && p.Status != status)
                continue;
            result.Add(p);
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
        var loaded = JsonSerializer.Deserialize(text, WikiJsonContext.Default.ProposalDataArray);
        if (loaded is null)
            return;

        foreach (var d in loaded)
        {
            _byId[d.Id] = new Proposal
            {
                Id = d.Id,
                Section = d.Section,
                NewText = d.NewText,
                Rationale = d.Rationale,
                Status = d.Status,
                CreatedAt = d.CreatedAt,
                Note = d.Note,
            };
        }
    }

    public void Save(Vault v)
    {
        var all = List(null);
        var data = new ProposalData[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            var p = all[i];
            data[i] = new ProposalData
            {
                Id = p.Id,
                Section = p.Section,
                NewText = p.NewText,
                Rationale = p.Rationale,
                Status = p.Status,
                CreatedAt = p.CreatedAt,
                Note = p.Note,
            };
        }

        var json = JsonSerializer.Serialize(data, WikiJsonContext.Default.ProposalDataArray);
        AtomicFile.Write(PathOf(v), json);
    }

    private static string PathOf(Vault v) => System.IO.Path.Combine(v.StateDir, "proposals.json");
}

// In-memory proposal. Status is a plain "open"/"approved"/"rejected" string
// (three lifecycle states) rather than an enum - same call as Issue.Status,
// for the same reason (not a real closed vocabulary reused elsewhere).
public sealed class Proposal
{
    public required string Id { get; init; }
    public required string Section { get; init; }
    public required string NewText { get; init; }
    public required string Rationale { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }

    // Not in the task's minimal Proposal field list, but `reject <id> --note`
    // needs somewhere to put the note - same rationale as Issue.ResolveNote.
    public string? Note { get; init; }
}

// Wire shape for one proposals.json array entry - same split as
// IssueData/LedgerEntryData: every field is already a plain string here, so
// System.Text.Json source-gen (de)serializes this directly with zero
// reflection/converters.
public sealed class ProposalData
{
    public required string Id { get; set; }
    public required string Section { get; set; }
    public required string NewText { get; set; }
    public required string Rationale { get; set; }
    public required string Status { get; set; }
    public required string CreatedAt { get; set; }
    public string? Note { get; set; }
}
