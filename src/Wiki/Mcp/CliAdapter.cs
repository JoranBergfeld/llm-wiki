using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Wiki.Cli;

namespace Wiki.Mcp;

// The in-process adapter every MCP tool goes through (issue #37). Each CLI
// step is a call to App.Main(argv, stdout, stdin, origin) - the same entry
// point a shell reaches - so the command catalog, the read/mutation boundary
// (lock, audit, recovery) and validation apply per step exactly as on the
// command line. Tools read the exit code and the --json envelope like an
// outside agent would; they never call services.
//
// The vault is bound once at startup to an absolute path and added to every
// call as --vault=<abs>, so neither WIKI_VAULT nor the working directory
// decides anything after startup. Likewise the server's lock timeout
// (`wiki mcp --lock-timeout`), when given, is added as --lock-timeout=<s>.
// The adapter never sets environment variables or changes directory.
public sealed class CliAdapter(string vaultRoot, InvocationOrigin origin, string? lockTimeout = null)
{
    public string VaultRoot { get; } = vaultRoot;

    // Seconds (invariant culture), or null for each step's own fallback
    // (WIKI_LOCK_TIMEOUT, then the 10 s default).
    public string? LockTimeout { get; } = lockTimeout;

    // How this server's steps arrived; the audit log records it per step.
    public InvocationOrigin Origin { get; } = origin;

    // Start an intent: a sequence of steps that stops at the first non-zero
    // exit. Cancellation is checked between steps, never during one.
    public IntentRun Begin(CancellationToken cancellationToken) => new(this, cancellationToken);

    internal CliStepResult Run(CliStep step)
    {
        var stdout = new StringWriter();
        var stdin = new StringReader(step.StdinText ?? "");
        var exit = App.Main(step.ToArgv(VaultRoot, LockTimeout), stdout, stdin, Origin);

        // --json guarantees the envelope is the last line.
        var lines = stdout.ToString().Trim().Split('\n');
        var envelope = JsonNode.Parse(lines[^1])!.AsObject();
        return new CliStepResult(step, exit, envelope);
    }
}

internal sealed record CliStepResult(CliStep Step, int ExitCode, JsonObject Envelope);
