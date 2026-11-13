using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #49 / ADR 0001: the owner marks a category `persona-source: true` in
// wiki.yaml to endorse what lands in it. Pages built from a source in such a
// category carry `persona: candidate` so the next distillation pass considers
// them; pages from any other category never do.
public class PersonaSourceTests
{
    private static string ConfigPath(TempVault tv) => Path.Combine(tv.Path, "wiki.yaml");

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    // Init, then mark the scaffolded `article` category with the given
    // persona-source line - a direct file edit, which is how the owner sets it.
    private static void InitWithPersonaSourceLine(TempVault tv, string line)
    {
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var path = ConfigPath(tv);
        var text = File.ReadAllText(path);
        var anchor = "description: \"Web articles and blog posts\"";
        Assert.Contains(anchor, text);
        File.WriteAllText(path, text.Replace(anchor, anchor + "\n    " + line));
    }

    private static string AddSource(TempVault tv, string category, string name)
    {
        var src = Path.Combine(tv.Path, name + ".md");
        File.WriteAllText(src, "Content of " + name);
        var r = tv.Run("source", "add", src, "--category", category, "--title", name, "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    private static CliResult Upsert(TempVault tv, string title, string sources, string? id = null)
    {
        var args = new System.Collections.Generic.List<string>
        {
            "page", "upsert", "--type", "summary", "--title", title, "--summary", title + " summary",
            "--sources", sources, "--stdin", "--json",
        };
        if (id is not null) { args.Add("--id"); args.Add(id); }
        return tv.RunStdin("Body of " + title, args.ToArray());
    }

    private static string PageText(TempVault tv, CliResult upsert)
        => File.ReadAllText(Path.Combine(tv.Path, Data(upsert).GetProperty("path").GetString()!));

    [Fact]
    public void PersonaSourceTrue_IsAccepted_AndShownByCategoryList()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");

        var r = tv.Run("category", "list", "--json");

        Assert.Equal(0, r.ExitCode);
        foreach (var c in Data(r).EnumerateArray())
        {
            var expected = c.GetProperty("id").GetString() == "article";
            Assert.Equal(expected, c.GetProperty("personaSource").GetBoolean());
        }
    }

    [Fact]
    public void PersonaSourceFalse_IsAccepted()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: false");

        var r = tv.Run("category", "list", "--json");

        Assert.Equal(0, r.ExitCode);
        foreach (var c in Data(r).EnumerateArray())
            Assert.False(c.GetProperty("personaSource").GetBoolean());
    }

    [Fact]
    public void PersonaSourceNotABoolean_IsAConfigError()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: yes");

        var r = tv.Run("category", "list", "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "config" && e.Message.Contains("persona-source"));
    }

    [Fact]
    public void UnknownCategoryKey_IsAConfigError()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "self-authored: true");

        var r = tv.Run("category", "list", "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "config");
    }

    [Fact]
    public void CategoryAdd_AfterAPersonaSourceCategory_KeepsTheFlagOnItsOwnCategory()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");

        var add = tv.Run("category", "add", "paper", "--description", "Research papers", "--json");
        Assert.Equal(0, add.ExitCode);

        var r = tv.Run("category", "list", "--json");
        Assert.Equal(0, r.ExitCode);
        foreach (var c in Data(r).EnumerateArray())
        {
            var expected = c.GetProperty("id").GetString() == "article";
            Assert.Equal(expected, c.GetProperty("personaSource").GetBoolean());
        }
    }
}
