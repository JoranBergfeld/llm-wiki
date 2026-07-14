using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 26: `wiki category add/list` (spec §5). The key guarantee under test
// here isn't just "add/list work" - it's that the CLI NEVER auto-adds a
// category. `wiki category add` is the only path that mutates wiki.yaml's
// `categories:` block; a rejected `source add --category <unknown>` must
// leave wiki.yaml byte-for-byte untouched (Add_UnknownCategory_ConfigByteUnchanged).
public class CategoryTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string ConfigPath(TempVault tv) => Path.Combine(tv.Path, "wiki.yaml");

    [Fact]
    public void Add_AppendsCategoryToWikiYaml_AndRoundTrips()
    {
        using var tv = new TempVault();
        Init(tv);

        var r = tv.Run("category", "add", "paper", "--description", "Research papers and reports", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Envelope.Ok);

        var data = (JsonElement)r.Envelope.Data!;
        Assert.Equal("paper", data.GetProperty("id").GetString());
        Assert.Equal("Research papers and reports", data.GetProperty("description").GetString());

        var configText = File.ReadAllText(ConfigPath(tv));
        Assert.Contains("- id: paper", configText);
        Assert.Contains("description: \"Research papers and reports\"", configText);

        // Must still parse cleanly through the real config loader.
        var cfg = Wiki.Core.VaultConfig.Load(ConfigPath(tv));
        Assert.True(cfg.HasCategory("paper"));
        // The two categories the init scaffold ships with are still there.
        Assert.True(cfg.HasCategory("meeting-transcript"));
        Assert.True(cfg.HasCategory("article"));
    }

    [Fact]
    public void List_ShowsAllConfiguredCategories()
    {
        using var tv = new TempVault();
        Init(tv);
        tv.Run("category", "add", "paper", "--description", "Research papers and reports", "--json");

        var r = tv.Run("category", "list", "--json");
        Assert.Equal(0, r.ExitCode);

        var data = (JsonElement)r.Envelope.Data!;
        Assert.Equal(3, data.GetArrayLength());

        var ids = new System.Collections.Generic.List<string>();
        foreach (var item in data.EnumerateArray())
            ids.Add(item.GetProperty("id").GetString()!);
        Assert.Contains("meeting-transcript", ids);
        Assert.Contains("article", ids);
        Assert.Contains("paper", ids);
    }

    [Fact]
    public void Add_Duplicate_Rejected_ConfigUnchanged()
    {
        using var tv = new TempVault();
        Init(tv);

        var before = File.ReadAllText(ConfigPath(tv));

        var r = tv.Run("category", "add", "article", "--description", "duplicate attempt", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "duplicate-category");

        Assert.Equal(before, File.ReadAllText(ConfigPath(tv)));
    }

    [Fact]
    public void Add_InvalidId_Rejected_ConfigUnchanged()
    {
        using var tv = new TempVault();
        Init(tv);

        var before = File.ReadAllText(ConfigPath(tv));

        var r = tv.Run("category", "add", "Not_Kebab!", "--description", "bad id", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-category-id");

        Assert.Equal(before, File.ReadAllText(ConfigPath(tv)));
    }

    // The spec §5 AUTHORITY guarantee: "The CLI never adds categories on its
    // own; there is no code path by which ingest creates one." Prove it at
    // the file level - a source-add rejected for an unknown category must
    // leave wiki.yaml exactly as it was, not just "unmodified content" but
    // byte-for-byte identical (no rewrite-with-same-content either).
    [Fact]
    public void SourceAdd_UnknownCategory_Rejected_WikiYamlByteUnchanged()
    {
        using var tv = new TempVault();
        Init(tv);

        var before = File.ReadAllText(ConfigPath(tv));
        var beforeWriteTimeUtc = File.GetLastWriteTimeUtc(ConfigPath(tv));

        var src = Path.Combine(tv.Path, "input.md");
        File.WriteAllText(src, "hello");

        var r = tv.Run("source", "add", src, "--category", "nope", "--title", "T", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-category");

        Assert.Equal(before, File.ReadAllText(ConfigPath(tv)));
        Assert.Equal(beforeWriteTimeUtc, File.GetLastWriteTimeUtc(ConfigPath(tv)));

        // 'nope' still isn't a real category afterward.
        var cfg = Wiki.Core.VaultConfig.Load(ConfigPath(tv));
        Assert.False(cfg.HasCategory("nope"));
    }

    // The init-scaffolded wiki.yaml carries real inline `# comments` (see
    // wiki-yaml.txt). A targeted insertion must leave every one of them -
    // and every other key - exactly as they were; only the `categories:`
    // block gains a new item.
    [Fact]
    public void Add_PreservesInlineCommentsAndOtherKeys()
    {
        using var tv = new TempVault();
        Init(tv);

        var before = File.ReadAllText(ConfigPath(tv));
        Assert.Contains("# vault display name", before);
        Assert.Contains("# source categories", before);
        Assert.Contains("# advisory: summaries older than this with newer related sources", before);

        var r = tv.Run("category", "add", "paper", "--description", "Research papers and reports", "--json");
        Assert.Equal(0, r.ExitCode);

        var after = File.ReadAllText(ConfigPath(tv));

        // Every comment survives verbatim.
        Assert.Contains("# vault display name", after);
        Assert.Contains("# source categories", after);
        Assert.Contains("# advisory: summaries older than this with newer related sources", after);
        Assert.Contains("# advisory: pages larger than this flagged for splitting", after);

        // Every pre-existing line survives verbatim (the diff is a pure insertion).
        foreach (var line in before.Replace("\r\n", "\n").Split('\n'))
        {
            Assert.Contains(line, after);
        }

        // And it re-loads cleanly with the new category plus everything else intact.
        var cfg = Wiki.Core.VaultConfig.Load(ConfigPath(tv));
        Assert.Equal(1, cfg.Version);
        Assert.Equal("t", cfg.Name);
        Assert.False(cfg.ReviewGate);
        Assert.Equal(90, cfg.StalenessDays);
        Assert.Equal(400, cfg.MaxPageLines);
        Assert.True(cfg.HasCategory("paper"));
    }
}
