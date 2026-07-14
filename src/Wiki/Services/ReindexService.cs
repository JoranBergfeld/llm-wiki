using System.Collections.Generic;
using System.IO;
using Wiki.Cli;
using Wiki.Core;
using Wiki.Docs;
using Wiki.State;

namespace Wiki.Services;

// `wiki reindex` result. M1 scope: idmap + index only - ledger/issues state
// rebuild is deferred to Task 27 (ledger recompute), once ledger state exists
// at all (M2+).
public sealed record ReindexReport(int Pages, int Sources, int IdmapEntries) : IHumanRenderable
{
    public string HumanSummary() => $"Reindexed: {Pages} page(s), {Sources} source(s), {IdmapEntries} idmap entrie(s)";
}

// Rebuilds the derived caches (idmap.json, index.md) from the markdown alone
// - `raw/*.md` source frontmatter and `wiki/**` page frontmatter are the only
// inputs; nothing here reads the existing idmap.json or index.md. This makes
// reindex a recovery tool: delete the caches, run reindex, get them back.
//
// Byte-identity: the fresh IdMap built here is Saved through the same
// IdMap.Save (Task 9) every other write path uses, which always serializes a
// freshly-populated, ordinal-sorted Dictionary<string,string> right before
// writing - so insertion order here (raw sources first, then pages) doesn't
// matter; the on-disk bytes are pinned by key sort order alone.
public sealed class ReindexService
{
    public ReindexReport Rebuild(Vault v)
    {
        var idmap = new IdMap();

        var sourceCount = 0;
        foreach (var (id, relPath) in EnumerateRawSources(v))
        {
            idmap.Put(id, relPath);
            sourceCount++;
        }

        var pages = PageStore.Enumerate(v);
        foreach (var (slug, front) in pages)
        {
            idmap.Put(front.Id, RelPathFor(v, slug, front));
        }

        idmap.Save(v);
        IndexFile.Regenerate(v, pages);

        return new ReindexReport(pages.Count, sourceCount, idmap.All.Count);
    }

    // raw/*.md carry source frontmatter (id, type: source, category, ...).
    // raw/assets/ holds attachments, not sources - TopDirectoryOnly already
    // excludes it since it's a subdirectory, so no explicit skip is needed.
    // Sorted for the same deterministic-enumeration reason PageStore sorts
    // its directory listings.
    private static IEnumerable<(string Id, string RelPath)> EnumerateRawSources(Vault v)
    {
        if (!Directory.Exists(v.RawDir))
            yield break;

        var files = new List<string>(Directory.EnumerateFiles(v.RawDir, "*.md", SearchOption.TopDirectoryOnly));
        files.Sort(System.StringComparer.Ordinal);

        foreach (var file in files)
        {
            var (scalars, lists, _) = Frontmatter.ReadBlock(File.ReadAllText(file));
            var front = SourceFrontmatter.FromRaw(scalars, lists);
            var relPath = Path.GetRelativePath(v.Root, file).Replace('\\', '/');
            yield return (front.Id, relPath);
        }
    }

    // Mirrors PageService.Show's slug->path reconstruction: overview is the
    // fixed-path singleton `wiki/overview.md`, everything else lives at
    // `wiki/<type-dir>/<slug>.md`.
    private static string RelPathFor(Vault v, string slug, PageFrontmatter front)
    {
        var full = front.Type == PageType.Overview
            ? Path.Combine(v.WikiDir, "overview.md")
            : Path.Combine(v.PageDir(front.Type), slug + ".md");
        return Path.GetRelativePath(v.Root, full).Replace('\\', '/');
    }
}
