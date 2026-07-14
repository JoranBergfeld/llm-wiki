using System.IO;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 12: `wiki page upsert` create path. Every rejection test asserts BOTH
// the exit-1/error-code contract AND the "nothing lands" invariant (no file
// written, idmap.json / index.md byte-unchanged) - blocking validation must
// run to completion before any write happens.
public class PageUpsertCreateTests
{
    // Brief's given snippet omits --json here, but TempVault.Run/RunStdin always
    // parse the last output line as a JSON envelope - without --json, init's
    // human-readable Spectre line ("OK Initialized vault ...") fails to parse.
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");

    [Fact]
    public void Create_WritesFile_UpdatesIndexAndIdmap_LogsOp()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("Body text.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "The vendor", "--json");
        Assert.Equal(0, r.ExitCode);
        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        Assert.True(File.Exists(file));
        Assert.Contains("[[contoso]]", File.ReadAllText(Path.Combine(tv.Path, "wiki", "index.md")));
        Assert.Contains("upsert", File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
    }

    [Fact]
    public void Create_MissingSummary_Rejected_NothingWritten()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("Body", "page", "upsert", "--type", "entity", "--title", "X", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "summary-required");
        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "x.md")));
        Assert.False(File.Exists(IdMapPath(tv)));
        Assert.Equal("", File.ReadAllText(IndexPath(tv)));
        Assert.Equal("", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Create_DanglingLink_Rejected_UnlessAllowed()
    {
        using var tv = new TempVault(); Init(tv);
        var bad = tv.RunStdin("See [[ghost]].", "page", "upsert", "--type", "concept",
            "--title", "T", "--summary", "s", "--json");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains(bad.Envelope.Errors, e => e.Code == "dangling-link");
        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "concepts", "t.md")));
        Assert.False(File.Exists(IdMapPath(tv)));
        Assert.Equal("", File.ReadAllText(IndexPath(tv)));

        var ok = tv.RunStdin("See [[ghost]].", "page", "upsert", "--type", "concept",
            "--title", "T", "--summary", "s", "--allow-dangling", "--json");
        Assert.Equal(0, ok.ExitCode); // filed as issue in M3; for now just permitted
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "concepts", "t.md")));
    }

    [Fact]
    public void Create_UnknownSourceId_Rejected_NothingWritten()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Acme",
            "--summary", "s", "--sources", "01NOTAREALSOURCEID0000000", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-source");
        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "acme.md")));
        Assert.False(File.Exists(IdMapPath(tv)));
        Assert.Equal("", File.ReadAllText(IndexPath(tv)));
        Assert.Equal("", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Create_DuplicateTitleWithinType_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var first = tv.RunStdin("Body one.", "page", "upsert", "--type", "entity",
            "--title", "Acme", "--summary", "s1", "--json");
        Assert.Equal(0, first.ExitCode);

        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));
        var logSnapshot = File.ReadAllText(LogPath(tv));

        // Case-insensitive match within the same type is still a duplicate.
        var second = tv.RunStdin("Body two.", "page", "upsert", "--type", "entity",
            "--title", "ACME", "--summary", "s2", "--json");
        Assert.Equal(1, second.ExitCode);
        Assert.Contains(second.Envelope.Errors, e => e.Code == "duplicate-title");

        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "acme-2.md")));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logSnapshot, File.ReadAllText(LogPath(tv)));
    }
}
