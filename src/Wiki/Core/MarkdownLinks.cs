using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Wiki.Core;

// One standard markdown link found in a page body.
public sealed record MarkdownLink(string Text, string Url);

// Extracts standard `[text](url)` markdown links (issue #2). The `[[wikilink]]`
// counterpart lives in Wikilinks and is a completely separate namespace: a
// wikilink resolves against page slugs and is checked deterministically at
// write time, an external URL resolves against the internet and is checked
// only by an explicit, opt-in command.
//
// Same skip discipline as Wikilinks.Extract: fenced code blocks are ignored,
// so a URL in an example config or shell snippet is never probed.
//
// The pattern deliberately does NOT try to be a markdown parser. It matches
// `[...](...)` where the target contains no whitespace or closing paren, which
// covers every link an agent actually writes and cleanly declines the exotic
// cases (angle-bracket targets, titles after the URL, nested parens in a
// Wikipedia URL). Declining to probe a link is a non-event - the whole
// command is advisory - whereas mis-parsing one and reporting a phantom
// broken URL is a false positive in the issue queue.
public static class MarkdownLinks
{
    private static readonly Regex LinkPattern = new(@"\[([^\]]*)\]\(([^)\s]+)\)", RegexOptions.Compiled);

    // Wikilinks are `[[target]]`, so `[[a]](b)` would otherwise match here
    // too. Strip wikilink spans first - they have their own checker.
    private static readonly Regex WikilinkSpan = new(@"\[\[[^\]]*\]\]", RegexOptions.Compiled);

    public static IReadOnlyList<MarkdownLink> Extract(string body)
    {
        var links = new List<MarkdownLink>();
        var inFence = false;

        foreach (var rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            var line = WikilinkSpan.Replace(rawLine, " ");
            foreach (Match m in LinkPattern.Matches(line))
                links.Add(new MarkdownLink(m.Groups[1].Value, m.Groups[2].Value));
        }

        return links;
    }

    // Only http/https are probeable. `mailto:`, `#anchor`, and relative paths
    // are links the CLI has no business resolving, and reporting them as
    // unverifiable noise would train the reader to ignore the report.
    public static bool IsProbeable(string url)
        => url.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase);
}
