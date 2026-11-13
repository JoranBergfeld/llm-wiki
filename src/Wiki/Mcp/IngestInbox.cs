using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Wiki.Core;

namespace Wiki.Mcp;

// The inbox `ingest_source` lands uploads in (issue #57, decision #30
// "Ingest"): `<inboxRoot>/<category>/<filename>`, one directory per category
// so `wiki source scan` registers it under that category (amendment T).
//
// Server config, fixed at startup like the vault binding: `--inbox-root` or
// WIKI_MCP_INBOX_ROOT, resolved to an absolute path, refused when it is
// inside the vault (`inbox-in-vault`; the scan would refuse it anyway, and a
// server should fail at startup, not on the first upload). No tool argument
// names a directory: the category and filename are validated to be one
// kebab-case directory and one plain file name, so the landed path is always
// a direct child of <inboxRoot>/<category>.
//
// `--max-upload-bytes` / WIKI_MCP_MAX_UPLOAD_BYTES caps one decoded upload
// (default 10 MiB). The HTTP transport (#61) reuses this same limit for its
// `upload-too-large` rejection.
public sealed class IngestInbox(string? root, long maxUploadBytes)
{
    public const string RootOption = "--inbox-root";
    public const string RootEnv = "WIKI_MCP_INBOX_ROOT";
    public const string MaxUploadOption = "--max-upload-bytes";
    public const string MaxUploadEnv = "WIKI_MCP_MAX_UPLOAD_BYTES";
    public const long DefaultMaxUploadBytes = 10 * 1024 * 1024;

    private static readonly Regex KebabId = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    // Absolute; null when the server was started without one.
    public string? Root { get; } = root;
    public long MaxUploadBytes { get; } = maxUploadBytes;

    // Flag over env var, the same precedence as --vault over WIKI_VAULT.
    public static IngestInbox Resolve(string? rootFlag, string? maxFlag, string vaultRoot)
    {
        var rawRoot = rootFlag ?? NullIfEmpty(Environment.GetEnvironmentVariable(RootEnv));
        var rawMax = maxFlag ?? NullIfEmpty(Environment.GetEnvironmentVariable(MaxUploadEnv));

        var max = DefaultMaxUploadBytes;
        if (rawMax is not null &&
            (!long.TryParse(rawMax, NumberStyles.None, CultureInfo.InvariantCulture, out max) || max <= 0))
            throw new ValidationException("invalid-max-upload-bytes",
                $"{MaxUploadOption}: '{rawMax}' is not a positive whole number of bytes");

        if (rawRoot is null)
            return new IngestInbox(null, max);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawRoot));
        var vault = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultRoot));
        if (root.Equals(vault, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(vault + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            vault.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("inbox-in-vault",
                $"{RootOption}: '{rawRoot}' overlaps the vault; the inbox must live outside the vault it feeds", rawRoot);

        return new IngestInbox(root, max);
    }

    // Validates and decodes the upload, then writes it atomically. Returns
    // the landed path, or a refusal (code, message) with nothing written.
    public Landing Land(string category, string filename, string base64)
    {
        if (Root is null)
            return Landing.Refused("inbox-not-configured",
                $"this server has no inbox; start 'wiki mcp' with {RootOption} or {RootEnv}");

        if (!KebabId.IsMatch(category))
            return Landing.Refused("invalid-category-id", $"category id '{category}' must be lowercase kebab-case");

        if (!IsPlainFileName(filename))
            return Landing.Refused("invalid-filename",
                $"'{filename}' is not a plain file name (no directories, no leading dot, no reserved characters)");

        // Reject before decoding: the decoded size is at most 3/4 of the text.
        if ((base64.Length / 4L) * 3 > MaxUploadBytes + 2)
            return TooLarge();

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return Landing.Refused("invalid-upload", "content is not valid base64");
        }
        if (bytes.Length > MaxUploadBytes)
            return TooLarge();

        var dir = Path.Combine(Root, category);
        var target = Path.Combine(dir, filename);
        // Belt and braces: the checks above already make this a direct child.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)), Path.GetFullPath(dir), StringComparison.Ordinal))
            return Landing.Refused("invalid-filename", $"'{filename}' does not land inside the inbox");

        Write(dir, target, bytes);
        return new Landing(target, bytes.Length, null, null);
    }

    // Atomic like AtomicFile.Write, but the temp file is dot-prefixed: a
    // concurrent `source scan` of this directory skips dot files, so it can
    // never register a half-written upload. Outside the vault, so no journal.
    private static void Write(string dir, string target, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private Landing TooLarge() => Landing.Refused("upload-too-large",
        $"the upload is larger than this server's limit of {MaxUploadBytes} bytes ({MaxUploadOption})");

    // Portable across Windows and POSIX: no separators, no characters Windows
    // reserves, no control characters, no leading dot (scan would skip it),
    // no trailing dot or space, no device names.
    private static bool IsPlainFileName(string name)
    {
        if (name.Length is 0 or > 255 || name[0] == '.' || name[^1] is '.' or ' ')
            return false;
        foreach (var c in name)
            if (c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return false;
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public sealed record Landing(string? Path, long Bytes, string? Code, string? Message)
    {
        public static Landing Refused(string code, string message) => new(null, 0, code, message);
    }
}
