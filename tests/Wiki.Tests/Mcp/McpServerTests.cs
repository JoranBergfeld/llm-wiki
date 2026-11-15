using System.IO;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #54: `wiki mcp` serves initialize, tools/list and tools/call, bound to
// one vault resolved at startup.
public class McpServerTests
{
    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ToolsList_ListsSearchAndRead(string transport)
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var tools = await mcp.Client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToArray();

        Assert.Contains("search_wiki", names);
        Assert.Contains("read_page", names);
        Assert.DoesNotContain("init", names);
    }

    [Fact]
    public void Mcp_RefusesToStart_WhenVaultCannotBeResolved()
    {
        using var tv = new TempVault(); // no wiki.yaml: not a vault
        var missing = Path.Combine(tv.Path, "nope");

        var r = tv.Run("mcp", "--vault", missing, "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.False(r.Envelope.Ok);
        Assert.Equal("no-vault", r.Envelope.Errors[0].Code);
    }
}
