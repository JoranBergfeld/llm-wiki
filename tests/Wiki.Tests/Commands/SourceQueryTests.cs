using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 20: `wiki source list/show/impact` - the read-only query trio over
// raw/ (immutable) sources. Every test asserts the JSON envelope contract; a
// dedicated test also asserts the "nothing on disk changes" invariant, since
// raw/ is immutable and these three commands must never write to it (or
// anywhere else).
public class SourceQueryTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string RawDir(TempVault tv) => Path.Combine(tv.Path, "raw");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string AddSource(TempVault tv, string fileName, string content, string category, string title)
    {
        var src = Path.Combine(tv.Path, fileName);
        File.WriteAllText(src, content);
        var r = tv.Run("source", "add", src, "--category", category, "--title", title, "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    // -------------------- source impact --------------------

    [Fact]
    public void Impact_ListsPageCitingIt()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "# transcript\nhello", "meeting-transcript", "Contoso mtg");

        var page = tv.RunStdin("Summary body.", "page", "upsert", "--type", "summary",
            "--title", "Contoso mtg summary", "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, page.ExitCode);

        var r = tv.Run("source", "impact", sourceId, "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        var entry = data[0];
        Assert.Equal("contoso-mtg-summary", entry.GetProperty("slug").GetString());
        Assert.Equal("Contoso mtg summary", entry.GetProperty("title").GetString());
        Assert.Equal("summary", entry.GetProperty("type").GetString());
        Assert.Equal("active", entry.GetProperty("status").GetString());
    }

    [Fact]
    public void Impact_NoPagesCiting_ReturnsEmptyArray()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "content", "article", "Lonely source");

        var r = tv.Run("source", "impact", sourceId, "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(JsonValueKind.Array, Data(r).ValueKind);
        Assert.Equal(0, Data(r).GetArrayLength());
    }

    [Fact]
    public void Impact_UnknownId_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("source", "impact", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Impact_IdResolvesToPage_NotFound()
    {
        // A page id is a valid idmap entry but not a source - impact must
        // reject it, mirroring how upsert --id rejects a source id.
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, created.ExitCode);
        var pageId = Data(created).GetProperty("id").GetString()!;

        var r = tv.Run("source", "impact", pageId, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    // -------------------- source list --------------------

    [Fact]
    public void List_FilterByCategory_OnlyMatchesThatCategory()
    {
        using var tv = new TempVault(); Init(tv);
        var articleId = AddSource(tv, "a.md", "article content", "article", "An article");
        AddSource(tv, "b.md", "meeting content", "meeting-transcript", "A meeting");

        var r = tv.Run("source", "list", "--category", "article", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal(articleId, data[0].GetProperty("id").GetString());
        Assert.Equal("article", data[0].GetProperty("category").GetString());
    }

    [Fact]
    public void List_FilterByStatus_OnlyMatchesThatStatus()
    {
        // No `source retract` command exists yet (later M3 task), so to
        // exercise the status filter discriminating a non-active status,
        // hand-write a second raw file directly with status `retracted` -
        // list only cares what's on disk, not how it got there (same
        // technique PageShowListTests.List_FilterByStatus uses for `archived`).
        using var tv = new TempVault(); Init(tv);
        var activeId = AddSource(tv, "a.md", "active content", "article", "Active source");

        var retractedFront = new Wiki.Core.SourceFrontmatter
        {
            Id = Wiki.Core.WikiUlid.New(1_700_000_000_000, new byte[10]),
            Title = "Retracted source",
            Category = "article",
            Added = "2024-01-01",
            Sha256 = "deadbeef",
            Origin = "manual",
            Status = Wiki.Core.SourceStatus.Retracted,
        };
        File.WriteAllText(Path.Combine(RawDir(tv), retractedFront.Id + ".md"),
            retractedFront.ToBlock() + "\nretracted body");

        var activeList = tv.Run("source", "list", "--status", "active", "--json");
        Assert.Equal(1, Data(activeList).GetArrayLength());
        Assert.Equal(activeId, Data(activeList)[0].GetProperty("id").GetString());

        var retractedList = tv.Run("source", "list", "--status", "retracted", "--json");
        Assert.Equal(1, Data(retractedList).GetArrayLength());
        Assert.Equal(retractedFront.Id, Data(retractedList)[0].GetProperty("id").GetString());
    }

    [Fact]
    public void List_NoFilter_ReturnsAllSources()
    {
        using var tv = new TempVault(); Init(tv);
        AddSource(tv, "a.md", "content a", "article", "A");
        AddSource(tv, "b.md", "content b", "meeting-transcript", "B");

        var r = tv.Run("source", "list", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(2, Data(r).GetArrayLength());
    }

    // -------------------- source show --------------------

    [Fact]
    public void Show_ReturnsFrontmatterAndBody()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "# transcript\nhello world", "meeting-transcript", "Contoso mtg");

        var r = tv.Run("source", "show", sourceId, "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(sourceId, data.GetProperty("id").GetString());
        Assert.Equal("Contoso mtg", data.GetProperty("title").GetString());
        Assert.Equal("meeting-transcript", data.GetProperty("category").GetString());
        Assert.Equal("active", data.GetProperty("status").GetString());
        Assert.Equal("manual", data.GetProperty("origin").GetString());
        Assert.Equal("# transcript\nhello world", data.GetProperty("body").GetString());
    }

    [Fact]
    public void Show_FrontmatterOnly_OmitsBody()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "secret content", "article", "T");

        var r = tv.Run("source", "show", sourceId, "--frontmatter-only", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.False(Data(r).TryGetProperty("body", out _));
    }

    [Fact]
    public void Show_UnknownId_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("source", "show", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Show_IdResolvesToPage_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var pageId = Data(created).GetProperty("id").GetString()!;

        var r = tv.Run("source", "show", pageId, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    // -------------------- read-only --------------------

    [Fact]
    public void AllThreeQueries_AreReadOnly_NothingOnDiskChanges()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "content", "article", "T");
        var page = tv.RunStdin("Body.", "page", "upsert", "--type", "summary",
            "--title", "Summary page", "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, page.ExitCode);

        var filesBefore = Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length;
        var rawSnapshot = File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]);
        var idmapSnapshot = File.ReadAllText(IdMapPath(tv));

        tv.Run("source", "list", "--json");
        tv.Run("source", "show", sourceId, "--json");
        tv.Run("source", "impact", sourceId, "--json");

        Assert.Equal(filesBefore, Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(rawSnapshot, File.ReadAllText(Directory.GetFiles(RawDir(tv), "*.md")[0]));
        Assert.Equal(idmapSnapshot, File.ReadAllText(IdMapPath(tv)));
    }
}
