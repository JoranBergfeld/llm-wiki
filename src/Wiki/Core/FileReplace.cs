using System;
using System.IO;

namespace Wiki.Core;

// The one temp-file-then-rename sequence: write the bytes to a temp file
// beside the target, optionally flush them to disk, then move it over the
// target, deleting the temp file if anything fails. The target is never seen
// half written.
//
// NOT journaled, deliberately: it is the mechanism under AtomicFile.Write
// (which journals before calling it), under write recovery putting a
// before-image back (JournalFile), and under the MCP inbox landing an upload
// outside the vault (IngestInbox). A command changing a vault file goes
// through AtomicFile, never through this directly (CONTRIBUTING.md).
//
// `tempPrefix` goes in front of the temp file's name: the inbox uses "." so a
// concurrent `source scan`, which skips dot files, never registers a
// half-written upload. `flushToDisk` makes the content durable before the
// rename, for callers that order writes for crash safety.
internal static class FileReplace
{
    public static void Write(string path, byte[] content, string tempPrefix = "", bool flushToDisk = true)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir ?? "", tempPrefix + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(content, 0, content.Length);
                if (flushToDisk)
                    fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
