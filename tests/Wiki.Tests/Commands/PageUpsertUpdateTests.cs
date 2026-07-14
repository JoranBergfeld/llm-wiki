using System;
using System.IO;
using System.Text.Json;
using Wiki.Core;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 13: `wiki page upsert --id` update path. Mirrors Task 12's
// PageUpsertCreateTests conventions: every rejection test asserts BOTH the
// exit-1/error-code contract AND the "nothing lands" invariant (file / idmap
// / index byte-unchanged on a rejected update) - blocking validation must run
// to completion before any write happens, same invariant as create.
public class PageUpsertUpdateTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");

    // Pulls "id" out of the raw JSON line rather than r.Envelope.Data (which
    // deserializes as `object?` and doesn't round-trip back into UpsertResult's
    // typed fields without a second source-gen registration) - same technique
    // InitTests uses for InitResult's fields.
    private static string ExtractId(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    [Fact]
    public void Update_ReplacesBodySummarySourcesTags_PreservesIdAndCreated_SetsUpdatedToday_RefreshesIndex()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Old body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "Old summary", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var before = PageDoc.Parse(File.ReadAllText(file));

        var updated = tv.RunStdin("New body text with more detail.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Contoso", "--summary", "New summary",
            "--tags", "alpha,beta", "--json");
        Assert.Equal(0, updated.ExitCode);

        var after = PageDoc.Parse(File.ReadAllText(file));
        Assert.Contains("New body text with more detail.", after.Body);
        Assert.DoesNotContain("Old body.", after.Body);
        Assert.Equal("New summary", after.Front.Summary);
        Assert.Equal(new[] { "alpha", "beta" }, after.Front.Tags);
        Assert.Equal(id, after.Front.Id);
        Assert.Equal(before.Front.Created, after.Front.Created);
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), after.Front.Updated);

        var index = File.ReadAllText(IndexPath(tv));
        Assert.Contains("New summary", index);
        Assert.DoesNotContain("Old summary", index);
        Assert.Contains("upsert", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Update_UnknownId_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var fileSnapshot = File.ReadAllText(file);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));

        var r = tv.RunStdin("New body.", "page", "upsert", "--id", "01AAAAAAAAAAAAAAAAAAAAAAAA",
            "--type", "entity", "--title", "X", "--summary", "s2", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");

        Assert.Equal(fileSnapshot, File.ReadAllText(file));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
    }

    [Fact]
    public void Update_StaleIdmapEntry_FileDeleted_Rejected_NothingChanged()
    {
        // idmap knows the id, but its target file is gone from disk (deleted
        // outside the CLI). The !File.Exists arm of the not-found guard must
        // catch this rather than crashing on a read of a missing file.
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        File.Delete(file);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));
        var logSnapshot = File.ReadAllText(LogPath(tv));

        var r = tv.RunStdin("New body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Contoso", "--summary", "s2", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");

        Assert.False(File.Exists(file));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logSnapshot, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Update_TitleMismatch_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var fileSnapshot = File.ReadAllText(file);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));
        var logSnapshot = File.ReadAllText(LogPath(tv));

        var r = tv.RunStdin("New body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Different", "--summary", "s2", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "title-mismatch");

        Assert.Equal(fileSnapshot, File.ReadAllText(file));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logSnapshot, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Update_SummaryWithQuote_Rejected_FrontmatterSchema_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var fileSnapshot = File.ReadAllText(file);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));
        var logSnapshot = File.ReadAllText(LogPath(tv));

        var r = tv.RunStdin("New body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Contoso", "--summary", "has a \" quote", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "frontmatter-schema");

        Assert.Equal(fileSnapshot, File.ReadAllText(file));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logSnapshot, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Update_IdResolvesToSource_Rejected_NothingChanged()
    {
        // A raw/ source id is a valid idmap entry but not a page - upsert
        // --id must reject it the same as an id idmap has never seen.
        using var tv = new TempVault(); Init(tv);
        var idmapPath = IdMapPath(tv);
        Directory.CreateDirectory(Path.Combine(tv.Path, "raw"));
        File.WriteAllText(Path.Combine(tv.Path, "raw", "doc.md"), "fake raw source");
        File.WriteAllText(idmapPath, "{\"01SOURCEFAKEIDAAAAAAAAAAAA\":\"raw/doc.md\"}");
        var idmapSnapshot = File.ReadAllText(idmapPath);
        var indexSnapshot = File.ReadAllText(IndexPath(tv));

        var r = tv.RunStdin("Body.", "page", "upsert", "--id", "01SOURCEFAKEIDAAAAAAAAAAAA",
            "--type", "entity", "--title", "X", "--summary", "s", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
        Assert.Equal(idmapSnapshot, File.ReadAllText(idmapPath));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
    }

    [Fact]
    public void Update_TypeMismatch_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var fileSnapshot = File.ReadAllText(file);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));

        var r = tv.RunStdin("New body.", "page", "upsert", "--id", id,
            "--type", "concept", "--title", "Contoso", "--summary", "s2", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "type-mismatch");

        Assert.Equal(fileSnapshot, File.ReadAllText(file));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki", "concepts", "contoso.md")));
    }

    [Fact]
    public void Update_MissingSummary_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var fileSnapshot = File.ReadAllText(file);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));

        var r = tv.RunStdin("New body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Contoso", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "summary-required");

        Assert.Equal(fileSnapshot, File.ReadAllText(file));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
    }

    [Fact]
    public void Update_UnknownSourceId_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var fileSnapshot = File.ReadAllText(file);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));

        var r = tv.RunStdin("New body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Contoso", "--summary", "s2",
            "--sources", "01NOTAREALSOURCEID0000000", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-source");

        Assert.Equal(fileSnapshot, File.ReadAllText(file));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
    }

    [Fact]
    public void Update_DanglingLink_Rejected_UnlessAllowed()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "concept",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var file = Path.Combine(tv.Path, "wiki", "concepts", "contoso.md");
        var fileSnapshot = File.ReadAllText(file);

        var bad = tv.RunStdin("See [[ghost]].", "page", "upsert", "--id", id,
            "--type", "concept", "--title", "Contoso", "--summary", "s2", "--json");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains(bad.Envelope.Errors, e => e.Code == "dangling-link");
        Assert.Equal(fileSnapshot, File.ReadAllText(file));

        var ok = tv.RunStdin("See [[ghost]].", "page", "upsert", "--id", id,
            "--type", "concept", "--title", "Contoso", "--summary", "s2",
            "--allow-dangling", "--json");
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("[[ghost]]", File.ReadAllText(file));
    }

    [Fact]
    public void Update_SelfLink_DoesNotTripDanglingLink()
    {
        // A page linking to its own slug references something that
        // definitely exists (itself) - must not be treated as dangling.
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "concept",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var r = tv.RunStdin("See [[contoso]] again.", "page", "upsert", "--id", id,
            "--type", "concept", "--title", "Contoso", "--summary", "s2", "--json");
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Update_SameTitle_DoesNotTripDuplicateTitle()
    {
        // Title never changes on update (renames are Task 20), so an update
        // that resubmits the page's own (already-existing) title must not be
        // rejected as a collision with itself.
        using var tv = new TempVault(); Init(tv);
        var other = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Fabrikam", "--summary", "s0", "--json");
        Assert.Equal(0, other.ExitCode);

        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var r = tv.RunStdin("Updated body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Contoso", "--summary", "s2", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain(r.Envelope.Errors, e => e.Code == "duplicate-title");
    }

    [Fact]
    public void Update_Overview_ViaId_Works()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Overview body one.", "page", "upsert", "--type", "overview",
            "--title", "Overview", "--summary", "s1", "--json");
        Assert.Equal(0, created.ExitCode);
        var id = ExtractId(created);

        var overviewPath = Path.Combine(tv.Path, "wiki", "overview.md");
        var before = PageDoc.Parse(File.ReadAllText(overviewPath));

        var updated = tv.RunStdin("Overview body two.", "page", "upsert", "--id", id,
            "--type", "overview", "--title", "Overview", "--summary", "s2", "--json");
        Assert.Equal(0, updated.ExitCode);

        var after = PageDoc.Parse(File.ReadAllText(overviewPath));
        Assert.Contains("Overview body two.", after.Body);
        Assert.Equal("s2", after.Front.Summary);
        Assert.Equal(id, after.Front.Id);
        Assert.Equal(before.Front.Created, after.Front.Created);
        Assert.Contains("s2", File.ReadAllText(IndexPath(tv)));
    }
}
