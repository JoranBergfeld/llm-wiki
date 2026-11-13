using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Wiki.Mcp;
using Xunit;

namespace Wiki.Tests.Support;

// The MCP test seam (spec, Testing Decisions, seam 2): a real MCP SDK client
// talking to the real `wiki mcp` command. The server is started exactly as
// the process entry point starts it - App.Main(["mcp", "--vault", ...],
// stdout, stdin, streams) - so vault resolution, the command catalog and the
// adapter are all on the path. Over stdio only the raw stdio streams are
// swapped for an in-memory pipe pair; over http (`wiki mcp --http`) only
// Kestrel is swapped for an in-memory TestServer.
//
// Tests take the transport as a theory parameter (McpTransports.All): every
// tool test runs over in-memory stdio and over streamable HTTP.
public sealed class McpHarness : IAsyncDisposable
{
    // stdio: the in-memory pipe pair. http: the TestServer host.
    private readonly Pipe? _clientToServer;
    private readonly Pipe? _serverToClient;
    private readonly McpHttpHost? _http;
    private readonly Task<int> _server;
    private readonly StringWriter _serverStdout;

    public McpClient Client { get; }

    private McpHarness(Pipe? c2s, Pipe? s2c, McpHttpHost? http, Task<int> server, StringWriter serverStdout, McpClient client)
    {
        _clientToServer = c2s;
        _serverToClient = s2c;
        _http = http;
        _server = server;
        _serverStdout = serverStdout;
        Client = client;
    }

    // `extraTools` registers test-only tools beside the catalogue, through the
    // same CliAdapter (StdioStreams.ConfigureForTests), for adapter behaviour
    // no shipped tool can trigger yet. `serverArgs` are appended to the
    // `wiki mcp` argv (e.g. --exclude-tools); over http the argv also gets
    // --http.
    public static async Task<McpHarness> StartAsync(TempVault vault, string transport,
        Action<IMcpServerBuilder, CliAdapter>? extraTools = null,
        params string[] serverArgs)
    {
        if (transport == McpTransports.Http)
        {
            var host = await McpHttpHost.StartAsync(vault, extraTools, serverArgs);
            try
            {
                var client = await host.ConnectAsync(token: null);
                return new McpHarness(null, null, host, host.Server, host.ServerStdout, client);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }
        if (transport != McpTransports.Stdio)
            throw new NotSupportedException($"transport '{transport}' is not wired into the harness");

        var c2s = new Pipe();
        var s2c = new Pipe();
        var streams = new StdioStreams(c2s.Reader.AsStream(), s2c.Writer.AsStream()) { ConfigureForTests = extraTools };

        var serverStdout = new StringWriter();
        // App.Main blocks its thread for the server's lifetime (RunStdio is
        // the process's synchronous entry point), so it gets a dedicated
        // thread rather than holding a pool thread the async MCP traffic needs.
        var server = Task.Factory.StartNew(
            () => App.Main(["mcp", "--vault", vault.Path, .. serverArgs], serverStdout, TextReader.Null, streams),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // A server that refuses to start never answers initialize; closing
        // its side of the pipe makes the client fail now, not at the timeout.
        _ = server.ContinueWith(_ => s2c.Writer.Complete(), TaskScheduler.Default);

        var clientTransport = new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        McpClient stdioClient;
        try
        {
            stdioClient = await McpClient.CreateAsync(clientTransport, ClientOptions(), cancellationToken: timeout.Token);
        }
        catch
        {
            // Surface the server's own failure (e.g. a startup envelope)
            // rather than a bare client timeout.
            await c2s.Writer.CompleteAsync();
            if (server.IsCompleted)
                throw new InvalidOperationException($"server exited {server.Result}: {serverStdout}");
            throw;
        }
        return new McpHarness(c2s, s2c, null, server, serverStdout, stdioClient);
    }

    // The options every test MCP client uses (harness and E2E). The SDK
    // client's default handshake probes with server/discover and, when no
    // reply arrives within DiscoverProbeTimeout (5 s), falls back to
    // initialize on the same connection - meant for servers that silently
    // drop the unknown method. `wiki mcp` speaks 2026-07-28: it answers the
    // probe, however late, and then rejects the fallback ("Method
    // 'initialize' is not available on protocol version '2026-07-28'"). In
    // the full parallel suite the reply is routinely > 5 s late because the
    // client's own continuations wait for a thread-pool thread, not because
    // the server is slow. So the probe gets no separate deadline: a late
    // reply is just late, bounded by the caller's connect timeout. See
    // HandshakeTests and ThreadPoolFloor.
    public static McpClientOptions ClientOptions() => new() { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };

    public Task<CallToolResult> CallAsync(string tool, IReadOnlyDictionary<string, object?>? args = null)
        => Client.CallToolAsync(tool, args ?? new Dictionary<string, object?>()).AsTask();

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        if (_http is not null)
        {
            await _http.DisposeAsync();
            return;
        }
        await _clientToServer!.Writer.CompleteAsync();
        var exit = await _server.WaitAsync(TimeSpan.FromSeconds(30));
        // The stdio server never writes to the CLI stdout: that is the
        // JSON-RPC channel. Anything here would have corrupted the protocol.
        Assert.True(_serverStdout.ToString() == "", _serverStdout.ToString());
        Assert.Equal(0, exit);
        await _serverToClient!.Reader.CompleteAsync();
    }
}

// `wiki mcp --http`, started through App.Main exactly as the process starts
// it, with one swap: the WebApplication runs on an in-memory TestServer
// instead of Kestrel (StdioStreams.ConfigureHttpForTests). Also hands out raw
// HttpClients, for the transport's own rejections (problem+json), which an
// MCP client only sees as a failed connection.
public sealed class McpHttpHost : IAsyncDisposable
{
    public const string Endpoint = "http://localhost/mcp";

    private readonly WebApplication _app;

    public Task<int> Server { get; }
    public StringWriter ServerStdout { get; }
    public TestServer TestServer { get; }

    private McpHttpHost(WebApplication app, Task<int> server, StringWriter stdout)
    {
        _app = app;
        Server = server;
        ServerStdout = stdout;
        TestServer = (TestServer)app.Services.GetRequiredService<IServer>();
    }

    public static async Task<McpHttpHost> StartAsync(TempVault vault,
        Action<IMcpServerBuilder, CliAdapter>? extraTools = null,
        params string[] serverArgs)
    {
        var started = new TaskCompletionSource<WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new StdioStreams(Stream.Null, Stream.Null)
        {
            ConfigureForTests = extraTools,
            ConfigureHttpForTests = builder => builder.WebHost.UseTestServer(),
            HttpStartedForTests = app => started.TrySetResult(app),
        };
        var stdout = new StringWriter();
        // App.Main blocks for the server's lifetime: a dedicated thread.
        var server = Task.Factory.StartNew(
            () => App.Main(["mcp", "--http", "--vault", vault.Path, .. serverArgs], stdout, TextReader.Null, streams),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var first = await Task.WhenAny(started.Task, server).WaitAsync(TimeSpan.FromSeconds(30));
        if (first == server)
            throw new InvalidOperationException($"server exited {server.Result}: {stdout}");
        return new McpHttpHost(await started.Task, server, stdout);
    }

    // TestServer sends Host: localhost unless a request sets another.
    public HttpClient CreateHttpClient()
    {
        var client = TestServer.CreateClient();
        client.BaseAddress = new Uri("http://localhost/");
        return client;
    }

    public async Task<McpClient> ConnectAsync(string? token)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(Endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "wiki-http",
        };
        if (token is not null)
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        var transport = new HttpClientTransport(options, CreateHttpClient(), ownsHttpClient: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await McpClient.CreateAsync(transport, McpHarness.ClientOptions(), cancellationToken: timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        var exit = await Server.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(exit == 0, $"exit {exit}: {ServerStdout}");
    }
}

public static class McpTransports
{
    public const string Stdio = "stdio";
    public const string Http = "http";

    // Every MCP transport theory runs once per row: in-memory stdio and
    // streamable HTTP on a TestServer (issue #61).
    public static readonly string[] Names = [Stdio, Http];

    public static TheoryData<string> All => new(Names);
}

// Reads the tool result's structuredContent as the CLI envelope (+ exitCode,
// completedSteps) the adapter returns.
public static class ToolResultExtensions
{
    public static JsonElement Structured(this CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        return JsonSerializer.SerializeToElement(result.StructuredContent!.Value);
    }

    public static string Text(this CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
