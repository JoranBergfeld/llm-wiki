using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// `wiki mcp --max-concurrent-calls <n>` (amendment BA): each tool call runs on
// a thread of its own (AS "Threads"), so without a cap a burst of calls is a
// burst of threads. At most n calls run at once; the rest wait their turn -
// they are not refused.
public class ConcurrencyCapTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    // Counts the calls inside it and holds each one until released.
    [McpServerToolType]
    public sealed class Gate
    {
        private readonly ManualResetEventSlim _open = new(false);
        private int _inside;
        private int _maxInside;
        private int _entered;

        public int MaxInside => Volatile.Read(ref _maxInside);
        public int Entered => Volatile.Read(ref _entered);
        public int Inside => Volatile.Read(ref _inside);

        public void Open() => _open.Set();

        [McpServerTool(Name = "probe_gate"), Description("test probe")]
        public CallToolResult Run()
        {
            var now = Interlocked.Increment(ref _inside);
            Interlocked.Increment(ref _entered);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInside)) && Interlocked.CompareExchange(ref _maxInside, now, seen) != seen) { }
            _open.Wait(TimeSpan.FromSeconds(60));
            Interlocked.Decrement(ref _inside);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task AtMostTheCapRunsAtOnce_TheRestWait_AndAllComplete(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var gate = new Gate();
        await using var mcp = await McpHarness.StartAsync(tv, transport, (b, _) => b.WithTools(gate), "--max-concurrent-calls", "2");
        try
        {
            var calls = Enumerable.Range(0, 6).Select(_ => mcp.CallAsync("probe_gate")).ToArray();

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (gate.Inside < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            await Task.Delay(500); // room for an over-admitted call to show up

            Assert.Equal(2, gate.Inside);
            Assert.Equal(2, gate.Entered);
            // Waiting calls are queued, not refused, and the server still answers.
            Assert.All(calls, c => Assert.False(c.IsCompleted));
            Assert.Contains("probe_gate", (await mcp.Client.ListToolsAsync()).Select(t => t.Name));

            gate.Open();
            var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.All(results, r => Assert.NotEqual(true, r.IsError));
            Assert.Equal(6, gate.Entered);
            Assert.Equal(2, gate.MaxInside);
        }
        finally
        {
            gate.Open();
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public async Task AnInvalidCap_RefusesToStart(string value)
    {
        using var tv = new TempVault(); Init(tv);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpHarness.StartAsync(tv, McpTransports.Stdio, null, "--json", "--max-concurrent-calls", value));

        Assert.Contains("server exited 1", ex.Message);
        Assert.Contains("\"invalid-max-concurrent-calls\"", ex.Message);
    }
}
