using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #11 part C: content-loss detection on `page upsert --id`. The measure
// is structural (wikilink targets + cited source ids removed), reported in the
// envelope on every update and filed as a `content-loss` issue above the
// configured threshold.
public class ContentLossTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static string AddSource(TempVault tv, string name, string content)
    {
        var src = Path.Combine(tv.Path, name);
        File.WriteAllText(src, content);
        return Data(tv.Run("source", "add", src, "--category", "article", "--title", name, "--json"))
            .GetProperty("id").GetString()!;
    }

    private static string CreatePage(TempVault tv, string type, string title, string body, string? sources = null)
    {
        var args = new System.Collections.Generic.List<string>
        {
            "page", "upsert", "--type", type, "--title", title, "--summary", title + " summary",
            "--stdin", "--allow-dangling", "--json",
        };
        if (sources is not null) { args.Add("--sources"); args.Add(sources); }
        var r = tv.RunStdin(body, args.ToArray());
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    private static string[] OpenIssueKinds(TempVault tv)
        => ((JsonElement)tv.Run("issues", "list", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray().Select(e => e.GetProperty("kind").GetString()!).ToArray();

    [Fact]
    public void Create_ReportsNoContentLoss()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("A body with [[alpha]] and [[beta]].",
            "page", "upsert", "--type", "concept", "--title", "Fresh", "--summary", "s",
            "--stdin", "--allow-dangling", "--json");

        Assert.Equal(0, r.ExitCode);
        // Null fields are omitted from the envelope entirely.
        Assert.False(Data(r).TryGetProperty("contentLoss", out _));
    }

    [Fact]
    public void Update_DroppingLinksAndSources_ReportsThemAndFilesAnIssue()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var s2 = AddSource(tv, "s2.md", "source two");

        var id = CreatePage(tv, "concept", "Routing",
            "Mentions [[alpha]], [[beta]] and [[gamma]].", s1 + "," + s2);

        var updated = tv.RunStdin("Now only [[alpha]] survives.",
            "page", "upsert", "--type", "concept", "--title", "Routing", "--summary", "s",
            "--id", id, "--sources", s1, "--stdin", "--allow-dangling", "--json");

        Assert.Equal(0, updated.ExitCode);
        var loss = Data(updated).GetProperty("contentLoss");

        Assert.Equal(new[] { "beta", "gamma" },
            loss.GetProperty("removedLinks").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { s2 },
            loss.GetProperty("removedSources").EnumerateArray().Select(e => e.GetString()).ToArray());

        // 3 links + 2 sources = 5 references; 3 removed => 60%.
        Assert.Equal(5, loss.GetProperty("oldReferences").GetInt32());
        Assert.Equal(60, loss.GetProperty("lossPercent").GetInt32());
        Assert.True(loss.GetProperty("issueFiled").GetBoolean());

        Assert.Contains("content-loss", OpenIssueKinds(tv));
    }

    [Fact]
    public void Update_BelowThreshold_ReportsButFilesNothing()
    {
        using var tv = new TempVault(); Init(tv);

        // 10 links, one removed = 10%, under the default 25% threshold.
        var links = string.Join(" ", Enumerable.Range(1, 10).Select(i => $"[[t{i}]]"));
        var id = CreatePage(tv, "concept", "Wide", links);

        var fewer = string.Join(" ", Enumerable.Range(1, 9).Select(i => $"[[t{i}]]"));
        var updated = tv.RunStdin(fewer,
            "page", "upsert", "--type", "concept", "--title", "Wide", "--summary", "s",
            "--id", id, "--stdin", "--allow-dangling", "--json");

        var loss = Data(updated).GetProperty("contentLoss");
        Assert.Equal(10, loss.GetProperty("lossPercent").GetInt32());
        Assert.False(loss.GetProperty("issueFiled").GetBoolean());
        Assert.DoesNotContain("content-loss", OpenIssueKinds(tv));
    }

    [Fact]
    public void Update_RephrasingWithoutRemovingReferences_ScoresZero()
    {
        using var tv = new TempVault(); Init(tv);
        var id = CreatePage(tv, "concept", "Prose",
            "Original wording entirely. Links: [[alpha]] and [[beta]].");

        // A total rewrite of the prose. A naive line diff would call this 100%
        // deletion; the structural measure correctly calls it zero.
        var updated = tv.RunStdin("Completely different sentences, restructured, reordered. [[beta]] then [[alpha]].",
            "page", "upsert", "--type", "concept", "--title", "Prose", "--summary", "s",
            "--id", id, "--stdin", "--allow-dangling", "--json");

        var loss = Data(updated).GetProperty("contentLoss");
        Assert.Equal(0, loss.GetProperty("lossPercent").GetInt32());
        Assert.Empty(loss.GetProperty("removedLinks").EnumerateArray());
        Assert.False(loss.GetProperty("issueFiled").GetBoolean());
    }

    [Fact]
    public void Update_ReportsLineCountsEvenWhenNoReferencesExist()
    {
        using var tv = new TempVault(); Init(tv);
        var id = CreatePage(tv, "concept", "Plain", "line one\nline two\nline three\nline four");

        var updated = tv.RunStdin("line one",
            "page", "upsert", "--type", "concept", "--title", "Plain", "--summary", "s",
            "--id", id, "--stdin", "--json");

        var loss = Data(updated).GetProperty("contentLoss");
        Assert.Equal(0, loss.GetProperty("oldReferences").GetInt32());
        Assert.Equal(0, loss.GetProperty("lossPercent").GetInt32());
        Assert.Equal(4, loss.GetProperty("oldLines").GetInt32());
        Assert.Equal(1, loss.GetProperty("newLines").GetInt32());
        Assert.False(loss.GetProperty("issueFiled").GetBoolean());
    }

    [Fact]
    public void ContentLossThreshold_IsConfigurable()
    {
        using var tv = new TempVault(); Init(tv);

        var yamlPath = Path.Combine(tv.Path, "wiki.yaml");
        File.WriteAllText(yamlPath,
            File.ReadAllText(yamlPath).TrimEnd() + "\n  content_loss_percent: 90\n");

        var id = CreatePage(tv, "concept", "Tolerant", "[[a]] [[b]] [[c]] [[d]]");

        var updated = tv.RunStdin("[[a]] [[b]]",
            "page", "upsert", "--type", "concept", "--title", "Tolerant", "--summary", "s",
            "--id", id, "--stdin", "--allow-dangling", "--json");

        var loss = Data(updated).GetProperty("contentLoss");
        Assert.Equal(50, loss.GetProperty("lossPercent").GetInt32());
        Assert.False(loss.GetProperty("issueFiled").GetBoolean());
    }

    [Fact]
    public void ContentLossIssue_IsResolvableLikeAnyOther()
    {
        using var tv = new TempVault(); Init(tv);
        var id = CreatePage(tv, "concept", "Split me", "[[a]] [[b]] [[c]] [[d]]");

        tv.RunStdin("[[a]]",
            "page", "upsert", "--type", "concept", "--title", "Split me", "--summary", "s",
            "--id", id, "--stdin", "--allow-dangling", "--json");

        var issues = ((JsonElement)tv.Run("issues", "list", "--kind", "content-loss", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray().ToArray();
        Assert.Single(issues);

        var issueId = issues[0].GetProperty("id").GetString()!;
        var resolved = tv.Run("issues", "resolve", issueId, "--note", "deliberate split", "--json");
        Assert.Equal(0, resolved.ExitCode);

        Assert.DoesNotContain("content-loss", OpenIssueKinds(tv));
    }

    [Fact]
    public void BadContentLossPercent_IsAConfigError()
    {
        using var tv = new TempVault(); Init(tv);
        var yamlPath = Path.Combine(tv.Path, "wiki.yaml");
        File.WriteAllText(yamlPath, File.ReadAllText(yamlPath).TrimEnd() + "\n  content_loss_percent: 150\n");

        // `page list` never reads wiki.yaml; `lint` does, like every other
        // config-reading command.
        var r = tv.Run("lint", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "config");
    }
}
