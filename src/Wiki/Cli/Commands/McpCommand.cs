using System;
using System.CommandLine;
using System.IO;
using Wiki.Core;
using Wiki.Mcp;

namespace Wiki.Cli.Commands;

// `wiki mcp`: serve the vault to MCP clients over stdio (issue #54). The
// vault is resolved once, here, to an absolute path, and the server refuses
// to start when it cannot be - a misconfiguration shows up immediately, not
// on the first tool call. Classified CommandKind.Host: the command itself
// touches nothing, and every tool call it serves is a separate App.Main
// invocation that passes the boundary per CLI step.
public static class McpCommand
{
    public static Command Build(Option<string?> vaultOption, Option<bool> jsonOption, TextWriter stdout, TextReader stdin, StdioStreams? stdio)
    {
        var mcp = new Command("mcp", "Serve this vault to MCP clients over stdio (JSON-RPC on stdin/stdout)");

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

            return WikiMcpServer.RunStdio(vault.Root, excluded, inbox, stdio);
        }));

        return mcp;
    }
}
