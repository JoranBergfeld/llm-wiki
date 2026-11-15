using System.Collections.Generic;
using System.Globalization;
using Wiki.Core;

namespace Wiki.Mcp;

// What `wiki mcp` fixed at startup that both transports share: the vault it
// is bound to, the tools it does not expose (amendment AG), the upload inbox
// (AP), the lock timeout of every CLI step it runs, and how many tool calls
// may run at once (BA). Transport-specific settings (McpHttpSettings, the
// stdio streams) are passed beside it.
public sealed record WikiServerOptions(
    string VaultRoot,
    IReadOnlySet<string> ExcludedTools,
    IngestInbox Inbox,
    string? LockTimeout,
    int MaxConcurrentCalls)
{
    public const string MaxConcurrentCallsOption = "--max-concurrent-calls";
    public const string MaxConcurrentCallsEnv = "WIKI_MCP_MAX_CONCURRENT_CALLS";

    // Each call in flight holds a thread of its own (AS "Threads") and, for
    // a write, waits on the one vault lock anyway; 16 is plenty for a few
    // assistants and keeps a burst from becoming hundreds of threads.
    public const int DefaultMaxConcurrentCalls = 16;

    // The flag, else the env var, else the default. A positive integer;
    // anything else refuses to start (exit 1), like the other server knobs.
    public static int ResolveMaxConcurrentCalls(string? flag, string? env)
    {
        var raw = flag ?? (string.IsNullOrEmpty(env) ? null : env);
        if (raw is null)
            return DefaultMaxConcurrentCalls;
        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
            return n;
        throw new ValidationException("invalid-max-concurrent-calls",
            $"'{raw}' is not a valid {MaxConcurrentCallsOption} ({MaxConcurrentCallsEnv}): expected a positive whole number of tool calls");
    }
}
