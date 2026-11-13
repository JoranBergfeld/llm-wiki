namespace Wiki.Core;

// Per-file crash safety: Write() never leaves a partially-written file at the
// target path. Cross-file atomicity (e.g. writing a page + updating the log)
// is explicitly NOT provided here - callers that need that coordinate it themselves.
//
// Spec §11.5's "no write path under raw/ except source add; no edit to
// index.md/log.md except by the CLI" is enforced STRUCTURALLY, not by a runtime
// guard: no command accepts a write path from the user at all. Every target is
// derived internally from a page's type and slug, a source's id, or a fixed
// well-known path. There is no input that could name a protected file.
//
// A GuardWritable() policy check used to live here for that rule. It had no
// callers in production - only tests - so it read as protection that wasn't
// actually in the request path. Deleted rather than left in place: dead
// safety code is worse than none, because it invites the assumption that
// something is being checked. If a command ever does take a caller-supplied
// write path, reintroduce the guard AND call it.
//
// Write recovery (issue #47): this class and LogFile.Append are the ONLY ways
// a command changes a vault file. Each tells the ambient WriteJournal before
// the change, so the journal holds the file's before-image on disk before the
// file is touched. A new write path that bypasses them is a change recovery
// cannot see; route it through here instead.
public static class AtomicFile
{
    // Note: creates any missing parent directories (mkdir -p) before writing, so
    // callers can write into wiki/summaries etc. before those dirs exist on disk.
    // Text is UTF-8 without a BOM. Not flushed before the rename: the write
    // journal flushes every touched file when the command commits (#47).
    public static void Write(string path, string content)
    {
        CreateParent(path);
        WriteJournal.BeforeChange(path, append: false);
        FileReplace.Write(path, Utf8NoBom.GetBytes(content), flushToDisk: false);
        WriteJournal.AfterChange();
    }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static void CreateParent(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
    }

    // Binary content (an archived audit segment, issue #45). Unlike the text
    // overload this flushes the temp file to disk before the rename: a caller
    // that orders writes for crash safety (segment before the manifest that
    // names it) needs the content durable, not only renamed into place.
    public static void Write(string path, byte[] content)
    {
        CreateParent(path);
        WriteJournal.BeforeChange(path, append: false);
        FileReplace.Write(path, content, flushToDisk: true);
        WriteJournal.AfterChange();
    }

    // Appends to a vault file, creating it (and its directory) when absent.
    // For append-only files whose existing bytes must never be rewritten
    // (interview transcripts, issue #53). Journaled as an append: undoing it
    // is a truncate back to the old length, or a delete for a new file.
    public static void Append(string path, string content)
    {
        CreateParent(path);
        WriteJournal.BeforeChange(path, append: true);
        System.IO.File.AppendAllText(path, content);
        WriteJournal.AfterChange();
    }

    // Deletes a vault file (a renamed page's old path, a cleared review
    // shadow). Journaled like Write, so an interrupted delete can be undone.
    public static void Delete(string path)
    {
        if (!System.IO.File.Exists(path))
            return;
        WriteJournal.BeforeChange(path, append: false);
        System.IO.File.Delete(path);
        WriteJournal.AfterChange();
    }
}
