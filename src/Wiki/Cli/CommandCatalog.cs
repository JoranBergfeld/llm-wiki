using System.Collections.Generic;

namespace Wiki.Cli;

// Whether a command may write to the vault. Decides which boundary
// (VaultBoundary) the command runs inside.
public enum CommandKind
{
    // Never writes a byte anywhere in the vault - not a state file, not
    // log.md. Runs inside the read boundary.
    Read,

    // May write, on any path through the command (including behind a flag:
    // `source scan` is Mutate even though `--dry-run` writes nothing, and
    // `links check` is Mutate because `--external` files issues). Runs
    // inside the mutation boundary.
    Mutate,

    // Hosts other invocations rather than touching the vault itself:
    // `wiki mcp` runs for the server's whole lifetime, and each tool call it
    // serves is its own App.Main invocation that passes the read or mutation
    // boundary per CLI step. Runs inside NO boundary - the lock (even the
    // shared read lock) or an audit step wrapped around the host would be
    // held for the server's lifetime and block, or double-record, every step
    // it hosts.
    Host,
}

// The one place every command is classified (issue #42). Keyed by the full
// command path as typed after `wiki`, space-separated: "page upsert",
// "lint". Adding a command means adding one line here; the
// CommandClassificationTests walk the real command tree and fail on any
// invocable command missing from this table, and on any entry here that no
// longer matches a command.
//
// When in doubt, classify as Mutate. Mis-classifying a writer as Read lets it
// past the vault lock and out of the audit log; the reverse only costs a
// lock acquisition.
public static class CommandCatalog
{
    public static readonly IReadOnlyDictionary<string, CommandKind> Entries = new Dictionary<string, CommandKind>(System.StringComparer.Ordinal)
    {
        ["init"] = CommandKind.Mutate,

        ["page upsert"] = CommandKind.Mutate,
        ["page show"] = CommandKind.Read,
        ["page list"] = CommandKind.Read,
        ["page backlinks"] = CommandKind.Read,
        ["page rename"] = CommandKind.Mutate,
        ["page set-status"] = CommandKind.Mutate,

        ["reindex"] = CommandKind.Mutate,

        ["source add"] = CommandKind.Mutate,
        ["source scan"] = CommandKind.Mutate,
        ["source list"] = CommandKind.Read,
        ["source show"] = CommandKind.Read,
        ["source impact"] = CommandKind.Read,
        ["source retract"] = CommandKind.Mutate,

        ["ingest status"] = CommandKind.Read,
        ["ingest advance"] = CommandKind.Mutate,
        ["ingest resume"] = CommandKind.Read,

        ["search"] = CommandKind.Read,

        ["index show"] = CommandKind.Read,

        ["issues list"] = CommandKind.Read,
        ["issues show"] = CommandKind.Read,
        ["issues resolve"] = CommandKind.Mutate,

        ["lint"] = CommandKind.Mutate,

        ["review list"] = CommandKind.Read,
        ["review approve"] = CommandKind.Mutate,
        ["review reject"] = CommandKind.Mutate,

        ["schema propose"] = CommandKind.Mutate,
        ["schema proposals"] = CommandKind.Read,
        ["schema approve"] = CommandKind.Mutate,
        ["schema reject"] = CommandKind.Mutate,

        ["category propose"] = CommandKind.Mutate,
        ["category proposals"] = CommandKind.Read,
        ["category approve"] = CommandKind.Mutate,
        ["category reject"] = CommandKind.Mutate,
        ["category add"] = CommandKind.Mutate,
        ["category list"] = CommandKind.Read,

        ["eval"] = CommandKind.Read,

        ["audit"] = CommandKind.Read,
        ["audit next"] = CommandKind.Read,
        ["audit record"] = CommandKind.Mutate,
        ["audit reanchor"] = CommandKind.Mutate,
        ["audit list"] = CommandKind.Read,

        ["links check"] = CommandKind.Mutate,
        ["persona read"] = CommandKind.Read,

        ["mcp"] = CommandKind.Host,
    };

    // null means unclassified: a bug, which the tests catch before it ships
    // and VaultBoundary refuses at runtime.
    public static CommandKind? Classify(string commandPath)
        => Entries.TryGetValue(commandPath, out var kind) ? kind : null;
}
