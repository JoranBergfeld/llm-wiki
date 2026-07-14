namespace Wiki.Docs;

// Append-only log file writer for wiki/log.md.
// Append is safe without temp-rename (no crash hazard from partial write).
public static class LogFile
{
    /// <summary>
    /// Appends a single log entry to the vault's log file.
    /// </summary>
    /// <param name="v">The vault providing the log file path.</param>
    /// <param name="utcIso">UTC ISO-8601 timestamp (passed in for testability/determinism).</param>
    /// <param name="op">Operation name (create, update, delete, etc.).</param>
    /// <param name="subject">Subject of the operation (e.g., page name).</param>
    /// <param name="detail">Details about the operation.</param>
    public static void Append(Wiki.Core.Vault v, string utcIso, string op, string subject, string detail)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(v.LogPath)!);
        var line = $"## [{utcIso}] {op} | {subject} | {detail}\n";
        // Append is safe without temp-rename; no crash hazard from partial write.
        System.IO.File.AppendAllText(v.LogPath, line);
    }
}
