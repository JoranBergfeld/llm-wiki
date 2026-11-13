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

    public static async Task<McpHarness> StartAsync(TempVault vault, string transport)
    {
        if (transport != McpTransports.Stdio)
            throw new NotSupportedException($"transport '{transport}' is not wired into the harness yet");

        var c2s = new Pipe();
        var s2c = new Pipe();
        var streams = new StdioStreams(c2s.Reader.AsStream(), s2c.Writer.AsStream());

        var serverStdout = new StringWriter();
        var server = Task.Run(() => App.Main(["mcp", "--vault", vault.Path], serverStdout, TextReader.Null, streams));

        var clientTransport = new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(clientTransport, cancellationToken: timeout.Token);
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
    public static TheoryData<string> All => new() { Stdio };
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
