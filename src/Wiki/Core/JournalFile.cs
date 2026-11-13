using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Wiki.Core;

// The on-disk format of `.wiki/journal` (issue #47) and the file operations
// recovery needs. One record per line, tab-separated, arbitrary text
// base64-encoded, each line ending in a checksum of the rest of it:
//
//   begin    <action>  <utc>                       first record
//   image    <path>    absent | length <n> | content <bytes>
//   after    <path>    <sha256 | absent>           written at commit
//   commit   <prev>    <segment>…                  the command's audit line
//   restored                                       before-images put back
//   rollback <prev>    <segment>…                  the `recover` audit line
//
// Every write is flushed through to disk before the caller goes on. A crash
// can therefore only tear the LAST line; a bad line followed by good ones is
// not a crash but damage, and makes the whole journal untrustworthy.
public static class JournalFile
{
    public const string CommitRecord = "commit";
    public const string RollbackRecord = "rollback";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string EncodeBegin(string action, DateTimeOffset now)
        => Line("begin", B64(action), now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));

    public static string EncodeAfter(string path, string? hash) => Line("after", B64(path), hash ?? "absent");

    public static string EncodeSeal(string kind, string prev, IReadOnlyList<string> segments)
    {
        var fields = new List<string> { kind, prev };
        foreach (var s in segments)
            fields.Add(B64(s));
        return Line(fields.ToArray());
    }

    public static string EncodeRestored() => Line("restored");

    internal static string EncodeImage(JournalImage image) => image.Kind switch
    {
        JournalImageKind.Absent => Line("image", B64(image.Path), "absent"),
        JournalImageKind.Length => Line("image", B64(image.Path), "length",
            image.OriginalLength.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => Line("image", B64(image.Path), "content", Convert.ToBase64String(image.Bytes!)),
    };

    public static void AppendDurably(FileStream file, string text)
    {
        var bytes = Utf8NoBom.GetBytes(text);
        file.Seek(0, SeekOrigin.End);
        file.Write(bytes, 0, bytes.Length);
        file.Flush(flushToDisk: true);
    }

    public static void AppendDurably(string path, string text)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete);
        AppendDurably(file, text);
    }

    public static void Remove(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    // Flushes `path` through to disk and returns the SHA-256 of its bytes,
    // or null when it does not exist.
    public static string? FlushAndHash(string path)
    {
        if (!File.Exists(path))
            return null;
        using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes, 0, bytes.Length);
        s.Flush(flushToDisk: true);
        return Hash(bytes);
    }

    public static string? HashOf(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Puts every before-image back, newest first, then flushes the restored
    // files. Idempotent, so an interruption during a rollback is recovered by
    // simply rolling back again.
    public static void Restore(IReadOnlyList<JournalImage> images)
    {
        var touched = new HashSet<string>(WriteJournal.PathComparer);
        for (var i = images.Count - 1; i >= 0; i--)
        {
            var image = images[i];
            touched.Add(image.Path);
            switch (image.Kind)
            {
                case JournalImageKind.Absent:
                    if (File.Exists(image.Path))
                        File.Delete(image.Path);
                    break;
                case JournalImageKind.Length:
                    if (File.Exists(image.Path))
                    {
                        using var s = new FileStream(image.Path, FileMode.Open, FileAccess.Write, FileShare.None);
                        s.SetLength(image.OriginalLength);
                        s.Flush(flushToDisk: true);
                    }
                    break;
                default:
                    WriteDurably(image.Path, image.Bytes!);
                    break;
            }
        }
        foreach (var path in touched)
            FlushAndHash(path);
    }

    // Same temp-then-rename as AtomicFile.Write, but never journaled: this
    // is recovery putting a file back, not a command changing it.
    private static void WriteDurably(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                s.Write(bytes, 0, bytes.Length);
                s.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // Null when there is no journal.
    public static ParsedJournal? Read(string path)
    {
        string text;
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s, Utf8NoBom);
            text = r.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var journal = new ParsedJournal();
        var lines = text.Split('\n');
        // The piece after the last '\n' is a torn tail (or empty): ignored.
        var complete = lines.Length - 1;
        for (var i = 0; i < complete; i++)
        {
            var fields = Verify(lines[i]);
            if (fields is null)
            {
                if (i == complete - 1)
                    break;   // the last record, torn mid-write
                journal.Phase = JournalPhase.Corrupt;
                return journal;
            }
            if (!Apply(journal, fields))
            {
                journal.Phase = JournalPhase.Corrupt;
                return journal;
            }
        }

        journal.Phase = journal.Seal is { } seal
            ? seal.Kind == CommitRecord ? JournalPhase.Committed : JournalPhase.RolledBack
            : journal.Restored ? JournalPhase.Restored : JournalPhase.Pending;
        return journal;
    }

    private static bool Apply(ParsedJournal journal, string[] f)
    {
        try
        {
            switch (f[0])
            {
                case "begin" when f.Length == 3:
                    journal.Action = Unb64(f[1]);
                    journal.Started = f[2];
                    return true;
                case "image" when f.Length >= 3:
                    var path = Unb64(f[1]);
                    journal.Images.Add(f[2] switch
                    {
                        "absent" => JournalImage.Absent(path),
                        "length" => JournalImage.Length(path, long.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture)),
                        "content" => JournalImage.Content(path, Convert.FromBase64String(f[3])),
                        _ => throw new FormatException(f[2]),
                    });
                    return true;
                case "after" when f.Length == 3:
                    journal.After.Add((Unb64(f[1]), f[2] == "absent" ? null : f[2]));
                    return true;
                case "restored" when f.Length == 1:
                    journal.Restored = true;
                    return true;
                case CommitRecord or RollbackRecord when f.Length >= 2 && journal.Seal is null:
                    var segments = new string[f.Length - 2];
                    for (var i = 2; i < f.Length; i++)
                        segments[i - 2] = Unb64(f[i]);
                    journal.Seal = new JournalSeal(f[0], f[1], segments);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static string Line(params string[] fields)
    {
        var body = string.Join('\t', fields);
        return body + "\t" + Checksum(body) + "\n";
    }

    private static string[]? Verify(string line)
    {
        var cut = line.LastIndexOf('\t');
        if (cut < 0)
            return null;
        var body = line[..cut];
        return line[(cut + 1)..] == Checksum(body) ? body.Split('\t') : null;
    }

    private static string Checksum(string body) => Hash(Utf8NoBom.GetBytes(body))[..16];

    private static string B64(string s) => Convert.ToBase64String(Utf8NoBom.GetBytes(s));

    private static string Unb64(string s) => Utf8NoBom.GetString(Convert.FromBase64String(s));
}

public enum JournalImageKind { Absent, Length, Content }

// What one file was before the command first changed it.
public sealed class JournalImage
{
    public JournalImageKind Kind { get; }
    public string Path { get; }
    public long OriginalLength { get; }
    public byte[]? Bytes { get; }

    private JournalImage(JournalImageKind kind, string path, long length, byte[]? bytes)
    {
        Kind = kind;
        Path = path;
        OriginalLength = length;
        Bytes = bytes;
    }

    public static JournalImage Absent(string path) => new(JournalImageKind.Absent, path, 0, null);
    public static JournalImage Length(string path, long length) => new(JournalImageKind.Length, path, length, null);
    public static JournalImage Content(string path, byte[] bytes) => new(JournalImageKind.Content, path, bytes.Length, bytes);

    public string Encode() => JournalFile.EncodeImage(this);
}

public sealed record JournalSeal(string Kind, string Prev, string[] Segments);

public sealed class ParsedJournal
{
    public JournalPhase Phase { get; internal set; }
    public string Action { get; internal set; } = "unknown";
    public string Started { get; internal set; } = "-";
    public List<JournalImage> Images { get; } = new();
    public List<(string Path, string? Hash)> After { get; } = new();
    public bool Restored { get; internal set; }
    public JournalSeal? Seal { get; internal set; }

    // A journal whose only record was torn before it was flushed: no file
    // was changed after it, so there is nothing to recover.
    public bool IsEmpty => Phase == JournalPhase.Pending && Images.Count == 0;

    // Readers must not run against it: files may be half changed.
    public bool IsDirty => Phase is JournalPhase.Pending or JournalPhase.Corrupt && !IsEmpty;
}
