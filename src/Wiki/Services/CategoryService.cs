using System.Collections.Generic;
using System.Text.RegularExpressions;
using Wiki.Cli;
using Wiki.Core;

namespace Wiki.Services;

// `wiki category add` result.
public sealed record CategoryAddResult(string Id, string Description) : IHumanRenderable
{
    public string HumanSummary() => $"Added category '{Id}'";
}

// `wiki category list` row shape.
public sealed record CategoryData(string Id, string Description);

// Backs `wiki category add/list` (spec §5). This is the ONLY place category
// ids ever get written to wiki.yaml - there is no code path from source-add
// or ingest into this service (spec §5's "the CLI never adds categories on
// its own" guarantee lives structurally: nothing but CategoryService.Add
// ever calls AtomicFile.Write(vault.ConfigPath, ...)).
public sealed class CategoryService
{
    private static readonly Regex KebabId = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    // Adds a category by a TARGETED textual insertion into wiki.yaml, not a
    // full re-serialize of the parsed VaultConfig. VaultConfig.Load discards
    // comments and exact formatting as it parses (see its own doc comment);
    // round-tripping through it and writing back out would silently strip
    // every human `# comment` in the file. Instead this reads the raw file
    // text, locates the `categories:` block, and inserts a new `- id: ... /
    // description: ...` pair immediately after the last existing category
    // item (before whatever comes next - `lint:`, EOF, or a blank line) -
    // every other line, comment, and key in the file is left byte-identical.
    public CategoryAddResult Add(Vault v, VaultConfig cfg, string id, string description)
    {
        // --- Blocking validation: ALL of it runs before anything below touches disk. ---

        if (!KebabId.IsMatch(id))
            throw new ValidationException("invalid-category-id", $"category id '{id}' must be lowercase kebab-case");

        if (cfg.HasCategory(id))
            throw new ValidationException("duplicate-category", $"category '{id}' already exists in wiki.yaml");

        GuardScalar(description, "description");

        var text = System.IO.File.ReadAllText(v.ConfigPath);
        var updated = InsertCategory(v.ConfigPath, text, id, description);

        // --- Validation complete. Everything from here on is the write. ---

        AtomicFile.Write(v.ConfigPath, updated);

        // Confirm the edited file still round-trips through the real parser
        // before returning success - a malformed insertion must never be
        // left on disk silently.
        VaultConfig.Load(v.ConfigPath);

        return new CategoryAddResult(id, description);
    }

    // Spec §5 / amendment N: "removing a category that sources still
    // reference is a blocking config error". Enforced here rather than inside
    // VaultConfig.Load, which parses a file path and has no vault to scan.
    //
    // Called from CommandContext.LoadConfig, so it gates every command that
    // reads config - which is the mutation surface (`page upsert`,
    // `source add`, `lint`, `ingest advance`) plus `category`. The `category`
    // command deliberately bypasses it: `wiki category add <dropped-id>` is
    // the repair, and a check that blocks its own fix is a trap.
    //
    // Retracted sources count. Their raw/ file is still on disk carrying the
    // category (retraction flips `status`, it does not delete the record), so
    // the reference is real and the category is still load-bearing.
    //
    // Cost is one frontmatter scan of raw/*.md per config-reading invocation -
    // the same scan `source list` already does on every call.
    public static void EnsureCategoriesCoverSources(Vault v, VaultConfig cfg)
    {
        // Which sources reference each missing category - collected rather
        // than short-circuited so the error can name them. A human who
        // deleted a category needs to know what they broke, not just that
        // they broke something.
        var missing = new SortedDictionary<string, List<string>>(System.StringComparer.Ordinal);

        // Tolerant read: this runs on every config-reading command, so an
        // unparseable stray file under raw/ must not brick the CLI with a
        // frontmatter error unrelated to what the caller asked for. Strict
        // readers (dedup, reindex) still surface those loudly.
        foreach (var (front, _) in SourceStore.Enumerate(v, skipUnparseable: true))
        {
            if (cfg.HasCategory(front.Category))
                continue;

            if (!missing.TryGetValue(front.Category, out var ids))
            {
                ids = new List<string>();
                missing[front.Category] = ids;
            }
            ids.Add(front.Id);
        }

        if (missing.Count == 0)
            return;

        var parts = new List<string>();
        foreach (var (category, ids) in missing)
            parts.Add($"'{category}' (referenced by {ids.Count} source(s): {string.Join(", ", ids)})");

        var first = System.Linq.Enumerable.First(missing.Keys);
        throw new ValidationException("category-in-use",
            $"wiki.yaml is missing {parts.Count} category/categories that registered sources still reference: " +
            $"{string.Join("; ", parts)}. Restore it with " +
            $"'wiki category add {first} --description \"...\"', or retract and purge the sources that use it.",
            v.ConfigPath);
    }

    public IReadOnlyList<CategoryData> List(VaultConfig cfg)
    {
        var result = new List<CategoryData>();
        foreach (var c in cfg.Categories)
            result.Add(new CategoryData(c.Id, c.Description));
        return result.ToArray();
    }

    // Splits on the raw file text (preserving \r\n vs \n and every comment),
    // finds the top-level `categories:` line, walks forward over `- id: ... /
    // description: ...` pairs (same 2-line-per-item shape VaultConfig.Load
    // itself expects), and inserts the new pair right after the last one -
    // i.e. right before whatever line ends the block (a blank line, `lint:`,
    // or EOF). Mirrors the 2-space/4-space indent style the wiki-yaml
    // template and VaultConfig.Load both use.
    private static string InsertCategory(string configPath, string text, string id, string description)
    {
        var hasCrLf = text.Contains("\r\n");
        var normalized = text.Replace("\r\n", "\n");
        var lines = new List<string>(normalized.Split('\n'));

        var categoriesLine = FindTopLevelKeyLine(lines, "categories:");
        if (categoriesLine < 0)
            throw new ValidationException("config", "wiki.yaml has no top-level 'categories:' key to insert into", configPath);

        var insertAt = categoriesLine + 1;
        var i = categoriesLine + 1;
        while (i < lines.Count)
        {
            if (!IsListItemStart(lines[i]) || !StripComment(lines[i]).Trim().StartsWith("- id:", System.StringComparison.Ordinal))
                break;
            if (i + 1 >= lines.Count || !IsIndentedContinuation(lines[i + 1]) ||
                !StripComment(lines[i + 1]).Trim().StartsWith("description:", System.StringComparison.Ordinal))
                break;
            i += 2;
            insertAt = i;
        }

        var newItem = new[]
        {
            $"  - id: {id}",
            $"    description: \"{description}\"",
        };
        lines.InsertRange(insertAt, newItem);

        var joined = string.Join("\n", lines);
        return hasCrLf ? joined.Replace("\n", "\r\n") : joined;
    }

    // An un-indented "key:" line (optionally followed by a trailing comment
    // or more content) - the same "top-level scalar" shape VaultConfig.Load
    // matches against for `version`/`name`/etc.
    private static int FindTopLevelKeyLine(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;
            if (StripComment(line).Trim() == key)
                return i;
        }
        return -1;
    }

    private static bool IsListItemStart(string line)
    {
        var trimmed = line.TrimStart();
        return line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && trimmed.StartsWith("- ", System.StringComparison.Ordinal);
    }

    // A continuation line for a list item's second field: indented, but not
    // itself the start of a new `- ` item.
    private static bool IsIndentedContinuation(string line)
    {
        if (line.Length == 0) return false;
        if (line[0] != ' ' && line[0] != '\t') return false;
        var trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith("- ", System.StringComparison.Ordinal);
    }

    // Same quote-aware inline-comment stripper as VaultConfig - duplicated
    // (not shared) because it's a five-line leaf helper and pulling it out
    // into a shared utility for one caller isn't worth the indirection.
    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == '#' && !inQuotes && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i].TrimEnd();
            }
        }
        return line.TrimEnd();
    }

    // wiki.yaml is config, not frontmatter: a description with a stray '"' or
    // newline would corrupt the single-line quoted value this inserts (the
    // parser has no quote-escaping), so it's rejected here. Code is
    // `invalid-description` - a config-appropriate code, NOT the
    // `frontmatter-schema` code SourceService/PageService use, since an agent
    // branching on errors[].code shouldn't be told a wiki.yaml edit failed a
    // page/source frontmatter rule.
    private static void GuardScalar(string value, string field)
    {
        foreach (var c in value)
        {
            if (c == '"' || c == '\n' || c == '\r')
                throw new ValidationException("invalid-description", $"'{field}' may not contain quotes or newlines");
        }
    }
}
