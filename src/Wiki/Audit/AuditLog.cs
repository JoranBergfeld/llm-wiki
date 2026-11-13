using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Wiki.Core;
using Wiki.Json;

namespace Wiki.Audit;

// Where a vault's audit log lives and how it is read (issue #44). One log per
// vault, at `audit.path` in wiki.yaml (default audit/audit.log), with detail
// files in a `details/` directory beside it, each named `<sha256>.txt` after
// its own content.
//
// Separate from `.wiki/ledger.json` (a rebuildable snapshot) and from
// `wiki/log.md` (the human narrative, unchanged): this is the only
// tamper-evident history of the vault.
//
// Segments (issue #45). The chain spans the LIVE segment (the log file itself)
// and any number of ARCHIVED segments beside it, `<stem>.<nnnnnn><ext>.gz`
// (audit.000001.log.gz, ...), each the exact bytes the live log held when it
// was archived, gzip-compressed. Nothing is ever deleted. The manifest,
// `<stem>.manifest.json`, lists them in order with the hash each one ends on;
// the next segment's first line chains from that hash, so the chain runs
// unbroken from the first archived line to the last live one. A segment is
// archived for one of two reasons:
//   - rotation: the live log reached `audit.rotate_bytes` (about 10 MB), so the
//     next mutation first rolls it over; it ends on its last line's hash;
//   - re-anchor: the live chain was broken and `wiki audit reanchor` set the
//     broken bytes aside as they are. Such a segment "ends" on the SHA-256 of
//     its whole content, which freezes the break as it was found: its own line
//     problems are acknowledged, any later edit to it is not.
//
// Writes check the live segment (and its link to the manifest) in full before
// every mutation; `wiki audit --deep` additionally verifies every archived
// segment and the links between them.
public sealed class AuditLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // The command whose step archives a broken live segment instead of
    // refusing to extend it.
    public const string ReanchorCommand = "audit reanchor";

    public string LogPath { get; }
    public string DetailsDir { get; }
    public string ManifestPath { get; }
    public long RotateBytes { get; }

    private readonly string _dir;
    private readonly string _stem;
    private readonly string _ext;

    // Stamps archived segments (`archivedAt`) and set-aside manifests.
    private readonly Func<long> _nowUnixMs;

    private AuditLog(string logPath, long rotateBytes, Func<long> nowUnixMs)
    {
        _nowUnixMs = nowUnixMs;
        LogPath = logPath;
        RotateBytes = rotateBytes;
        _dir = Path.GetDirectoryName(logPath)!;
        _stem = Path.GetFileNameWithoutExtension(logPath);
        _ext = Path.GetExtension(logPath);
        DetailsDir = Path.Combine(_dir, "details");
        ManifestPath = Path.Combine(_dir, _stem + ".manifest.json");
    }

    public static AuditLog For(Vault vault, Func<long>? nowUnixMs = null)
        => For(vault, VaultConfig.Load(vault.ConfigPath), nowUnixMs);

    public static AuditLog For(Vault vault, VaultConfig config, Func<long>? nowUnixMs = null)
    {
        var path = Path.GetFullPath(Path.Combine(vault.Root, config.AuditPath));
        var state = Path.GetFullPath(vault.StateDir) + Path.DirectorySeparatorChar;
        if (path.StartsWith(state, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ValidationException("config",
                $"'audit.path' resolves to '{path}', inside .wiki/, which is a rebuildable cache; move it elsewhere", vault.ConfigPath);
        return new AuditLog(path, config.AuditRotateBytes, nowUnixMs ?? Timestamp.NowUnixMs);
    }

    public string DetailPath(string hash) => Path.Combine(DetailsDir, hash + ".txt");

    public string SegmentPath(string file) => Path.Combine(_dir, file);

    // Every raw line of the log, newest first: the live segment from its end,
    // then each archived segment from the newest back (issue #52: the accept
    // a PERSONA.md names may have rotated away). A lookup, not a verifier:
    // an unreadable manifest leaves only the live segment, and a missing or
    // corrupt segment is skipped; `wiki audit --deep` is where those surface.
    // Lazy, so a caller that finds what it needs early decompresses nothing.
    public IEnumerable<string> LinesNewestFirst()
    {
        if (File.Exists(LogPath))
        {
            string live;
            // ReadWrite share: a mutating command reads while its own audit
            // writer holds the file open.
            using (var s = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(s, Utf8NoBom))
                live = r.ReadToEnd();
            var lines = live.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
                yield return lines[i];
        }

        var manifest = LoadManifest();
        if (manifest is null)
            yield break;
        for (var k = manifest.Segments.Count - 1; k >= 0; k--)
        {
            var text = ReadSegment(manifest.Segments[k]);
            if (text is null)
                continue;
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
                yield return lines[i];
        }
    }

    private string? ReadSegment(AuditSegment seg)
    {
        var path = SegmentPath(seg.File);
        if (!File.Exists(path))
            return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new StreamReader(gz, Utf8NoBom);
            return r.ReadToEnd();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    // The file name the next archived segment gets.
    public string NextSegmentFile()
        => NextSegmentFile(LoadManifest() ?? new AuditManifest());

    private string NextSegmentFile(AuditManifest manifest)
        => $"{_stem}.{(manifest.Segments.Count + 1).ToString("D6", CultureInfo.InvariantCulture)}{_ext}.gz";

    // Opens the log for one append, holding the file until the writer is
    // disposed. Called BEFORE the command runs, under the vault lock, so a log
    // that cannot be opened or whose chain is broken fails the mutation while
    // the vault is still untouched. Rotation happens here too, for the same
    // reason: it runs under the lock, before the command, and the line the
    // command is about to append starts the fresh live segment.
    public AuditWriter OpenForAppend()
    {
        var (stream, createdFile, createdDir) = OpenStream();
        try
        {
            var manifest = LoadManifest() ?? throw ChainBroken("its manifest, which is not readable", ManifestPath);
            var bytes = ReadLive(stream, manifest);

            // The whole live segment is verified before every mutation, not
            // just its tail: once the chain is broken anywhere in it, writes
            // are refused until `wiki audit reanchor`. Reads never come
            // through here, so they keep working.
            var report = Verify(Utf8NoBom.GetString(bytes), BasePrev(manifest), "live", LogPath);
            if (!report.Intact)
            {
                var p = report.Problems[0];
                throw ChainBroken($"line {p.Line} ({p.Code}: {p.Message})" +
                    (report.Problems.Length > 1 ? $" and {report.Problems.Length - 1} more problem(s)" : ""), LogPath);
            }

            var prev = report.LastHash ?? BasePrev(manifest);
            if (bytes.Length >= RotateBytes)
                Archive(stream, manifest, bytes, report.Entries, prev, reanchored: false);
            return new AuditWriter(this, stream, prev, createdFile ? LogPath : null, createdDir ? _dir : null);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // `wiki audit reanchor`: sets the live segment aside exactly as it is,
    // broken or not, as an archived segment that ends on the SHA-256 of its
    // content, and returns a writer on the now-empty live log whose next line
    // (the re-anchor line, carrying the reason) chains from that hash.
    public AuditWriter Reanchor()
    {
        var (stream, createdFile, createdDir) = OpenStream();
        try
        {
            // An unreadable manifest is itself the break: start a fresh one
            // beside it rather than overwrite it, so nothing is lost.
            var manifest = LoadManifest() ?? SetAsideManifest();
            var bytes = ReadLive(stream, manifest);
            var text = Utf8NoBom.GetString(bytes);
            var anchor = AuditLine.Sha256Hex(bytes);
            var entries = Verify(text, BasePrev(manifest), "live", LogPath).Entries;
            Archive(stream, manifest, bytes, entries, anchor, reanchored: true);
            return new AuditWriter(this, stream, anchor, createdFile ? LogPath : null, createdDir ? _dir : null);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private (FileStream Stream, bool CreatedFile, bool CreatedDir) OpenStream()
    {
        var createdDir = !Directory.Exists(_dir);
        var createdFile = !File.Exists(LogPath);
        try
        {
            Directory.CreateDirectory(_dir);
            // FileShare.Read: `wiki audit` may read while we hold it, nobody
            // else may write. The vault lock (#46) is what serialises writers;
            // this only makes a second writer fail rather than interleave.
            var stream = new FileStream(LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            return (stream, createdFile, createdDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"audit log '{LogPath}' cannot be opened for writing, so the change was not made: {ex.Message}", ex);
        }
    }

    // The live bytes, after finishing a rotation that was interrupted between
    // recording the segment in the manifest and emptying the live log (the
    // live log then still holds exactly the newest segment's content).
    private byte[] ReadLive(FileStream stream, AuditManifest manifest)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes, 0, bytes.Length);
        if (AlreadyArchived(bytes, manifest))
        {
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
            return [];
        }
        return bytes;
    }

    private static bool AlreadyArchived(byte[] live, AuditManifest manifest)
        => live.Length > 0 && manifest.Segments.Count > 0 && AuditLine.Sha256Hex(live) == manifest.Segments[^1].Sha256;

    // Order matters for a crash at any point: the segment file is complete
    // before the manifest names it, and the manifest names it before the live
    // log is emptied (ReadLive finishes that last step if it was missed).
    private string Archive(FileStream stream, AuditManifest manifest, byte[] bytes, int entries, string lastHash, bool reanchored)
    {
        var file = NextSegmentFile(manifest);
        var path = SegmentPath(file);
        // Through AtomicFile like every vault write (#47); the caller holds
        // WriteJournal.Suspend, so the audit log's own files stay out of the
        // journal and are never rolled back with the change they record.
        using (var compressed = new MemoryStream())
        {
            using (var gz = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(bytes, 0, bytes.Length);
            AtomicFile.Write(path, compressed.ToArray());
        }

        manifest.Segments.Add(new AuditSegment
        {
            File = file,
            Entries = entries,
            LastHash = lastHash,
            Sha256 = AuditLine.Sha256Hex(bytes),
            Reanchored = reanchored,
            ArchivedAt = Timestamp.Iso(_nowUnixMs()),
        });
        AtomicFile.Write(ManifestPath, JsonSerializer.Serialize(manifest, WikiJsonContext.Default.AuditManifest) + "\n");

        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
        return file;
    }

    // Null when the manifest exists but cannot be read; empty when absent.
    private AuditManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath))
            return new AuditManifest();
        try
        {
            var m = JsonSerializer.Deserialize(File.ReadAllText(ManifestPath), WikiJsonContext.Default.AuditManifest);
            if (m is null)
                return null;
            foreach (var s in m.Segments)
                if (s is null || string.IsNullOrEmpty(s.File) || s.File != Path.GetFileName(s.File)
                    || !AuditLine.IsHex64(s.LastHash ?? "") || !AuditLine.IsHex64(s.Sha256 ?? ""))
                    return null;
            return m;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private AuditManifest SetAsideManifest()
    {
        // Copy, then delete, through AtomicFile (#47) rather than File.Move:
        // the unreadable manifest is kept byte-for-byte, never overwritten.
        var aside = ManifestPath + "." + _nowUnixMs().ToString(CultureInfo.InvariantCulture) + ".unreadable";
        AtomicFile.Write(aside, File.ReadAllBytes(ManifestPath));
        AtomicFile.Delete(ManifestPath);
        return new AuditManifest();
    }

    // The hash the live segment's first line chains from.
    private static string BasePrev(AuditManifest manifest)
        => manifest.Segments.Count == 0 ? AuditLine.Genesis : manifest.Segments[^1].LastHash;

    private static AuditChainBrokenException ChainBroken(string where, string path)
        => new(
            $"the audit log is broken at {where}, so writes are refused and the change was not made. " +
            "Run 'wiki audit' for details; once understood, 'wiki audit reanchor --reason <why>' restores writes",
            path);

    // Verifies the live segment chaining from the manifest's last hash.
    // Read-only.
    public ChainReport VerifyLive()
    {
        var manifest = LoadManifest();
        if (!File.Exists(LogPath))
            return manifest is null
                ? new ChainReport("live", LogPath, false, 0, false, null, [ManifestUnreadable()])
                : new ChainReport("live", LogPath, false, 0, true, null, []);

        byte[] bytes;
        using (var s = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[s.Length];
            s.ReadExactly(bytes, 0, bytes.Length);
        }

        if (manifest is null)
        {
            var r = Verify(Utf8NoBom.GetString(bytes), AuditLine.Genesis, "live", LogPath);
            return r with { Intact = false, Problems = [ManifestUnreadable(), .. r.Problems] };
        }
        if (AlreadyArchived(bytes, manifest))
            return new ChainReport("live", LogPath, true, 0, true, null, []);
        return Verify(Utf8NoBom.GetString(bytes), BasePrev(manifest), "live", LogPath);
    }

    private ChainProblem ManifestUnreadable()
        => new(0, "manifest-unreadable", $"the segment manifest '{ManifestPath}' cannot be read");

    // `wiki audit --deep`: every archived segment in manifest order, then the
    // live one, each checked on its own and against the hash the one before
    // it ends on.
    public DeepChainReport VerifyDeep()
    {
        var manifest = LoadManifest();
        if (manifest is null)
        {
            var live = VerifyLive();
            return new DeepChainReport(false, [live]);
        }

        var reports = new List<ChainReport>();
        var prev = AuditLine.Genesis;
        foreach (var seg in manifest.Segments)
        {
            reports.Add(VerifySegment(seg, prev));
            prev = seg.LastHash;
        }
        reports.Add(VerifyLive());

        var intact = true;
        foreach (var r in reports)
            intact &= r.Intact;
        return new DeepChainReport(intact, reports.ToArray());
    }

    private ChainReport VerifySegment(AuditSegment seg, string basePrev)
    {
        var path = SegmentPath(seg.File);
        if (!File.Exists(path))
            return new ChainReport(seg.File, path, false, seg.Entries, false, null,
                [new ChainProblem(0, "segment-missing", "the archived segment named in the manifest is missing")], seg.Reanchored);

        byte[] bytes;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            bytes = ms.ToArray();
        }
        catch (InvalidDataException)
        {
            return new ChainReport(seg.File, path, true, seg.Entries, false, null,
                [new ChainProblem(0, "segment-unreadable", "the archived segment is not valid gzip")], seg.Reanchored);
        }

        var report = Verify(Utf8NoBom.GetString(bytes), basePrev, seg.File, path) with { Reanchored = seg.Reanchored };
        var contentMatches = AuditLine.Sha256Hex(bytes) == seg.Sha256;

        if (seg.Reanchored)
        {
            // The break inside it was acknowledged when it was set aside; what
            // must still hold is that it is byte-for-byte what was set aside.
            return contentMatches
                ? report with { Intact = true }
                : report with { Intact = false, Problems = [.. report.Problems, new ChainProblem(0, "segment-mismatch", "the segment no longer matches the hash it was re-anchored on")] };
        }

        var problems = new List<ChainProblem>(report.Problems);
        if (!contentMatches)
            problems.Add(new ChainProblem(0, "segment-mismatch", "the segment does not match the hash recorded in the manifest"));
        if (report.LastHash != seg.LastHash)
            problems.Add(new ChainProblem(report.Entries, "link-mismatch", "the segment's last hash differs from the one the manifest records"));
        return report with { Intact = problems.Count == 0, Problems = problems.ToArray() };
    }

    // Verifies one segment's text chaining from `basePrev`: every line
    // parses, matches its own hash and chains from the one before, and every
    // detail file it references exists with matching content.
    private ChainReport Verify(string text, string basePrev, string segment, string path)
    {
        var problems = new List<ChainProblem>();
        var lines = text.Split('\n');
        var count = lines.Length - 1;
        if (lines[^1].Length != 0)
        {
            count = lines.Length;
            problems.Add(new ChainProblem(lines.Length, "incomplete-line", "the last line has no trailing newline"));
        }

        string? prev = basePrev;
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
                problems.Add(new ChainProblem(n, "prev-mismatch", n == 1
                    ? "prev= does not match the hash the previous segment ends on"
                    : "prev= does not match the hash of the line before"));
            if (!parsed.HashMatches)
                problems.Add(new ChainProblem(n, "hash-mismatch", "the line does not match its own hash"));
            foreach (var d in parsed.DetailRefs())
            {
                var detail = DetailPath(d);
                if (!File.Exists(detail))
                    problems.Add(new ChainProblem(n, "detail-missing", $"detail file {d} is missing"));
                else if (AuditLine.Sha256Hex(File.ReadAllBytes(detail)) != d)
                    problems.Add(new ChainProblem(n, "detail-mismatch", $"detail file {d} does not match its hash"));
            }
            // Chain on from what the line claims, so one edited line is
            // reported once rather than cascading down the rest of the log.
            prev = parsed.Hash;
            last = parsed.Hash;
        }

        return new ChainReport(segment, path, true, count, problems.Count == 0, last, problems.ToArray());
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
    //
    // Written straight to the stream, NOT through AtomicFile/LogFile: the
    // audit log is recovery infrastructure, exempt from the journaled write
    // rule (CONTRIBUTING.md, "Every vault write goes through ..."). The line
    // is what the write journal seals on commit, so journaling it would be
    // circular, and rolling it back would erase the record of the change.
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

// One segment's chain report. Segment is "live" for the log file itself, else
// the archived segment's file name. Line 0 in a problem means the segment as a
// whole (missing, unreadable, not matching the manifest). For a re-anchored
// segment, `intact` means it is unchanged since it was set aside; the line
// problems it lists are the break that was acknowledged then.
public sealed record ChainReport(
    string Segment,
    string Path,
    bool Exists,
    int Entries,
    bool Intact,
    string? LastHash,
    ChainProblem[] Problems,
    bool Reanchored = false);

// `wiki audit --deep`: every segment in chain order, the live one last.
public sealed record DeepChainReport(bool Intact, ChainReport[] Segments);

// `<stem>.manifest.json` beside the live log.
public sealed class AuditManifest
{
    public List<AuditSegment> Segments { get; set; } = [];
}

public sealed class AuditSegment
{
    public string File { get; set; } = "";
    public int Entries { get; set; }
    // The hash the next segment's first line chains from: the last line's
    // hash, or for a re-anchored segment the SHA-256 of its whole content.
    public string LastHash { get; set; } = "";
    // SHA-256 of the segment's uncompressed bytes.
    public string Sha256 { get; set; } = "";
    public bool Reanchored { get; set; }
    public string ArchivedAt { get; set; } = "";
}

// `wiki audit reanchor` result: which archived segment now holds the broken
// live log, how many problems it had, and the reason recorded in the chain.
public sealed record ReanchorResult(string Segment, int Problems, string Reason) : Wiki.Cli.IHumanRenderable
{
    public string HumanSummary()
        => $"Re-anchored the audit log: the broken live segment ({Problems} problem(s)) is archived as {Segment}; writes are allowed again";
}
