using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// PERSONA.md structural lint (issue #48, amendment AC). Each rule is its own
// issue kind with subject `PERSONA.md`, filed by `wiki lint` like any other
// finding. The lint report's per-kind counts carry a severity: the
// structural rules are errors, tone-of-voice length is a warning. Lint still
// exits 0 either way - it reports, it does not gate.
public class PersonaLintTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static void WritePersona(TempVault tv, string text)
        => File.WriteAllText(Path.Combine(tv.Path, "PERSONA.md"), text);

    private const string Front =
        "---\n" +
        "schema_version: 1\n" +
        "last_distilled: 2026-11-01\n" +
        "audit_entry: 3f2a9c\n" +
        "---\n";

    private static string Persona(string values = "- I value craft.\n", string principles = "- I decide in writing.\n",
        string tone = "- I write plainly.\n", string boundaries = "- I keep client names private.\n")
        => Front +
           "## Values\n" + values + "\n" +
           "## Principles\n" + principles + "\n" +
           "## Tone of voice\n" + tone + "\n" +
           "## Boundaries\n" + boundaries;

    // Runs lint and returns the open persona-* issues as (kind, detail).
    private static (string Kind, string Subject, string Detail)[] LintPersona(TempVault tv, out JsonElement report)
    {
        var r = tv.Run("lint", "--json");
        Assert.Equal(0, r.ExitCode);
        report = Data(r);

        var issues = tv.Run("issues", "list", "--status", "open", "--json");
        Assert.Equal(0, issues.ExitCode);
        return Data(issues).EnumerateArray()
            .Select(i => (i.GetProperty("kind").GetString()!, i.GetProperty("subject").GetString()!, i.GetProperty("detail").GetString()!))
            .Where(i => i.Item1.StartsWith("persona-"))
            .ToArray();
    }

    private static string SeverityOf(JsonElement report, string kind)
        => report.GetProperty("counts").EnumerateArray()
            .Single(c => c.GetProperty("kind").GetString() == kind)
            .GetProperty("severity").GetString()!;

    [Fact]
    public void Lint_WithoutPersonaFile_FilesNoPersonaIssues()
    {
        using var tv = new TempVault(); Init(tv);

        Assert.Empty(LintPersona(tv, out _));
    }

    [Fact]
    public void Lint_ValidPersona_FilesNoPersonaIssues()
    {
        using var tv = new TempVault(); Init(tv);
        WritePersona(tv, PersonaReadTests.ValidPersona);

        Assert.Empty(LintPersona(tv, out _));
    }

    [Fact]
    public void Lint_MissingSection_IsAPersonaSectionsError()
    {
        using var tv = new TempVault(); Init(tv);
        WritePersona(tv, Front + "## Values\n- I value craft.\n\n## Principles\n\n## Tone of voice\n");

        var found = LintPersona(tv, out var report);

        var issue = Assert.Single(found);
        Assert.Equal("persona-sections", issue.Kind);
        Assert.Equal("PERSONA.md", issue.Subject);
        Assert.Contains("Boundaries", issue.Detail);
        Assert.Equal("error", SeverityOf(report, "persona-sections"));
    }

    [Fact]
    public void Lint_SectionsOutOfOrder_IsAPersonaSectionsError()
    {
        using var tv = new TempVault(); Init(tv);
        WritePersona(tv, Front +
            "## Principles\n- I decide in writing.\n\n" +
            "## Values\n- I value craft.\n\n" +
            "## Tone of voice\n- I write plainly.\n\n" +
            "## Boundaries\n- I keep client names private.\n");

        var issue = Assert.Single(LintPersona(tv, out _));
        Assert.Equal("persona-sections", issue.Kind);
        Assert.Contains("out of order", issue.Detail);
    }

    [Theory]
    [InlineData("## Values\n- I value craft.\n", "no frontmatter")]                                          // no block at all
    [InlineData("---\nschema_version: 1\nlast_distilled: 2026-11-01\n---\n", "audit_entry")]                 // key missing
    [InlineData("---\nschema_version: 2\nlast_distilled: 2026-11-01\naudit_entry: a\n---\n", "schema_version")] // unknown version
    [InlineData("---\nschema_version: 1\nlast_distilled: soon\naudit_entry: a\n---\n", "last_distilled")]    // not a date
    [InlineData("---\nschema_version: 1\nlast_distilled: 2026-11-01\naudit_entry: a\ntitle: me\n---\n", "title")] // extra key
    public void Lint_InvalidFrontmatter_IsAPersonaFrontmatterError(string head, string mentioned)
    {
        using var tv = new TempVault(); Init(tv);
        var body = head.StartsWith("---")
            ? "## Values\n\n## Principles\n\n## Tone of voice\n\n## Boundaries\n"
            : "\n## Principles\n\n## Tone of voice\n\n## Boundaries\n";
        WritePersona(tv, head + body);

        var found = LintPersona(tv, out var report);

        var issue = Assert.Single(found);
        Assert.Equal("persona-frontmatter", issue.Kind);
        Assert.Contains(mentioned, issue.Detail);
        Assert.Equal("error", SeverityOf(report, "persona-frontmatter"));
    }

    [Theory]
    [InlineData("- I value [[craftsmanship]].\n")]
    [InlineData("- I value [craft](https://example.com).\n")]
    [InlineData("- I value craft, see https://example.com\n")]
    [InlineData("- I value craft <https://example.com>\n")]
    public void Lint_AnyLink_IsAPersonaLinkError(string values)
    {
        using var tv = new TempVault(); Init(tv);
        WritePersona(tv, Persona(values: values));

        var found = LintPersona(tv, out var report);

        var issue = Assert.Single(found);
        Assert.Equal("persona-link", issue.Kind);
        Assert.Contains("Values", issue.Detail);
        Assert.Equal("error", SeverityOf(report, "persona-link"));
    }

    [Fact]
    public void Lint_EntryOver200Characters_IsAPersonaEntryLengthError()
    {
        using var tv = new TempVault(); Init(tv);
        var exactly200 = "I " + new string('a', 198);
        var over = "I " + new string('b', 199);
        WritePersona(tv, Persona(principles: $"- {exactly200}\n- {over}\n"));

        var found = LintPersona(tv, out var report);

        var issue = Assert.Single(found);
        Assert.Equal("persona-entry-length", issue.Kind);
        Assert.Contains("Principles", issue.Detail);
        Assert.Contains("201", issue.Detail);
        Assert.Equal("error", SeverityOf(report, "persona-entry-length"));
    }

    [Theory]
    [InlineData("values", 10)]
    [InlineData("principles", 15)]
    [InlineData("boundaries", 10)]
    public void Lint_SectionOverItsCap_IsAPersonaSectionCapError(string section, int cap)
    {
        using var tv = new TempVault(); Init(tv);
        string Lines(int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"- I hold line {i}.\n"));

        // At the cap is fine; one over is the error.
        WritePersona(tv, section switch
        {
            "values" => Persona(values: Lines(cap)),
            "principles" => Persona(principles: Lines(cap)),
            _ => Persona(boundaries: Lines(cap)),
        });
        Assert.Empty(LintPersona(tv, out _));

        WritePersona(tv, section switch
        {
            "values" => Persona(values: Lines(cap + 1)),
            "principles" => Persona(principles: Lines(cap + 1)),
            _ => Persona(boundaries: Lines(cap + 1)),
        });
        var found = LintPersona(tv, out var report);

        var issue = Assert.Single(found);
        Assert.Equal("persona-section-cap", issue.Kind);
        Assert.Contains($"{cap + 1}", issue.Detail);
        Assert.Contains($"{cap}", issue.Detail);
        Assert.Equal("error", SeverityOf(report, "persona-section-cap"));
    }

    [Fact]
    public void Lint_ToneOfVoiceHasNoLineCap()
    {
        using var tv = new TempVault(); Init(tv);
        var many = string.Concat(Enumerable.Range(1, 40).Select(i => $"- I write line {i}.\n"));
        WritePersona(tv, Persona(tone: many));

        Assert.Empty(LintPersona(tv, out _));
    }

    [Fact]
    public void Lint_ToneOfVoicePast600Words_IsAPersonaToneLengthWarning()
    {
        using var tv = new TempVault(); Init(tv);
        // 60 lines x 10 words = 600 words: at the threshold, no finding.
        string Tone(int lines) => string.Concat(Enumerable.Range(1, lines)
            .Select(_ => "- I write one two three four five six seven eight.\n"));
        WritePersona(tv, Persona(tone: Tone(60)));
        Assert.Empty(LintPersona(tv, out _));

        WritePersona(tv, Persona(tone: Tone(61)));
        var found = LintPersona(tv, out var report);

        var issue = Assert.Single(found);
        Assert.Equal("persona-tone-length", issue.Kind);
        Assert.Contains("610", issue.Detail);
        Assert.Equal("warning", SeverityOf(report, "persona-tone-length"));
    }

    [Fact]
    public void Lint_SeveralRulesBroken_FilesOneIssuePerKind()
    {
        using var tv = new TempVault(); Init(tv);
        var tooLong = "I " + new string('x', 250);
        WritePersona(tv, Persona(values: $"- {tooLong}\n- See [[a-page]].\n", boundaries: $"- {tooLong}\n"));

        var kinds = LintPersona(tv, out _).Select(i => i.Kind).OrderBy(k => k).ToArray();

        Assert.Equal(new[] { "persona-entry-length", "persona-link" }, kinds);
    }

    [Fact]
    public void Lint_Rerun_RefreshesPersonaIssueInsteadOfDuplicating()
    {
        using var tv = new TempVault(); Init(tv);
        WritePersona(tv, Persona(values: "- See [[a-page]].\n"));

        LintPersona(tv, out _);
        var found = LintPersona(tv, out var second);

        Assert.Single(found);
        Assert.Equal(1, second.GetProperty("refreshed").GetInt32());
    }
}
