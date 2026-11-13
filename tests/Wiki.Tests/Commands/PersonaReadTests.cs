using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// `wiki persona read` (issue #48): returns PERSONA.md at the vault root as
// its four fixed sections plus the three frontmatter fields. A vault with no
// PERSONA.md is an empty persona, not an error.
public class PersonaReadTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string[] Lines(JsonElement data, string section)
        => data.GetProperty(section).EnumerateArray().Select(e => e.GetString()!).ToArray();

    // A well-formed persona as the CLI would write it. Hand-written here
    // because no command in this ticket writes PERSONA.md (propose/accept
    // land later); the file format is the contract under test.
    internal const string ValidPersona =
        "---\n" +
        "schema_version: 1\n" +
        "last_distilled: 2026-11-01\n" +
        "audit_entry: 3f2a9c\n" +
        "---\n" +
        "# Persona\n" +
        "\n" +
        "## Values\n" +
        "\n" +
        "- I value honesty over comfort.\n" +
        "- I value craft.\n" +
        "\n" +
        "## Principles\n" +
        "\n" +
        "- I write things down once I have decided them.\n" +
        "\n" +
        "## Tone of voice\n" +
        "\n" +
        "- I write short, plain sentences.\n" +
        "\n" +
        "## Boundaries\n" +
        "\n" +
        "- I do not share client names outside the team.\n";

    [Fact]
    public void Read_ValidPersona_ReturnsSectionsAndFrontmatter()
    {
        using var tv = new TempVault(); Init(tv);
        File.WriteAllText(Path.Combine(tv.Path, "PERSONA.md"), ValidPersona);

        var r = tv.Run("persona", "read", "--json");

        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.True(data.GetProperty("exists").GetBoolean());
        Assert.Equal("1", data.GetProperty("schemaVersion").GetString());
        Assert.Equal("2026-11-01", data.GetProperty("lastDistilled").GetString());
        Assert.Equal("3f2a9c", data.GetProperty("auditEntry").GetString());
        Assert.Equal(new[] { "I value honesty over comfort.", "I value craft." }, Lines(data, "values"));
        Assert.Equal(new[] { "I write things down once I have decided them." }, Lines(data, "principles"));
        Assert.Equal(new[] { "I write short, plain sentences." }, Lines(data, "toneOfVoice"));
        Assert.Equal(new[] { "I do not share client names outside the team." }, Lines(data, "boundaries"));
    }

    [Fact]
    public void Read_HumanMode_PrintsEverySectionAndItsLines()
    {
        using var tv = new TempVault(); Init(tv);
        File.WriteAllText(Path.Combine(tv.Path, "PERSONA.md"), ValidPersona);

        var sw = new StringWriter();
        var exit = Wiki.App.Main(new[] { "persona", "read", "--vault", tv.Path }, sw, new StringReader(""));

        Assert.Equal(0, exit);
        var output = sw.ToString();
        Assert.Contains("Values", output);
        Assert.Contains("I value honesty over comfort.", output);
        Assert.Contains("Tone of voice", output);
        Assert.Contains("I write short, plain sentences.", output);
        Assert.Contains("Boundaries", output);
        Assert.DoesNotContain("\"ok\"", output);
    }

    [Fact]
    public void Read_HumanMode_WithoutPersonaFile_SaysThereIsNone()
    {
        using var tv = new TempVault(); Init(tv);

        var sw = new StringWriter();
        var exit = Wiki.App.Main(new[] { "persona", "read", "--vault", tv.Path }, sw, new StringReader(""));

        Assert.Equal(0, exit);
        Assert.Contains("No persona yet", sw.ToString());
    }

    [Fact]
    public void Read_OutsideAVault_FailsWithNoVault()
    {
        using var tv = new TempVault();

        var r = tv.Run("persona", "read", "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("no-vault", r.Envelope.Errors.Single().Code);
    }

    [Fact]
    public void Read_WithoutPersonaFile_ReturnsEmptyPersona()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.Run("persona", "read", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Envelope.Ok);
        var data = Data(r);
        Assert.False(data.GetProperty("exists").GetBoolean());
        Assert.Empty(Lines(data, "values"));
        Assert.Empty(Lines(data, "principles"));
        Assert.Empty(Lines(data, "toneOfVoice"));
        Assert.Empty(Lines(data, "boundaries"));
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
    }
}
