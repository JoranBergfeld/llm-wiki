using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using Wiki.Core;
using Wiki.Mcp;

namespace Wiki.Cli.Commands;

// `wiki mcp`: serve the vault to MCP clients over stdio (issue #54), or over
// streamable HTTP with --http (issue #61). The
// vault is resolved once, here, to an absolute path, and the server refuses
// to start when it cannot be - a misconfiguration shows up immediately, not
// on the first tool call. Classified CommandKind.Host: the command itself
// touches nothing, and every tool call it serves is a separate App.Main
// invocation that passes the boundary per CLI step.
public static class McpCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin, StdioStreams? stdio)
    {
        var mcp = new Command("mcp", "Serve this vault to MCP clients over stdio (JSON-RPC on stdin/stdout), or over streamable HTTP with --http");

        // Issue #60: remove tools from this server instance. Repeatable and
        // comma-separated; an unknown name refuses to start.
        var excludeOption = new Option<string[]>(ToolNarrowing.Option)
        {
            Description = $"Tools this server does not expose, comma-separated or repeated (overrides {ToolNarrowing.Env}; default: none, every tool is exposed)",
            Arity = ArgumentArity.OneOrMore,
        };
        mcp.Add(excludeOption);

        // Issue #57: where ingest_source lands uploads, and how large one may
        // be. Server config fixed at startup; no tool argument reaches either.
        var inboxRootOption = new Option<string?>(IngestInbox.RootOption)
        {
            Description = $"Inbox directory ingest_source lands uploads in, as <dir>/<category>/<filename>; must be outside the vault (overrides {IngestInbox.RootEnv}; default: none, ingest_source refuses)",
        };
        var maxUploadOption = new Option<string?>(IngestInbox.MaxUploadOption)
        {
            Description = $"Largest upload ingest_source accepts, in bytes (overrides {IngestInbox.MaxUploadEnv}; default: {IngestInbox.DefaultMaxUploadBytes})",
        };
        mcp.Add(inboxRootOption);
        mcp.Add(maxUploadOption);

        // Issue #61: streamable HTTP instead of stdio, and its server config.
        var httpOption = new Option<bool>(McpHttpSettings.HttpOption)
        {
            Description = $"Serve streamable HTTP (stateless, plain http, endpoint {McpHttpSettings.EndpointPath}) instead of stdio; runs until stopped",
        };
        var listenOption = new Option<string?>(McpHttpSettings.ListenOption)
        {
            Description = $"With --http: the http://<host>:<port> to listen on (overrides {McpHttpSettings.ListenEnv}; default {McpHttpSettings.DefaultListen})",
        };
        var tokenOption = new Option<string?>(McpHttpSettings.TokenOption)
        {
            Description = $"With --http: the bearer token every request must present (overrides {McpHttpSettings.TokenEnv}, which is preferred: argv is visible to other users; default: none)",
        };
        var allowedHostsOption = new Option<string[]>(McpHttpSettings.AllowedHostsOption)
        {
            Description = $"With --http: Host header names this server answers for, comma-separated or repeated; '*' allows any (overrides {McpHttpSettings.AllowedHostsEnv}; default: localhost,127.0.0.1,[::1])",
            Arity = ArgumentArity.OneOrMore,
        };
        mcp.Add(httpOption);
        mcp.Add(listenOption);
        mcp.Add(tokenOption);
        mcp.Add(allowedHostsOption);

        mcp.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();

            // Only the process entry point (and the MCP test seam) hands in
            // raw streams. An in-process caller - including an MCP tool step,
            // should one ever build this argv - has none, and must not start
            // a nested server.
            if (stdio is null)
                throw new ValidationException("mcp-unavailable", "'wiki mcp' can only be started as its own process");

            var excluded = ToolNarrowing.Parse(
                parseResult.GetResult(excludeOption) is null ? null : parseResult.GetValue(excludeOption),
                Environment.GetEnvironmentVariable(ToolNarrowing.Env));

            var inbox = IngestInbox.Resolve(parseResult.GetValue(inboxRootOption), parseResult.GetValue(maxUploadOption), vault.Root);

            // The global --lock-timeout, given to `wiki mcp`, is the lock
            // timeout of every CLI step this server runs (a step's argv
            // cannot carry one: tools never build raw argv). Validated now,
            // so a bad value refuses to start instead of failing every later
            // tool call. Absent, each step falls back to WIKI_LOCK_TIMEOUT,
            // then the 10 s default, exactly as on the command line.
            var lockTimeout = parseResult.GetValue<string?>(VaultLock.TimeoutOption) is { Length: > 0 } flag
                ? VaultLock.ResolveTimeout(flag, env: null).TotalSeconds.ToString(CultureInfo.InvariantCulture)
                : null;

            if (!parseResult.GetValue(httpOption))
            {
                // A transport setting without the transport is a mistake the
                // operator should hear about, not a silently open server.
                foreach (var name in new[] { McpHttpSettings.ListenOption, McpHttpSettings.TokenOption, McpHttpSettings.AllowedHostsOption })
                    if (parseResult.GetResult(name) is not null)
                        throw new ValidationException("http-only-option", $"{name} only applies with {McpHttpSettings.HttpOption}");
                return WikiMcpServer.RunStdio(vault.Root, excluded, inbox, lockTimeout, stdio);
            }

            var http = McpHttpSettings.Resolve(
                parseResult.GetValue(listenOption),
                parseResult.GetValue(tokenOption),
                parseResult.GetResult(allowedHostsOption) is null ? null : parseResult.GetValue(allowedHostsOption),
                inbox);
            return WikiMcpServer.RunHttp(vault.Root, excluded, inbox, lockTimeout, http, stdio, ctx.Out, ctx.Json);
        }));

        return mcp;
    }
}
