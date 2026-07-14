using System.IO;
using Wiki.Core;

namespace Wiki.State;

// Shadow-copy store for the review gate (spec §15) - the ONE place the CLI
// keeps a shadow copy of page content. When `review_gate` is on, an UPDATE's
// pre-write body is stashed here (PageService.Update, before it overwrites
// the page file) so `wiki review list` can render a diff against the new
// body and `wiki review reject` can restore it. A CREATE never has one (no
// prior version exists), which is exactly how ReviewService tells "reject
// this create -> archive" apart from "reject this update -> restore".
//
// Purely derived/historical state (amendment A): `wiki reindex` never reads
// or rebuilds this directory, and it holds only the raw markdown BODY (not
// full frontmatter+body) - the gate cares about content revision, not about
// resurrecting a stale status/summary/sources/tags alongside it.
public static class ReviewShadow
{
    public static string PathFor(Vault v, string pageId)
        => Path.Combine(v.StateDir, "review", pageId + ".prev.md");

    public static void Save(Vault v, string pageId, string previousBody)
        => AtomicFile.Write(PathFor(v, pageId), previousBody);

    public static string? Load(Vault v, string pageId)
    {
        var path = PathFor(v, pageId);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public static void Clear(Vault v, string pageId)
    {
        var path = PathFor(v, pageId);
        if (File.Exists(path))
            File.Delete(path);
    }
}
