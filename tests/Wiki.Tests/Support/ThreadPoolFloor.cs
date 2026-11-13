using System.Runtime.CompilerServices;

namespace Wiki.Tests.Support;

// Why the MCP tests flaked in full-suite runs (and passed alone): xUnit
// 2.9's default parallel algorithm runs test collections on thread-pool
// threads, up to maxParallelThreads (= core count) at once, and the CLI
// tests run App.Main synchronously end to end. So the pool's floor (also
// about the core count) is fully taken by synchronous test bodies, and the
// async MCP traffic - client and in-memory server read loops, the SDK's
// handshake timers - queues behind them while the pool grows only by its
// slow starvation injection. A stack dump taken during a stall showed a
// 24-thread pool with 27 queued work items and nearly every worker inside a
// synchronous test; the client saw the server's (sub-millisecond)
// server/discover reply about 6 s late.
//
// Raising the floor lets the pool create threads for that async work on
// demand instead. It costs nothing until the threads are needed. This is a
// property of the test host, not of `wiki mcp`: the real server runs one
// synchronous entry point on the main thread and its tool steps on pool
// threads it has to itself.
internal static class ThreadPoolFloor
{
    [ModuleInitializer]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        var floor = Math.Max(64, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(Math.Max(workers, floor), Math.Max(io, floor));
    }
}
