using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/persona-changesets.json store (issue #50): persona changesets
// and their lifecycle, the persona's counterpart of proposals.json (schema
// amendments). Same reviewed-amendment pattern - propose stores, review
// decides, only the decision writes the target file - and the same
// Load/Save contract: Save rebuilds a snapshot sorted by id (ordinal) so the
// file is byte-deterministic.
//
// At most one changeset is `open` at a time (issue #32): a new proposal
// supersedes the open one. Nothing here locks a changeset; the vault lock
// serialises the commands that touch it.
public sealed class PersonaChangesets
{
    private readonly Dictionary<string, PersonaChangesetData> _byId = new(StringComparer.Ordinal);

    public PersonaChangesetData? Get(string id) => _byId.TryGetValue(id, out var c) ? c : null;

    public PersonaChangesetData? Open() => _byId.Values.FirstOrDefault(c => c.Status == "open");

    public void Put(PersonaChangesetData changeset) => _byId[changeset.Id] = changeset;

    public void Load(Vault v)
    {
        _byId.Clear();
        var path = PathOf(v);
        if (!System.IO.File.Exists(path))
            return;
        var loaded = JsonSerializer.Deserialize(System.IO.File.ReadAllText(path), WikiJsonContext.Default.PersonaChangesetDataArray);
        foreach (var c in loaded ?? Array.Empty<PersonaChangesetData>())
            _byId[c.Id] = c;
    }

    public void Save(Vault v)
    {
        var all = _byId.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        AtomicFile.Write(PathOf(v), JsonSerializer.Serialize(all, WikiJsonContext.Default.PersonaChangesetDataArray));
    }

    private static string PathOf(Vault v) => System.IO.Path.Combine(v.StateDir, "persona-changesets.json");
}
