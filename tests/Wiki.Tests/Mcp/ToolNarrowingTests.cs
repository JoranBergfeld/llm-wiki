using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #60 (decision #30, Exposure): every tool is exposed by default; a
// server instance removes tools with --exclude-tools / WIKI_MCP_EXCLUDE_TOOLS.
// Removed tools are absent from tools/list and calling one is the same
// unknown-tool protocol error as a tool that never existed.
public class ToolNarrowingTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ExcludedTool_IsAbsentFromToolsList_OthersRemain(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport, null, "--exclude-tools", "read_page");

        var names = (await mcp.Client.ListToolsAsync()).Select(t => t.Name).ToArray();

        Assert.DoesNotContain("read_page", names);
        Assert.Contains("search_wiki", names);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task CallingAnExcludedTool_IsAProtocolError(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport, null, "--exclude-tools", "read_page");

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() =>
            mcp.CallAsync("read_page", new Dictionary<string, object?> { ["page"] = "x" }));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ExcludeList_AcceptsCommasAndRepeats(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport, null,
            "--exclude-tools", " read_page , ", "--exclude-tools", "search_wiki");

        var names = (await mcp.Client.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.DoesNotContain("read_page", names);
        Assert.DoesNotContain("search_wiki", names);
    }

    // Narrowing is keyed on tool names, so a tool registered by any other
    // means (here a test-only probe) is narrowed exactly like a shipped one.
    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Narrowing_AppliesToEveryRegisteredTool(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport,
            (b, cli) => b.WithTools(new ProbeTool()), "--exclude-tools", "probe_tool");

        var names = (await mcp.Client.ListToolsAsync()).Select(t => t.Name).ToArray();

        Assert.DoesNotContain("probe_tool", names);
        Assert.Contains("read_page", names);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task UnknownToolName_RefusesToStart(string transport)
    {
        using var tv = new TempVault(); Init(tv);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpHarness.StartAsync(tv, transport, null, "--exclude-tools", "read_page,reed_page", "--json"));

        Assert.Contains("server exited 1", ex.Message);
        Assert.Contains("\"unknown-tool\"", ex.Message);
        Assert.Contains("reed_page", ex.Message);
    }

    // No tool argument can widen (or narrow) the enabled set: the set is
    // fixed at startup, and an undeclared argument is rejected by the guard.
    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task NoToolArgument_ChangesTheEnabledSet(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport, null, "--exclude-tools", "read_page");

        foreach (var smuggled in new[] { "excludeTools", "exclude_tools", "tools", "enabledTools" })
        {
            var ex = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync("search_wiki",
                new Dictionary<string, object?> { ["query"] = "x", [smuggled] = "" }));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }

        var names = (await mcp.Client.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.DoesNotContain("read_page", names);
        Assert.Contains("search_wiki", names);
    }
}

[ModelContextProtocol.Server.McpServerToolType]
public sealed class ProbeTool
{
    [ModelContextProtocol.Server.McpServerTool(Name = "probe_tool")]
    [System.ComponentModel.Description("Test-only probe.")]
    public string Probe() => "ok";
}
