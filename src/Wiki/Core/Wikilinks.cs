using System.Text.RegularExpressions;

namespace Wiki.Core;

public record Link(string Target, string? Display);

public static class Wikilinks
{
    private static readonly Regex LinkPattern = new(@"\[\[([^\]|]+)(\|[^\]]+)?\]\]", RegexOptions.Compiled);

    public static IReadOnlyList<Link> Extract(string body)
    {
        var links = new List<Link>();
        var inFence = false;
        foreach (var line in SplitLines(body))
        {
            if (line.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            foreach (Match match in LinkPattern.Matches(line))
            {
                var target = match.Groups[1].Value;
                var display = match.Groups[2].Success ? match.Groups[2].Value[1..] : null;
                links.Add(new Link(target, display));
            }
        }
        return links;
    }

    public static string Rewrite(string body, string oldSlug, string newSlug)
    {
        var lines = SplitLines(body).ToList();
        var inFence = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            lines[i] = LinkPattern.Replace(line, match =>
            {
                var target = match.Groups[1].Value;
                if (target != oldSlug) return match.Value;
                var displaySuffix = match.Groups[2].Success ? match.Groups[2].Value : "";
                return $"[[{newSlug}{displaySuffix}]]";
            });
        }
        return string.Join("\n", lines);
    }

    private static string[] SplitLines(string body) => body.Split('\n');
}
