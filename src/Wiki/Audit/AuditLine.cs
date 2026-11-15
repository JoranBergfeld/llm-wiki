using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Wiki.Audit;

// The audit line format (issue #44, designed in #35). One line per mutation:
//
//   2026-11-13T19:53:12Z | page.upsert | contoso | exit=0 | via=cli | prev=<64 hex> | hash=<64 hex>
//
// Segments are separated by " | ". The first three are positional (UTC time,
// action, subject); then the command's own `key=value` fields; then `exit=`
// (or `error=`), `via=`, and finally `prev=` and `hash=`.
//
//   hash = lowercase hex SHA-256 of the UTF-8 bytes of everything before
//          " | hash=", i.e. the line up to and including its prev= segment.
//   prev = the previous line's hash, or Genesis on the first line.
//
// So each line's hash covers its own content and, through prev, every line
// before it. A field whose value is `@<64 hex>` references a detail file
// holding that content, which the chain therefore covers too.
public static class AuditLine
{
    public static readonly string Genesis = new('0', 64);

    public const string Separator = " | ";

    // Longer field values go to a detail file. Short enough that a line stays
    // readable in a terminal; a persona evidence list or a reason fits.
    public const int MaxInlineValue = 200;

    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    // Percent-escapes the characters that would break the line structure:
    // `%` itself, `|`, CR, LF, and a leading `@` (reserved for detail refs).
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '%': sb.Append("%25"); break;
                case '|': sb.Append("%7C"); break;
                case '\n': sb.Append("%0A"); break;
                case '\r': sb.Append("%0D"); break;
                case '@' when i == 0: sb.Append("%40"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // Seals a line: appends prev= and hash= to the already-escaped segments.
    public static (string Line, string Hash) Seal(IReadOnlyList<string> segments, string prev)
    {
        var prefix = string.Join(Separator, segments) + Separator + "prev=" + prev;
        var hash = Sha256Hex(prefix);
        return (prefix + Separator + "hash=" + hash, hash);
    }

    // Splits a sealed line, or returns null when it does not have the shape
    // (too few segments, no prev=/hash= tail). Does not check the hash.
    public static Parsed? Parse(string line)
    {
        var segments = line.Split(Separator);
        if (segments.Length < 5)
            return null;
        var prevSeg = segments[^2];
        var hashSeg = segments[^1];
        if (!prevSeg.StartsWith("prev=", StringComparison.Ordinal) || !hashSeg.StartsWith("hash=", StringComparison.Ordinal))
            return null;
        var prev = prevSeg["prev=".Length..];
        var hash = hashSeg["hash=".Length..];
        if (!IsHex64(prev) || !IsHex64(hash))
            return null;
        var prefix = line[..^(Separator.Length + hashSeg.Length)];
        return new Parsed(segments[..^2], prev, hash, prefix);
    }

    public static bool IsHex64(string s)
    {
        if (s.Length != 64)
            return false;
        foreach (var c in s)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        return true;
    }

    public sealed record Parsed(string[] Segments, string Prev, string Hash, string Prefix)
    {
        public bool HashMatches => Sha256Hex(Prefix) == Hash;

        // Detail-file hashes this line references (`key=@<hex>`).
        public IEnumerable<string> DetailRefs()
        {
            for (var i = 3; i < Segments.Length; i++)
            {
                var eq = Segments[i].IndexOf('=');
                if (eq < 0)
                    continue;
                var value = Segments[i][(eq + 1)..];
                if (value.Length == 65 && value[0] == '@' && IsHex64(value[1..]))
                    yield return value[1..];
            }
        }
    }
}
