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
    public void PageFromPersonaSourceCategory_CarriesCandidateFlag()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");
        var note = AddSource(tv, "article", "my-note");

        var r = Upsert(tv, "My note", note);

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Envelope.Ok);
        Assert.Contains("\npersona: candidate\n", PageText(tv, r));
    }

    [Fact]
    public void PageFromOtherCategory_NeverCarriesFlag()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");
        var transcript = AddSource(tv, "meeting-transcript", "call");

        var r = Upsert(tv, "Call", transcript);

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("persona:", PageText(tv, r));
    }

    [Fact]
    public void PageFromCategoryMarkedFalse_NeverCarriesFlag()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: false");
        var note = AddSource(tv, "article", "my-note");

        var r = Upsert(tv, "My note", note);

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("persona:", PageText(tv, r));
    }

    [Fact]
    public void PageWithoutSources_NeverCarriesFlag()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");

        var r = tv.RunStdin("Body", "page", "upsert", "--type", "concept", "--title", "Idea",
            "--summary", "An idea", "--stdin", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("persona:", PageText(tv, r));
    }

    // Amendment AV: only a page whose cited sources are ALL in persona-source
    // categories is an endorsed note; one other source and it is not.
    [Fact]
    public void PageCitingAPersonaSourceAndAnotherSource_NeverCarriesFlag()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");
        var note = AddSource(tv, "article", "my-note");
        var transcript = AddSource(tv, "meeting-transcript", "call");

        var r = Upsert(tv, "Mixed", note + "," + transcript);

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("persona:", PageText(tv, r));
    }

    [Fact]
    public void PageCitingOnlyPersonaSources_CarriesFlag()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");
        var one = AddSource(tv, "article", "note-one");
        var two = AddSource(tv, "article", "note-two");

        var r = Upsert(tv, "Two notes", one + "," + two);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("\npersona: candidate\n", PageText(tv, r));
    }

    [Fact]
    public void UpdateThatAddsANonPersonaSource_ClearsFlag_AndUpdateThatDropsIt_SetsIt()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");
        var note = AddSource(tv, "article", "my-note");
        var transcript = AddSource(tv, "meeting-transcript", "call");

        var created = Upsert(tv, "Mixed", note);
        var id = Data(created).GetProperty("id").GetString()!;
        Assert.Contains("\npersona: candidate\n", PageText(tv, created));

        var withTranscript = Upsert(tv, "Mixed", transcript + "," + note, id);
        Assert.Equal(0, withTranscript.ExitCode);
        Assert.DoesNotContain("persona:", PageText(tv, withTranscript));

        var noteOnly = Upsert(tv, "Mixed", note, id);
        Assert.Equal(0, noteOnly.ExitCode);
        Assert.Contains("\npersona: candidate\n", PageText(tv, noteOnly));
    }

    [Fact]
    public void CandidatePage_PassesLint_AndSurvivesAStatusChange()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");
        var note = AddSource(tv, "article", "my-note");
        var created = Upsert(tv, "My note", note);
        var id = Data(created).GetProperty("id").GetString()!;

        var lint = tv.Run("lint", "--json");
        Assert.Equal(0, lint.ExitCode);
        Assert.True(lint.Envelope.Ok);

        var status = tv.Run("page", "set-status", id, "needs-review", "--json");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("\npersona: candidate\n", PageText(tv, created));
    }

    [Fact]
    public void HandEditedPersonaValueOtherThanCandidate_IsRejectedByTheSchema()
    {
        using var tv = new TempVault();
        InitWithPersonaSourceLine(tv, "persona-source: true");
        var note = AddSource(tv, "article", "my-note");
        var created = Upsert(tv, "My note", note);
        var path = Path.Combine(tv.Path, Data(created).GetProperty("path").GetString()!);
        File.WriteAllText(path, File.ReadAllText(path).Replace("persona: candidate", "persona: value"));

        var lint = tv.Run("lint", "--json");

        Assert.Equal(1, lint.ExitCode);
        Assert.Contains(lint.Envelope.Errors, e => e.Code == "frontmatter-schema");
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
