using System;
using System.Collections.Generic;
using System.Text;

namespace Wiki.Core;

// Raw frontmatter block reader/writer. Hand-rolled, no reflection: splits the
// closed `---`-delimited YAML-lite block at the top of a wiki page file into
// scalar and list key/value maps, plus the verbatim body that follows. Typed
// mappers (PageFrontmatter, SourceFrontmatter) sit below and enforce the
// closed key sets for each page kind.
public static class Frontmatter
{
    public const string Delimiter = "---";

    public static (Dictionary<string, string> Scalars, Dictionary<string, string[]> Lists, string Body) ReadBlock(string fileText)
    {
        if (fileText is null)
            throw new ValidationException("frontmatter-schema", "file text is null");

        var text = fileText.Replace("\r\n", "\n");
        var lines = text.Split('\n');

        if (lines.Length == 0 || lines[0] != Delimiter)
            throw new ValidationException("frontmatter-schema", "frontmatter must start with a '---' delimiter line");

        var scalars = new Dictionary<string, string>();
        var lists = new Dictionary<string, string[]>();

        int closingIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i] == Delimiter) { closingIndex = i; break; }

            var line = lines[i];
            // Blank lines inside the block are tolerated and canonicalized away: they carry no
            // key, so skipping them doesn't weaken the closed-key check, but they are NOT
            // reproduced by ToBlock(). This means the round-trip guarantee (Parse then Serialize
            // reproduces the input) holds for canonical, serializer-produced frontmatter only;
            // hand-edited input with stray blank lines parses fine but normalizes on the way out.
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            if (colon < 0)
                throw new ValidationException("frontmatter-schema", $"malformed frontmatter line: '{line}'");

            var key = line[..colon].Trim();
            var rawValue = line[(colon + 1)..].Trim();

            if (rawValue.StartsWith('[') && rawValue.EndsWith(']'))
            {
                var inner = rawValue[1..^1].Trim();
                lists[key] = inner.Length == 0 ? Array.Empty<string>() : SplitList(inner);
            }
            else
            {
                scalars[key] = Unquote(rawValue);
            }
        }

        if (closingIndex < 0)
            throw new ValidationException("frontmatter-schema", "frontmatter block is missing closing '---' delimiter");

        var bodyLines = lines[(closingIndex + 1)..];
        var body = string.Join("\n", bodyLines);

        return (scalars, lists, body);
    }

    // Every key that occurs in either the scalar or list maps must be a member
    // of `allowed`, otherwise the frontmatter is rejected.
    public static void ValidateKeys(Dictionary<string, string> scalars, Dictionary<string, string[]> lists, string[] allowed)
    {
        foreach (var key in scalars.Keys)
            if (Array.IndexOf(allowed, key) < 0)
                throw new ValidationException("frontmatter-schema", $"unknown frontmatter key '{key}'");

        foreach (var key in lists.Keys)
            if (Array.IndexOf(allowed, key) < 0)
                throw new ValidationException("frontmatter-schema", $"unknown frontmatter key '{key}'");
    }

    public static void RequireScalarKeys(Dictionary<string, string> scalars, string[] required)
    {
        foreach (var key in required)
            if (!scalars.ContainsKey(key))
                throw new ValidationException("frontmatter-schema", $"missing required frontmatter key '{key}'");
    }

    public static void RequireListKeys(Dictionary<string, string[]> lists, string[] required)
    {
        foreach (var key in required)
            if (!lists.ContainsKey(key))
                throw new ValidationException("frontmatter-schema", $"missing required frontmatter key '{key}'");
    }

    public static string ValidId(Dictionary<string, string> scalars, string key = "id")
    {
        var id = scalars[key];
        if (!WikiUlid.IsValid(id))
            throw new ValidationException("frontmatter-schema", $"invalid '{key}': '{id}'");
        return id;
    }

    public static string WriteScalar(string key, string value, bool quoted)
        => quoted ? $"{key}: \"{value}\"" : $"{key}: {value}";

    public static string WriteList(string key, IReadOnlyList<string> values)
        => $"{key}: [{string.Join(", ", values)}]";

    private static string[] SplitList(string inner)
    {
        var parts = inner.Split(',');
        var result = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = Unquote(parts[i].Trim());
        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}

// Frontmatter for a normal wiki page: id, type, title, status, created,
// updated, summary, sources, tags. Fixed serialization order matches this
// list. `summary` is required (amendment B).
public sealed class PageFrontmatter
{
    public required string Id { get; init; }
    public required PageType Type { get; init; }
    public required string Title { get; init; }
    public required PageStatus Status { get; init; }
    public required string Created { get; init; }
    public required string Updated { get; init; }
    public required string Summary { get; init; }
    public required string[] Sources { get; init; }
    public required string[] Tags { get; init; }

    private static readonly string[] AllowedKeys =
        { "id", "type", "title", "status", "created", "updated", "summary", "sources", "tags" };

    private static readonly string[] RequiredScalarKeys =
        { "id", "type", "title", "status", "created", "updated", "summary" };

    private static readonly string[] RequiredListKeys = { "sources", "tags" };

    public static PageFrontmatter FromRaw(Dictionary<string, string> scalars, Dictionary<string, string[]> lists)
    {
        Frontmatter.ValidateKeys(scalars, lists, AllowedKeys);
        Frontmatter.RequireScalarKeys(scalars, RequiredScalarKeys);
        Frontmatter.RequireListKeys(lists, RequiredListKeys);

        return new PageFrontmatter
        {
            Id = Frontmatter.ValidId(scalars),
            Type = PageTypeX.Parse(scalars["type"]),
            Title = scalars["title"],
            Status = PageStatusX.Parse(scalars["status"]),
            Created = scalars["created"],
            Updated = scalars["updated"],
            Summary = scalars["summary"],
            Sources = lists["sources"],
            Tags = lists["tags"],
        };
    }

    public string ToBlock()
    {
        var sb = new StringBuilder();
        sb.Append(Frontmatter.Delimiter).Append('\n');
        sb.Append(Frontmatter.WriteScalar("id", Id, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("type", PageTypeX.ToWire(Type), quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("title", Title, quoted: true)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("status", PageStatusX.ToWire(Status), quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("created", Created, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("updated", Updated, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("summary", Summary, quoted: true)).Append('\n');
        sb.Append(Frontmatter.WriteList("sources", Sources)).Append('\n');
        sb.Append(Frontmatter.WriteList("tags", Tags)).Append('\n');
        sb.Append(Frontmatter.Delimiter);
        return sb.ToString();
    }
}

// Frontmatter for a source record: id, type(fixed "source"), title, category,
// added, sha256, origin, status.
public sealed class SourceFrontmatter
{
    public const string FixedType = "source";

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Added { get; init; }
    public required string Sha256 { get; init; }
    public required string Origin { get; init; }
    public required SourceStatus Status { get; init; }

    private static readonly string[] AllowedKeys =
        { "id", "type", "title", "category", "added", "sha256", "origin", "status" };

    private static readonly string[] RequiredScalarKeys = AllowedKeys;

    public static SourceFrontmatter FromRaw(Dictionary<string, string> scalars, Dictionary<string, string[]> lists)
    {
        Frontmatter.ValidateKeys(scalars, lists, AllowedKeys);
        Frontmatter.RequireScalarKeys(scalars, RequiredScalarKeys);

        var type = scalars["type"];
        if (type != FixedType)
            throw new ValidationException("frontmatter-schema", $"source frontmatter 'type' must be '{FixedType}', got '{type}'");

        return new SourceFrontmatter
        {
            Id = Frontmatter.ValidId(scalars),
            Title = scalars["title"],
            Category = scalars["category"],
            Added = scalars["added"],
            Sha256 = scalars["sha256"],
            Origin = scalars["origin"],
            Status = SourceStatusX.Parse(scalars["status"]),
        };
    }

    public string ToBlock()
    {
        var sb = new StringBuilder();
        sb.Append(Frontmatter.Delimiter).Append('\n');
        sb.Append(Frontmatter.WriteScalar("id", Id, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("type", FixedType, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("title", Title, quoted: true)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("category", Category, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("added", Added, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("sha256", Sha256, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("origin", Origin, quoted: false)).Append('\n');
        sb.Append(Frontmatter.WriteScalar("status", SourceStatusX.ToWire(Status), quoted: false)).Append('\n');
        sb.Append(Frontmatter.Delimiter);
        return sb.ToString();
    }
}
