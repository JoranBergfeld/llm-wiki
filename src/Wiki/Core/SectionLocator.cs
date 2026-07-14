using System.Collections.Generic;

namespace Wiki.Core;

// The core mechanism behind amendment C (spec Appendix B): locate a named
// markdown section in AGENTS.md and replace its BODY verbatim, without a
// diff/patch engine. A "section" is a `##` or `###` ATX heading line plus
// everything up to (but not including) the next heading of EQUAL-OR-HIGHER
// level, or EOF.
//
// Match rule: the heading TEXT after the leading `#`s, trimmed of
// surrounding whitespace, compared to the caller's `--section` value with an
// exact (ordinal, case-sensitive) match. Only level-2 (`##`) and level-3
// (`###`) headings are eligible section anchors - the spec explicitly scopes
// amendment C to "the named `##`/`###` section"; matching against the
// document's single `#` title would let a proposal accidentally replace the
// whole file's opening paragraph, which is never what "amend one section"
// means here.
//
// Equal-or-higher-level stop: for a `##` section the walk stops at the next
// `##` or `#`; a nested `###` subsection does NOT end it - the whole
// subsection tree belongs to its parent's body. For a `###` section the walk
// stops at the next `###`, `##`, or `#` (a sibling or an ancestor), so
// replacing one `###` subsection never bleeds into the next one.
public static class SectionLocator
{
    public static void EnsureExists(string agentsText, string headingText)
    {
        if (!TryFind(agentsText, headingText, out _, out _, out _, out _))
            throw NotFound(headingText);
    }

    // Splits `agentsText` on the named section's body and returns the full
    // file with that body swapped for `newBody`, keeping the heading line
    // itself and every other line untouched. `newBody` is normalized to `\n`
    // line endings and re-joined with a single trailing newline, matching
    // every other file this codebase writes (see AtomicFile callers).
    public static string Replace(string agentsText, string headingText, string newBody)
    {
        if (!TryFind(agentsText, headingText, out var lines, out var bodyStart, out var bodyEnd, out _))
            throw NotFound(headingText);

        var newBodyLines = SplitLines(newBody);

        var result = new List<string>(bodyStart + newBodyLines.Count + (lines.Count - bodyEnd));
        for (var i = 0; i < bodyStart; i++)
            result.Add(lines[i]);
        result.AddRange(newBodyLines);
        for (var i = bodyEnd; i < lines.Count; i++)
            result.Add(lines[i]);

        return string.Join("\n", result).TrimEnd('\n') + "\n";
    }

    private static ValidationException NotFound(string headingText)
        => new("unknown-section", $"no '##' or '###' section heading matching '{headingText}' found in AGENTS.md");

    // `lines`: the whole file split on `\n`. `bodyStart`/`bodyEnd`: the
    // half-open line range that is the section's body (heading line
    // excluded). `headingLevel`: 2 or 3, out for callers that want it.
    private static bool TryFind(string agentsText, string headingText, out List<string> lines, out int bodyStart, out int bodyEnd, out int headingLevel)
    {
        lines = SplitLines(agentsText);
        bodyStart = -1;
        bodyEnd = -1;
        headingLevel = -1;

        var target = headingText.Trim();
        var headingLine = -1;
        var matchCount = 0;

        // Fail CLOSED on ambiguity. AGENTS.md is hand/agent-editable (not a
        // CLI-only artifact), so it can legitimately end up with two `###
        // Ingest` headings. Breaking on the first match would let `schema
        // approve` silently overwrite one of them and return exit 0 - the
        // exact "wrong section silently mutated" failure this locator exists
        // to prevent. So scan ALL lines (no early break), count eligible
        // matches, and throw `ambiguous-section` on 2+. This lives in the
        // shared locate path so BOTH propose (early reject) and approve
        // (re-check, since the file may have gained a duplicate since propose)
        // fail closed.
        for (var i = 0; i < lines.Count; i++)
        {
            var (level, text) = ParseHeading(lines[i]);
            if ((level == 2 || level == 3) && text == target)
            {
                matchCount++;
                if (headingLine < 0)
                {
                    headingLine = i;
                    headingLevel = level;
                }
            }
        }

        if (matchCount > 1)
            throw new ValidationException("ambiguous-section",
                $"{matchCount} '##'/'###' section headings match '{target}' in AGENTS.md; " +
                "resolve the duplicate heading before proposing/approving an amendment to it");

        if (headingLine < 0)
            return false;

        bodyStart = headingLine + 1;
        bodyEnd = lines.Count;
        for (var j = bodyStart; j < lines.Count; j++)
        {
            var (level, _) = ParseHeading(lines[j]);
            if (level > 0 && level <= headingLevel)
            {
                bodyEnd = j;
                break;
            }
        }

        return true;
    }

    // Returns (0, "") for a non-heading line. A valid ATX heading is 1-6
    // leading `#` characters followed by whitespace (or end of line, for a
    // bare "##" with no text) - the CommonMark rule, minus the "not inside a
    // code fence" nuance, which AGENTS.md's own template never triggers and
    // which the spec's amendment C write-up doesn't ask this locator to
    // handle.
    private static (int Level, string Text) ParseHeading(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == '#')
            i++;

        if (i is 0 or > 6)
            return (0, "");
        if (i < line.Length && line[i] != ' ' && line[i] != '\t')
            return (0, "");

        return (i, line[i..].Trim());
    }

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.EndsWith("\n"))
            normalized = normalized[..^1];
        return new List<string>(normalized.Split('\n'));
    }
}
