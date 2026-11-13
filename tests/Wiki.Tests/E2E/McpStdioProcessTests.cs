using System.Text.Json;
using ModelContextProtocol.Client;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.E2E;

// Issue #54: a stdio smoke test against a real `wiki mcp` process, driven by
// the SDK's own stdio client (what Claude Code and friends do). The in-memory
// MCP seam cannot catch a trimmed tool method or a reflection-based JSON path
// that only fails in the published native-AOT binary (issue #29), so CI runs
// this test again with WIKI_SMOKE_BINARY pointing at the AOT publish output.
// Without the variable it runs the IL build through the dotnet host, like
// VersionTests.
public class McpStdioProcessTests
{
    [Fact]
    public async Task WikiMcp_ServesInitializeListAndCall_OverRealStdio()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        tv.RunStdin("Contoso does platform engineering.", "page", "upsert",
            "--type", "entity", "--title", "Contoso", "--summary", "A company", "--json");

        var binary = Environment.GetEnvironmentVariable("WIKI_SMOKE_BINARY");
        var options = binary is { Length: > 0 }
            ? new StdioClientTransportOptions { Command = binary, Arguments = ["mcp", "--vault", tv.Path] }
            : new StdioClientTransportOptions
            {
                Command = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                Arguments = [typeof(App).Assembly.Location, "mcp", "--vault", tv.Path],
            };
        options.Name = "wiki";
        options.WorkingDirectory = tv.Path;
        // The vault comes from --vault alone.
        options.EnvironmentVariables = new Dictionary<string, string?> { ["WIKI_VAULT"] = null };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(options), cancellationToken: timeout.Token);

        Assert.Equal("wiki", client.ServerInfo?.Name);

        var tools = (await client.ListToolsAsync(cancellationToken: timeout.Token)).Select(t => t.Name).ToArray();
        Assert.Contains("search_wiki", tools);
        Assert.Contains("read_page", tools);

        var search = await client.CallToolAsync("search_wiki",
            new Dictionary<string, object?> { ["query"] = "platform" }, cancellationToken: timeout.Token);
        Assert.False(search.IsError);
        Assert.Equal(1, search.Structured().GetProperty("data").GetProperty("search").GetProperty("hits").GetArrayLength());

        var read = await client.CallToolAsync("read_page",
            new Dictionary<string, object?> { ["page"] = "contoso", ["backlinks"] = true }, cancellationToken: timeout.Token);
        Assert.False(read.IsError);
        Assert.Equal("Contoso", read.Structured().GetProperty("data").GetProperty("page").GetProperty("title").GetString());

        var missing = await client.CallToolAsync("read_page",
            new Dictionary<string, object?> { ["page"] = "nope" }, cancellationToken: timeout.Token);
        Assert.True(missing.IsError);
        Assert.Equal(1, missing.Structured().GetProperty("exitCode").GetInt32());
    }
}
