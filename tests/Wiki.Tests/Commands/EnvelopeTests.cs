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

    // -------------------- amendment P: human-mode failures are Spectre, not JSON --------------------

    // These bypass TempVault.Run, which deserializes the last stdout line as
    // an envelope - the whole point here is that human mode does NOT emit one.
    private static (int Exit, string Out) RunRaw(params string[] args)
    {
        var sw = new StringWriter();
        var exit = Wiki.App.Main(args, sw, new StringReader(""));
        return (exit, sw.ToString());
    }

    [Fact]
    public void HumanMode_ValidationFailure_RendersErrorLine_NotJson()
    {
        using var tv = new TempVault();
        Assert.Equal(0, tv.Run("init", tv.Path, "--name", "t", "--json").ExitCode);

        var (exit, output) = RunRaw("page", "show", "nosuch", "--vault", tv.Path);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("\"ok\"", output);
        Assert.DoesNotContain("\\u0027", output);
        Assert.Contains("ERROR", output);
        Assert.Contains("not-found", output);
    }

    [Fact]
    public void HumanMode_UnknownCommand_RendersErrorLine_NotJson()
    {
        var (exit, output) = RunRaw("nonesuch");

        Assert.Equal(1, exit);
        Assert.DoesNotContain("\"ok\"", output);
        Assert.Contains("ERROR", output);
    }

    // The flag still wins on the parse-error path, where there is no parsed
    // option to read it from.
    [Fact]
    public void JsonMode_UnknownCommand_StillEmitsEnvelope()
    {
        var (exit, output) = RunRaw("nonesuch", "--json");

        Assert.Equal(1, exit);
        using var doc = JsonDocument.Parse(output.Trim().Split('\n')[^1]);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown-command", doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    // Exit codes are presentation-independent: the same failure must return
    // the same code in both modes.
    [Fact]
    public void ExitCodes_AreIdentical_InBothModes()
    {
        using var tv = new TempVault();
        Assert.Equal(0, tv.Run("init", tv.Path, "--name", "t", "--json").ExitCode);

        // exit 1: blocking validation.
        Assert.Equal(RunRaw("page", "show", "nosuch", "--vault", tv.Path, "--json").Exit,
                     RunRaw("page", "show", "nosuch", "--vault", tv.Path).Exit);

        // exit 3: state conflict (re-init an existing vault).
        var jsonExit = RunRaw("init", tv.Path, "--name", "t", "--json").Exit;
        var humanExit = RunRaw("init", tv.Path, "--name", "t").Exit;
        Assert.Equal(3, jsonExit);
        Assert.Equal(jsonExit, humanExit);
    }

    // A human-mode failure that carries a path surfaces it.
    [Fact]
    public void HumanMode_FailureWithPath_ShowsThePath()
    {
        using var tv = new TempVault();
        var missing = Path.Combine(tv.Path, "not-a-vault");
        Directory.CreateDirectory(missing);

        var (exit, output) = RunRaw("page", "list", "--vault", missing);

        Assert.Equal(1, exit);
        Assert.Contains("no-vault", output);
        Assert.Contains("path:", output);
    }
}
