using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wiki.Cli;

namespace Wiki.Mcp;

// `wiki mcp`: one MCP server for one vault, bound at startup, over stdio or
// (`--http`) streamable HTTP. Both transports register the same tools, guard
// and narrowing; only the transport and the origin the audit log records
// differ.
public static class WikiMcpServer
{
    // `lockTimeout`: seconds (invariant culture) passed as --lock-timeout to
    // every CLI step; null leaves each step to WIKI_LOCK_TIMEOUT / the default.
    //
    // stdio is built on a bare ServiceCollection rather than the generic host:
    // the host's console lifetime and default console logger would write to
    // the process stdout, which in stdio mode carries the JSON-RPC stream.
    public static int RunStdio(string vaultRoot, IReadOnlySet<string> excludedTools, IngestInbox inbox, string? lockTimeout, StdioStreams streams)
    {
        var cli = new CliAdapter(vaultRoot, new InvocationOrigin(Transport.Stdio, TokenUsed: false), lockTimeout);

        var services = new ServiceCollection();
        var builder = services
            .AddMcpServer(Configure)
            .WithStreamServerTransport(streams.Input, streams.Output);
        AddTools(builder, cli, inbox, streams, excludedTools);

        return Serve(services).GetAwaiter().GetResult();
    }

    // `wiki mcp --http` (issue #61; decision #29): ASP.NET Core's slim
    // builder (no TLS, no HTTP/3 - AOT-clean), the SDK's streamable HTTP
    // transport in stateless mode, mapped at /mcp behind HttpGate. Runs until
    // the process is stopped (Ctrl+C / SIGTERM), then returns 0.
    //
    // Every step it runs is audited `via=http`, or `via=http+token` when the
    // server has a token: every request that reaches a tool then presented it.
    public static int RunHttp(string vaultRoot, IReadOnlySet<string> excludedTools, IngestInbox inbox, string? lockTimeout,
        McpHttpSettings settings, StdioStreams seam, TextWriter stdout, bool json)
    {
        var cli = new CliAdapter(vaultRoot, new InvocationOrigin(Transport.Http, TokenUsed: settings.Token is not null), lockTimeout);

        // No args and an explicit content root: neither the working
        // directory's appsettings.json nor command-line config can reach it.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = vaultRoot });
        // Logging would go to the process console; the CLI owns stdout.
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(settings.Listen);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = settings.MaxBodyBytes);

        var mcp = builder.Services
            .AddMcpServer(Configure)
            .WithHttpTransport(o => o.Stateless = true);
        AddTools(mcp, cli, inbox, seam, excludedTools);
        seam.ConfigureHttpForTests?.Invoke(builder);

        return ServeHttp(builder, settings, seam, stdout, json).GetAwaiter().GetResult();
    }

    private static async Task<int> ServeHttp(WebApplicationBuilder builder, McpHttpSettings settings,
        StdioStreams seam, TextWriter stdout, bool json)
    {
        await using var app = builder.Build();

        // Validates --exclude-tools before anything is served: an unknown
        // name refuses to start (exit 1). Each request's server is narrowed
        // by the same PostConfigure.
        _ = app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;

        app.UseHttpGate(settings);
        app.MapMcp(McpHttpSettings.EndpointPath);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        if (!json && addresses is { Count: > 0 })
        {
            // The one line the server writes; with port 0 it is how to find
            // the port. Over HTTP the stdout carries no protocol.
            stdout.WriteLine($"wiki mcp: serving over streamable HTTP at {string.Join(", ", addresses.Select(a => a.TrimEnd('/') + McpHttpSettings.EndpointPath))}");
            stdout.Flush();
        }
        seam.HttpStartedForTests?.Invoke(app);

        await app.WaitForShutdownAsync();
        return 0;
    }

    private static void Configure(McpServerOptions options)
        => options.ServerInfo = new Implementation { Name = "wiki", Version = Version() };

    private static void AddTools(IMcpServerBuilder builder, CliAdapter cli, IngestInbox inbox, StdioStreams seam, IReadOnlySet<string> excludedTools)
    {
        builder
            .WithToolArgumentGuard()
            .WithDedicatedToolThreads()
            .AddWikiTools(cli, inbox);
        seam.ConfigureForTests?.Invoke(builder, cli);
        builder.Services.AddToolNarrowing(excludedTools);
    }

    private static async Task<int> Serve(ServiceCollection services)
    {
        // The server only implements IAsyncDisposable.
        await using var provider = services.BuildServiceProvider();

        // Narrow (and validate the names) before the server exists: an
        // unknown name refuses to start (exit 1).
        _ = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        var server = provider.GetRequiredService<McpServer>();

        // Returns when the client closes the input stream.
        await server.RunAsync();
        return 0;
    }

    // Tool bodies are synchronous: each CLI step is App.Main, which blocks
    // its thread for the whole step - a vault-busy wait included (up to the
    // lock timeout). On thread-pool threads, a burst of concurrent HTTP calls
    // would hold the pool Kestrel and the SDK need to accept and answer
    // requests at all. So the tool itself runs on a thread of its own; the
    // request's thread only awaits it. One thread per call in flight.
    private static IMcpServerBuilder WithDedicatedToolThreads(this IMcpServerBuilder builder)
        => builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
            new ValueTask<CallToolResult>(Task.Factory.StartNew(
                () => next(context, cancellationToken).AsTask(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap())));

    private static string Version()
        => typeof(WikiMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
