using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class VaultConfigTests
{
    const string Yaml = """
        version: 1
        name: "work"
        review_gate: true
        categories:
          - id: meeting-transcript
            description: "Customer meeting transcripts"
          - id: article
            description: "Web articles"
        lint:
          staleness_days: 90
          max_page_lines: 400
        """;

    static string WriteTmp(string content)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wiki-config-" + System.Guid.NewGuid().ToString("N") + ".yaml");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact] public void Load_ParsesCategoriesAndFlags()
    {
        var p = WriteTmp(Yaml);
        var c = VaultConfig.Load(p);
        Assert.True(c.ReviewGate);
        Assert.True(c.HasCategory("article"));
        Assert.Equal(90, c.StalenessDays);
    }

    [Fact] public void Load_RejectsNonKebabCategory()
    {
        var p = WriteTmp(Yaml.Replace("meeting-transcript", "Meeting_Transcript"));
        var ex = Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
        Assert.Equal("config", ex.Code);
    }

    [Fact] public void Load_RejectsDuplicateCategoryId()
    {
        var p = WriteTmp(Yaml.Replace("id: article", "id: meeting-transcript"));
        var ex = Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
        Assert.Equal("config", ex.Code);
    }

    [Fact] public void Load_RejectsWrongVersion()
    {
        var p = WriteTmp(Yaml.Replace("version: 1", "version: 2"));
        var ex = Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
        Assert.Equal("config", ex.Code);
    }

    [Fact] public void Load_RejectsMissingRequiredKey()
    {
        var p = WriteTmp(Yaml.Replace("name: \"work\"\n", ""));
        var ex = Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
        Assert.Equal("config", ex.Code);
    }

    [Fact] public void Load_RejectsMissingLintBlock()
    {
        var p = WriteTmp(Yaml.Replace("lint:\n  staleness_days: 90\n  max_page_lines: 400", ""));
        var ex = Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
        Assert.Equal("config", ex.Code);
    }

    // The regression test for the inline-comment bug: spec §5's own comment-laden sample.
    // A freshly-inited vault (Task 8 scaffolds from this) must Load without error.
    const string CommentedYaml = """
        version: 1
        name: "work"                     # vault display name
        review_gate: true                # pages land as pending-review when true
        categories:                      # source categories — fully user-defined
          - id: meeting-transcript
            description: "Customer meeting transcripts"
          - id: article
            description: "Web articles and blog posts"
          - id: paper
            description: "Research papers and reports"
        lint:
          staleness_days: 90             # advisory: summaries older than this with newer related sources
          max_page_lines: 400            # advisory: pages larger than this flagged for splitting
        """;

    [Fact] public void Load_ParsesCommentedSpecSample()
    {
        var p = WriteTmp(CommentedYaml);
        var c = VaultConfig.Load(p);
        Assert.Equal(1, c.Version);
        Assert.Equal("work", c.Name);
        Assert.True(c.ReviewGate);
        Assert.Equal(3, c.Categories.Count);
        Assert.True(c.HasCategory("meeting-transcript"));
        Assert.True(c.HasCategory("article"));
        Assert.True(c.HasCategory("paper"));
        Assert.Equal("Research papers and reports", c.Categories[2].Description);
        Assert.Equal(90, c.StalenessDays);
        Assert.Equal(400, c.MaxPageLines);
    }

    [Fact] public void Load_KeepsHashInsideQuotesStripsTrailingComment()
    {
        var p = WriteTmp(Yaml.Replace("name: \"work\"", "name: \"C# shop\"   # display name"));
        var c = VaultConfig.Load(p);
        Assert.Equal("C# shop", c.Name);
    }

    [Fact] public void Load_ParsesAllFields()
    {
        var p = WriteTmp(Yaml);
        var c = VaultConfig.Load(p);
        Assert.Equal(1, c.Version);
        Assert.Equal("work", c.Name);
        Assert.Equal(2, c.Categories.Count);
        Assert.Equal("meeting-transcript", c.Categories[0].Id);
        Assert.Equal("Customer meeting transcripts", c.Categories[0].Description);
        Assert.Equal(400, c.MaxPageLines);
        Assert.False(c.HasCategory("nonexistent"));
    }
}
