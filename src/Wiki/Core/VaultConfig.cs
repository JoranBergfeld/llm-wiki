using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wiki.Core;

// One source category, as declared under `categories:` in wiki.yaml.
//
// `PersonaSource` (issue #49, ADR 0001) is the owner's endorsement: pages
// built from sources in this category are endorsed notes and land flagged
// `persona: candidate`. Endorsement is per category, all or nothing - a mixed
// inbox gets split rather than marked per file. Optional, default false, so
// every wiki.yaml written before it existed still loads.
public sealed record Category(string Id, string Description, bool PersonaSource = false);

// Hand-parsed, validated `wiki.yaml`. No YamlDotNet, no reflection: the file
// shape is small and fixed (spec §5), so this walks the lines itself looking
// for the known top-level scalars, the `categories:` list, and the `lint:`
// block. Any deviation from the expected shape, or any rule violation
// (version != 1, non-kebab or duplicate category id, missing required key),
// throws ValidationException(Code="config") so a bad config fails hard on
// every CLI invocation that reads it.
public sealed class VaultConfig
{
    private static readonly Regex KebabId = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public required int Version { get; init; }
    public required string Name { get; init; }
    public required bool ReviewGate { get; init; }
    public required List<Category> Categories { get; init; }
    public required int StalenessDays { get; init; }
    public required int MaxPageLines { get; init; }

    // `lint.content_loss_percent` (issue #11 / amendment V): the share of a
    // page's structural references (wikilink targets + cited source ids) a
    // single full-body replacement may drop before `page upsert --id` files a
    // `content-loss` issue. Expressed as a whole-number percentage, matching
    // the integer-scalar style of every other value in this file.
    //
    // OPTIONAL, unlike staleness_days/max_page_lines. Making it required
    // would fail every wiki.yaml written before it existed - a config that
    // was valid yesterday must not brick the CLI today - so it defaults
    // instead. Default 25: dropping a quarter of a page's connections in one
    // rewrite is already a strong signal, and a threshold that fires too
    // rarely is worse than one that fires occasionally on a legitimate
    // restructure, which is resolvable with a note like any other issue.
    public int ContentLossPercent { get; init; } = DefaultContentLossPercent;

    public const int DefaultContentLossPercent = 25;

    // `audit.path` (issue #44): where the hash-chained audit log lives,
    // relative to the vault root or absolute. Optional; defaults to
    // audit/audit.log so it sits OUTSIDE `.wiki/`, which is a rebuildable
    // cache that `wiki reindex` regenerates - the audit log is history and can
    // never be rebuilt. Detail files go in a `details/` directory beside it.
    public string AuditPath { get; init; } = DefaultAuditPath;

    public const string DefaultAuditPath = "audit/audit.log";

    // `audit.rotate_bytes` (issue #45): once the live audit log reaches this
    // size, the next mutation first rolls it into a compressed segment. About
    // 10 MB by default; a positive integer number of bytes.
    public long AuditRotateBytes { get; init; } = DefaultAuditRotateBytes;

    public const long DefaultAuditRotateBytes = 10L * 1024 * 1024;

    public bool HasCategory(string id)
    {
        foreach (var c in Categories)
            if (c.Id == id)
                return true;
        return false;
    }

    public bool IsPersonaSource(string categoryId)
    {
        foreach (var c in Categories)
            if (c.Id == categoryId)
                return c.PersonaSource;
        return false;
    }

    public static VaultConfig Load(string yamlPath)
    {
        string text;
        try
        {
            text = System.IO.File.ReadAllText(yamlPath);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            throw new ValidationException("config", $"cannot read config file '{yamlPath}': {ex.Message}", yamlPath);
        }

        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        // Strip inline comments up front, quote-aware, so every downstream matcher sees
        // comment-free lines. Leading indentation is preserved (the indent-based loop
        // guards depend on it); only the trailing comment + its whitespace are removed.
        var lines = new string[rawLines.Length];
        for (int li = 0; li < rawLines.Length; li++)
            lines[li] = StripInlineComment(rawLines[li]);

        string? version = null;
        string? name = null;
        string? reviewGate = null;
        var categories = new List<Category>();
        string? stalenessDays = null;
        string? maxPageLines = null;
        string? contentLossPercent = null;
        string? auditPath = null;
        string? auditRotateBytes = null;

        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (TryTopLevelScalar(line, "version", out var v)) { version = v; i++; continue; }
            if (TryTopLevelScalar(line, "name", out var n)) { name = n; i++; continue; }
            if (TryTopLevelScalar(line, "review_gate", out var rg)) { reviewGate = rg; i++; continue; }

            if (trimmed == "categories:")
            {
                i++;
                while (i < lines.Length && IsListItemStart(lines[i]))
                {
                    var idLine = lines[i].Trim();
                    // Expect "- id: <value>"
                    var afterDash = idLine[1..].TrimStart();
                    if (!TryScalarLine(afterDash, "id", out var id))
                        throw new ValidationException("config", $"malformed category entry: '{idLine}'", yamlPath);
                    i++;

                    if (i >= lines.Length || !TryScalarLine(lines[i].Trim(), "description", out var description))
                        throw new ValidationException("config", $"category '{id}' is missing 'description'", yamlPath);
                    i++;

                    // Optional per-category keys follow the description, one
                    // per indented line, until the next `- ` item or the end
                    // of the block. A closed set: an unknown key is a typo or
                    // a guess at a flag that does not exist, and a silently
                    // ignored endorsement is worse than a config error.
                    var personaSource = false;
                    while (i < lines.Length && IsIndentedKey(lines[i]))
                    {
                        var kv = lines[i].Trim();
                        if (!TryScalarLine(kv, "persona-source", out var ps))
                            throw new ValidationException("config", $"category '{id}' has unknown key: '{kv}'", yamlPath);
                        if (!TryParseBool(ps, out personaSource))
                            throw new ValidationException("config",
                                $"category '{id}': 'persona-source' must be true or false, got '{ps}'", yamlPath);
                        i++;
                    }

                    categories.Add(new Category(id, description, personaSource));
                }
                continue;
            }

            if (trimmed == "lint:")
            {
                i++;
                while (i < lines.Length && IsIndentedKey(lines[i]))
                {
                    var kv = lines[i].Trim();
                    if (TryScalarLine(kv, "staleness_days", out var sd)) stalenessDays = sd;
                    else if (TryScalarLine(kv, "max_page_lines", out var mpl)) maxPageLines = mpl;
                    else if (TryScalarLine(kv, "content_loss_percent", out var clp)) contentLossPercent = clp;
                    else throw new ValidationException("config", $"unknown 'lint' key: '{kv}'", yamlPath);
                    i++;
                }
                continue;
            }

            if (trimmed == "audit:")
            {
                i++;
                while (i < lines.Length && IsIndentedKey(lines[i]))
                {
                    var kv = lines[i].Trim();
                    if (TryScalarLine(kv, "path", out var ap)) auditPath = ap;
                    else if (TryScalarLine(kv, "rotate_bytes", out var rb)) auditRotateBytes = rb;
                    else throw new ValidationException("config", $"unknown 'audit' key: '{kv}'", yamlPath);
                    i++;
                }
                continue;
            }

            throw new ValidationException("config", $"unrecognized config line: '{line}'", yamlPath);
        }

        if (version is null)
            throw new ValidationException("config", "missing required key 'version'", yamlPath);
        if (name is null)
            throw new ValidationException("config", "missing required key 'name'", yamlPath);
        if (reviewGate is null)
            throw new ValidationException("config", "missing required key 'review_gate'", yamlPath);
        if (stalenessDays is null)
            throw new ValidationException("config", "missing required key 'lint.staleness_days'", yamlPath);
        if (maxPageLines is null)
            throw new ValidationException("config", "missing required key 'lint.max_page_lines'", yamlPath);

        if (!int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var versionNum))
            throw new ValidationException("config", $"'version' must be an integer, got '{version}'", yamlPath);
        if (versionNum != 1)
            throw new ValidationException("config", $"unsupported config version '{versionNum}'; only version 1 is supported", yamlPath);

        if (!TryParseBool(reviewGate, out var reviewGateBool))
            throw new ValidationException("config", $"'review_gate' must be true or false, got '{reviewGate}'", yamlPath);

        if (!int.TryParse(stalenessDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stalenessDaysNum))
            throw new ValidationException("config", $"'lint.staleness_days' must be an integer, got '{stalenessDays}'", yamlPath);
        if (!int.TryParse(maxPageLines, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxPageLinesNum))
            throw new ValidationException("config", $"'lint.max_page_lines' must be an integer, got '{maxPageLines}'", yamlPath);

        var contentLossPercentNum = DefaultContentLossPercent;
        if (contentLossPercent is not null)
        {
            if (!int.TryParse(contentLossPercent, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLossPercentNum))
                throw new ValidationException("config", $"'lint.content_loss_percent' must be an integer, got '{contentLossPercent}'", yamlPath);
            // 0 is meaningful (report every removal); 100 means "never file".
            // Outside that range the value cannot express anything, so it is a
            // typo rather than a preference.
            if (contentLossPercentNum is < 0 or > 100)
                throw new ValidationException("config",
                    $"'lint.content_loss_percent' must be between 0 and 100, got {contentLossPercentNum}", yamlPath);
        }

        if (auditPath is not null)
        {
            if (auditPath.Length == 0)
                throw new ValidationException("config", "'audit.path' must not be empty", yamlPath);
            // Containment outside .wiki/ is checked where the vault root is
            // known: AuditLog.For.
        }

        var auditRotateBytesNum = DefaultAuditRotateBytes;
        if (auditRotateBytes is not null
            && (!long.TryParse(auditRotateBytes, NumberStyles.None, CultureInfo.InvariantCulture, out auditRotateBytesNum)
                || auditRotateBytesNum < 1))
            throw new ValidationException("config",
                $"'audit.rotate_bytes' must be a positive whole number of bytes, got '{auditRotateBytes}'", yamlPath);

        var seenIds = new HashSet<string>();
        foreach (var category in categories)
        {
            if (!KebabId.IsMatch(category.Id))
                throw new ValidationException("config", $"category id '{category.Id}' must be lowercase kebab-case", yamlPath);
            if (!seenIds.Add(category.Id))
                throw new ValidationException("config", $"duplicate category id '{category.Id}'", yamlPath);
        }

        return new VaultConfig
        {
            Version = versionNum,
            Name = name,
            ReviewGate = reviewGateBool,
            Categories = categories,
            StalenessDays = stalenessDaysNum,
            MaxPageLines = maxPageLinesNum,
            ContentLossPercent = contentLossPercentNum,
            AuditPath = auditPath ?? DefaultAuditPath,
            AuditRotateBytes = auditRotateBytesNum,
        };
    }

    // A top-level (unindented) "key: value" line matching the given key.
    private static bool TryTopLevelScalar(string line, string key, out string value)
    {
        value = "";
        if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            return false;
        return TryScalarLine(line.Trim(), key, out value);
    }

    // A "key: value" line (already trimmed of leading indentation) matching the given key.
    private static bool TryScalarLine(string trimmedLine, string key, out string value)
    {
        value = "";
        var prefix = key + ":";
        if (!trimmedLine.StartsWith(prefix, System.StringComparison.Ordinal))
            return false;
        var rest = trimmedLine[prefix.Length..].Trim();
        value = Unquote(rest);
        return true;
    }

    private static bool IsListItemStart(string line)
    {
        var trimmed = line.TrimStart();
        return line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && trimmed.StartsWith("- ", System.StringComparison.Ordinal);
    }

    private static bool IsIndentedKey(string line)
    {
        if (line.Length == 0) return false;
        if (line[0] != ' ' && line[0] != '\t') return false;
        var trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith("- ", System.StringComparison.Ordinal);
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        if (raw == "true") { value = true; return true; }
        if (raw == "false") { value = false; return true; }
        value = false;
        return false;
    }

    // Remove a trailing inline comment, quote-aware. A '#' only starts a comment when it is
    // outside double quotes AND is the first non-whitespace char on the line or is preceded
    // by whitespace. So `name: "C# shop"  # note` keeps the internal `#`, `a#b` stays literal
    // (no space before `#`), and `categories:  # ...` / `staleness_days: 90  # ...` are cut.
    // Leading indentation is preserved; only from the comment onward (and trailing space) is dropped.
    internal static string StripInlineComment(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
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

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}
