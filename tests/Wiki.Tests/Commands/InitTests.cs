using System.IO;
using System.Text.Json;
using Wiki.Core;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

public class InitTests
{
    [Fact]
    public void Init_ScaffoldsVault_Idempotently()
    {
        using var tv = new TempVault();
        var r = tv.Run("init", tv.Path, "--name", "work", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki.yaml")));
        Assert.True(File.Exists(Path.Combine(tv.Path, "AGENTS.md")));
        AssertAgentsTemplateIsComplete(File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md")));
        Assert.True(Directory.Exists(Path.Combine(tv.Path, "wiki", "entities")));
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "index.md")));

        // re-init on existing vault is a state conflict, not a crash
        var r2 = tv.Run("init", tv.Path, "--json");
        Assert.Equal(3, r2.ExitCode);
    }

    [Fact]
    public void Init_ScaffoldsFullDirectoryLayout()
    {
        using var tv = new TempVault();
        var r = tv.Run("init", tv.Path, "--json");
        Assert.Equal(0, r.ExitCode);

        Assert.True(Directory.Exists(Path.Combine(tv.Path, "raw")));
        Assert.True(Directory.Exists(Path.Combine(tv.Path, "raw", "assets")));
        Assert.True(Directory.Exists(Path.Combine(tv.Path, "wiki", "summaries")));
        Assert.True(Directory.Exists(Path.Combine(tv.Path, "wiki", "entities")));
        Assert.True(Directory.Exists(Path.Combine(tv.Path, "wiki", "concepts")));
        Assert.True(Directory.Exists(Path.Combine(tv.Path, ".wiki")));
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "log.md")));
        Assert.Equal("", File.ReadAllText(Path.Combine(tv.Path, "wiki", "index.md")));
        Assert.Equal("", File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
    }

    // Risk #2: Envelope.Data is `object?`. Under System.Text.Json source-gen,
    // serializing a polymorphic object property requires the runtime type to
    // be registered in WikiJsonContext (InitResult is), or it throws / emits
    // "{}". This proves the real DTO's fields come through the wire intact,
    // not just that *some* JSON came out.
    [Fact]
    public void Init_Json_DataContainsRealFields()
    {
        using var tv = new TempVault();
        var r = tv.Run("init", tv.Path, "--name", "work", "--json");
        Assert.Equal(0, r.ExitCode);

        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        var data = doc.RootElement.GetProperty("data");

        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.True(data.TryGetProperty("vault", out var vaultProp));
        Assert.Equal(Path.GetFullPath(tv.Path), vaultProp.GetString());
        Assert.True(data.TryGetProperty("name", out var nameProp));
        Assert.Equal("work", nameProp.GetString());
        Assert.True(data.TryGetProperty("reviewGate", out var reviewGateProp));
        Assert.Equal(JsonValueKind.False, reviewGateProp.ValueKind);
        Assert.True(data.TryGetProperty("created", out var createdProp));
        Assert.True(createdProp.GetArrayLength() > 0);
    }

    // Risk #3: the scaffolded wiki.yaml must round-trip through the real
    // VaultConfig parser, not just "look like YAML".
    [Fact]
    public void Init_ScaffoldedConfig_RoundTripsThroughVaultConfig()
    {
        using var tv = new TempVault();
        var r = tv.Run("init", tv.Path, "--name", "work", "--review-gate", "--json");
        Assert.Equal(0, r.ExitCode);

        var config = VaultConfig.Load(Path.Combine(tv.Path, "wiki.yaml"));
        Assert.Equal(1, config.Version);
        Assert.Equal("work", config.Name);
        Assert.True(config.ReviewGate);
    }

    [Fact]
    public void Init_WithoutReviewGateFlag_DefaultsFalse()
    {
        using var tv = new TempVault();
        var r = tv.Run("init", tv.Path, "--name", "work", "--json");
        Assert.Equal(0, r.ExitCode);

        var config = VaultConfig.Load(Path.Combine(tv.Path, "wiki.yaml"));
        Assert.False(config.ReviewGate);
    }

    // Fix 1: our YAML parser has no quote-escaping, so a `"` in --name would
    // corrupt the scaffolded wiki.yaml and silently mis-parse. Reject it at
    // the boundary before any filesystem writes happen.
    [Fact]
    public void Init_NameWithDoubleQuote_RejectedBeforeAnyWrites()
    {
        using var tv = new TempVault();
        var r = tv.Run("init", tv.Path, "--name", "a\"b", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-name");

        Assert.False(File.Exists(Path.Combine(tv.Path, "wiki.yaml")));
    }

    // Fix 2: `--vault` and the positional <path> silently diverging means
    // init scaffolds one directory while --vault points somewhere else.
    // Guard it explicitly instead of quietly ignoring --vault.
    [Fact]
    public void Init_VaultFlagDivergesFromPath_RejectedAsConflict()
    {
        using var tv = new TempVault();
        using var other = new TempVault();
        var r = tv.Run("init", tv.Path, "--vault", other.Path, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "vault-flag-conflict");
    }

    // -------------------- the AGENTS.md template is §13-complete --------------------

    // The scaffolded AGENTS.md is the system's only learning surface (§13).
    // It shipped for a while with `### Ingest` reading "(§10 sequence of the
    // spec, verbatim.)" - a placeholder, so every new vault handed its agent
    // an empty ingest procedure - and with no tool-selection table at all.
    private static void AssertAgentsTemplateIsComplete(string text)
    {
        foreach (var heading in new[]
                 {
                     "## Conventions", "## Playbooks", "### Session start",
                     "### Retrieval (answering questions)", "### Tool selection",
                     "### Ingest", "### Reflect",
                 })
        {
            Assert.Contains(heading, text);
        }

        // No placeholder left behind.
        Assert.DoesNotContain("(§10 sequence of the spec, verbatim.)", text);

        // The ingest playbook names every ledger state and the commands that
        // reach them, rather than pointing at a document the agent can't read.
        foreach (var required in new[]
                 {
                     "wiki source add", "wiki ingest advance", "summarized",
                     "integrated", "linted", "--touched", "wiki ingest resume",
                 })
        {
            Assert.Contains(required, text);
        }

        // §13's mandated tool-selection table, as an actual table.
        Assert.Contains("| Intent | Command |", text);
        Assert.Contains("wiki page backlinks", text);
        Assert.Contains("wiki schema propose", text);

        // Exit-code contract the agent branches on.
        foreach (var code in new[] { "exit code 1", "Exit codes:" })
            Assert.Contains(code, text, System.StringComparison.OrdinalIgnoreCase);
    }

    // Every mandated heading must be uniquely addressable, or the reflect
    // loop cannot amend it: SectionLocator fails closed on duplicates.
    [Fact]
    public void AgentsTemplate_EverySection_IsUniquelyAddressableByTheReflectLoop()
    {
        using var tv = new TempVault();
        Assert.Equal(0, tv.Run("init", tv.Path, "--name", "t", "--json").ExitCode);

        foreach (var section in new[]
                 {
                     "Conventions", "Playbooks", "Session start",
                     "Retrieval (answering questions)", "Tool selection", "Ingest", "Reflect",
                 })
        {
            var r = tv.RunStdin($"replacement text for {section}",
                "schema", "propose", "--section", section, "--json");
            Assert.Equal(0, r.ExitCode);
        }
    }

    // End-to-end proof the finalized template is amendable, not just parseable.
    [Fact]
    public void AgentsTemplate_IngestSection_CanBeAmendedThroughTheReflectLoop()
    {
        using var tv = new TempVault();
        Assert.Equal(0, tv.Run("init", tv.Path, "--name", "t", "--json").ExitCode);

        var propose = tv.RunStdin("1. New ingest procedure.",
            "schema", "propose", "--section", "Ingest", "--rationale", "recurring issues", "--json");
        Assert.Equal(0, propose.ExitCode);
        var id = ((System.Text.Json.JsonElement)propose.Envelope.Data!).GetProperty("id").GetString()!;

        Assert.Equal(0, tv.Run("schema", "approve", id, "--json").ExitCode);

        var text = File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md"));
        Assert.Contains("1. New ingest procedure.", text);
        // Neighbouring sections survive untouched.
        Assert.Contains("### Tool selection", text);
        Assert.Contains("### Reflect", text);
    }
}
