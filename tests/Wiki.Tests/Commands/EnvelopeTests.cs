using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

public class EnvelopeTests
{
    [Fact]
    public void UnknownCommand_EmitsErrorEnvelope_Exit1()
    {
        using var v = new TempVault();
        var r = v.Run("nonesuch", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.False(r.Envelope.Ok);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-command");
    }

    [Fact]
    public void FailureEnvelope_AlwaysIncludesDataKey_AsNull()
    {
        using var v = new TempVault();
        var r = v.Run("nonesuch", "--json");

        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // All four top-level keys present on every response.
        Assert.True(root.TryGetProperty("data", out var data), "envelope must always include a 'data' key");
        Assert.Equal(JsonValueKind.Null, data.ValueKind);
        Assert.True(root.TryGetProperty("v", out _));
        Assert.True(root.TryGetProperty("ok", out _));
        Assert.True(root.TryGetProperty("errors", out var errors));
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
    }
}
