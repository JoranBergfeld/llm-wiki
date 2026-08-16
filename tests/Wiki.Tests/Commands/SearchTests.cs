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

    // `data` is a report object since amendment O ({hits, truncated,
    // scanned}), not a bare hits array - a truncated result used to be
    // indistinguishable from an exhaustive one.
    private static JsonElement Hits(CliResult r) => Data(r).GetProperty("hits");

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
        var data = Hits(r);
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
        Assert.Equal(1, Hits(r).GetArrayLength());
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
        Assert.Equal(2, Hits(r).GetArrayLength());
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
        var data = Hits(r);
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
        var data = Hits(r);
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
        var hit = Hits(r)[0];
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
        Assert.Equal(0, Hits(r).GetArrayLength());
    }

    // -------------------- amendment O: sources are searchable, truncation is reported --------------------

    private static string AddSource(TempVault tv, string fileName, string content, string title)
    {
        var path = Path.Combine(tv.Path, fileName);
        File.WriteAllText(path, content);
        var r = tv.Run("source", "add", path, "--category", "article", "--title", title, "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    // The agent's one text-search primitive used to be blind to raw/, so
    // "which source mentioned this?" had no answer short of reading files
    // directly - which §13's retrieval playbook forbids.
    [Fact]
    public void Search_CoversRawSources_NotJustPages()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "s.md", "The transcript mentions kubernetes migration.", "Meeting");

        var r = tv.Run("search", "kubernetes", "--json");
        Assert.Equal(0, r.ExitCode);

        var hits = Hits(r);
        Assert.Equal(1, hits.GetArrayLength());
        Assert.Equal("source", hits[0].GetProperty("kind").GetString());
        Assert.Equal(sourceId, hits[0].GetProperty("id").GetString());
        Assert.Equal("Meeting", hits[0].GetProperty("title").GetString());
        Assert.StartsWith("raw/", hits[0].GetProperty("path").GetString());
        Assert.Contains("kubernetes", hits[0].GetProperty("matchLine").GetString());
    }

    [Fact]
    public void Search_PagesAndSourcesBoth_CarryTheirKind()
    {
        using var tv = new TempVault(); Init(tv);
        AddSource(tv, "s.md", "shared-term in the raw source.", "Meeting");
        tv.RunStdin("shared-term in the page.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");

        var hits = Hits(tv.Run("search", "shared-term", "--json"));
        Assert.Equal(2, hits.GetArrayLength());

        var kinds = new System.Collections.Generic.List<string>();
        foreach (var h in hits.EnumerateArray())
            kinds.Add(h.GetProperty("kind").GetString()!);
        Assert.Contains("page", kinds);
        Assert.Contains("source", kinds);
    }

    [Fact]
    public void Search_KindFilter_NarrowsToPagesOrSources()
    {
        using var tv = new TempVault(); Init(tv);
        AddSource(tv, "s.md", "shared-term in the raw source.", "Meeting");
        tv.RunStdin("shared-term in the page.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");

        var pagesOnly = Hits(tv.Run("search", "shared-term", "--kind", "page", "--json"));
        Assert.Equal(1, pagesOnly.GetArrayLength());
        Assert.Equal("page", pagesOnly[0].GetProperty("kind").GetString());

        var sourcesOnly = Hits(tv.Run("search", "shared-term", "--kind", "source", "--json"));
        Assert.Equal(1, sourcesOnly.GetArrayLength());
        Assert.Equal("source", sourcesOnly[0].GetProperty("kind").GetString());
    }

    // --type is a PAGE-type filter, so naming one implies pages only -
    // otherwise every typed search would drag in unrelated source hits.
    [Fact]
    public void Search_TypeFilter_ImpliesPagesOnly()
    {
        using var tv = new TempVault(); Init(tv);
        AddSource(tv, "s.md", "shared-term in the raw source.", "Meeting");
        tv.RunStdin("shared-term in the page.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");

        var hits = Hits(tv.Run("search", "shared-term", "--type", "entity", "--json"));
        Assert.Equal(1, hits.GetArrayLength());
        Assert.Equal("page", hits[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Search_KindAndTypeConflict_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("search", "x", "--kind", "source", "--type", "entity", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "kind-type-conflict");
    }

    [Fact]
    public void Search_BadKind_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("search", "x", "--kind", "nonsense", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-kind");
    }

    // Truncation used to be silent: --limit stopped the scan and the caller
    // could not tell a capped result from an exhaustive one.
    [Fact]
    public void Search_ReportsTruncation_WhenLimitCutsTheScanShort()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("widget one\nwidget two\nwidget three\nwidget four\nwidget five",
            "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");

        var capped = Data(tv.Run("search", "widget", "--limit", "2", "--json"));
        Assert.Equal(2, capped.GetProperty("hits").GetArrayLength());
        Assert.True(capped.GetProperty("truncated").GetBoolean());

        var full = Data(tv.Run("search", "widget", "--limit", "50", "--json"));
        Assert.Equal(5, full.GetProperty("hits").GetArrayLength());
        Assert.False(full.GetProperty("truncated").GetBoolean());
    }

    // Exactly-at-limit is not truncation: there was nothing further to find.
    [Fact]
    public void Search_LimitExactlyMatchingHitCount_IsNotTruncated()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("widget one\nwidget two", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s1", "--json");

        var r = Data(tv.Run("search", "widget", "--limit", "2", "--json"));
        Assert.Equal(2, r.GetProperty("hits").GetArrayLength());
        Assert.False(r.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Search_SourceScan_IsStillReadOnly()
    {
        using var tv = new TempVault(); Init(tv);
        AddSource(tv, "s.md", "raw content with a term.", "Meeting");

        var filesBefore = Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length;
        var rawBefore = File.ReadAllText(Directory.GetFiles(Path.Combine(tv.Path, "raw"), "*.md")[0]);

        tv.Run("search", "term", "--json");

        Assert.Equal(filesBefore, Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(rawBefore, File.ReadAllText(Directory.GetFiles(Path.Combine(tv.Path, "raw"), "*.md")[0]));
    }
}
