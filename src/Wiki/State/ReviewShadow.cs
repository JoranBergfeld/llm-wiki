using System.IO;
using Wiki.Core;

namespace Wiki.State;

// Shadow-copy store for the review gate (spec §15) - the ONE place the CLI
// keeps a shadow copy of page content. When `review_gate` is on, the page as
// it stood BEFORE the first un-reviewed edit is stashed here
// (PageService.Update, before it overwrites the page file) so `wiki review
// list` can render a diff against the new body and `wiki review reject` can
// restore it. A CREATE never has one (no prior version exists), which is
// exactly how ReviewService tells "reject this create -> archive" apart from
// "reject this update -> restore".
//
// Capture rule (amendment K): SaveIfAbsent writes ONLY when no shadow exists.
// The old Save-on-every-update walked the shadow forward through consecutive
// un-reviewed edits, so `reject` restored an intermediate revision nobody had
// approved - the exact outcome the gate exists to prevent. approve/reject
// Clear() the shadow, which re-arms the capture: the next update stashes the
// body that was just reviewed.
//
// Storage format: the FULL previous document (frontmatter + body) serialized
// exactly as the page file itself is, not the bare body. `reject` has to
// restore the page's pre-gate STATUS as well as its text (amendment K's
// second half - rejecting an edit to a `needs-review` page must not clear the
// flag), and the frontmatter is where that status already lives. Reading it
// back is PageDoc.Parse, no bespoke format.
//
// Purely derived/historical state (amendment A): `wiki reindex` never reads
// or rebuilds this directory.
public static class ReviewShadow
{
    // What a shadow restores: the previous body, plus the status the page
    // held before the gate captured it. PreviousStatus is null for a
    // body-only shadow written by a build predating amendment K - callers
    // fall back to `active`, the pre-amendment behaviour, rather than failing
    // a reject on a vault that has been through an upgrade mid-review.
    public sealed record Snapshot(string Body, PageStatus? PreviousStatus);

    public static string PathFor(Vault v, string pageId)
        => Path.Combine(v.StateDir, "review", pageId + ".prev.md");

    public static bool Exists(Vault v, string pageId) => File.Exists(PathFor(v, pageId));

    // Returns true when this call actually captured the snapshot, false when
    // one was already present and was left untouched.
    public static bool SaveIfAbsent(Vault v, string pageId, PageDoc previous)
    {
        var path = PathFor(v, pageId);
        if (File.Exists(path))
            return false;

        AtomicFile.Write(path, previous.Serialize());
        return true;
    }

    public static Snapshot? Load(Vault v, string pageId)
    {
        var path = PathFor(v, pageId);
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path);

        // Post-amendment-K shadows are a full page document; pre-amendment
        // ones are a bare body that PageDoc.Parse rejects for want of a
        // frontmatter block. Fall back to treating the whole file as the body.
        try
        {
            var doc = PageDoc.Parse(text);
            return new Snapshot(doc.Body, doc.Front.Status);
        }
        catch (ValidationException)
        {
            return new Snapshot(text, null);
        }
    }

    public static void Clear(Vault v, string pageId)
    {
        var path = PathFor(v, pageId);
        if (File.Exists(path))
            File.Delete(path);
    }
}
