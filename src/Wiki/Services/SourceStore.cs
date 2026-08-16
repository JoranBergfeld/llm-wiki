using System.Collections.Generic;
using System.IO;
using Wiki.Core;

namespace Wiki.Services;

// Reusable raw-source enumeration helper - the `raw/` counterpart to
// PageStore. Scans `raw/*.md` and parses each file's SOURCE frontmatter.
//
// TopDirectoryOnly: `raw/assets/` holds Obsidian attachments, not sources, and
// being a subdirectory it is excluded without an explicit skip. Sorted
// ordinally for the same deterministic-enumeration reason PageStore sorts.
//
// Extracted because three callers had grown their own copy of this walk -
// SourceService.EnumerateSources, SourceService.FindExistingSourceIdBySha, and
// ReindexService.EnumerateRawSources - and amendment N's category-in-use check
// would have made a fourth.
public static class SourceStore
{
    // `skipUnparseable: false` (the default) is the strict read every
    // correctness-critical caller wants: sha256 dedup must see every
    // registered source, and `wiki reindex` must not quietly drop one from
    // the rebuilt idmap. A malformed file under raw/ is a genuine problem
    // there and should surface loudly.
    //
    // `skipUnparseable: true` is for advisory passes that merely inspect the
    // source set - amendment N's category cross-check. That check runs on
    // every config-reading command, so exploding on a stray `.md` a human
    // dropped into raw/ would brick the whole CLI with a frontmatter error
    // that has nothing to do with what they were trying to do. Files that
    // aren't parseable sources simply aren't sources for its purposes.
    public static IReadOnlyList<(SourceFrontmatter Front, string FullPath)> Enumerate(
        Vault v, bool skipUnparseable = false)
    {
        var result = new List<(SourceFrontmatter, string)>();
        if (!Directory.Exists(v.RawDir))
            return result;

        var files = new List<string>(Directory.EnumerateFiles(v.RawDir, "*.md", SearchOption.TopDirectoryOnly));
        files.Sort(System.StringComparer.Ordinal);

        foreach (var file in files)
        {
            SourceFrontmatter front;
            try
            {
                var (scalars, lists, _) = Frontmatter.ReadBlock(File.ReadAllText(file));
                front = SourceFrontmatter.FromRaw(scalars, lists);
            }
            catch (ValidationException) when (skipUnparseable)
            {
                continue;
            }
            result.Add((front, file));
        }
        return result;
    }
}
