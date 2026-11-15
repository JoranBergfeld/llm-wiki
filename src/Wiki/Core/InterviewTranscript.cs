using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wiki.Core;

// An interview transcript (issue #53, decided in #40): the word-for-word
// record of one onboarding interview session, at
// `persona/interviews/<date>[-vN].md`. Not a knowledge page: no page schema,
// outside wiki/, so search, the index and reindex never see it. It reads as
// plain Markdown in Obsidian.
//
//   ---
//   transcript: 2026-11-13
//   persona: candidate              <- removed when a distillation pass considers it
//   ---
//   # Interview transcript 2026-11-13
//   ...
//   ## a1
//
//   - section: values
//   - guide: 1                      <- shipped guide version, or sha256:<hex> of the vault override
//   - recorded: 2026-11-13T10:00:00Z
//   - recordedVia: cli              <- or mcp, then `transport:` and (http) `token:`
//
//   **Question**
//
//   > What do you care about most?
//
//   **Answer**
//
//   > The answer, every line prefixed with "> " (an empty line is ">").
//
// Text is stored as a blockquote so nothing an owner says - a `## ` line, a
// `---`, a code fence - can be mistaken for transcript structure, and it
// decodes back to exactly the string that was recorded. The CLI only ever
// appends entries; the one edit it makes is dropping the `persona:
// candidate` line when a pass considers the transcript, after which the
// transcript is finished and the CLI never touches it again.
public sealed class InterviewTranscript
{
    public const string Dir = "persona/interviews";
    public const string CandidateLine = "persona: candidate";

    private static readonly Regex IdPattern = new(@"^\d{4}-\d{2}-\d{2}(-v([2-9]|[1-9]\d+))?$", RegexOptions.CultureInvariant);
    private static readonly Regex AnchorHeading = new(@"^## (a[1-9]\d*)$", RegexOptions.CultureInvariant);

    public required string Id { get; init; }
    public bool Candidate { get; init; }
    public required IReadOnlyList<InterviewEntry> Entries { get; init; }

    public static bool IsId(string s) => IdPattern.IsMatch(s);

    public static string RelPath(string id) => $"{Dir}/{id}.md";

    // The id for the n-th transcript of `date` (n = 1 is the bare date).
    public static string IdFor(string date, int n)
        => n == 1 ? date : date + "-v" + n.ToString(CultureInfo.InvariantCulture);

    // The hash recorded for owner-stated evidence: SHA-256 of the answer
    // exactly as recorded, line endings normalised to LF like page evidence.
    public static string AnswerHash(string answer) => Audit.AuditLine.Sha256Hex(answer.Replace("\r\n", "\n"));

    public static string Header(string id)
        => "---\n"
           + "transcript: " + id + "\n"
           + CandidateLine + "\n"
           + "---\n"
           + "# Interview transcript " + id + "\n\n"
           + "Recorded word for word by `wiki persona interview record`. Every answer is owner-stated\n"
           + "evidence for PERSONA.md: editing an answer makes the persona lines resting on it stale,\n"
           + "and deleting this file retracts everything in it.\n";

    // One entry, as appended to the file.
    public static string FormatEntry(InterviewEntry e)
    {
        var sb = new StringBuilder();
        sb.Append("\n## ").Append(e.Anchor).Append("\n\n");
        sb.Append("- section: ").Append(e.Section).Append('\n');
        sb.Append("- guide: ").Append(e.Guide).Append('\n');
        sb.Append("- recorded: ").Append(e.Recorded).Append('\n');
        sb.Append("- recordedVia: ").Append(e.RecordedVia).Append('\n');
        if (e.Transport is not null)
            sb.Append("- transport: ").Append(e.Transport).Append('\n');
        if (e.Token is not null)
            sb.Append("- token: ").Append(e.Token.Value ? "true" : "false").Append('\n');
        if (e.Question is not null)
            sb.Append("\n**Question**\n\n").Append(Quote(e.Question)).Append('\n');
        sb.Append("\n**Answer**\n\n").Append(Quote(e.Answer)).Append('\n');
        return sb.ToString();
    }

    private static string Quote(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].Length == 0 ? ">" : "> " + lines[i];
        return string.Join("\n", lines);
    }

    // Lenient: a hand-edited transcript keeps whatever entries still parse.
    public static InterviewTranscript Parse(string id, string text)
    {
        var lines = text.Split('\n');
        var i = 0;
        var candidate = false;

        if (lines.Length > 0 && lines[0].TrimEnd('\r') == "---")
        {
            for (i = 1; i < lines.Length; i++)
            {
                var l = lines[i].TrimEnd('\r');
                if (l == "---") { i++; break; }
                if (l.Trim() == CandidateLine)
                    candidate = true;
            }
        }

        var entries = new List<InterviewEntry>();
        while (i < lines.Length)
        {
            var m = AnchorHeading.Match(lines[i].TrimEnd('\r'));
            if (!m.Success) { i++; continue; }
            var anchor = m.Groups[1].Value;
            i++;

            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            string? question = null, answer = null;
            while (i < lines.Length && !AnchorHeading.IsMatch(lines[i].TrimEnd('\r')))
            {
                var l = lines[i].TrimEnd('\r');
                if (l.StartsWith("- ", StringComparison.Ordinal) && l.IndexOf(':') > 2 && question is null && answer is null)
                {
                    var colon = l.IndexOf(':');
                    meta[l[2..colon].Trim()] = l[(colon + 1)..].Trim();
                    i++;
                }
                else if (l == "**Question**")
                    question = Unquote(lines, ref i);
                else if (l == "**Answer**")
                    answer = Unquote(lines, ref i);
                else
                    i++;
            }

            if (answer is null)
                continue;
            entries.Add(new InterviewEntry(
                anchor,
                meta.GetValueOrDefault("section") ?? "",
                question,
                answer,
                meta.GetValueOrDefault("guide") ?? "",
                meta.GetValueOrDefault("recorded") ?? "",
                meta.GetValueOrDefault("recordedVia") ?? "",
                meta.GetValueOrDefault("transport"),
                meta.TryGetValue("token", out var t) ? t == "true" : null));
        }

        return new InterviewTranscript { Id = id, Candidate = candidate, Entries = entries };
    }

    // From the marker line at `i`: skips blank lines, then decodes the
    // blockquote that follows. Leaves `i` on the first line after it.
    private static string? Unquote(string[] lines, ref int i)
    {
        i++;
        while (i < lines.Length && lines[i].TrimEnd('\r').Length == 0)
            i++;
        var parts = new List<string>();
        while (i < lines.Length && lines[i].StartsWith('>'))
        {
            var l = lines[i];
            parts.Add(l.StartsWith("> ", StringComparison.Ordinal) ? l[2..] : l[1..]);
            i++;
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    // The file text with the candidate flag dropped and every other byte
    // kept, or null when it carries no flag.
    public static string? WithoutCandidateFlag(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal))
            return null;
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return null;
        var at = text.IndexOf("\n" + CandidateLine, 0, end + 1, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var lineEnd = text.IndexOf('\n', at + 1);
        if (text[(at + 1)..lineEnd].TrimEnd('\r') != CandidateLine)
            return null;
        return text[..at] + text[lineEnd..];
    }
}

// One question and answer. `Question` is null for a dictated line. `Guide`
// is the shipped guide version or `sha256:<hex>` of the vault's override.
public sealed record InterviewEntry(
    string Anchor,
    string Section,
    string? Question,
    string Answer,
    string Guide,
    string Recorded,
    string RecordedVia,
    string? Transport,
    bool? Token);
