using System;
using System.Collections.Generic;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/category-proposals.json store: proposed additions to the
// category taxonomy in wiki.yaml (issue #9).
//
// Why a SEPARATE store rather than reusing Proposals. The issue asks to
// "reuse the existing proposal machinery rather than building a parallel one,
// if the shapes are close enough to share" - and they are not. A schema
// proposal's payload is (section heading, new full text of that section) and
// approving it runs SectionLocator.Replace against AGENTS.md; a category
// proposal's payload is (kebab id, one-line description, the source ids that
// fit nothing existing) and approving it runs CategoryService.Add against
// wiki.yaml. Sharing the store would mean overloading `Section` as a category
// id and `NewText` as a description, which then leaks: `wiki schema
// proposals` would list category proposals as AGENTS.md amendments, and
// `wiki schema approve <that id>` would try to replace a section named after
// a category and fail in a way nobody could read. Same PATTERN
// (propose/list/approve/reject, human disposes), separate state.
//
// Load/Save contract mirrors Proposals/Issues/Ledger exactly: Save rebuilds a
// fresh snapshot sorted by Id (ordinal) immediately before serializing, so
// the file's byte order is deterministic regardless of insertion order.
//
// This store only records state. CategoryService owns the actual wiki.yaml
// write on approve - the same split Proposals/SchemaService use.
public sealed class CategoryProposals
{
    private readonly Dictionary<string, CategoryProposal> _byId = new();

    public CategoryProposal? Get(string proposalId) => _byId.TryGetValue(proposalId, out var p) ? p : null;

    public CategoryProposal Add(string id, string categoryId, string description, string rationale, string[] sources, string createdAt)
    {
        var created = new CategoryProposal
        {
            Id = id,
            CategoryId = categoryId,
            Description = description,
            Rationale = rationale,
            Sources = sources,
            Status = "open",
            CreatedAt = createdAt,
            Note = null,
        };
        _byId[id] = created;
        return created;
    }

    // "approved" or "rejected" - the only two terminal transitions out of
    // "open". Callers own the not-found/already-decided preconditions.
    public CategoryProposal SetStatus(string proposalId, string status, string? note)
    {
        var existing = _byId[proposalId];
        var updated = new CategoryProposal
        {
            Id = existing.Id,
            CategoryId = existing.CategoryId,
            Description = existing.Description,
            Rationale = existing.Rationale,
            Sources = existing.Sources,
            Status = status,
            CreatedAt = existing.CreatedAt,
            Note = note,
        };
        _byId[proposalId] = updated;
        return updated;
    }

    public IReadOnlyList<CategoryProposal> List(string? status)
    {
        var keys = new List<string>(_byId.Keys);
        keys.Sort(StringComparer.Ordinal);

        var result = new List<CategoryProposal>(keys.Count);
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
        var loaded = JsonSerializer.Deserialize(text, WikiJsonContext.Default.CategoryProposalDataArray);
        if (loaded is null)
            return;

        foreach (var d in loaded)
        {
            _byId[d.Id] = new CategoryProposal
            {
                Id = d.Id,
                CategoryId = d.CategoryId,
                Description = d.Description,
                Rationale = d.Rationale,
                Sources = d.Sources,
                Status = d.Status,
                CreatedAt = d.CreatedAt,
                Note = d.Note,
            };
        }
    }

    public void Save(Vault v)
    {
        var all = List(null);
        var data = new CategoryProposalData[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            var p = all[i];
            data[i] = new CategoryProposalData
            {
                Id = p.Id,
                CategoryId = p.CategoryId,
                Description = p.Description,
                Rationale = p.Rationale,
                Sources = p.Sources,
                Status = p.Status,
                CreatedAt = p.CreatedAt,
                Note = p.Note,
            };
        }

        var json = JsonSerializer.Serialize(data, WikiJsonContext.Default.CategoryProposalDataArray);
        AtomicFile.Write(PathOf(v), json);
    }

    private static string PathOf(Vault v) => System.IO.Path.Combine(v.StateDir, "category-proposals.json");
}

// In-memory category proposal. `Sources` is the evidence: the source ids that
// fit no existing category. That is what makes the decision reviewable rather
// than a judgement call about a name in the abstract - the same role recurring
// issue ids play in a `schema propose` rationale.
public sealed class CategoryProposal
{
    public required string Id { get; init; }
    public required string CategoryId { get; init; }
    public required string Description { get; init; }
    public required string Rationale { get; init; }
    public required string[] Sources { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
    public string? Note { get; init; }
}

// Wire shape for one category-proposals.json entry - same split as
// ProposalData/IssueData: plain strings only, so System.Text.Json source-gen
// handles it with zero reflection.
public sealed class CategoryProposalData
{
    public required string Id { get; set; }
    public required string CategoryId { get; set; }
    public required string Description { get; set; }
    public required string Rationale { get; set; }
    public required string[] Sources { get; set; }
    public required string Status { get; set; }
    public required string CreatedAt { get; set; }
    public string? Note { get; set; }
}
