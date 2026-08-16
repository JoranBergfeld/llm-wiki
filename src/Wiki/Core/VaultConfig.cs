using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wiki.Core;

// One source category, as declared under `categories:` in wiki.yaml.
public sealed record Category(string Id, string Description);

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

    public bool HasCategory(string id)
    {
        foreach (var c in Categories)
            if (c.Id == id)
                return true;
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

                    categories.Add(new Category(id, description));
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
                    else throw new ValidationException("config", $"unknown 'lint' key: '{kv}'", yamlPath);
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
