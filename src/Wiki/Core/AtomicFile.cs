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
public static class AtomicFile
{
    // Note: creates any missing parent directories (mkdir -p) before writing, so
    // callers can write into wiki/summaries etc. before those dirs exist on disk.
    public static void Write(string path, string content)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        var tmp = path + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllText(tmp, content);
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { System.IO.File.Delete(tmp); } catch { }
            throw;
        }
    }
}
