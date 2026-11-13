using System.IO;
using System.Text.Json;
using ModelContextProtocol;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #59: the persona over MCP. `read_persona` returns the persona and,
// when asked, the interview guide; `distill_persona` is the read side of a
// distillation pass (candidate pages and candidate transcripts beside the
// current persona); `record_owner_statement` appends one answer, word for
// word, to an interview transcript. `recordedVia` comes from the server's
// origin, never from an argument.
public class PersonaToolTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string[] CompletedSteps(JsonElement s)
        => s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Codes(JsonElement s)
        => s.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();

    // ---- read_persona -----------------------------------------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReadPersona_ReturnsThePersona_WithoutTheGuideByDefault(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_persona");

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["persona read"], CompletedSteps(s));
        var persona = s.GetProperty("data").GetProperty("persona");
        Assert.False(persona.GetProperty("exists").GetBoolean());
        Assert.Equal(0, persona.GetProperty("values").GetArrayLength());
        Assert.False(s.GetProperty("data").TryGetProperty("guide", out _));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReadPersona_IncludeGuide_AlsoReturnsTheInterviewGuide(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_persona", new Dictionary<string, object?> { ["includeGuide"] = true });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["persona read", "persona interview guide"], CompletedSteps(s));
        var guide = s.GetProperty("data").GetProperty("guide");
        Assert.Equal("shipped", guide.GetProperty("source").GetString());
        Assert.Equal("1", guide.GetProperty("version").GetString());
        Assert.Contains("word for word", guide.GetProperty("text").GetString());
    }
}
