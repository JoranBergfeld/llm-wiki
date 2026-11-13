using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #54: `search_wiki` and `read_page` return the CLI's results. Each
// successful tool result is the envelope shape with each step's data under
// its key, plus exitCode and the CLI steps that ran.
public class SearchAndReadToolTests
{
    private static string Seed(TempVault tv)
    {
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var contoso = tv.RunStdin("Contoso does platform engineering.", "page", "upsert",
            "--type", "entity", "--title", "Contoso", "--summary", "A company", "--json");
        tv.RunStdin("Widgets are made by [[contoso]].", "page", "upsert",
            "--type", "concept", "--title", "Widgets", "--summary", "A product", "--json");
        using var doc = JsonDocument.Parse(contoso.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    private static JsonElement CliData(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task SearchWiki_WithQuery_ReturnsTheSearchCommandsHits(string transport)
    {
        using var tv = new TempVault();
        Seed(tv);
        var expected = CliData(tv.Run("search", "platform", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("search_wiki", new Dictionary<string, object?> { ["query"] = "platform" });

        Assert.False(result.IsError);
        var s = result.Structured();
        Assert.True(s.GetProperty("ok").GetBoolean());
        Assert.Equal(0, s.GetProperty("exitCode").GetInt32());
        Assert.Equal(["search"], s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()));
        var data = s.GetProperty("data").GetProperty("search");
        Assert.Equal(expected.GetRawText(), data.GetRawText());
        Assert.Equal(1, data.GetProperty("hits").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task SearchWiki_PassesTypedOptionsThrough(string transport)
    {
        using var tv = new TempVault();
        Seed(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("search_wiki", new Dictionary<string, object?>
        {
            ["query"] = "^Widgets are",
            ["regex"] = true,
            ["type"] = "concept",
            ["limit"] = 1,
        });

        Assert.False(result.IsError);
        var hits = result.Structured().GetProperty("data").GetProperty("search").GetProperty("hits");
        Assert.Equal(1, hits.GetArrayLength());
        Assert.Equal("Widgets", hits[0].GetProperty("title").GetString());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task SearchWiki_WithoutQuery_ReturnsTheRoutingIndex(string transport)
    {
        using var tv = new TempVault();
        Seed(tv);
        var expected = CliData(tv.Run("index", "show", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("search_wiki");

        Assert.False(result.IsError);
        var s = result.Structured();
        Assert.Equal(["index show"], s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(expected.GetRawText(), s.GetProperty("data").GetProperty("index").GetRawText());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReadPage_ReturnsThePageShowResult(string transport)
    {
        using var tv = new TempVault();
        var id = Seed(tv);
        var expected = CliData(tv.Run("page", "show", id, "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_page", new Dictionary<string, object?> { ["page"] = id });

        Assert.False(result.IsError);
        var s = result.Structured();
        Assert.Equal(["page show"], s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(expected.GetRawText(), s.GetProperty("data").GetProperty("page").GetRawText());
        Assert.False(s.GetProperty("data").TryGetProperty("backlinks", out _));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReadPage_WithBacklinks_AlsoReturnsInboundLinks(string transport)
    {
        using var tv = new TempVault();
        Seed(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_page", new Dictionary<string, object?> { ["page"] = "contoso", ["backlinks"] = true });

        Assert.False(result.IsError);
        var s = result.Structured();
        Assert.Equal(["page show", "page backlinks"], s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Contoso", s.GetProperty("data").GetProperty("page").GetProperty("title").GetString());
        Assert.Equal(["widgets"], s.GetProperty("data").GetProperty("backlinks").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReadPage_FrontmatterOnly_OmitsTheBody(string transport)
    {
        using var tv = new TempVault();
        Seed(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_page", new Dictionary<string, object?> { ["page"] = "contoso", ["frontmatterOnly"] = true });

        var page = result.Structured().GetProperty("data").GetProperty("page");
        Assert.False(page.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null);
    }
}
