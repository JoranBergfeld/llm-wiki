using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Wiki.Mcp;
using Xunit;

namespace Wiki.Tests.Support;

// The MCP test seam (spec, Testing Decisions, seam 2): a real MCP SDK client
// talking to the real `wiki mcp` command over an in-memory transport. The
// server is started exactly as the process entry point starts it -
// App.Main(["mcp", "--vault", ...], stdout, stdin, streams) - so vault
// resolution, the command catalog and the adapter are all on the path. Only
// the raw stdio streams are swapped for an in-memory pipe pair.
//
// Tests take the transport as a theory parameter (McpTransports.All) so the
// HTTP transport can join later by adding one row: every existing tool test
// then runs over both.
public sealed class McpHarness : IAsyncDisposable
{
    private readonly Pipe _clientToServer;
    private readonly Pipe _serverToClient;
    private readonly Task<int> _server;
    private readonly StringWriter _serverStdout;

    public McpClient Client { get; }

    private McpHarness(Pipe c2s, Pipe s2c, Task<int> server, StringWriter serverStdout, McpClient client)
    {
        _clientToServer = c2s;
        _serverToClient = s2c;
        _server = server;
        _serverStdout = serverStdout;
        Client = client;
    }

    // `extraTools` registers test-only tools beside the catalogue, through the
    // same CliAdapter (StdioStreams.ConfigureForTests), for adapter behaviour
    // no shipped tool can trigger yet. `serverArgs` are appended to the
    // `wiki mcp` argv (e.g. --exclude-tools).
    public static async Task<McpHarness> StartAsync(TempVault vault, string transport,
        Action<Microsoft.Extensions.DependencyInjection.IMcpServerBuilder, CliAdapter>? extraTools = null,
        params string[] serverArgs)
    {
        if (transport != McpTransports.Stdio)
            throw new NotSupportedException($"transport '{transport}' is not wired into the harness yet");

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
        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(clientTransport, ClientOptions(), cancellationToken: timeout.Token);
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
        return new McpHarness(c2s, s2c, server, serverStdout, client);
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
        await _clientToServer.Writer.CompleteAsync();
        var exit = await _server.WaitAsync(TimeSpan.FromSeconds(30));
        // The stdio server never writes to the CLI stdout: that is the
        // JSON-RPC channel. Anything here would have corrupted the protocol.
        Assert.True(_serverStdout.ToString() == "", _serverStdout.ToString());
        Assert.Equal(0, exit);
        await _serverToClient.Reader.CompleteAsync();
    }
}

public static class McpTransports
{
    public const string Stdio = "stdio";

    // Add "http" here once the streamable HTTP transport lands (TestServer).
    public static readonly string[] Names = [Stdio];

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
