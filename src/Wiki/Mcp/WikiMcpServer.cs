using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wiki.Cli;

namespace Wiki.Mcp;

// `wiki mcp` (stdio): one MCP server for one vault, bound at startup. Built on
// a bare ServiceCollection rather than the generic host - the host's console
// lifetime and default console logger would write to the process stdout,
// which in stdio mode carries the JSON-RPC stream.
public static class WikiMcpServer
{
    // `lockTimeout`: seconds (invariant culture) passed as --lock-timeout to
    // every CLI step; null leaves each step to WIKI_LOCK_TIMEOUT / the default.
    public static int RunStdio(string vaultRoot, IReadOnlySet<string> excludedTools, IngestInbox inbox, string? lockTimeout, StdioStreams streams)
    {
        var cli = new CliAdapter(vaultRoot, new InvocationOrigin(Transport.Stdio, TokenUsed: false), lockTimeout);

        var services = new ServiceCollection();
        var builder = services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "wiki", Version = Version() })
            .WithStreamServerTransport(streams.Input, streams.Output)
            .WithToolArgumentGuard()
            .AddWikiTools(cli, inbox);
        streams.ConfigureForTests?.Invoke(builder, cli);

        return Serve(services, excludedTools).GetAwaiter().GetResult();
    }

    private static async Task<int> Serve(ServiceCollection services, IReadOnlySet<string> excludedTools)
    {
        // The server only implements IAsyncDisposable.
        await using var provider = services.BuildServiceProvider();

        // Narrow before the server exists: it reads this same options
        // instance, and an unknown name refuses to start (exit 1).
        ToolNarrowing.Apply(provider.GetRequiredService<IOptions<McpServerOptions>>().Value, excludedTools);

        var server = provider.GetRequiredService<McpServer>();

        // Returns when the client closes the input stream.
        await server.RunAsync();
        return 0;
    }

    private static string Version()
        => typeof(WikiMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
