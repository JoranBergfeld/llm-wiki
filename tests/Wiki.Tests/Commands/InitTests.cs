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
}
