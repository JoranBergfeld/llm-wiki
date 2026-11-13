using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Wiki.Core;

namespace Wiki.Audit;

// Where a vault's audit log lives and how it is read (issue #44). One log per
// vault, at `audit.path` in wiki.yaml (default audit/audit.log), with detail
// files in a `details/` directory beside it, each named `<sha256>.txt` after
// its own content.
//
// Separate from `.wiki/ledger.json` (a rebuildable snapshot) and from
// `wiki/log.md` (the human narrative, unchanged): this is the only
// tamper-evident history of the vault.
public sealed class AuditLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string LogPath { get; }
    public string DetailsDir { get; }

    private AuditLog(string logPath)
    {
        LogPath = logPath;
        DetailsDir = Path.Combine(Path.GetDirectoryName(logPath)!, "details");
    }

    public static AuditLog For(Vault vault) => For(vault, VaultConfig.Load(vault.ConfigPath));

    public static AuditLog For(Vault vault, VaultConfig config)
    {
        var path = Path.GetFullPath(Path.Combine(vault.Root, config.AuditPath));
        var state = Path.GetFullPath(vault.StateDir) + Path.DirectorySeparatorChar;
        if (path.StartsWith(state, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ValidationException("config",
                $"'audit.path' resolves to '{path}', inside .wiki/, which is a rebuildable cache; move it elsewhere", vault.ConfigPath);
        return new AuditLog(path);
    }

    public string DetailPath(string hash) => Path.Combine(DetailsDir, hash + ".txt");

    // Opens the log for one append, holding the file until the writer is
    // disposed. Called BEFORE the command runs, so a log that cannot be opened
    // or whose tail is unreadable fails the mutation while the vault is still
    // untouched.
    public AuditWriter OpenForAppend()
    {
        var dir = Path.GetDirectoryName(LogPath)!;
        var createdDir = !Directory.Exists(dir);
        var createdFile = !File.Exists(LogPath);
        FileStream stream;
        try
        {
            Directory.CreateDirectory(dir);
            // FileShare.Read: `wiki audit` may read while we hold it, nobody
            // else may write. The vault lock (#46) is what serialises writers;
            // this only makes a second writer fail rather than interleave.
            stream = new FileStream(LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"audit log '{LogPath}' cannot be opened for writing, so the change was not made: {ex.Message}", ex);
        }

        try
        {
            var prev = ReadTailHash(stream);
            return new AuditWriter(this, stream, prev, createdFile ? LogPath : null, createdDir ? dir : null);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // The last line's hash, which the next line chains from. Reads backwards
    // from the end so an append costs the same on a 10 MB live segment as on
    // an empty one. Only the tail line is checked here; `wiki audit` verifies
    // the whole live segment.
    private string ReadTailHash(FileStream stream)
    {
        var length = stream.Length;
        if (length == 0)
            return AuditLine.Genesis;

        stream.Seek(length - 1, SeekOrigin.Begin);
        if (stream.ReadByte() != '\n')
            throw ChainBroken("the last line is incomplete (no trailing newline)");

        var end = length - 1;
        var start = 0L;
        var buffer = new byte[8192];
        var cursor = end;
        while (cursor > 0)
        {
            var n = (int)Math.Min(buffer.Length, cursor);
            stream.Seek(cursor - n, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, n);
            var idx = Array.LastIndexOf(buffer, (byte)'\n', n - 1, n);
            if (idx >= 0)
            {
                start = cursor - n + idx + 1;
                break;
            }
            cursor -= n;
        }

        var bytes = new byte[end - start];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(bytes, 0, bytes.Length);
        var parsed = AuditLine.Parse(Utf8NoBom.GetString(bytes));
        if (parsed is null)
            throw ChainBroken("the last line is not a sealed audit line");
        if (!parsed.HashMatches)
            throw ChainBroken("the last line does not match its own hash");
        return parsed.Hash;
    }

    private StateConflictException ChainBroken(string why)
        => new("audit-chain-broken",
            $"audit log '{LogPath}' cannot be extended: {why}. Run 'wiki audit' for details; the change was not made",
            LogPath);

    // Verifies the live segment: every line parses, matches its own hash and
    // chains from the one before, and every detail file it references exists
    // with matching content. Read-only.
    public ChainReport VerifyLive()
    {
        if (!File.Exists(LogPath))
            return new ChainReport("live", LogPath, false, 0, true, null, Array.Empty<ChainProblem>());

        string text;
        using (var s = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(s, Utf8NoBom))
            text = r.ReadToEnd();

        var problems = new List<ChainProblem>();
        var lines = text.Split('\n');
        var count = lines.Length - 1;
        if (lines[^1].Length != 0)
        {
            count = lines.Length;
            problems.Add(new ChainProblem(lines.Length, "incomplete-line", "the last line has no trailing newline"));
        }

        string? prev = AuditLine.Genesis;
        string? last = null;
        for (var i = 0; i < count; i++)
        {
            var n = i + 1;
            var parsed = AuditLine.Parse(lines[i]);
            if (parsed is null)
            {
                problems.Add(new ChainProblem(n, "malformed-line", "not a sealed audit line"));
                prev = null;
                continue;
            }
            if (prev is not null && parsed.Prev != prev)
                problems.Add(new ChainProblem(n, "prev-mismatch", "prev= does not match the hash of the line before"));
            if (!parsed.HashMatches)
                problems.Add(new ChainProblem(n, "hash-mismatch", "the line does not match its own hash"));
            foreach (var d in parsed.DetailRefs())
            {
                var path = DetailPath(d);
                if (!File.Exists(path))
                    problems.Add(new ChainProblem(n, "detail-missing", $"detail file {d} is missing"));
                else if (AuditLine.Sha256Hex(File.ReadAllBytes(path)) != d)
                    problems.Add(new ChainProblem(n, "detail-mismatch", $"detail file {d} does not match its hash"));
            }
            // Chain on from what the line claims, so one edited line is
            // reported once rather than cascading down the rest of the log.
            prev = parsed.Hash;
            last = parsed.Hash;
        }

        return new ChainReport("live", LogPath, true, count, problems.Count == 0, last, problems.ToArray());
    }
}

// An open log, held between the pre-check and the append. Disposing without
// appending removes a log file and directory the open itself created, so a
// rejected first mutation leaves no trace.
public sealed class AuditWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AuditLog _log;
    private readonly FileStream _stream;
    private readonly string? _createdFile;
    private readonly string? _createdDir;
    private bool _appended;

    public string Prev { get; private set; }
    public string LogPath => _log.LogPath;

    internal AuditWriter(AuditLog log, FileStream stream, string prev, string? createdFile, string? createdDir)
    {
        _log = log;
        _stream = stream;
        Prev = prev;
        _createdFile = createdFile;
        _createdDir = createdDir;
    }

    // Content-addressed: the same content always lands in the same file, and
    // an existing one is left alone. Returns the hash the line references.
    public string StoreDetail(string content)
    {
        var bytes = Utf8NoBom.GetBytes(content);
        var hash = AuditLine.Sha256Hex(bytes);
        var path = _log.DetailPath(hash);
        if (!File.Exists(path))
            AtomicFile.Write(path, content);
        return hash;
    }

    // Seals and appends one line, flushed through to disk before returning:
    // success is reported only once the line is durable.
    public string Append(IReadOnlyList<string> segments)
    {
        var (line, hash) = AuditLine.Seal(segments, Prev);
        var bytes = Utf8NoBom.GetBytes(line + "\n");
        _stream.Seek(0, SeekOrigin.End);
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush(flushToDisk: true);
        Prev = hash;
        _appended = true;
        return hash;
    }

    public void Dispose()
    {
        _stream.Dispose();
        if (_appended)
            return;
        try
        {
            if (_createdFile is not null)
                File.Delete(_createdFile);
            if (_createdDir is not null && Directory.GetFileSystemEntries(_createdDir).Length == 0)
                Directory.Delete(_createdDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an empty log left behind is harmless (it reads as a
            // log with no lines yet).
        }
    }
}

public sealed record ChainProblem(int Line, string Code, string Message);

// `wiki audit`'s chain half. Segment is "live" until rotation (#45) adds
// compressed segments and `--deep`.
public sealed record ChainReport(
    string Segment,
    string Path,
    bool Exists,
    int Entries,
    bool Intact,
    string? LastHash,
    ChainProblem[] Problems);
