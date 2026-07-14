using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 20: `wiki page rename` and `wiki page set-status`. Rename moves a
// page's file to a new slug and rewrites every inbound [[wikilink]] so links
// keep resolving after the move; set-status flips the frontmatter `status`
// field in place (the primitive later review-gate/retraction tasks reuse).
// Every rejection test asserts the "nothing lands" invariant, same discipline
// as PageUpsertUpdateTests.
public class RenameTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");
    private static string EntityPath(TempVault tv, string slug) => Path.Combine(tv.Path, "wiki", "entities", slug + ".md");

    // Same technique PageUpsertUpdateTests/BacklinksTests use: Envelope.Data
    // is `object?` and doesn't round-trip into a typed DTO, so pull fields
    // straight out of the raw JSON line instead.
    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string ExtractId(CliResult r) => Data(r).GetProperty("id").GetString()!;

    // -------------------- rename --------------------

    [Fact]
    public void Rename_MovesFile_RewritesInboundLinks_UpdatesIdmapAndIndex()
    {
        using var tv = new TempVault(); Init(tv);

        var contoso = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "The vendor", "--json");
        Assert.Equal(0, contoso.ExitCode);
        var contosoId = ExtractId(contoso);

        var pageA = tv.RunStdin("See [[contoso]] for details.", "page", "upsert", "--type", "entity",
            "--title", "Alpha", "--summary", "links to contoso", "--json");
        Assert.Equal(0, pageA.ExitCode);

        var r = tv.Run("page", "rename", contosoId, "acme", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(contosoId, data.GetProperty("id").GetString());
        Assert.Equal("contoso", data.GetProperty("oldSlug").GetString());
        Assert.Equal("acme", data.GetProperty("newSlug").GetString());
        Assert.Equal(1, data.GetProperty("linksRewritten").GetInt32());

        // The file moved: entities/acme.md exists, entities/contoso.md gone.
        Assert.True(File.Exists(EntityPath(tv, "acme")));
        Assert.False(File.Exists(EntityPath(tv, "contoso")));

        // A's body now links to [[acme]], not [[contoso]].
        var aBody = File.ReadAllText(EntityPath(tv, "alpha"));
        Assert.Contains("[[acme]]", aBody);
        Assert.DoesNotContain("[[contoso]]", aBody);

        // idmap now resolves the id to the new path.
        var idmapJson = File.ReadAllText(IdMapPath(tv));
        var idmap = JsonSerializer.Deserialize(idmapJson, Wiki.Json.WikiJsonContext.Default.DictionaryStringString)!;
        Assert.Equal("wiki/entities/acme.md", idmap[contosoId]);

        // index.md shows the new slug, not the old one.
        var index = File.ReadAllText(IndexPath(tv));
        Assert.Contains("[[acme]]", index);
        Assert.DoesNotContain("[[contoso]]", index);

        Assert.Contains("rename", File.ReadAllText(LogPath(tv)));

        // page show by the id still resolves - to the new slug.
        var show = tv.Run("page", "show", contosoId, "--json");
        Assert.Equal(0, show.ExitCode);
        Assert.Equal("acme", Data(show).GetProperty("slug").GetString());
    }

    [Fact]
    public void Rename_ToTakenSlug_Rejected_NothingMoves()
    {
        using var tv = new TempVault(); Init(tv);

        var contoso = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s1", "--json");
        var contosoId = ExtractId(contoso);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Acme", "--summary", "s2", "--json");

        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));
        var indexSnapshot = File.ReadAllText(IndexPath(tv));
        var logSnapshot = File.ReadAllText(LogPath(tv));
        var contosoSnapshot = File.ReadAllText(EntityPath(tv, "contoso"));

        var r = tv.Run("page", "rename", contosoId, "acme", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "slug-taken");

        Assert.True(File.Exists(EntityPath(tv, "contoso")));
        Assert.True(File.Exists(EntityPath(tv, "acme")));
        Assert.Equal(contosoSnapshot, File.ReadAllText(EntityPath(tv, "contoso")));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexSnapshot, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logSnapshot, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Rename_UnknownId_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        // No page has been created yet, so idmap.json doesn't exist on disk
        // at all - the rejection must not conjure one into existence.
        var r = tv.Run("page", "rename", "01AAAAAAAAAAAAAAAAAAAAAAAA", "whatever", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-id");
        Assert.False(File.Exists(IdMapPath(tv)));
    }

    [Fact]
    public void Rename_Overview_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Welcome.", "page", "upsert", "--type", "overview",
            "--title", "Overview", "--summary", "s", "--json");
        var id = ExtractId(created);

        var overviewPath = Path.Combine(tv.Path, "wiki", "overview.md");
        var snapshot = File.ReadAllText(overviewPath);

        var r = tv.Run("page", "rename", id, "welcome", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "cannot-rename-overview");
        Assert.Equal(snapshot, File.ReadAllText(overviewPath));
        Assert.True(File.Exists(overviewPath));
    }

    [Fact]
    public void Rename_NonNormalizedSlug_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);
        var snapshot = File.ReadAllText(EntityPath(tv, "contoso"));

        var r = tv.Run("page", "rename", id, "Not Clean!", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-slug");
        Assert.True(File.Exists(EntityPath(tv, "contoso")));
        Assert.Equal(snapshot, File.ReadAllText(EntityPath(tv, "contoso")));
    }

    [Fact]
    public void Rename_ReadOnlyPages_Unaffected_NoSpuriousRewrite()
    {
        // A page whose body does NOT link to the renamed slug must be left
        // byte-for-byte untouched (only linking pages get rewritten).
        using var tv = new TempVault(); Init(tv);
        var contoso = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var contosoId = ExtractId(contoso);
        tv.RunStdin("Nothing to see here.", "page", "upsert", "--type", "entity",
            "--title", "Bystander", "--summary", "s2", "--json");

        var bystanderSnapshot = File.ReadAllText(EntityPath(tv, "bystander"));

        var r = tv.Run("page", "rename", contosoId, "acme", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, Data(r).GetProperty("linksRewritten").GetInt32());
        Assert.Equal(bystanderSnapshot, File.ReadAllText(EntityPath(tv, "bystander")));
    }

    // -------------------- set-status --------------------

    [Fact]
    public void SetStatus_ChangesStatus_ReflectsInIndex_SetsUpdatedToday()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "concept",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);

        var r = tv.Run("page", "set-status", id, "archived", "--json");
        Assert.Equal(0, r.ExitCode);

        var file = Path.Combine(tv.Path, "wiki", "concepts", "contoso.md");
        var after = Wiki.Core.PageDoc.Parse(File.ReadAllText(file));
        Assert.Equal(Wiki.Core.PageStatus.Archived, after.Front.Status);
        Assert.Equal(System.DateTime.UtcNow.ToString("yyyy-MM-dd"), after.Front.Updated);

        // archived pages are excluded from the index entirely (IndexFile.GroupedEntries).
        var index = File.ReadAllText(IndexPath(tv));
        Assert.DoesNotContain("contoso", index);

        Assert.Contains("set-status", File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void SetStatus_UnknownId_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("page", "set-status", "01AAAAAAAAAAAAAAAAAAAAAAAA", "active", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-id");
    }

    [Fact]
    public void SetStatus_InvalidStatus_Rejected_NothingChanged()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);
        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var snapshot = File.ReadAllText(file);

        var r = tv.Run("page", "set-status", id, "not-a-status", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-page-status");
        Assert.Equal(snapshot, File.ReadAllText(file));
    }
}
