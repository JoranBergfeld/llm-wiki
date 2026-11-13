using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #61: `wiki mcp --http`. The tool suite runs over HTTP through the
// McpTransports rows; these cover what only HTTP has: the optional bearer
// token, the Host allowlist, the request-size limit, their RFC 9457
// problem+json rejections, and the audit origin.
public class HttpTransportTests
{
    private const string Token = "s3cret-token";

    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    // A well-formed stateless MCP request, as the SDK client sends it.
    private static HttpRequestMessage ToolsList(string? token = null, string? host = null, HttpContent? content = null)
    {
        var meta = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "raw", ["version"] = "1" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list", ["params"] = new JsonObject { ["_meta"] = meta } };
        var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = content ?? new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/list");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (host is not null)
            request.Headers.Host = host;
        return request;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var p = doc.RootElement.Clone();
        Assert.Equal(code, p.GetProperty("code").GetString());
        Assert.Equal((int)status, p.GetProperty("status").GetInt32());
        Assert.EndsWith("/docs/mcp-http-errors.md#" + code, p.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("detail").GetString()));
        Assert.Equal("/mcp", p.GetProperty("instance").GetString());
        return p;
    }

    [Fact]
    public async Task WithoutAToken_RequestsAreAccepted()
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv);
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(ToolsList());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("search_wiki", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("s3cret-token-and-more")]
    public async Task WithAToken_AMissingOrWrongToken_Is401Unauthorized(string? presented)
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv, null, "--token", Token);
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(ToolsList(presented));

        await Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task WithAToken_TheRightToken_Connects_AndMutationsAreAuditedHttpPlusToken()
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv, null, "--token", Token);
        await using var client = await host.ConnectAsync(Token);

        var result = await client.CallToolAsync("add_category",
            new Dictionary<string, object?> { ["id"] = "podcast", ["description"] = "Podcast episodes" });

        Assert.False(result.IsError, result.Text());
        var last = File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log"))[^1];
        Assert.Contains(" | via=http+token | ", last);
    }

    [Fact]
    public async Task WithoutAToken_MutationsAreAuditedHttp()
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, McpTransports.Http);

        var result = await mcp.CallAsync("add_category",
            new Dictionary<string, object?> { ["id"] = "podcast", ["description"] = "Podcast episodes" });

        Assert.False(result.IsError, result.Text());
        var last = File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log"))[^1];
        Assert.Contains(" | via=http | ", last);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:8765")]
    [InlineData("127.0.0.2")]
    public async Task ByDefault_ANonLoopbackHost_Is400HostNotAllowed(string hostHeader)
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv);
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(ToolsList(host: hostHeader));

        var p = await Problem(response, HttpStatusCode.BadRequest, "host-not-allowed");
        Assert.Equal(hostHeader.Split(':')[0], p.GetProperty("host").GetString());
    }

    [Theory]
    [InlineData("localhost:8765")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]:8765")]
    public async Task ByDefault_LoopbackHosts_AreAllowed(string hostHeader)
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv);
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(ToolsList(host: hostHeader));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The flag replaces the default list (like --exclude-tools over its env
    // var), so a proxied deployment names exactly the hosts it serves.
    [Fact]
    public async Task AllowedHosts_ReplacesTheDefault()
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv, null, "--allowed-hosts", "wiki.lan,localhost", "--allowed-hosts", "Other.Lan");
        using var http = host.CreateHttpClient();

        foreach (var allowed in new[] { "wiki.lan", "WIKI.LAN:443", "other.lan", "localhost" })
        {
            using var ok = await http.SendAsync(ToolsList(host: allowed));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var refused = await http.SendAsync(ToolsList(host: "127.0.0.1"));
        await Problem(refused, HttpStatusCode.BadRequest, "host-not-allowed");
    }

    [Fact]
    public async Task AllowedHostsStar_AllowsAnyHost()
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv, null, "--allowed-hosts", "*");
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(ToolsList(host: "anything.example"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The host check comes first: a request for a foreign host learns nothing
    // about whether the server wants a token.
    [Fact]
    public async Task HostIsCheckedBeforeTheToken()
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv, null, "--token", Token);
        using var http = host.CreateHttpClient();

        using var response = await http.SendAsync(ToolsList(host: "evil.example"));

        await Problem(response, HttpStatusCode.BadRequest, "host-not-allowed");
    }

    [Fact]
    public async Task AnUploadOverTheLimit_Is413UploadTooLarge_AndNothingLands()
    {
        using var tv = new TempVault(); Init(tv);
        var inbox = Path.Combine(Path.GetTempPath(), "wiki-inbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await McpHttpHost.StartAsync(tv, null, "--inbox-root", inbox, "--max-upload-bytes", "1024");
            await using var client = await host.ConnectAsync(token: null);

            var upload = Convert.ToBase64String(new byte[200 * 1024]);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.CallToolAsync("ingest_source",
                new Dictionary<string, object?> { ["category"] = "article", ["filename"] = "big.md", ["content"] = upload }).AsTask());
            Assert.Contains("413", ex.ToString());
            Assert.False(Directory.Exists(Path.Combine(inbox, "article")));

            // The same body, raw, to read the problem document.
            using var http = host.CreateHttpClient();
            using var response = await http.SendAsync(ToolsList(content: new StringContent(new string('x', 200 * 1024), Encoding.UTF8, "application/json")));
            var p = await Problem(response, HttpStatusCode.RequestEntityTooLarge, "upload-too-large");
            Assert.Equal(2 * 1024 + 64 * 1024, p.GetProperty("maxBodyBytes").GetInt64());
        }
        finally
        {
            try { Directory.Delete(inbox, true); } catch { }
        }
    }

    // No Content-Length (chunked): the body is counted as it is read.
    [Fact]
    public async Task AChunkedBodyOverTheLimit_Is413UploadTooLarge()
    {
        using var tv = new TempVault(); Init(tv);
        await using var host = await McpHttpHost.StartAsync(tv, null, "--max-upload-bytes", "1024");
        using var http = host.CreateHttpClient();

        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 200 * 1024))));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = ToolsList(content: content);
        request.Headers.TransferEncodingChunked = true;
        using var response = await http.SendAsync(request);

        await Problem(response, HttpStatusCode.RequestEntityTooLarge, "upload-too-large");
    }

    // An upload under the HTTP body limit but over the upload limit is still
    // refused by ingest_source itself, exactly as over stdio.
    [Fact]
    public async Task AnUploadJustOverTheLimit_IsRefusedByTheTool()
    {
        using var tv = new TempVault(); Init(tv);
        var inbox = Path.Combine(Path.GetTempPath(), "wiki-inbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var mcp = await McpHarness.StartAsync(tv, McpTransports.Http, null, "--inbox-root", inbox, "--max-upload-bytes", "1024");

            var result = await mcp.CallAsync("ingest_source", new Dictionary<string, object?>
            {
                ["category"] = "article", ["filename"] = "big.md", ["content"] = Convert.ToBase64String(new byte[1500]),
            });

            Assert.True(result.IsError);
            Assert.Equal("upload-too-large", result.Structured().GetProperty("errors")[0].GetProperty("code").GetString());
        }
        finally
        {
            try { Directory.Delete(inbox, true); } catch { }
        }
    }

    [Theory]
    [InlineData("https://127.0.0.1:8765")]
    [InlineData("http://127.0.0.1:8765/wiki")]
    [InlineData("127.0.0.1:8765")]
    public async Task AListenUrlThatIsNotPlainHttp_RefusesToStart(string listen)
    {
        using var tv = new TempVault(); Init(tv);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpHttpHost.StartAsync(tv, null, "--listen", listen, "--json"));

        Assert.Contains("server exited 1", ex.Message);
        Assert.Contains("\"invalid-listen-url\"", ex.Message);
    }

    [Theory]
    [InlineData("--token", "x")]
    [InlineData("--listen", "http://127.0.0.1:1")]
    [InlineData("--allowed-hosts", "wiki.lan")]
    public async Task AnHttpSettingWithoutHttp_RefusesToStart(string option, string value)
    {
        using var tv = new TempVault(); Init(tv);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpHarness.StartAsync(tv, McpTransports.Stdio, null, option, value, "--json"));

        Assert.Contains("server exited 1", ex.Message);
        Assert.Contains("\"http-only-option\"", ex.Message);
    }

    // Tool steps block their thread (App.Main, a vault-busy wait included).
    // Many calls stuck on the lock must not starve the server: it still
    // answers other requests while they wait.
    [Fact]
    public async Task ManyCallsWaitingOnTheVaultLock_DoNotStarveTheServer()
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, McpTransports.Http, null, "--lock-timeout", "60");

        var held = new HeldStdin("Body.");
        var writer = Task.Run(() => tv.RunWith(held, new StringWriter(),
            "page", "upsert", "--type", "entity", "--title", "Gamma", "--summary", "s", "--stdin", "--json"));
        Assert.True(held.WaitUntilEntered(TimeSpan.FromSeconds(30)));
        try
        {
            var blocked = Enumerable.Range(0, 200).Select(i => mcp.CallAsync("add_category",
                new Dictionary<string, object?> { ["id"] = $"cat-{i}", ["description"] = "d" })).ToArray();
            await Task.Delay(TimeSpan.FromSeconds(1));

            var list = mcp.Client.ListToolsAsync().AsTask();
            var first = await Task.WhenAny(list, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(first == list, "tools/list was not answered while tool calls waited on the vault lock");
            Assert.All(blocked, b => Assert.False(b.IsCompleted));
            held.Release();
            await writer;
            // Only liveness: 200 writes drain one at a time, each fsyncing
            // and checking the audit chain, which takes well over a minute
            // on a loaded 4-core Windows runner. What this test is about was
            // asserted above.
            await Task.WhenAll(blocked).WaitAsync(TimeSpan.FromMinutes(5));
        }
        finally
        {
            held.Release();
        }
    }

    // The catalogue every `type` points at documents each code under its own
    // heading (the anchor).
    [Fact]
    public void TheErrorCatalogue_DocumentsEveryCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "mcp-http-errors.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var catalogue = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "mcp-http-errors.md"));

        foreach (var (code, status) in new[] { ("unauthorized", 401), ("host-not-allowed", 400), ("upload-too-large", 413) })
        {
            Assert.Matches(new Regex($"^## `{Regex.Escape(code)}`$", RegexOptions.Multiline), catalogue);
            Assert.Contains($"{status}", catalogue);
        }
    }
}
