using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// "Ingest never writes PERSONA.md" (issue #48): no source lands in the
// persona directly. Drives a whole ingest - register, summarize, integrate,
// lint, linted - plus a reindex, and checks the persona file is untouched
// when it exists and never created when it does not.
public class PersonaIngestTests
{
    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static void IngestOneSource(TempVault tv)
    {
        var srcFile = Path.Combine(tv.Path, "note.md");
        File.WriteAllText(srcFile, "# My values\nI value honesty over comfort. I never share client names.");
        var add = tv.Run("source", "add", srcFile, "--category", "meeting-transcript", "--title", "Values note", "--json");
        Assert.Equal(0, add.ExitCode);
        var sourceId = Data(add).GetProperty("id").GetString()!;

        var summary = tv.RunStdin("The owner values honesty over comfort.", "page", "upsert", "--type", "summary",
            "--title", "Values note summary", "--summary", "Honesty", "--sources", sourceId, "--json");
        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", sourceId, "--to", "summarized", "--json").ExitCode);

        var concept = tv.RunStdin("Honesty over comfort.", "page", "upsert", "--type", "concept",
            "--title", "Honesty", "--summary", "A value", "--sources", sourceId, "--json");
        Assert.Equal(0, concept.ExitCode);
        var conceptId = Data(concept).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("ingest", "advance", sourceId, "--to", "integrated", "--touched", conceptId, "--json").ExitCode);

        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", sourceId, "--to", "linted", "--json").ExitCode);
        Assert.Equal(0, tv.Run("reindex", "--json").ExitCode);
    }

    [Fact]
    public void Ingest_LeavesExistingPersonaByteIdentical()
    {
        using var tv = new TempVault();
        Assert.Equal(0, tv.Run("init", tv.Path, "--name", "t", "--json").ExitCode);
        var personaPath = Path.Combine(tv.Path, "PERSONA.md");
        File.WriteAllText(personaPath, PersonaReadTests.ValidPersona);
        var before = File.ReadAllBytes(personaPath);

        IngestOneSource(tv);

        Assert.Equal(before, File.ReadAllBytes(personaPath));
    }

    [Fact]
    public void Ingest_NeverCreatesPersona()
    {
        using var tv = new TempVault();
        Assert.Equal(0, tv.Run("init", tv.Path, "--name", "t", "--json").ExitCode);

        IngestOneSource(tv);

        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
    }
}
