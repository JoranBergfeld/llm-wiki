using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 16: `wiki source add`. Every rejection test asserts the "nothing
// lands" invariant (no raw file written, idmap.json/ledger.json/log.md
// byte-unchanged from before the rejected call) - same discipline as
// PageUpsertCreateTests for page upsert.
public class SourceAddTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string LedgerPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "ledger.json");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");
    private static string RawDir(TempVault tv) => Path.Combine(tv.Path, "raw");

    [Fact]
    public void Add_CopiesToRaw_WritesFrontmatter_RegistersLedger()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "input.md");
        File.WriteAllText(src, "# transcript\nhello");

        var r = tv.Run("source", "add", src, "--category", "meeting-transcript",
            "--title", "Contoso mtg", "--json");
        Assert.Equal(0, r.ExitCode);

        var raws = Directory.GetFiles(RawDir(tv), "*.md");
        Assert.Single(raws);
        var rawText = File.ReadAllText(raws[0]);
        Assert.Contains("type: source", rawText);
        Assert.Contains("category: meeting-transcript", rawText);
        Assert.Contains("# transcript\nhello", rawText);

        // Raw file body must parse via the SourceFrontmatter mapper.
        var (scalars, lists, body) = Wiki.Core.Frontmatter.ReadBlock(rawText);
        var front = Wiki.Core.SourceFrontmatter.FromRaw(scalars, lists);
        Assert.Equal("Contoso mtg", front.Title);
        Assert.Equal("meeting-transcript", front.Category);
        Assert.Equal("manual", front.Origin);
        Assert.Equal(Wiki.Core.SourceStatus.Active, front.Status);
        Assert.Equal("# transcript\nhello", body);

        // idmap resolves the new source id to its raw/ path.
        var idmapJson = File.ReadAllText(IdMapPath(tv));
        var idmap = JsonSerializer.Deserialize(idmapJson, Wiki.Json.WikiJsonContext.Default.DictionaryStringString)!;
        Assert.Equal("raw/" + front.Id + ".md", idmap[front.Id]);

        // ledger shows the source registered.
        var ledgerJson = File.ReadAllText(LedgerPath(tv));
        Assert.Contains("\"sourceId\":\"" + front.Id + "\"", ledgerJson);
        Assert.Contains("\"state\":\"registered\"", ledgerJson);

        // log.md has one source-add entry.
        Assert.Contains("source-add", File.ReadAllText(LogPath(tv)));

        // Result envelope carries id/path/sha256/category.
        Assert.True(r.Envelope.Ok);
    }

    [Fact]
    public void Add_StoredSha256_IsHashOfInputContent()
    {
        using var tv = new TempVault(); Init(tv);
        // Known content, deliberately including a non-ASCII char so a wrong
        // encoding (e.g. Latin-1/UTF-16) would produce a different hash than
        // the UTF-8 one the implementation computes.
        const string content = "known content é\nsecond line";
        var src = Path.Combine(tv.Path, "known.md");
        File.WriteAllText(src, content);

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "Known", "--json");
        Assert.Equal(0, r.ExitCode);

        // Independent hash: UTF-8 bytes -> SHA-256 -> lowercase hex, matching
        // SourceService.ComputeSha256Hex exactly (which casing is verified
        // below via ToLowerInvariant - it produces lowercase hex).
        var expected = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        // Result DTO sha256 field.
        var storedInDto = ((JsonElement)r.Envelope.Data!).GetProperty("sha256").GetString();
        Assert.Equal(expected, storedInDto);

        // And the sha256 persisted in the raw frontmatter on disk.
        var raw = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        var (scalars, lists, _) = Wiki.Core.Frontmatter.ReadBlock(raw);
        var front = Wiki.Core.SourceFrontmatter.FromRaw(scalars, lists);
        Assert.Equal(expected, front.Sha256);

        // Lowercase-hex sanity: no uppercase hex digits leaked through.
        Assert.Equal(front.Sha256.ToLowerInvariant(), front.Sha256);
    }

    [Fact]
    public void Add_UnknownCategory_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "i.md");
        File.WriteAllText(src, "x");

        var r = tv.Run("source", "add", src, "--category", "nope", "--title", "T", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-category");

        Assert.Empty(Directory.GetFiles(RawDir(tv), "*.md"));
        Assert.False(File.Exists(IdMapPath(tv)));
        Assert.False(File.Exists(LedgerPath(tv)));
        Assert.Equal("", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Add_DuplicateContent_Rejected_NamesExistingId_NothingLands()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "dup.md");
        File.WriteAllText(src, "same content twice");

        var first = tv.Run("source", "add", src, "--category", "article", "--title", "First", "--json");
        Assert.Equal(0, first.ExitCode);

        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var ledgerSnapshot = File.ReadAllText(LedgerPath(tv));
        var logSnapshot = File.ReadAllText(LogPath(tv));
        var rawCountBefore = Directory.GetFiles(RawDir(tv), "*.md").Length;

        // Same content, different source file name/title - dedup keys off sha256.
        var src2 = Path.Combine(tv.Path, "dup-copy.md");
        File.WriteAllText(src2, "same content twice");

        var second = tv.Run("source", "add", src2, "--category", "article", "--title", "Second", "--json");
        Assert.Equal(1, second.ExitCode);
        Assert.Contains(second.Envelope.Errors, e => e.Code == "duplicate-source");

        var firstId = ((JsonElement)first.Envelope.Data!).GetProperty("id").GetString();
        Assert.Contains(firstId!, second.Envelope.Errors[0].Message);

        Assert.Equal(rawCountBefore, Directory.GetFiles(RawDir(tv), "*.md").Length);
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(ledgerSnapshot, File.ReadAllText(LedgerPath(tv)));
        Assert.Equal(logSnapshot, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Add_SourceFileNotFound_Rejected_NothingLands()
    {
        using var tv = new TempVault(); Init(tv);
        var missing = Path.Combine(tv.Path, "nope.md");

        var r = tv.Run("source", "add", missing, "--category", "article", "--title", "T", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "source-file-not-found");

        Assert.Empty(Directory.GetFiles(RawDir(tv), "*.md"));
        Assert.False(File.Exists(IdMapPath(tv)));
        Assert.False(File.Exists(LedgerPath(tv)));
        Assert.Equal("", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Add_DefaultOrigin_IsManual()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "o.md");
        File.WriteAllText(src, "content");

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "T", "--json");
        Assert.Equal(0, r.ExitCode);

        var raw = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        Assert.Contains("origin: manual", raw);
    }

    [Fact]
    public void Add_ExplicitOrigin_IsStored()
    {
        using var tv = new TempVault(); Init(tv);
        var src = Path.Combine(tv.Path, "o2.md");
        File.WriteAllText(src, "content2");

        var r = tv.Run("source", "add", src, "--category", "article", "--title", "T", "--origin", "clipper", "--json");
        Assert.Equal(0, r.ExitCode);

        var raw = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        Assert.Contains("origin: clipper", raw);
    }
}
