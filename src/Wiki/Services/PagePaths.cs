using System.IO;
using Wiki.Core;

namespace Wiki.Services;

// Where a page's file lives, derived from its (slug, frontmatter) pair. The
// `overview` singleton sits at the fixed path `wiki/overview.md`; every other
// page is `wiki/<type-dir>/<slug>.md`.
//
// This reconstruction had four independent copies - PageService.FullPathFor,
// LintService.FullPathFor/RelPathFor, ReindexService.RelPathFor, and an inline
// pair in SearchService - each carrying the same overview carve-out comment.
// One divergence in any of them would have put a page's index entry, its idmap
// path and the file actually on disk out of agreement, which is exactly the
// drift lint's rename-drift check exists to catch.
public static class PagePaths
{
    public static string Full(Vault v, string slug, PageFrontmatter front)
        => front.Type == PageType.Overview
            ? Path.Combine(v.WikiDir, "overview.md")
            : Path.Combine(v.PageDir(front.Type), slug + ".md");

    // Forward-slashed and vault-relative: the form idmap.json stores and the
    // JSON envelope reports, stable across platforms (spec §16).
    public static string Relative(Vault v, string slug, PageFrontmatter front)
        => v.RelativePath(Full(v, slug, front));
}
