using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Wiki.Core;

namespace Wiki.Services;

// One matching line from `wiki search`. Deliberately narrow: Id/Path/Title
// identify which page the hit came from, Line is the 1-based line number
// within that page's raw file text (frontmatter block counts - line 1 is
// always the opening '---'), MatchLine is the single matching line's text.
// Never the full body - that's the whole point of this being the agent's
// retrieval primitive instead of a "just read the file" shortcut (spec §13).
public sealed record Hit(string Id, string Path, string Title, int Line, string MatchLine);

// `wiki search <terms> [--type] [--limit] [--regex]`: line-by-line scan of
// every page's raw file text (frontmatter + body together, exactly what's on
// disk), matching `terms` against each line. Default mode is a
// case-insensitive substring Contains; --regex treats `terms` as a
// case-insensitive regex instead. Read-only - never opens a file for
// anything but File.ReadAllText, never touches idmap/index/log.
//
// Reuses PageStore.Enumerate (Task 12's helper) for the page set and its
// deterministic ordering rather than re-walking the vault's directories
// itself - PageStore only hands back (Slug, PageFrontmatter), not raw text
// or a path, so the file path is reconstructed the same way
// PageService.Show / ReindexService.RelPathFor already do (type-derived
// directory + slug, overview as the fixed-path singleton). Id and Title come
// straight off the already-parsed frontmatter, no second idmap lookup
// needed.
public sealed class SearchService
{
    public IReadOnlyList<Hit> Search(Vault v, string terms, PageType? type, int limit, bool regex)
    {
        if (limit < 1)
            throw new ValidationException("invalid-limit", $"--limit must be >= 1, got {limit}");

        Regex? compiled = null;
        if (regex)
        {
            try
            {
                compiled = new Regex(terms, RegexOptions.IgnoreCase);
            }
            catch (System.ArgumentException ex)
            {
                throw new ValidationException("bad-regex", $"invalid --regex pattern '{terms}': {ex.Message}");
            }
        }

        var hits = new List<Hit>();

        foreach (var (slug, front) in PageStore.Enumerate(v))
        {
            if (hits.Count >= limit) break;
            if (type is not null && front.Type != type.Value) continue;

            var fullPath = front.Type == PageType.Overview
                ? Path.Combine(v.WikiDir, "overview.md")
                : Path.Combine(v.PageDir(front.Type), slug + ".md");
            var relPath = Path.GetRelativePath(v.Root, fullPath).Replace('\\', '/');

            var text = File.ReadAllText(fullPath).Replace("\r\n", "\n");
            var lines = text.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                if (hits.Count >= limit) break;

                var line = lines[i];
                var isMatch = regex
                    ? compiled!.IsMatch(line)
                    : line.Contains(terms, System.StringComparison.OrdinalIgnoreCase);

                if (isMatch)
                    hits.Add(new Hit(front.Id, relPath, front.Title, i + 1, line));
            }
        }

        return hits;
    }
}
