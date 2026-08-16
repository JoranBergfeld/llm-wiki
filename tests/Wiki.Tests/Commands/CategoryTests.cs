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

    // A bad --description (stray quote/newline) is a wiki.yaml CONFIG error,
    // not a page/source frontmatter error - the code must reflect that so an
    // agent branching on errors[].code isn't misled. Nothing lands.
    [Fact]
    public void Add_DescriptionWithQuote_Rejected_InvalidDescription_ConfigUnchanged()
    {
        using var tv = new TempVault();
        Init(tv);

        var before = File.ReadAllText(ConfigPath(tv));

        var r = tv.Run("category", "add", "paper", "--description", "has a \" quote", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-description");
        Assert.DoesNotContain(r.Envelope.Errors, e => e.Code == "frontmatter-schema");

        Assert.Equal(before, File.ReadAllText(ConfigPath(tv)));
    }

    // Edge case a: an empty `categories:` block (zero items). A hand-edited or
    // freshly-authored wiki.yaml can be in this state; the first `category
    // add` must insert the item directly under `categories:` and re-load
    // cleanly, leaving the surrounding keys/comments intact.
    [Fact]
    public void Add_IntoEmptyCategoriesBlock_InsertsFirstItem_RoundTrips()
    {
        using var tv = new TempVault();
        Init(tv);

        // Hand-write a valid wiki.yaml whose categories block has no items.
        File.WriteAllText(ConfigPath(tv),
            "version: 1\n" +
            "name: \"t\"                       # vault display name\n" +
            "review_gate: false\n" +
            "categories:                      # source categories\n" +
            "lint:\n" +
            "  staleness_days: 90\n" +
            "  max_page_lines: 400\n");

        var r = tv.Run("category", "add", "paper", "--description", "Research papers", "--json");
        Assert.Equal(0, r.ExitCode);

        var after = File.ReadAllText(ConfigPath(tv));
        Assert.Contains("categories:                      # source categories", after);
        Assert.Contains("  - id: paper", after);
        Assert.Contains("    description: \"Research papers\"", after);
        // The comment on the categories line and other keys survive.
        Assert.Contains("# vault display name", after);
        Assert.Contains("lint:", after);

        var cfg = Wiki.Core.VaultConfig.Load(ConfigPath(tv));
        Assert.True(cfg.HasCategory("paper"));
        Assert.Single(cfg.Categories);
        Assert.Equal(1, cfg.Version);
        Assert.Equal(90, cfg.StalenessDays);
        Assert.Equal(400, cfg.MaxPageLines);
    }

    // Edge case b: `categories:` is the LAST top-level block, running to EOF
    // with no following top-level key. VaultConfig.Load parses top-level keys
    // order-independently, so a human could legitimately put `lint:` first.
    // The insertion must land after the last existing category (at EOF) and
    // re-load cleanly.
    [Fact]
    public void Add_WhenCategoriesIsLastBlock_InsertsAtEof_RoundTrips()
    {
        using var tv = new TempVault();
        Init(tv);

        // lint: BEFORE categories:, categories runs to EOF.
        File.WriteAllText(ConfigPath(tv),
            "version: 1\n" +
            "name: \"t\"\n" +
            "review_gate: false\n" +
            "lint:\n" +
            "  staleness_days: 90             # advisory staleness\n" +
            "  max_page_lines: 400\n" +
            "categories:\n" +
            "  - id: meeting-transcript\n" +
            "    description: \"Customer meeting transcripts\"\n");

        var r = tv.Run("category", "add", "paper", "--description", "Research papers", "--json");
        Assert.Equal(0, r.ExitCode);

        var after = File.ReadAllText(ConfigPath(tv));
        Assert.Contains("  - id: paper", after);
        Assert.Contains("    description: \"Research papers\"", after);
        // The pre-existing category and the lint comment both survive.
        Assert.Contains("  - id: meeting-transcript", after);
        Assert.Contains("# advisory staleness", after);

        // The new item lands AFTER the existing one (block appended at EOF).
        var idxExisting = after.IndexOf("- id: meeting-transcript", System.StringComparison.Ordinal);
        var idxNew = after.IndexOf("- id: paper", System.StringComparison.Ordinal);
        Assert.True(idxNew > idxExisting);

        var cfg = Wiki.Core.VaultConfig.Load(ConfigPath(tv));
        Assert.True(cfg.HasCategory("paper"));
        Assert.True(cfg.HasCategory("meeting-transcript"));
        Assert.Equal(90, cfg.StalenessDays);
        Assert.Equal(400, cfg.MaxPageLines);
    }

    // -------------------- amendment N: removing an in-use category is blocking --------------------

    // Registers a source under `meeting-transcript`, then deletes that
    // category from wiki.yaml by hand - the exact thing §5 has always called
    // a blocking config error, and which nothing used to detect.
    private static string RegisterSourceThenDropItsCategory(TempVault tv)
    {
        var srcFile = Path.Combine(tv.Path, "s.md");
        File.WriteAllText(srcFile, "content");
        var add = tv.Run("source", "add", srcFile, "--category", "meeting-transcript", "--title", "S", "--json");
        Assert.Equal(0, add.ExitCode);
        var id = ((JsonElement)add.Envelope.Data!).GetProperty("id").GetString()!;

        var text = File.ReadAllText(ConfigPath(tv));
        text = text.Replace("  - id: meeting-transcript\n    description: \"Customer meeting transcripts\"\n", "");
        File.WriteAllText(ConfigPath(tv), text);
        Assert.DoesNotContain("meeting-transcript", Wiki.Core.VaultConfig.Load(ConfigPath(tv)).Categories.ConvertAll(c => c.Id));

        return id;
    }

    [Fact]
    public void RemovingInUseCategory_BlocksConfigReadingCommands()
    {
        using var tv = new TempVault();
        Init(tv);
        RegisterSourceThenDropItsCategory(tv);

        // A mutation command that reads config now fails, naming the problem.
        var upsert = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Acme", "--summary", "s", "--json");
        Assert.Equal(1, upsert.ExitCode);
        var err = Assert.Single(upsert.Envelope.Errors);
        Assert.Equal("category-in-use", err.Code);
        Assert.Contains("meeting-transcript", err.Message);

        Assert.Equal(1, tv.Run("lint", "--json").ExitCode);
    }

    // The carve-out that keeps the rule from being a trap: `wiki category
    // add` is how you put the category back, so it must not be blocked by
    // the very condition it repairs.
    [Fact]
    public void RemovingInUseCategory_LeavesCategoryCommandUsable_AsTheRepairPath()
    {
        using var tv = new TempVault();
        Init(tv);
        RegisterSourceThenDropItsCategory(tv);

        // list still works, so you can see what you have.
        Assert.Equal(0, tv.Run("category", "list", "--json").ExitCode);

        // and add is the documented repair.
        var repair = tv.Run("category", "add", "meeting-transcript", "--description", "Customer meeting transcripts", "--json");
        Assert.Equal(0, repair.ExitCode);

        // Once repaired, the previously-blocked command works again.
        var upsert = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Acme", "--summary", "s", "--json");
        Assert.Equal(0, upsert.ExitCode);
    }

    [Fact]
    public void RetractedSourcesStillCount_ACategoryTheyReferenceCannotBeDropped()
    {
        using var tv = new TempVault();
        Init(tv);
        var id = RegisterSourceThenDropItsCategory(tv);

        // Put it back so we can retract through the normal path.
        Assert.Equal(0, tv.Run("category", "add", "meeting-transcript", "--description", "d", "--json").ExitCode);
        Assert.Equal(0, tv.Run("source", "retract", id, "--reason", "r", "--json").ExitCode);

        // A retracted source's raw file is still on disk carrying the
        // category, so the reference is still real.
        var text = File.ReadAllText(ConfigPath(tv));
        File.WriteAllText(ConfigPath(tv), text.Replace("  - id: meeting-transcript\n    description: \"d\"\n", ""));

        var lint = tv.Run("lint", "--json");
        Assert.Equal(1, lint.ExitCode);
        Assert.Contains(lint.Envelope.Errors, e => e.Code == "category-in-use");
    }

    // No sources at all: dropping an unused category is perfectly legal.
    [Fact]
    public void RemovingUnusedCategory_IsAllowed()
    {
        using var tv = new TempVault();
        Init(tv);

        var text = File.ReadAllText(ConfigPath(tv));
        File.WriteAllText(ConfigPath(tv),
            text.Replace("  - id: meeting-transcript\n    description: \"Customer meeting transcripts\"\n", ""));

        var upsert = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Acme", "--summary", "s", "--json");
        Assert.Equal(0, upsert.ExitCode);
    }
}
