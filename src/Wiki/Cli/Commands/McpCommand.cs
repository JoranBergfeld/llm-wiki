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

        mcp.SetAction(CommandBinding.Bind(vaultOption, jsonOption, stdout, stdin, (parseResult, ctx) =>
        {
            var vault = ctx.ResolveVault();

            // Only the process entry point (and the MCP test seam) hands in
            // raw streams. An in-process caller - including an MCP tool step,
            // should one ever build this argv - has none, and must not start
            // a nested server.
            if (stdio is null)
                throw new ValidationException("mcp-unavailable", "'wiki mcp' can only be started as its own process");

            return WikiMcpServer.RunStdio(vault.Root, stdio);
        }));

        return mcp;
    }
}
