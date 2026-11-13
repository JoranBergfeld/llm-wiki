using System;
using System.Collections.Generic;
using System.Linq;
using ModelContextProtocol.Server;
using Wiki.Core;

namespace Wiki.Mcp;

// Per-instance tool narrowing (issue #60, decision #30 "Exposure"). Every
// tool is exposed by default - approvals and repair included, a deliberate
// choice of automation over a hard human gate - and an operator removes
// tools from ONE server instance with `--exclude-tools` or
// WIKI_MCP_EXCLUDE_TOOLS, configured the same way as the server's vault and
// token. Never wiki.yaml: the vault does not decide what its clients may do.
//
// The set is fixed at startup. Removed tools are taken out of the server's
// tool collection, so they are absent from tools/list and a call to one is
// the ordinary unknown-tool protocol error. No tool argument or request
// header reaches this: it runs before the server answers anything.
//
// Keyed on the advertised tool name only, so a tool added to the catalogue
// later is narrowable with no work of its own.
public static class ToolNarrowing
{
    public const string Option = "--exclude-tools";
    public const string Env = "WIKI_MCP_EXCLUDE_TOOLS";

    // The flag, when given (even empty), replaces the env var rather than
    // adding to it - the same precedence as --vault over WIKI_VAULT.
    // Each value is a comma-separated list; blanks are ignored.
    public static IReadOnlySet<string> Parse(IEnumerable<string>? flag, string? env)
    {
        var raw = flag ?? (string.IsNullOrEmpty(env) ? [] : [env]);
        return raw
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    // Fails fast on a name that matches no registered tool: a typo would
    // otherwise silently leave the tool the operator meant to remove exposed.
    public static void Apply(McpServerOptions options, IReadOnlySet<string> excluded)
    {
        if (excluded.Count == 0)
            return;

        var tools = options.ToolCollection ?? [];
        var known = tools.Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = excluded.Where(n => !known.Contains(n)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new ValidationException("unknown-tool",
                $"{Option}: no such tool {string.Join(", ", unknown.Select(n => $"'{n}'"))}; " +
                $"tools: {string.Join(", ", known.Order(StringComparer.Ordinal))}");

        foreach (var tool in tools.Where(t => excluded.Contains(t.ProtocolTool.Name)).ToArray())
            tools.Remove(tool);
    }
}
