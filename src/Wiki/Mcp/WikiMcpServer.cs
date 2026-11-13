using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
    public static int RunStdio(string vaultRoot, StdioStreams streams)
    {
        var cli = new CliAdapter(vaultRoot, CommandOrigin.Stdio);

        var services = new ServiceCollection();
        var builder = services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "wiki", Version = Version() })
            .WithStreamServerTransport(streams.Input, streams.Output)
            .AddWikiTools(cli);
        streams.ConfigureForTests?.Invoke(builder, cli);

        return Serve(services).GetAwaiter().GetResult();
    }

    private static async Task<int> Serve(ServiceCollection services)
    {
        // The server only implements IAsyncDisposable.
        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();

        // Returns when the client closes the input stream.
        await server.RunAsync();
        return 0;
    }

    private static string Version()
        => typeof(WikiMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
