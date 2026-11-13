using System;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Linq;
using Wiki.Cli;
using Wiki.Core;

namespace Wiki.Audit;

// The mutation-boundary step that writes one audit line per mutating command
// (issue #44). Registered in VaultBoundary.MutationSteps.
//
// Fail closed, in two halves:
//   before next(): resolve the vault, read wiki.yaml for `audit.path`, open
//                  the log and check its tail line. Any failure here throws
//                  before the command runs, so the vault is untouched.
//   after next():  store detail files, seal the line into the write journal
//                  (#47), then append and fsync it. A failure here surfaces
//                  as the command's failure (exit 2); the change has been
//                  made by then, and the sealed journal is what lets the next
//                  writer append the line it is owed (WriteRecovery).
//
// The audit log's own files are never journaled (WriteJournal.Suspend): they
// must not be rolled back with the vault change they record.
//
// What gets a line:
//   - the command returned, whatever its exit code (`exit=<n>`): it ran to
//     the end, and lint, for one, writes and returns non-zero by design;
//   - the command threw anything other than ValidationException /
//     StateConflictException (`error=<type>`): it may have written part of a
//     change before failing, and that must not go unrecorded.
//   A ValidationException or StateConflictException gets NO line: by the
//   codebase's validate-then-write rule (CONTRIBUTING.md) those are raised
//   before any write, so nothing changed.
//
// Ordering with the other steps: the vault lock (#46) must be OUTSIDE this
// step (earlier in MutationSteps), so the tail read, the command and the
// append all happen under one exclusive hold; write recovery (#47) sits
// between the lock and this step.
//
// Stateless: everything comes from the invocation, so parallel in-process
// App.Main calls are safe.
public static class AuditStep
{
    public static int Run(CommandInvocation invocation, Func<int> next)
    {
        var ctx = invocation.Context;
        AuditWriter? writer = null;
        string? openedRoot = null;
        try
        {
            // `init` (and any command run outside a vault) cannot be resolved
            // yet. Such a command fails on its own unless it creates the
            // vault, in which case it says so through ctx.Audit.VaultRoot.
            var vault = TryResolve(ctx);
            if (vault is not null)
            {
                using (WriteJournal.Suspend())
                    writer = AuditLog.For(vault).OpenForAppend();
                openedRoot = vault.Root;
            }

            int exit;
            try
            {
                exit = next();
            }
            catch (Exception ex) when (ex is not ValidationException and not StateConflictException and not SimulatedInterruption)
            {
                if (writer is not null)
                {
                    try
                    {
                        using (WriteJournal.Suspend())
                            writer.Append(Segments(writer, invocation, $"error={AuditLine.Escape(ex.GetType().Name)}"));
                    }
                    catch (Exception) { /* the original failure is the one to report */ }
                }
                throw;
            }

            var declared = ctx.Audit.VaultRoot is null ? null : Vault.At(ctx.Audit.VaultRoot);
            if (declared is not null && !SamePath(declared.Root, openedRoot))
            {
                // The command created or targeted a vault other than the one
                // resolved up front (`init <path>` while WIKI_VAULT names
                // another vault): the line belongs in the new one.
                writer?.Dispose();
                using (WriteJournal.Suspend())
                    writer = AuditLog.For(declared).OpenForAppend();
            }

            if (writer is null)
            {
                if (exit == 0)
                    throw new InvalidOperationException(
                        $"'{invocation.Name}' succeeded without a resolvable vault, so it cannot be audited");
                return exit;
            }

            using (WriteJournal.Suspend())
            {
                var segments = Segments(writer, invocation, "exit=" + exit.ToString(CultureInfo.InvariantCulture));
                // Sealed into the journal BEFORE the append (#47): from here
                // an interruption is finished by the next writer, line included.
                WriteJournal.Current?.Commit(segments, writer.Prev);
                writer.Append(segments);
            }
            return exit;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static Vault? TryResolve(CommandContext ctx)
    {
        try
        {
            return ctx.ResolveVault();
        }
        catch (ValidationException)
        {
            return null;
        }
    }

    private static bool SamePath(string a, string? b)
        => b is not null && string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The line's segments, before sealing. Stores any detail files it needs.
    private static List<string> Segments(AuditWriter writer, CommandInvocation invocation, string outcome)
    {
        var draft = invocation.Context.Audit;
        var segments = new List<string>
        {
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            AuditLine.Escape(draft.Action ?? invocation.Name.Replace(' ', '.')),
            AuditLine.Escape(draft.Subject ?? DefaultSubject(invocation)),
        };

        foreach (var f in draft.Fields)
        {
            var value = f.IsDetail || f.Value.Length > AuditLine.MaxInlineValue
                ? "@" + writer.StoreDetail(f.Value)
                : AuditLine.Escape(f.Value);
            segments.Add(f.Key + "=" + value);
        }

        segments.Add(outcome);
        segments.Add("via=" + invocation.Context.Origin.Via);
        return segments;
    }

    // The positional arguments as typed (`page rename <id> <slug>` ->
    // "<id>,<slug>"), or "-" for a command that takes none.
    private static string DefaultSubject(CommandInvocation invocation)
    {
        var values = invocation.ParseResult.CommandResult.Children
            .OfType<ArgumentResult>()
            .SelectMany(a => a.Tokens.Select(t => t.Value))
            .ToArray();
        return values.Length == 0 ? "-" : string.Join(",", values);
    }
}
