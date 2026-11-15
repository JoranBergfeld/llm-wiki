using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Wiki.Mcp;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// The MCP handshake flake (full-suite runs, "Method 'initialize' is not
// available on protocol version '2026-07-28'"). A default SDK client probes
// with server/discover and, if no reply lands within DiscoverProbeTimeout
// (5 s), falls back to initialize on the same connection. `wiki mcp` speaks
// 2026-07-28, so it answers the probe - late - and then rejects the
// fallback initialize. Under thread-pool starvation the reply is late
// without the server being slow at all. Every test client therefore uses
// McpHarness.ClientOptions, which never takes that fallback against this
// server: a late reply is only late, bounded by the connect timeout.
public class HandshakeTests
{
    [Fact]
    public async Task AReplyLaterThanTheDiscoverProbe_StillConnects()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");

        var c2s = new Pipe();
        var s2c = new Pipe();
        var streams = new StdioStreams(c2s.Reader.AsStream(), s2c.Writer.AsStream());
        var server = Task.Run(() => App.Main(["mcp", "--vault", tv.Path], TextWriter.Null, TextReader.Null, streams));

        // The server's first reply reaches the client after the SDK's default
        // 5 s probe window, as it does when the client's read loop is starved.
        var slowReplies = new FirstReadDelayedStream(s2c.Reader.AsStream(), TimeSpan.FromSeconds(6));
        var transport = new StreamClientTransport(c2s.Writer.AsStream(), slowReplies);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using (var client = await McpClient.CreateAsync(transport, McpHarness.ClientOptions(), cancellationToken: timeout.Token))
        {
            Assert.Equal("wiki", client.ServerInfo?.Name);
            Assert.Contains("search_wiki", (await client.ListToolsAsync(cancellationToken: timeout.Token)).Select(t => t.Name));
        }

        await c2s.Writer.CompleteAsync();
        Assert.Equal(0, await server.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private sealed class FirstReadDelayedStream(Stream inner, TimeSpan delay) : Stream
    {
        private bool _delayed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, cancellationToken);
            }
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
