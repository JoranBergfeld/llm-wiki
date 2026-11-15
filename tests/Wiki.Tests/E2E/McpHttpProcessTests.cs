using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.E2E;

// Issue #61: `wiki mcp --http` as a real process on real Kestrel, configured
// the way an operator does it (token in WIKI_MCP_TOKEN; the env var cannot be
// set in-process without leaking into parallel tests). Like
// McpStdioProcessTests, CI reruns it against the native-AOT binary
// (WIKI_SMOKE_BINARY), where a trimmed tool or a reflection-based JSON path
// would only fail at runtime.
public class McpHttpProcessTests
{
    private const string Token = "e2e-token";

    [Fact]
    public async Task WikiMcpHttp_ServesToolsWithTheEnvToken_AndRefusesWithoutIt()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        tv.RunStdin("Contoso does platform engineering.", "page", "upsert",
            "--type", "entity", "--title", "Contoso", "--summary", "A company", "--json");

        using var server = Start(tv, "--listen", "http://127.0.0.1:0");
        try
        {
            var endpoint = await ReadEndpoint(server);

            var options = new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token },
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using (var client = await McpClient.CreateAsync(new HttpClientTransport(options), McpHarness.ClientOptions(), cancellationToken: timeout.Token))
            {
                Assert.Equal("wiki", client.ServerInfo?.Name);
                var search = await client.CallToolAsync("search_wiki",
                    new Dictionary<string, object?> { ["query"] = "platform" }, cancellationToken: timeout.Token);
                Assert.False(search.IsError);
                Assert.Equal(1, search.Structured().GetProperty("data").GetProperty("search").GetProperty("hits").GetArrayLength());
            }

            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent("{}") };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var refused = await http.SendAsync(request, timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
            Assert.Contains("\"code\":\"unauthorized\"", await refused.Content.ReadAsStringAsync(timeout.Token));
        }
        finally
        {
            server.Kill(entireProcessTree: true);
        }
    }

    private static Process Start(TempVault tv, params string[] args)
    {
        var binary = Environment.GetEnvironmentVariable("WIKI_SMOKE_BINARY");
        var info = binary is { Length: > 0 }
            ? new ProcessStartInfo(binary)
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { ArgumentList = { typeof(App).Assembly.Location } };
        foreach (var a in (string[])["mcp", "--http", "--vault", tv.Path, .. args])
            info.ArgumentList.Add(a);
        info.WorkingDirectory = tv.Path;
        info.RedirectStandardOutput = true;
        info.RedirectStandardInput = true;
        info.UseShellExecute = false;
        info.Environment["WIKI_VAULT"] = null;
        info.Environment["WIKI_MCP_TOKEN"] = Token;
        return Process.Start(info)!;
    }

    // With port 0 the startup line is how to learn the port.
    private static async Task<Uri> ReadEndpoint(Process server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var line = await server.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException($"server exited before listening (exit {(server.HasExited ? server.ExitCode : -1)})");
            var match = Regex.Match(line, @"(http://\S+/mcp)");
            if (match.Success)
                return new Uri(match.Groups[1].Value);
        }
    }
}
