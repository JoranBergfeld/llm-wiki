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
        Assert.Equal(0, ok.ExitCode); // permitted, and filed as a dangling-link issue by the upsert (amendment L)
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
    public void Create_SecondOverviewWithDifferentTitle_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var first = tv.RunStdin("Body one.", "page", "upsert", "--type", "overview",
            "--title", "Overview", "--summary", "s1", "--json");
        Assert.Equal(0, first.ExitCode);

        var overviewPath = Path.Combine(tv.Path, "wiki", "overview.md");
        var overviewSnapshot = File.ReadAllText(overviewPath);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));

        // Different title, still no --id - the duplicate-title check wouldn't
        // catch this (titles differ), so this exercises the dedicated
        // overview-singleton guard instead.
        var second = tv.RunStdin("Body two.", "page", "upsert", "--type", "overview",
            "--title", "Different Title", "--summary", "s2", "--json");
        Assert.Equal(1, second.ExitCode);
        Assert.Contains(second.Envelope.Errors, e => e.Code == "overview-exists");

        Assert.Equal(overviewSnapshot, File.ReadAllText(overviewPath));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
    }

    [Fact]
    public void Create_TitleWithQuote_Rejected_FrontmatterSchema_NothingWritten()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Bad\"Title", "--summary", "s", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "frontmatter-schema");
        Assert.False(Directory.Exists(Path.Combine(tv.Path, "wiki", "entities"))
            && Directory.EnumerateFiles(Path.Combine(tv.Path, "wiki", "entities"), "*.md").Any());
        Assert.False(File.Exists(IdMapPath(tv)));
        Assert.Equal("", File.ReadAllText(IndexPath(tv)));
        Assert.Equal("", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Create_BodyFile_HappyPath_WritesBodyFromFile()
    {
        using var tv = new TempVault(); Init(tv);
        var bodyFile = Path.Combine(tv.Path, "body.txt");
        File.WriteAllText(bodyFile, "Body from a file, no links here.");

        var r = tv.Run("page", "upsert", "--type", "entity", "--title", "Fileco",
            "--summary", "s", "--body-file", bodyFile, "--json");
        Assert.Equal(0, r.ExitCode);
        var file = Path.Combine(tv.Path, "wiki", "entities", "fileco.md");
        Assert.True(File.Exists(file));
        Assert.Contains("Body from a file, no links here.", File.ReadAllText(file));
    }

    [Fact]
    public void Create_SameTitleDifferentType_BothAllowed()
    {
        using var tv = new TempVault(); Init(tv);
        // duplicate-title is scoped per-type, so a concept titled "Acme" is
        // not blocked by an existing entity "Acme". Note the slug *namespace*
        // is global across types (verified against the built binary), so the
        // second write is suffixed to acme-2.md rather than colliding with
        // (or reusing) the entity's acme.md - "allowed" means both writes
        // succeed as distinct pages, not that they share a slug.
        var entity = tv.RunStdin("Body one.", "page", "upsert", "--type", "entity",
            "--title", "Acme", "--summary", "s1", "--json");
        Assert.Equal(0, entity.ExitCode);

        var concept = tv.RunStdin("Body two.", "page", "upsert", "--type", "concept",
            "--title", "Acme", "--summary", "s2", "--json");
        Assert.Equal(0, concept.ExitCode);

        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "acme.md")));
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "concepts", "acme-2.md")));
    }

    [Fact]
    public void Create_SlugCollisionAcrossDifferentTitles_SuffixesSecond()
    {
        using var tv = new TempVault(); Init(tv);
        // "Acme Inc" and "Acme, Inc." both slugify to "acme-inc" under
        // Slug.From (non-alnum runs collapse to a single '-'), so the second
        // create must suffix rather than collide with the first.
        var first = tv.RunStdin("Body one.", "page", "upsert", "--type", "entity",
            "--title", "Acme Inc", "--summary", "s1", "--json");
        Assert.Equal(0, first.ExitCode);

        var second = tv.RunStdin("Body two.", "page", "upsert", "--type", "entity",
            "--title", "Acme, Inc.", "--summary", "s2", "--json");
        Assert.Equal(0, second.ExitCode);

        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "acme-inc.md")));
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "acme-inc-2.md")));
    }

    // Update_WithId_Rejected_NotImplemented lived here for Task 12, when
    // the --id branch was an explicit stub. Task 13 implements that branch
    // for real; its unknown-id / type-mismatch / etc. rejection tests now
    // live in PageUpsertUpdateTests.cs alongside the rest of the update path.

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
