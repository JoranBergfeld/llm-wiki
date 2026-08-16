using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #8: `wiki source scan <dir> --category <id> [--dry-run]`.
public class SourceScanTests : IDisposable
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string RawDir(TempVault tv) => Path.Combine(tv.Path, "raw");

    // The inbox deliberately lives outside the vault - scan rejects an
    // in-vault inbox, and that is its own test below.
    private readonly string _inbox =
        Path.Combine(Path.GetTempPath(), "wiki-inbox-" + Guid.NewGuid().ToString("N"));

    public SourceScanTests() => Directory.CreateDirectory(_inbox);

    public void Dispose()
    {
        try { Directory.Delete(_inbox, true); } catch { }
    }

    private string Drop(string relative, string content)
    {
        var path = Path.Combine(_inbox, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private void DropBytes(string relative, byte[] bytes)
    {
        var path = Path.Combine(_inbox, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static JsonElement[] Entries(CliResult r) => Data(r).GetProperty("entries").EnumerateArray().ToArray();

    private static JsonElement Entry(CliResult r, string path)
        => Entries(r).Single(e => e.GetProperty("path").GetString() == path);

    [Fact]
    public void Scan_RegistersNewFiles_AndIsANoOpOnRerun()
    {
        using var tv = new TempVault(); Init(tv);
        Drop("first-note.md", "content one");
        Drop("nested/second note.txt", "content two");

        var first = tv.Run("source", "scan", _inbox, "--category", "article", "--json");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(2, Data(first).GetProperty("registered").GetInt32());
        Assert.Equal(2, Directory.GetFiles(RawDir(tv), "*.md").Length);

        // Titles are derived from filenames, separators collapsed to spaces.
        Assert.Equal("first note", Entry(first, "first-note.md").GetProperty("detail").GetString());
        Assert.Equal("second note", Entry(first, "nested/second note.txt").GetProperty("detail").GetString());

        // Re-running immediately changes nothing.
        var second = tv.Run("source", "scan", _inbox, "--category", "article", "--json");
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(0, Data(second).GetProperty("registered").GetInt32());
        Assert.Equal(2, Data(second).GetProperty("skippedDuplicate").GetInt32());
        Assert.Equal(2, Directory.GetFiles(RawDir(tv), "*.md").Length);
    }

    [Fact]
    public void Scan_RecordsInboxPathAsOrigin()
    {
        using var tv = new TempVault(); Init(tv);
        Drop("nested/deep/report.md", "reportish content");

        var r = tv.Run("source", "scan", _inbox, "--category", "article", "--json");
        Assert.Equal(0, r.ExitCode);

        var raw = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        var (scalars, lists, _) = Wiki.Core.Frontmatter.ReadBlock(raw);
        var front = Wiki.Core.SourceFrontmatter.FromRaw(scalars, lists);
        Assert.Equal("nested/deep/report.md", front.Origin);
        Assert.Equal("article", front.Category);
    }

    [Fact]
    public void Scan_RejectedFile_DoesNotStopTheBatch()
    {
        using var tv = new TempVault(); Init(tv);
        Drop("aaa-good.md", "good content aaa");
        DropBytes("bbb-binary.md", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0x01, 0x02 });
        Drop("ccc-good.md", "good content ccc");

        var r = tv.Run("source", "scan", _inbox, "--category", "article", "--json");

        // The scan itself succeeded; per-file failures are data.
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Envelope.Ok);
        Assert.Equal(2, Data(r).GetProperty("registered").GetInt32());
        Assert.Equal(1, Data(r).GetProperty("rejected").GetInt32());

        var bad = Entry(r, "bbb-binary.md");
        Assert.Equal("rejected", bad.GetProperty("outcome").GetString());
        Assert.Equal("source-not-text", bad.GetProperty("code").GetString());

        Assert.Equal(2, Directory.GetFiles(RawDir(tv), "*.md").Length);
    }

    [Fact]
    public void Scan_DryRun_WritesNothing()
    {
        using var tv = new TempVault(); Init(tv);
        Drop("one.md", "dry one");
        Drop("two.md", "dry two");

        var logBefore = File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md"));

        var r = tv.Run("source", "scan", _inbox, "--category", "article", "--dry-run", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.True(Data(r).GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, Data(r).GetProperty("wouldRegister").GetInt32());
        Assert.Equal(0, Data(r).GetProperty("registered").GetInt32());

        Assert.Empty(Directory.GetFiles(RawDir(tv), "*.md"));
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "ledger.json")));
        Assert.Equal(logBefore, File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
    }

    // Two byte-identical files in one inbox: the first registers, the second
    // must be reported as a duplicate against it - the batch dedups against
    // itself, not just against what was on disk when it started. The dry run
    // has to predict the same thing.
    [Fact]
    public void Scan_IdenticalFilesInOneBatch_SecondIsADuplicate()
    {
        using var tv = new TempVault(); Init(tv);
        Drop("a.md", "identical body");
        Drop("b.md", "identical body");

        var dry = tv.Run("source", "scan", _inbox, "--category", "article", "--dry-run", "--json");
        Assert.Equal(1, Data(dry).GetProperty("wouldRegister").GetInt32());
        Assert.Equal(1, Data(dry).GetProperty("skippedDuplicate").GetInt32());

        var real = tv.Run("source", "scan", _inbox, "--category", "article", "--json");
        Assert.Equal(1, Data(real).GetProperty("registered").GetInt32());
        Assert.Equal(1, Data(real).GetProperty("skippedDuplicate").GetInt32());
        Assert.Single(Directory.GetFiles(RawDir(tv), "*.md"));
    }

    [Fact]
    public void Scan_SkipsDotFilesAndDotDirectories_AndEmptyFiles()
    {
        using var tv = new TempVault(); Init(tv);
        Drop(".DS_Store", "junk");
        Drop(".git/config", "junk too");
        Drop("empty.md", "   \n  ");
        Drop("real.md", "real content");

        var r = tv.Run("source", "scan", _inbox, "--category", "article", "--json");
        Assert.Equal(0, r.ExitCode);

        var paths = Entries(r).Select(e => e.GetProperty("path").GetString()).ToArray();
        Assert.DoesNotContain(".DS_Store", paths);
        Assert.DoesNotContain(".git/config", paths);

        Assert.Equal(1, Data(r).GetProperty("registered").GetInt32());
        Assert.Equal(1, Data(r).GetProperty("skippedEmpty").GetInt32());
        Assert.Equal("skipped-empty", Entry(r, "empty.md").GetProperty("outcome").GetString());
    }

    [Fact]
    public void Scan_UnknownCategory_Rejected_NothingLands()
    {
        using var tv = new TempVault(); Init(tv);
        Drop("x.md", "content");

        var r = tv.Run("source", "scan", _inbox, "--category", "nope", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-category");
        Assert.Empty(Directory.GetFiles(RawDir(tv), "*.md"));
    }

    [Fact]
    public void Scan_MissingDirectory_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var missing = Path.Combine(Path.GetTempPath(), "wiki-inbox-missing-" + Guid.NewGuid().ToString("N"));

        var r = tv.Run("source", "scan", missing, "--category", "article", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "scan-dir-not-found");
    }

    // An inbox inside the vault would let a scan launder the vault's own
    // generated output back in as source material.
    [Fact]
    public void Scan_InboxInsideTheVault_Rejected()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.Run("source", "scan", Path.Combine(tv.Path, "raw"), "--category", "article", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "scan-dir-in-vault");

        var atRoot = tv.Run("source", "scan", tv.Path, "--category", "article", "--json");
        Assert.Equal(1, atRoot.ExitCode);
        Assert.Contains(atRoot.Envelope.Errors, e => e.Code == "scan-dir-in-vault");
    }

    [Fact]
    public void Scan_RegisteredSources_EnterTheLedgerAndAreIngestible()
    {
        using var tv = new TempVault(); Init(tv);
        Drop("ingestible.md", "something worth summarising");

        var r = tv.Run("source", "scan", _inbox, "--category", "article", "--json");
        var id = Entry(r, "ingestible.md").GetProperty("id").GetString();

        var status = tv.Run("ingest", "status", "--json");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains(id!, status.Stdout);

        // And the raw body is readable through the normal source path.
        var show = tv.Run("source", "show", id!, "--json");
        Assert.Equal("something worth summarising", ((JsonElement)show.Envelope.Data!).GetProperty("body").GetString());
    }
}
