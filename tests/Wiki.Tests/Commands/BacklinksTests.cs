using System;
using System.IO;
using System.Text.Json;
using Wiki.Core;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 19: `wiki page backlinks`, `wiki page list --orphans`, `wiki index
// show`. All three are read-only query commands - no idmap/index/log write.
public class BacklinksTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");

    // Envelope.Data is `object?` and doesn't round-trip into a typed DTO -
    // same technique PageShowListTests uses: pull fields off the raw JSON.
    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    // -------------------- backlinks --------------------

    [Fact]
    public void Backlinks_PageALinksToB_ReturnsA()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Beta", "--summary", "s", "--json");
        tv.RunStdin("Links to [[beta]].", "page", "upsert", "--type", "entity", "--title", "Alpha", "--summary", "s", "--json");

        var r = tv.Run("page", "backlinks", "beta", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("alpha", data[0].GetString());
    }

    [Fact]
    public void Backlinks_ById_ResolvesSamePage()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Beta", "--summary", "s", "--json");
        var id = Data(created).GetProperty("id").GetString();
        tv.RunStdin("Links to [[beta]].", "page", "upsert", "--type", "entity", "--title", "Alpha", "--summary", "s", "--json");

        var r = tv.Run("page", "backlinks", id!, "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("alpha", data[0].GetString());
    }

    [Fact]
    public void Backlinks_PageWithNoInboundLinks_ReturnsEmptyArray()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Gamma", "--summary", "s", "--json");

        var r = tv.Run("page", "backlinks", "gamma", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(0, data.GetArrayLength());
    }

    [Fact]
    public void Backlinks_NonexistentPage_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("page", "backlinks", "no-such-page", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    // -------------------- list --orphans --------------------

    [Fact]
    public void ListOrphans_PageWithNoInboundLinks_Appears()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Beta", "--summary", "s", "--json");
        tv.RunStdin("Links to [[beta]].", "page", "upsert", "--type", "entity", "--title", "Alpha", "--summary", "s", "--json");
        // Charlie links to Alpha (so Alpha isn't itself an orphan) but
        // nobody links to Charlie -> Charlie is the sole orphan.
        tv.RunStdin("Links to [[alpha]].", "page", "upsert", "--type", "entity", "--title", "Charlie", "--summary", "s", "--json");

        var r = tv.Run("page", "list", "--orphans", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("charlie", data[0].GetProperty("slug").GetString());
    }

    [Fact]
    public void ListOrphans_LinkedPage_IsNotReported()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Beta", "--summary", "s", "--json");
        tv.RunStdin("Links to [[beta]].", "page", "upsert", "--type", "entity", "--title", "Alpha", "--summary", "s", "--json");

        var r = tv.Run("page", "list", "--orphans", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        var slugs = new System.Collections.Generic.List<string>();
        for (var i = 0; i < data.GetArrayLength(); i++) slugs.Add(data[i].GetProperty("slug").GetString()!);
        Assert.DoesNotContain("beta", slugs);
    }

    [Fact]
    public void ListOrphans_ExcludesOverview()
    {
        using var tv = new TempVault(); Init(tv);
        // Overview is a singleton with no inbound links by construction, but
        // per §11 orphan detection targets active content pages, not the
        // vault's own entry point - it must never show up under --orphans.
        tv.RunStdin("Welcome.", "page", "upsert", "--type", "overview", "--title", "Overview", "--summary", "s", "--json");

        var r = tv.Run("page", "list", "--orphans", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        var slugs = new System.Collections.Generic.List<string>();
        for (var i = 0; i < data.GetArrayLength(); i++) slugs.Add(data[i].GetProperty("slug").GetString()!);
        Assert.DoesNotContain("overview", slugs);
    }

    [Fact]
    public void ListOrphans_ExcludesPendingReview()
    {
        // No CLI path creates a pending-review page yet (the review gate is
        // Task 23 - every `page upsert` today lands `active`), so this test
        // hand-writes a pending-review page file directly, same technique
        // PageShowListTests.List_FilterByStatus_OnlyMatchesThatStatus uses
        // for `archived`.
        using var tv = new TempVault(); Init(tv);

        var pendingFront = new PageFrontmatter
        {
            Id = WikiUlid.New(1_700_000_000_000, new byte[10]),
            Type = PageType.Concept,
            Title = "Pending Concept",
            Status = PageStatus.PendingReview,
            Created = "2024-01-01",
            Updated = "2024-01-01",
            Summary = "not yet reviewed",
            Sources = Array.Empty<string>(),
            Tags = Array.Empty<string>(),
        };
        var doc = new PageDoc(pendingFront, "No inbound links, and not active yet.");
        Directory.CreateDirectory(Path.Combine(tv.Path, "wiki", "concepts"));
        File.WriteAllText(Path.Combine(tv.Path, "wiki", "concepts", "pending-concept.md"), doc.Serialize());

        var r = tv.Run("page", "list", "--orphans", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        var slugs = new System.Collections.Generic.List<string>();
        for (var i = 0; i < data.GetArrayLength(); i++) slugs.Add(data[i].GetProperty("slug").GetString()!);
        Assert.DoesNotContain("pending-concept", slugs);
    }

    // -------------------- index show --------------------

    [Fact]
    public void IndexShow_FilterByType_ReturnsEntriesWithExpectedFields()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "The vendor", "--json");
        tv.RunStdin("Body.", "page", "upsert", "--type", "concept", "--title", "Widgets", "--summary", "s2", "--json");

        var r = tv.Run("index", "show", "--type", "entity", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        var item = data[0];
        Assert.Equal("contoso", item.GetProperty("slug").GetString());
        Assert.Equal("Contoso", item.GetProperty("title").GetString());
        Assert.Equal("The vendor", item.GetProperty("summary").GetString());
        Assert.Equal("entity", item.GetProperty("type").GetString());
        Assert.Equal(0, item.GetProperty("sourcesCount").GetInt32());
        Assert.True(WikiUlid.IsValid(item.GetProperty("id").GetString()!));
    }

    [Fact]
    public void IndexShow_NoFilter_MatchesIndexFileContent()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        tv.RunStdin("Body.", "page", "upsert", "--type", "concept", "--title", "Widgets", "--summary", "s2", "--json");

        var r = tv.Run("index", "show", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(2, data.GetArrayLength());
        // index.md groups Overview -> Concepts -> Entities -> Summaries, so
        // the concept must be listed before the entity - unlike `page list`,
        // which is in PageStore.Enumerate's summary/entity/concept order.
        Assert.Equal("widgets", data[0].GetProperty("slug").GetString());
        Assert.Equal("contoso", data[1].GetProperty("slug").GetString());
    }

    [Fact]
    public void IndexShow_ExcludesArchivedPages()
    {
        using var tv = new TempVault(); Init(tv);
        var archivedFront = new PageFrontmatter
        {
            Id = WikiUlid.New(1_700_000_000_000, new byte[10]),
            Type = PageType.Concept,
            Title = "Old Concept",
            Status = PageStatus.Archived,
            Created = "2024-01-01",
            Updated = "2024-01-01",
            Summary = "stale",
            Sources = Array.Empty<string>(),
            Tags = Array.Empty<string>(),
        };
        var doc = new PageDoc(archivedFront, "Archived body.");
        Directory.CreateDirectory(Path.Combine(tv.Path, "wiki", "concepts"));
        File.WriteAllText(Path.Combine(tv.Path, "wiki", "concepts", "old-concept.md"), doc.Serialize());

        var r = tv.Run("index", "show", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, Data(r).GetArrayLength());
    }

    // -------------------- read-only --------------------

    [Fact]
    public void AllThreeCommands_AreReadOnly_NothingOnDiskChanges()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Beta", "--summary", "s", "--json");
        tv.RunStdin("Links to [[beta]].", "page", "upsert", "--type", "entity", "--title", "Alpha", "--summary", "s", "--json");

        var idmapBefore = File.ReadAllText(IdMapPath(tv));
        var indexBefore = File.ReadAllText(IndexPath(tv));
        var logBefore = File.ReadAllText(LogPath(tv));
        var filesBefore = Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length;

        tv.Run("page", "backlinks", "beta", "--json");
        tv.Run("page", "list", "--orphans", "--json");
        tv.Run("index", "show", "--json");

        Assert.Equal(idmapBefore, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexBefore, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logBefore, File.ReadAllText(LogPath(tv)));
        Assert.Equal(filesBefore, Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length);
    }
}
