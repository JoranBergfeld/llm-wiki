using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wiki.Core;

namespace Wiki.Docs;

// Deterministic generator for wiki/index.md, the routing catalog an agent
// walks to find pages. Grouped by PageType in a fixed order, archived pages
// excluded entirely, pending-review pages included but visibly flagged so an
// agent may route to them without citing them (amendment E).
public static class IndexFile
{
    // Em dash (U+2014) - this is spec-defined machine/Obsidian output, not prose.
    private const string Dash = "—";

    // Fixed group order and header text. Deterministic, never reordered by input.
    private static readonly (PageType Type, string Header)[] Groups =
    {
        (PageType.Overview, "Overview"),
        (PageType.Concept, "Concepts"),
        (PageType.Entity, "Entities"),
        (PageType.Summary, "Summaries"),
    };

    // Same selection Render uses (exclude archived, group by type in the
    // fixed order above, sort each group by title then slug) but returning
    // the structured (type, pages) groups instead of markdown text. Task 19's
    // `index show` reuses this so the JSON view and index.md can never drift
    // apart on what counts as "in the index" or what order it's in.
    public static IReadOnlyList<(PageType Type, IReadOnlyList<(string Slug, PageFrontmatter Front)> Pages)> GroupedEntries(
        IEnumerable<(string Slug, PageFrontmatter Front)> pages)
    {
        var all = pages.ToList();
        var result = new List<(PageType, IReadOnlyList<(string Slug, PageFrontmatter Front)>)>();

        foreach (var (type, _) in Groups)
        {
            var group = all
                .Where(p => p.Front.Type == type && p.Front.Status != PageStatus.Archived)
                .OrderBy(p => p.Front.Title, System.StringComparer.Ordinal)
                .ThenBy(p => p.Slug, System.StringComparer.Ordinal)
                .ToList();
            result.Add((type, group));
        }

        return result;
    }

    public static string Render(IEnumerable<(string Slug, PageFrontmatter Front)> pages)
    {
        var all = pages.ToList();
        var sb = new StringBuilder();
        var firstGroup = true;

        foreach (var (type, header) in Groups)
        {
            var group = all
                .Where(p => p.Front.Type == type && p.Front.Status != PageStatus.Archived)
                .OrderBy(p => p.Front.Title, System.StringComparer.Ordinal)
                .ThenBy(p => p.Slug, System.StringComparer.Ordinal)
                .ToList();

            if (group.Count == 0)
                continue;

            if (!firstGroup)
                sb.Append('\n');
            firstGroup = false;

            sb.Append("## ").Append(header).Append('\n');
            foreach (var (slug, front) in group)
            {
                sb.Append("- [[").Append(slug).Append("]] ")
                  .Append(Dash).Append(' ').Append(front.Title).Append(' ')
                  .Append(Dash).Append(' ').Append(front.Summary)
                  .Append(" (sources: ").Append(front.Sources.Length).Append(')');

                if (front.Status == PageStatus.PendingReview)
                    sb.Append(" [pending-review]");

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    public static void Regenerate(Vault v, IEnumerable<(string Slug, PageFrontmatter Front)> pages)
    {
        AtomicFile.Write(v.IndexPath, Render(pages));
    }
}
