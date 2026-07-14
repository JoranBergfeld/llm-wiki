using System.Collections.Generic;
using System.IO;
using Wiki.Core;

namespace Wiki.Services;

// Reusable page-enumeration helper: scans the vault's page directories (and
// the overview singleton), parsing each file's frontmatter into a
// (Slug, Front) pair. Read-only - never writes anything, so it's always safe
// to call before blocking validation has finished. PageService (Task 12)
// uses this for slug-collision checks, duplicate-title checks, dangling-link
// resolution, and index regeneration; Tasks 14/15/19/22 reuse it for the
// same "give me every page in the vault" need.
//
// EnumerateWithBody (Task 19) is the same walk, just also keeping the body
// PageDoc.Parse already parsed off disk - no extra file read. Backlinks/
// orphan detection need every page's body to run Wikilinks.Extract over,
// which Enumerate's (Slug, Front) shape can't carry; a second directory scan
// re-reading the same files would be wasted I/O, so both public methods
// share one internal walk (EnumerateFull) that captures the body once and
// Enumerate simply drops it.
public static class PageStore
{
    public static IReadOnlyList<(string Slug, PageFrontmatter Front)> Enumerate(Vault v)
    {
        var full = EnumerateFull(v);
        var pages = new List<(string Slug, PageFrontmatter Front)>(full.Count);
        foreach (var p in full)
            pages.Add((p.Slug, p.Front));
        return pages;
    }

    public static IReadOnlyList<(string Slug, PageFrontmatter Front, string Body)> EnumerateWithBody(Vault v)
        => EnumerateFull(v);

    private static List<(string Slug, PageFrontmatter Front, string Body)> EnumerateFull(Vault v)
    {
        var pages = new List<(string Slug, PageFrontmatter Front, string Body)>();

        AddDirectory(v.PageDir(PageType.Summary), pages);
        AddDirectory(v.PageDir(PageType.Entity), pages);
        AddDirectory(v.PageDir(PageType.Concept), pages);

        // Overview is the singleton `wiki/overview.md` - not a directory of
        // slugged files, so it's handled as a single well-known path rather
        // than folded into AddDirectory (which would also have to special-case
        // excluding index.md/log.md if pointed at WikiDir itself).
        var overviewPath = Path.Combine(v.WikiDir, "overview.md");
        if (File.Exists(overviewPath))
        {
            var doc = PageDoc.Parse(File.ReadAllText(overviewPath));
            pages.Add(("overview", doc.Front, doc.Body));
        }

        return pages;
    }

    private static void AddDirectory(string dir, List<(string Slug, PageFrontmatter Front, string Body)> pages)
    {
        if (!Directory.Exists(dir))
            return;

        // Sorted for deterministic enumeration order across filesystems -
        // callers that care about order (e.g. reproducible reindex) shouldn't
        // have to depend on OS directory-listing order.
        var files = new List<string>(Directory.EnumerateFiles(dir, "*.md"));
        files.Sort(System.StringComparer.Ordinal);

        foreach (var file in files)
        {
            var slug = Path.GetFileNameWithoutExtension(file);
            var doc = PageDoc.Parse(File.ReadAllText(file));
            pages.Add((slug, doc.Front, doc.Body));
        }
    }
}
