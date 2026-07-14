using System.Collections.Generic;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.State;

// The .wiki/idmap.json cache: page/source `id` -> vault-relative path (forward
// slashes, even on Windows). Round-trips through the source-gen
// Dictionary<string,string> serializer already registered in WikiJsonContext -
// no wrapper DTO needed since the on-disk shape IS just { "<id>": "<path>" }.
//
// Save() always rebuilds a fresh Dictionary<string,string> from the keys in
// ordinal-sorted order immediately before serializing. A freshly-populated
// Dictionary with no removals enumerates in insertion order under the current
// .NET implementation, so this pins the JSON key order to be sorted and
// deterministic - required so `wiki reindex` (Task 15) can reproduce
// idmap.json byte-identically regardless of filesystem enumeration order.
public sealed class IdMap
{
    private readonly Dictionary<string, string> _byId = new();

    public IReadOnlyDictionary<string, string> All => _byId;

    public string? PathFor(string id) => _byId.TryGetValue(id, out var path) ? path : null;

    // Reverse lookup: path -> id. O(n) linear scan; the map is small (one
    // entry per source/page) so this is fine and avoids maintaining a second
    // index that could drift out of sync.
    public string? IdFor(string relPath)
    {
        var normalized = Normalize(relPath);
        foreach (var kv in _byId)
        {
            if (kv.Value == normalized)
                return kv.Key;
        }
        return null;
    }

    public void Put(string id, string relPath) => _byId[id] = Normalize(relPath);

    public void Remove(string id) => _byId.Remove(id);

    public void Load(Vault v)
    {
        _byId.Clear();
        var path = PathOf(v);
        if (!System.IO.File.Exists(path))
            return;

        var text = System.IO.File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize(text, WikiJsonContext.Default.DictionaryStringString);
        if (loaded is null)
            return;

        foreach (var kv in loaded)
            _byId[kv.Key] = kv.Value;
    }

    public void Save(Vault v)
    {
        var keys = new List<string>(_byId.Keys);
        keys.Sort(System.StringComparer.Ordinal);

        var ordered = new Dictionary<string, string>(keys.Count);
        foreach (var key in keys)
            ordered[key] = _byId[key];

        var json = JsonSerializer.Serialize(ordered, WikiJsonContext.Default.DictionaryStringString);
        AtomicFile.Write(PathOf(v), json);
    }

    private static string PathOf(Vault v) => System.IO.Path.Combine(v.StateDir, "idmap.json");

    private static string Normalize(string relPath) => relPath.Replace('\\', '/');
}
