using System;
using System.Collections.Generic;

namespace Wiki.Core;

// The four fixed PERSONA.md sections, in the order the file must hold them
// (issue #31, spec amendment AB). Order of the enum IS the required file order.
public enum PersonaSection { Values, Principles, ToneOfVoice, Boundaries }

public static class PersonaSectionX
{
    public static readonly PersonaSection[] InOrder =
        { PersonaSection.Values, PersonaSection.Principles, PersonaSection.ToneOfVoice, PersonaSection.Boundaries };

    // The `## ` heading text as written in the file.
    public static string Heading(PersonaSection s) => s switch
    {
        PersonaSection.Values => "Values",
        PersonaSection.Principles => "Principles",
        PersonaSection.ToneOfVoice => "Tone of voice",
        PersonaSection.Boundaries => "Boundaries",
        _ => throw new ValidationException("invalid-persona-section", $"unknown PersonaSection '{s}'"),
    };

    // Kebab-case wire name, for CLI options and finding details.
    public static string ToWire(PersonaSection s) => s switch
    {
        PersonaSection.Values => "values",
        PersonaSection.Principles => "principles",
        PersonaSection.ToneOfVoice => "tone-of-voice",
        PersonaSection.Boundaries => "boundaries",
        _ => throw new ValidationException("invalid-persona-section", $"unknown PersonaSection '{s}'"),
    };

    // Line cap per section; tone of voice has none (it gets a word-count
    // warning instead - see PersonaDoc.ToneWordWarning).
    public static int? LineCap(PersonaSection s) => s switch
    {
        PersonaSection.Values => 10,
        PersonaSection.Principles => 15,
        PersonaSection.ToneOfVoice => null,
        PersonaSection.Boundaries => 10,
        _ => throw new ValidationException("invalid-persona-section", $"unknown PersonaSection '{s}'"),
    };

    // Headings match case-insensitively: the CLI writes the canonical
    // spelling, but a hand-edit that lower-cases a heading is still clearly
    // the same section, and calling it "missing" would be a false finding.
    public static PersonaSection? FromHeading(string headingText)
    {
        foreach (var s in InOrder)
            if (string.Equals(Heading(s), headingText.Trim(), StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }
}

// A parsed PERSONA.md. Parsing is deliberately LENIENT: it never throws on a
// malformed file, it records what it could not make sense of in `Problems`
// and keeps whatever entries it found. `wiki persona read` serves the
// lenient view (an assistant is better off with the lines that are there
// than with nothing); `wiki lint` reports the problems.
public sealed class PersonaDoc
{
    public const string FileName = "PERSONA.md";
    public const string CurrentSchemaVersion = "1";

    // Frontmatter keys, exactly these three (issue #31): schema version,
    // date of the last distillation, and the audit-log entry that produced
    // the file. snake_case to match wiki.yaml's key style.
    public const string SchemaVersionKey = "schema_version";
    public const string LastDistilledKey = "last_distilled";
    public const string AuditEntryKey = "audit_entry";
    private static readonly string[] FrontmatterKeys = { SchemaVersionKey, LastDistilledKey, AuditEntryKey };

    public string? SchemaVersion { get; private init; }
    public string? LastDistilled { get; private init; }
    public string? AuditEntry { get; private init; }

    public IReadOnlyDictionary<PersonaSection, IReadOnlyList<string>> Entries { get; private init; } =
        new Dictionary<PersonaSection, IReadOnlyList<string>>();

    // Structural problems found while parsing, each tagged with the issue
    // kind lint files it under. Frontmatter and section-structure problems
    // only; the content rules (links, lengths, caps) are in Validate().
    public IReadOnlyList<(IssueKind Kind, string Detail)> Problems { get; private init; } =
        Array.Empty<(IssueKind, string)>();

    // The body text after the frontmatter, kept for the link scan (a link
    // can sit anywhere, not only inside an entry).
    private string Body { get; init; } = "";

    public static PersonaDoc Empty() => Parse("");

    public IReadOnlyList<string> Section(PersonaSection s)
        => Entries.TryGetValue(s, out var lines) ? lines : Array.Empty<string>();

    public static PersonaDoc Parse(string fileText)
    {
        var text = fileText.Replace("\r\n", "\n");
        var problems = new List<(IssueKind, string)>();

        string? schemaVersion = null, lastDistilled = null, auditEntry = null;
        string body;
        if (text.Length == 0)
        {
            body = "";
        }
        else if (!text.StartsWith(Frontmatter.Delimiter + "\n", StringComparison.Ordinal))
        {
            problems.Add((IssueKind.PersonaFrontmatter, "PERSONA.md has no frontmatter block; expected '---' with schema_version, last_distilled and audit_entry"));
            body = text;
        }
        else
        {
            try
            {
                var (scalars, lists, rest) = Frontmatter.ReadBlock(text);
                body = rest;
                (schemaVersion, lastDistilled, auditEntry) = ReadFrontmatter(scalars, lists, problems);
            }
            catch (ValidationException ex)
            {
                problems.Add((IssueKind.PersonaFrontmatter, ex.Message));
                body = "";
            }
        }

        var entries = ParseSections(body, problems);
        return new PersonaDoc
        {
            SchemaVersion = schemaVersion,
            LastDistilled = lastDistilled,
            AuditEntry = auditEntry,
            Entries = entries,
            Problems = problems,
            Body = body,
        };
    }

    private static (string?, string?, string?) ReadFrontmatter(
        Dictionary<string, string> scalars, Dictionary<string, string[]> lists, List<(IssueKind, string)> problems)
    {
        foreach (var key in lists.Keys)
            problems.Add((IssueKind.PersonaFrontmatter, $"frontmatter key '{key}' must be a scalar, not a list"));
        foreach (var key in scalars.Keys)
            if (Array.IndexOf(FrontmatterKeys, key) < 0)
                problems.Add((IssueKind.PersonaFrontmatter, $"unknown frontmatter key '{key}'; PERSONA.md holds exactly schema_version, last_distilled and audit_entry"));

        scalars.TryGetValue(SchemaVersionKey, out var version);
        scalars.TryGetValue(LastDistilledKey, out var distilled);
        scalars.TryGetValue(AuditEntryKey, out var audit);

        if (version is null)
            problems.Add((IssueKind.PersonaFrontmatter, $"missing frontmatter key '{SchemaVersionKey}'"));
        else if (version != CurrentSchemaVersion)
            problems.Add((IssueKind.PersonaFrontmatter, $"unsupported {SchemaVersionKey} '{version}'; expected '{CurrentSchemaVersion}'"));

        if (distilled is null)
            problems.Add((IssueKind.PersonaFrontmatter, $"missing frontmatter key '{LastDistilledKey}'"));
        else if (!DateOnly.TryParseExact(distilled, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.None, out _))
            problems.Add((IssueKind.PersonaFrontmatter, $"{LastDistilledKey} '{distilled}' is not a yyyy-MM-dd date"));

        // The audit-log entry format belongs to the audit log; here it only
        // has to be present. Tightening it is the audit-log ticket's call.
        if (string.IsNullOrWhiteSpace(audit))
            problems.Add((IssueKind.PersonaFrontmatter, $"missing or empty frontmatter key '{AuditEntryKey}'"));

        return (version, distilled, audit);
    }

    private static Dictionary<PersonaSection, IReadOnlyList<string>> ParseSections(string body, List<(IssueKind, string)> problems)
    {
        var entries = new Dictionary<PersonaSection, IReadOnlyList<string>>();
        var seen = new List<PersonaSection>();
        PersonaSection? current = null;
        List<string>? currentLines = null;
        var titleSeen = false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("## ", StringComparison.Ordinal) && !line.StartsWith("###", StringComparison.Ordinal))
            {
                var section = PersonaSectionX.FromHeading(line[3..]);
                if (section is null)
                {
                    problems.Add((IssueKind.PersonaSections, $"unknown section '{line[3..].Trim()}'"));
                    current = null; currentLines = null;
                    continue;
                }
                if (seen.Contains(section.Value))
                {
                    problems.Add((IssueKind.PersonaSections, $"section '{PersonaSectionX.Heading(section.Value)}' appears more than once"));
                    current = null; currentLines = null;
                    continue;
                }
                seen.Add(section.Value);
                current = section.Value;
                currentLines = new List<string>();
                entries[section.Value] = currentLines;
                continue;
            }

            // One optional `# ` title before the first section.
            if (current is null && seen.Count == 0 && !titleSeen
                && line.StartsWith("# ", StringComparison.Ordinal))
            {
                titleSeen = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                problems.Add((IssueKind.PersonaSections, $"unexpected heading '{line}'; PERSONA.md has only the four '## ' sections"));
                continue;
            }

            if (currentLines is null)
            {
                if (seen.Count == 0)
                    problems.Add((IssueKind.PersonaSections, $"content outside any section: '{Truncate(line)}'"));
                continue;
            }

            currentLines.Add(StripBullet(line));
        }

        var missing = new List<string>();
        foreach (var s in PersonaSectionX.InOrder)
            if (!seen.Contains(s)) missing.Add(PersonaSectionX.Heading(s));
        if (missing.Count > 0)
            problems.Add((IssueKind.PersonaSections, $"missing section(s): {string.Join(", ", missing)}"));

        var present = new List<PersonaSection>(seen);
        var expected = new List<PersonaSection>();
        foreach (var s in PersonaSectionX.InOrder)
            if (present.Contains(s)) expected.Add(s);
        for (int i = 0; i < present.Count; i++)
        {
            if (present[i] == expected[i]) continue;
            var order = new List<string>();
            foreach (var s in present) order.Add(PersonaSectionX.Heading(s));
            problems.Add((IssueKind.PersonaSections,
                $"sections out of order ({string.Join(", ", order)}); expected Values, Principles, Tone of voice, Boundaries"));
            break;
        }

        return entries;
    }

    // An entry is one line; a leading list marker is presentation, not
    // content, so it neither counts towards the 200-character limit nor
    // shows up in `persona read`.
    private static string StripBullet(string line)
    {
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            return line[2..].TrimStart();
        return line;
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
