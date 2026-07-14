using System;
using System.IO;
using System.Text.Json;
using Wiki.Core;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 14: `wiki page show` / `wiki page list` - read-only query commands.
// Every test asserts the JSON envelope contract; a subset also asserts the
// "nothing on disk changed" invariant, since these commands must never write.
public class PageShowListTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");

    // Same technique PageUpsertUpdateTests uses: Envelope.Data is `object?`
    // and doesn't round-trip back into a typed DTO, so pull fields straight
    // out of the raw JSON line instead.
    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string ExtractId(CliResult r) => Data(r).GetProperty("id").GetString()!;

    [Fact]
    public void List_FilterByType_ReturnsOnlyMatchingType()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        tv.RunStdin("Body.", "page", "upsert", "--type", "concept", "--title", "Widgets", "--summary", "s2", "--json");

        var r = tv.Run("page", "list", "--type", "entity", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(1, data.GetArrayLength());
        var item = data[0];
        Assert.Equal("contoso", item.GetProperty("slug").GetString());
        Assert.Equal("entity", item.GetProperty("type").GetString());
        Assert.Equal("Contoso", item.GetProperty("title").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.Equal("s1", item.GetProperty("summary").GetString());
        Assert.Equal(0, item.GetProperty("sourcesCount").GetInt32());
        Assert.True(WikiUlid.IsValid(item.GetProperty("id").GetString()!));
    }

    [Fact]
    public void List_NoFilter_ReturnsAllPagesSortedDeterministically()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        tv.RunStdin("Body.", "page", "upsert", "--type", "concept", "--title", "Widgets", "--summary", "s2", "--json");

        var r = tv.Run("page", "list", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(2, data.GetArrayLength());

        // PageStore.Enumerate scans summaries -> entities -> concepts (each
        // ordinal-sorted within its dir), so the entity "contoso" must come
        // before the concept "widgets". Lock that deterministic order in.
        Assert.Equal("contoso", data[0].GetProperty("slug").GetString());
        Assert.Equal("widgets", data[1].GetProperty("slug").GetString());
    }

    [Fact]
    public void List_FilterBySourcesCount_ReflectsSourcesArrayLength()
    {
        using var tv = new TempVault(); Init(tv);
        // No `source add` command exists yet (later task) - hand-write a raw
        // source + idmap entry directly, same technique
        // Update_IdResolvesToSource_Rejected_NothingChanged uses.
        Directory.CreateDirectory(Path.Combine(tv.Path, "raw"));
        File.WriteAllText(Path.Combine(tv.Path, "raw", "doc.md"), "fake raw source");
        var sourceId = "01SOURCEFAKEIDAAAAAAAAAAAA";
        File.WriteAllText(IdMapPath(tv), $"{{\"{sourceId}\":\"raw/doc.md\"}}");

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Acme",
            "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, r.ExitCode);

        var list = tv.Run("page", "list", "--json");
        var item = Data(list)[0];
        Assert.Equal(1, item.GetProperty("sourcesCount").GetInt32());
    }

    [Fact]
    public void List_FilterByStatus_OnlyMatchesThatStatus()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s1", "--json");
        Assert.Equal(0, created.ExitCode);

        // Every page created via the CLI today lands `active` (review gate is
        // Task 23), so to exercise the filter discriminating a non-active
        // status, hand-write a second page file directly with status
        // `archived` - list only cares what's on disk (PageStore.Enumerate),
        // not how it got there.
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
        File.WriteAllText(Path.Combine(tv.Path, "wiki", "concepts", "old-concept.md"), doc.Serialize());

        var activeList = tv.Run("page", "list", "--status", "active", "--json");
        Assert.Equal(1, Data(activeList).GetArrayLength());
        Assert.Equal("contoso", Data(activeList)[0].GetProperty("slug").GetString());

        var archivedList = tv.Run("page", "list", "--status", "archived", "--json");
        Assert.Equal(1, Data(archivedList).GetArrayLength());
        Assert.Equal("old-concept", Data(archivedList)[0].GetProperty("slug").GetString());
    }

    [Fact]
    public void Show_BySlug_ReturnsFrontmatterAndBody()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("The body text.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "The vendor", "--tags", "a,b", "--json");
        Assert.Equal(0, created.ExitCode);

        var r = tv.Run("page", "show", "contoso", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal("contoso", data.GetProperty("slug").GetString());
        Assert.Equal("entity", data.GetProperty("type").GetString());
        Assert.Equal("Contoso", data.GetProperty("title").GetString());
        Assert.Equal("The vendor", data.GetProperty("summary").GetString());
        Assert.Equal(new[] { "a", "b" }, JsonElementToStrings(data.GetProperty("tags")));
        // Body round-trips verbatim: RunStdin feeds exactly "The body text."
        // (no trailing newline), so show must return that exact string -
        // assert equality so padding/corruption can't slip through.
        Assert.Equal("The body text.", data.GetProperty("body").GetString());
    }

    [Fact]
    public void Show_ById_ResolvesSamePage()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s", "--json");
        var id = ExtractId(created);

        var byId = tv.Run("page", "show", id, "--json");
        var bySlug = tv.Run("page", "show", "contoso", "--json");
        Assert.Equal(0, byId.ExitCode);
        Assert.Equal(0, bySlug.ExitCode);

        Assert.Equal(Data(bySlug).GetProperty("id").GetString(), Data(byId).GetProperty("id").GetString());
        Assert.Equal(Data(bySlug).GetProperty("slug").GetString(), Data(byId).GetProperty("slug").GetString());
        Assert.Equal(id, Data(byId).GetProperty("id").GetString());
    }

    [Fact]
    public void Show_FrontmatterOnly_OmitsBody()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Secret body content.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s", "--json");

        var r = tv.Run("page", "show", "contoso", "--frontmatter-only", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.False(data.TryGetProperty("body", out _));
    }

    [Fact]
    public void Show_NonexistentSlug_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("page", "show", "no-such-page", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Show_NonexistentId_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("page", "show", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Show_IdResolvesToSource_NotFound()
    {
        // A raw/ source id is a valid idmap entry but not a page - show must
        // reject it, same as upsert --id does.
        using var tv = new TempVault(); Init(tv);
        Directory.CreateDirectory(Path.Combine(tv.Path, "raw"));
        File.WriteAllText(Path.Combine(tv.Path, "raw", "doc.md"), "fake raw source");
        var sourceId = "01SOURCEFAKEIDAAAAAAAAAAAA";
        File.WriteAllText(IdMapPath(tv), $"{{\"{sourceId}\":\"raw/doc.md\"}}");

        var r = tv.Run("page", "show", sourceId, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void ShowAndList_AreReadOnly_NothingOnDiskChanges()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s", "--json");

        var idmapBefore = File.ReadAllText(IdMapPath(tv));
        var indexBefore = File.ReadAllText(IndexPath(tv));
        var logBefore = File.ReadAllText(LogPath(tv));
        var pageBefore = File.ReadAllText(Path.Combine(tv.Path, "wiki", "entities", "contoso.md"));
        var filesBefore = Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length;

        tv.Run("page", "list", "--json");
        tv.Run("page", "show", "contoso", "--json");
        tv.Run("page", "list", "--type", "concept", "--status", "archived", "--json");

        Assert.Equal(idmapBefore, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexBefore, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logBefore, File.ReadAllText(LogPath(tv)));
        Assert.Equal(pageBefore, File.ReadAllText(Path.Combine(tv.Path, "wiki", "entities", "contoso.md")));
        Assert.Equal(filesBefore, Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length);
    }

    private static string[] JsonElementToStrings(JsonElement arr)
    {
        var result = new string[arr.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
            result[i] = arr[i].GetString()!;
        return result;
    }
}
