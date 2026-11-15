using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Wiki.Mcp;

// The raw byte streams `wiki mcp` speaks JSON-RPC over. Program.cs opens the
// process's standard streams and hands them in (System.Console is banned
// everywhere else, issue #43); tests hand in an in-memory pipe pair instead.
// Nothing else in the CLI ever sees these: every command writes only to the
// TextWriter App.Main was given.
public sealed class StdioStreams(Stream input, Stream output)
{
    public Stream Input { get; } = input;
    public Stream Output { get; } = output;

    // Test seam only (InternalsVisibleTo Wiki.Tests): registers extra tools
    // next to the catalogue, so the adapter's error mapping can be driven
    // through a real MCP client for exit codes no shipped tool can produce
    // yet. Registration still goes through the generic WithTools<T>.
    internal Action<IMcpServerBuilder, CliAdapter>? ConfigureForTests { get; init; }

    // Test seam only, `wiki mcp --http` (issue #61): swaps Kestrel for an
    // in-memory TestServer before the app is built, and hands the started app
    // back so the test can reach that server and stop it. The middleware,
    // endpoint and tool set are the shipped ones.
    internal Action<WebApplicationBuilder>? ConfigureHttpForTests { get; init; }
    internal Action<WebApplication>? HttpStartedForTests { get; init; }
}
