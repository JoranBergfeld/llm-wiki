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
        Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
    }

    [Fact] public void Load_RejectsDuplicateCategoryId()
    {
        var p = WriteTmp(Yaml.Replace("id: article", "id: meeting-transcript"));
        Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
    }

    [Fact] public void Load_RejectsWrongVersion()
    {
        var p = WriteTmp(Yaml.Replace("version: 1", "version: 2"));
        Assert.Throws<ValidationException>(() => VaultConfig.Load(p));
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
