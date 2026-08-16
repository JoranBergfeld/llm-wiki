using System.IO;
using System.Text;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #4 (binary content is not text) and issue #5 (sha256 dedup was
// newline-sensitive). Both are guards on `wiki source add`'s content
// handling, so they share a file. Rejection tests assert the same "nothing
// lands" invariant as SourceAddTests.
public class SourceContentTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string RawDir(TempVault tv) => Path.Combine(tv.Path, "raw");
    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string LedgerPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "ledger.json");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");

    // A real PDF header: %PDF-1.7 then a NUL-bearing binary stretch. The
    // extension is deliberately .md - the guard is a CONTENT check, so a
    // binary blob pasted into a markdown file must be rejected too.
    private static readonly byte[] PdfLikeBytes =
    {
        0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A,
        0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x00, 0x42,
    };

    [Fact]
    public void Add_BinaryContent_Rejected_NothingLands()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "scan.md");
        File.WriteAllBytes(src, PdfLikeBytes);

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "Scan", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "source-not-text");
        // The message must name the offending path so a bulk caller can act on it.
        Assert.Contains("scan.md", r.Envelope.Errors[0].Message);

        Assert.Empty(Directory.GetFiles(RawDir(tv), "*.md"));
        Assert.False(File.Exists(IdMapPath(tv)));
        Assert.False(File.Exists(LedgerPath(tv)));
        Assert.Equal("", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Add_InvalidUtf8_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "latin1.md");
        // 0xE9 alone is a valid Latin-1 'é' but an invalid UTF-8 sequence.
        // No NUL byte anywhere, so this exercises the decode check rather
        // than the NUL probe.
        File.WriteAllBytes(src, new byte[] { 0x68, 0x69, 0x20, 0xE9, 0x0A });

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "L", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "source-not-text");
        Assert.Empty(Directory.GetFiles(RawDir(tv), "*.md"));
    }

    [Fact]
    public void Add_NonAsciiUtf8Text_StillRegisters()
    {
        using var tv = new TempVault(); Init(tv);
        const string content = "Café ☕ 東京 — em dash, emoji 🎉\nsecond line";
        var src = Path.Combine(tv.Path, "unicode.md");
        File.WriteAllText(src, content, new UTF8Encoding(false));

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "Unicode", "--json");
        Assert.Equal(0, r.ExitCode);

        var raw = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        var (_, _, body) = Wiki.Core.Frontmatter.ReadBlock(raw);
        Assert.Equal(content, body);
    }

    [Fact]
    public void Add_Utf8Bom_IsStrippedFromStoredBody()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "bom.md");
        File.WriteAllText(src, "bom'd content", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "Bom", "--json");
        Assert.Equal(0, r.ExitCode);

        var raw = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        var (_, _, body) = Wiki.Core.Frontmatter.ReadBlock(raw);
        // Exact equality is the BOM assertion: a surviving U+FEFF would sit at
        // position 0 and fail this.
        Assert.Equal("bom'd content", body);
        Assert.NotEqual('\uFEFF', body[0]);
    }

    [Fact]
    public void Add_SameDocument_CrlfThenLf_IsDeduped()
    {
        using var tv = new TempVault(); Init(tv);

        var crlf = Path.Combine(tv.Path, "win.md");
        File.WriteAllText(crlf, "line one\r\nline two\r\nline three\r\n");

        var first = tv.Run("source", "add", crlf, "--category", "article", "--title", "Windows copy", "--json");
        Assert.Equal(0, first.ExitCode);
        var firstId = ((JsonElement)first.Envelope.Data!).GetProperty("id").GetString();

        var lf = Path.Combine(tv.Path, "nix.md");
        File.WriteAllText(lf, "line one\nline two\nline three\n");

        var second = tv.Run("source", "add", lf, "--category", "article", "--title", "Linux copy", "--json");
        Assert.Equal(1, second.ExitCode);
        Assert.Contains(second.Envelope.Errors, e => e.Code == "duplicate-source");
        Assert.Contains(firstId!, second.Envelope.Errors[0].Message);

        Assert.Single(Directory.GetFiles(RawDir(tv), "*.md"));
    }

    [Fact]
    public void Add_CrlfInput_IsStoredAsLf_AndHashedAsLf()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "win.md");
        File.WriteAllText(src, "alpha\r\nbeta\r\n");

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "W", "--json");
        Assert.Equal(0, r.ExitCode);

        var raw = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        var (scalars, lists, body) = Wiki.Core.Frontmatter.ReadBlock(raw);
        var front = Wiki.Core.SourceFrontmatter.FromRaw(scalars, lists);

        Assert.Equal("alpha\nbeta\n", body);
        // On the RAW file text, not the parsed body - Frontmatter.ReadBlock
        // normalises CRLF on the way out, so asserting on `body` alone would
        // pass even if the bytes on disk were still CRLF.
        Assert.DoesNotContain("\r", raw);

        // The stored hash is of the LF form, not of the bytes as they arrived.
        var expected = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("alpha\nbeta\n"))).ToLowerInvariant();
        Assert.Equal(expected, front.Sha256);
    }

    // The legacy-tolerant half of the dedup fix: a source registered by an
    // older build carries a hash of its raw CRLF bytes. Simulated here by
    // hand-writing a raw/ file the way that build would have, then checking a
    // fresh add of the same document (LF) is still caught as a duplicate.
    [Fact]
    public void Add_LegacyCrlfHashedSource_StillDeduped()
    {
        using var tv = new TempVault(); Init(tv);

        // Register normally, then rewrite the raw file back to the pre-fix
        // shape: CRLF body, sha256 of the CRLF bytes.
        var seed = Path.Combine(tv.Path, "seed.md");
        File.WriteAllText(seed, "gamma\ndelta\n");
        var first = tv.Run("source", "add", seed, "--category", "article", "--title", "Seed", "--json");
        Assert.Equal(0, first.ExitCode);

        var rawPath = Directory.GetFiles(RawDir(tv), "*.md")[0];
        var (scalars, lists, _) = Wiki.Core.Frontmatter.ReadBlock(File.ReadAllText(rawPath));
        var front = Wiki.Core.SourceFrontmatter.FromRaw(scalars, lists);

        const string crlfBody = "gamma\r\ndelta\r\n";
        var legacySha = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(crlfBody))).ToLowerInvariant();

        var legacyFront = new Wiki.Core.SourceFrontmatter
        {
            Id = front.Id,
            Title = front.Title,
            Category = front.Category,
            Added = front.Added,
            Sha256 = legacySha,
            Origin = front.Origin,
            Status = front.Status,
        };
        File.WriteAllText(rawPath, legacyFront.ToBlock() + "\n" + crlfBody);

        // Same document, LF this time - the stored (legacy) hash cannot match,
        // so dedup has to fall back to re-hashing the stored body the new way.
        var again = Path.Combine(tv.Path, "again.md");
        File.WriteAllText(again, "gamma\ndelta\n");
        var second = tv.Run("source", "add", again, "--category", "article", "--title", "Again", "--json");

        Assert.Equal(1, second.ExitCode);
        Assert.Contains(second.Envelope.Errors, e => e.Code == "duplicate-source");
        Assert.Single(Directory.GetFiles(RawDir(tv), "*.md"));
    }
}
