using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 18: `wiki search` - the agent's retrieval primitive. Plain-text
// (default, case-insensitive substring) or --regex search over every page's
// raw text (frontmatter + body), returning MATCH LINES only - never full
// bodies. Every test asserts the JSON envelope contract; several also assert
// the "search writes nothing" invariant, since this is a read-only command.
public class SearchTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");

    // Envelope.Data is `object?` and doesn't round-trip back into a typed
    // DTO, so pull fields straight out of the raw JSON line - same technique
    // PageShowListTests uses.
    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public void Search_DistinctiveTerm_ReturnsOneHitWithCorrectLineAndText()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin(
            "Line one.\nThis line mentions platform engineering.\nLine three.",
            "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        tv.RunStdin(
            "Nothing interesting here.\nJust filler text.",
            "page", "upsert", "--type", "concept", "--title", "Widgets", "--summary", "s2", "--json");

        var r = tv.Run("search", "platform", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(1, data.GetArrayLength());

        var hit = data[0];
        Assert.Equal("This line mentions platform engineering.", hit.GetProperty("matchLine").GetString());
        Assert.Equal("Contoso", hit.GetProperty("title").GetString());
        Assert.Equal("wiki/entities/contoso.md", hit.GetProperty("path").GetString());
        Assert.True(Wiki.Core.WikiUlid.IsValid(hit.GetProperty("id").GetString()!));

        // Line number is 1-based over the FULL raw file text (frontmatter
        // block included), so it must point at the actual line in the file
        // on disk, not just "2nd line of the body".
        var fileLines = File.ReadAllLines(Path.Combine(tv.Path, "wiki", "entities", "contoso.md"));
        var expectedLineNo = hit.GetProperty("line").GetInt32();
        Assert.Equal("This line mentions platform engineering.", fileLines[expectedLineNo - 1]);
    }

    [Fact]
    public void Search_CaseInsensitiveByDefault_Matches()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("The Platform is stable.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s1", "--json");

        var r = tv.Run("search", "PLATFORM", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(1, Data(r).GetArrayLength());
    }

    [Fact]
    public void Search_Limit_CapsTotalHits()
    {
        using var tv = new TempVault(); Init(tv);
        // Five lines each mentioning "widget" -> five matching lines from one page.
        tv.RunStdin(
            "widget one\nwidget two\nwidget three\nwidget four\nwidget five",
            "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");

        var r = tv.Run("search", "widget", "--limit", "2", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(2, Data(r).GetArrayLength());
    }

    [Fact]
    public void Search_TypeFilter_OnlyMatchesThatType()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("shared-term appears here.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s1", "--json");
        tv.RunStdin("shared-term also appears here.", "page", "upsert", "--type", "concept", "--title", "Widgets",
            "--summary", "s2", "--json");

        var r = tv.Run("search", "shared-term", "--type", "concept", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("Widgets", data[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Search_Regex_HappyPath_Matches()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("build 1234 succeeded", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s1", "--json");

        var r = tv.Run("search", @"build \d+", "--regex", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("build 1234 succeeded", data[0].GetProperty("matchLine").GetString());
    }

    [Fact]
    public void Search_Regex_BadPattern_RejectedWithBadRegex()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("some body text.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s1", "--json");

        var r = tv.Run("search", "([unclosed", "--regex", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "bad-regex");
    }

    [Fact]
    public void Search_HitNeverContainsFullBody_OnlyTheMatchingLine()
    {
        using var tv = new TempVault(); Init(tv);
        var body = "First unrelated line.\nSecond line has NEEDLE in it.\nThird unrelated line.\nFourth unrelated line.";
        tv.RunStdin(body, "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");

        var r = tv.Run("search", "NEEDLE", "--json");
        Assert.Equal(0, r.ExitCode);
        var hit = Data(r)[0];
        var matchLine = hit.GetProperty("matchLine").GetString()!;

        Assert.Equal("Second line has NEEDLE in it.", matchLine);
        Assert.DoesNotContain("First unrelated line.", matchLine);
        Assert.DoesNotContain("Third unrelated line.", matchLine);
        Assert.DoesNotContain("Fourth unrelated line.", matchLine);
        Assert.DoesNotContain("\n", matchLine);

        // Also assert at the raw JSON-line level: nowhere in the whole
        // response does the full multi-line body appear as a substring.
        var line = r.Stdout.Trim().Split('\n')[^1];
        Assert.DoesNotContain(body, line);
    }

    [Fact]
    public void Search_IsReadOnly_NothingOnDiskChanges()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("platform body text.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s", "--json");

        var idmapBefore = File.ReadAllText(IdMapPath(tv));
        var indexBefore = File.ReadAllText(IndexPath(tv));
        var logBefore = File.ReadAllText(LogPath(tv));
        var pageBefore = File.ReadAllText(Path.Combine(tv.Path, "wiki", "entities", "contoso.md"));
        var filesBefore = Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length;

        tv.Run("search", "platform", "--json");
        tv.Run("search", "plat.*", "--regex", "--json");
        tv.Run("search", "platform", "--type", "concept", "--json");
        tv.Run("search", "no-such-term-anywhere", "--json");

        Assert.Equal(idmapBefore, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(indexBefore, File.ReadAllText(IndexPath(tv)));
        Assert.Equal(logBefore, File.ReadAllText(LogPath(tv)));
        Assert.Equal(pageBefore, File.ReadAllText(Path.Combine(tv.Path, "wiki", "entities", "contoso.md")));
        Assert.Equal(filesBefore, Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Search_NoMatches_ReturnsEmptyArray()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("some body text.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "s1", "--json");

        var r = tv.Run("search", "no-such-term-anywhere", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, Data(r).GetArrayLength());
    }
}
