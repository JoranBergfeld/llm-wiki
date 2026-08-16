using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Wiki.Cli;
using Wiki.Core;

namespace Wiki.Services;

// What a hit came from (amendment O). The agent's retrieval playbook routes
// differently for the two - a page hit is followed by `wiki page show`, a
// source hit by `wiki source show` - so the caller must not have to infer it
// from the path prefix.
public enum SearchKind { Page, Source }

public static class SearchKindX
{
    public static string ToWire(SearchKind k) => k switch
    {
        SearchKind.Page => "page",
        SearchKind.Source => "source",
        _ => throw new ValidationException("invalid-kind", $"unknown SearchKind '{k}'"),
    };

    public static SearchKind Parse(string wire) => wire switch
    {
        "page" => SearchKind.Page,
        "source" => SearchKind.Source,
        _ => throw new ValidationException("invalid-kind", $"unknown kind '{wire}'; expected 'page' or 'source'"),
    };
}

// One matching line from `wiki search`. Deliberately narrow: Kind/Id/Path/
// Title identify what the hit came from, Line is the 1-based line number
// within that file's raw text (frontmatter block counts - line 1 is always
// the opening '---'), MatchLine is the single matching line's text. Never the
// full body - that's the whole point of this being the agent's retrieval
// primitive instead of a "just read the file" shortcut (spec §13).
public sealed record Hit(string Kind, string Id, string Path, string Title, int Line, string MatchLine);

// `wiki search`'s result (amendment O). A bare hits array left a
// `--limit`-truncated result indistinguishable from an exhaustive one, so the
// agent could not tell "these are all the mentions" from "these are the first
// N". `Scanned` reports how many files were opened before the scan stopped,
// which makes a truncated result interpretable rather than just flagged.
public sealed record SearchReport(Hit[] Hits, bool Truncated, int Scanned) : IHumanRenderable
{
    public string HumanSummary()
        => Truncated
            ? $"{Hits.Length} hit(s) (truncated at --limit; {Scanned} file(s) scanned)"
            : $"{Hits.Length} hit(s) across {Scanned} file(s)";
}

// `wiki search <terms> [--type] [--kind] [--limit] [--regex]`: line-by-line
// scan of every wiki page AND every raw source, matching `terms` against each
// line of the file's raw text (frontmatter + body together, exactly what's on
// disk). Default mode is a case-insensitive substring Contains; --regex
// treats `terms` as a case-insensitive regex instead. Read-only - never opens
// a file for anything but File.ReadAllText, never touches idmap/index/log.
//
// Pages come from PageStore, sources from SourceStore, both already
// deterministically ordered; pages are scanned first so a mixed result reads
// wiki-first, which is the order the retrieval playbook wants (synthesized
// knowledge before raw material).
public sealed class SearchService
{
    public SearchReport Search(Vault v, string terms, PageType? type, SearchKind? kind, int limit, bool regex)
    {
        if (limit < 1)
            throw new ValidationException("invalid-limit", $"--limit must be >= 1, got {limit}");

        // --type names a PAGE type, so it implies --kind page. Combining it
        // with --kind source asks for source hits filtered by a page type,
        // which is not a thing - reject rather than silently returning
        // nothing, which would read as "no matches".
        if (type is not null && kind == SearchKind.Source)
            throw new ValidationException("kind-type-conflict",
                "--type filters page types and so implies --kind page; it cannot be combined with --kind source");

        var effectiveKind = type is not null ? SearchKind.Page : kind;

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
        var scanned = 0;
        var truncated = false;

        bool IsMatch(string line) => compiled is not null
            ? compiled.IsMatch(line)
            : line.Contains(terms, System.StringComparison.OrdinalIgnoreCase);

        // Scans one file's raw text. Returns false once the limit is hit, so
        // the caller stops opening files. `truncated` is set only when a
        // match had to be DROPPED - reaching the limit on the very last match
        // in the vault is a complete result, not a truncated one.
        bool ScanFile(string fullPath, SearchKind hitKind, string id, string title)
        {
            var relPath = Path.GetRelativePath(v.Root, fullPath).Replace('\\', '/');
            var lines = File.ReadAllText(fullPath).Replace("\r\n", "\n").Split('\n');
            scanned++;

            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsMatch(lines[i]))
                    continue;

                if (hits.Count >= limit)
                {
                    truncated = true;
                    return false;
                }

                hits.Add(new Hit(SearchKindX.ToWire(hitKind), id, relPath, title, i + 1, lines[i]));
            }
            return true;
        }

        if (effectiveKind != SearchKind.Source)
        {
            foreach (var (slug, front) in PageStore.Enumerate(v))
            {
                if (type is not null && front.Type != type.Value) continue;

                if (!ScanFile(PagePaths.Full(v, slug, front), SearchKind.Page, front.Id, front.Title))
                    return new SearchReport(hits.ToArray(), truncated, scanned);
            }
        }

        if (effectiveKind != SearchKind.Page)
        {
            // Strict enumeration: a raw file that doesn't parse as a source is
            // a real problem the operator should see, and search is a
            // read-only diagnostic where surfacing it costs nothing.
            foreach (var (front, fullPath) in SourceStore.Enumerate(v))
            {
                if (!ScanFile(fullPath, SearchKind.Source, front.Id, front.Title))
                    return new SearchReport(hits.ToArray(), truncated, scanned);
            }
        }

        return new SearchReport(hits.ToArray(), truncated, scanned);
    }
}
