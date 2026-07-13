namespace Wiki.Core;

// Per-file crash safety: Write() never leaves a partially-written file at the
// target path. Cross-file atomicity (e.g. writing a page + updating the log)
// is explicitly NOT provided here - callers that need that coordinate it themselves.
//
// GuardWritable is a policy check, not a write guard baked into Write(). Commands
// that write user-facing content (page create/edit, etc.) call GuardWritable(vault,
// path) before calling Write(). The CLI's own allowed writers - the source-add
// pipeline prepending frontmatter under raw/, and index/log generation - call
// Write() directly without going through GuardWritable, since they are the
// sanctioned producers of those paths.
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

    public static void GuardWritable(Vault vault, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var rawDir = System.IO.Path.GetFullPath(vault.RawDir);
        var indexPath = System.IO.Path.GetFullPath(vault.IndexPath);
        var logPath = System.IO.Path.GetFullPath(vault.LogPath);

        if (IsUnder(full, rawDir) || PathsEqual(full, indexPath) || PathsEqual(full, logPath))
        {
            throw new ValidationException("protected-path", $"'{path}' is a protected path and cannot be written directly", path);
        }
    }

    // Comparisons are case-INsensitive on purpose. This guard is a write-refusal
    // safety boundary: on APFS (macOS) and NTFS (Windows) - the default, case-
    // insensitive filesystems on both target platforms - `RAW/a.md` resolves to the
    // same file as `raw/a.md`, so a case-sensitive check would let a case-variant
    // spelling slip past and let AtomicFile.Write clobber a protected file. Over-
    // rejecting a case-variant path costs nothing (no legitimate caller writes to
    // RAW/); under-rejecting risks silent data loss. This is deliberately separate
    // from the spec's "case-sensitive internally" idmap/duplicate-detection rule.
    private static bool IsUnder(string path, string dir)
    {
        var prefix = dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                     + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
}
