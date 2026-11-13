using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #52: stale persona evidence. A persona line's evidence is stale when
// the page's body no longer hashes to what the accept recorded, the page is
// gone, or it no longer cites a persona-source category. `wiki lint` files a
// `persona-stale-evidence` warning per stale line (the re-check proposal) and
// `wiki persona distill` lists those lines for the next pass. Observed only
// through the CLI seam.
public class PersonaStaleEvidenceTests
{
    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private const string Craft = "I value careful craft.";
    private const string Writing = "I decide in writing.";

    // Init with the scaffolded `article` category endorsed (persona-source).
    private static void Init(TempVault tv)
    {
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var text = File.ReadAllText(path);
        var anchor = "description: \"Web articles and blog posts\"";
        Assert.Contains(anchor, text);
        File.WriteAllText(path, text.Replace(anchor, anchor + "\n    persona-source: true"));
    }

    private static (string Id, string Path) Page(TempVault tv, string title)
    {
        var src = Path.Combine(tv.Path, title.ToLowerInvariant().Replace(' ', '-') + "-src.md");
        File.WriteAllText(src, "Content of " + title);
        var s = tv.Run("source", "add", src, "--category", "article", "--title", title, "--json");
        Assert.Equal(0, s.ExitCode);
        var r = tv.RunStdin("Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
            "--summary", title + " summary", "--sources", Data(s).GetProperty("id").GetString()!, "--stdin", "--json");
        Assert.Equal(0, r.ExitCode);
        return (Data(r).GetProperty("id").GetString()!, Data(r).GetProperty("path").GetString()!);
    }

    private static string Change(string op, string section, string? from, string? text, params string[] pages)
        => "{\"op\":\"" + op + "\",\"section\":\"" + section + "\""
           + (from is null ? "" : ",\"from\":" + JsonSerializer.Serialize(from))
           + (text is null ? "" : ",\"text\":" + JsonSerializer.Serialize(text))
           + ",\"evidence\":[" + string.Join(",", pages.Select(p => "{\"page\":\"" + p + "\"}")) + "]}";

    // Proposes and approves one changeset; returns its id.
    private static string Accept(TempVault tv, params string[] changes)
    {
        var p = tv.RunStdin("{\"reason\":\"r\",\"changes\":[" + string.Join(",", changes) + "]}",
            "persona", "propose", "--stdin", "--json");
        Assert.Equal(0, p.ExitCode);
        var id = Data(p).GetProperty("changeset").GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);
        return id;
    }

    // Four endorsed pages; Craft rests on a+b, Writing on c+d.
    private static ((string Id, string Path) A, (string Id, string Path) B, (string Id, string Path) C, (string Id, string Path) D)
        Persona(TempVault tv)
    {
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var c = Page(tv, "Writing one");
        var d = Page(tv, "Writing two");
        Accept(tv, Change("add", "values", null, Craft, a.Id, b.Id), Change("add", "principles", null, Writing, c.Id, d.Id));
        return (a, b, c, d);
    }

    private static JsonElement[] StaleIssues(TempVault tv, string status = "open")
        => Data(tv.Run("issues", "list", "--status", status, "--json")).EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString() == "persona-stale-evidence").ToArray();

    private static void EditBody(TempVault tv, string rel)
    {
        var path = Path.Combine(tv.Path, rel);
        File.WriteAllText(path, File.ReadAllText(path).TrimEnd('\n') + "\nAn added paragraph.\n");
    }

    [Fact]
    public void EditingAnEvidencePage_WarnsForExactlyTheLinesRestingOnIt()
    {
        using var tv = new TempVault(); Init(tv);
        var (a, _, _, _) = Persona(tv);
        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);
        Assert.Empty(StaleIssues(tv));

        EditBody(tv, a.Path);
        tv.Run("lint", "--json");

        var issue = Assert.Single(StaleIssues(tv));
        Assert.Equal("PERSONA.md#values: " + Craft, issue.GetProperty("subject").GetString());
        var detail = issue.GetProperty("detail").GetString()!;
        Assert.Contains("[[craft-one]]", detail);
        Assert.Contains("changed", detail);
        Assert.DoesNotContain("craft-two", detail);
    }

    [Fact]
    public void DeletingAnEvidencePage_WarnsThatItIsGone()
    {
        using var tv = new TempVault(); Init(tv);
        var (_, _, c, _) = Persona(tv);

        File.Delete(Path.Combine(tv.Path, c.Path));
        tv.Run("lint", "--json");

        var issue = Assert.Single(StaleIssues(tv));
        Assert.Equal("PERSONA.md#principles: " + Writing, issue.GetProperty("subject").GetString());
        Assert.Contains(c.Id, issue.GetProperty("detail").GetString());
        Assert.Contains("is gone", issue.GetProperty("detail").GetString());
    }

    [Fact]
    public void ACategoryLosingPersonaSource_MakesItsEvidenceStale()
    {
        using var tv = new TempVault(); Init(tv);
        Persona(tv);

        var yaml = Path.Combine(tv.Path, "wiki.yaml");
        File.WriteAllText(yaml, File.ReadAllText(yaml).Replace("\n    persona-source: true", ""));
        tv.Run("lint", "--json");

        var issues = StaleIssues(tv);
        Assert.Equal(new[] { "PERSONA.md#principles: " + Writing, "PERSONA.md#values: " + Craft },
            issues.Select(i => i.GetProperty("subject").GetString()).OrderBy(s => s, System.StringComparer.Ordinal).ToArray());
        Assert.All(issues, i => Assert.Contains("persona-source", i.GetProperty("detail").GetString()));
        Assert.All(Data(tv.Run("lint", "--json")).GetProperty("counts").EnumerateArray()
                .Where(c => c.GetProperty("kind").GetString() == "persona-stale-evidence"),
            c => Assert.Equal("warning", c.GetProperty("severity").GetString()));
    }
}
