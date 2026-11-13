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
//   after next():  store detail files, then append and fsync the line. A
//                  failure here surfaces as the command's failure (exit 2).
//                  The change itself has been made by then; making that
//                  window recoverable is #47's journal, not this step.
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

            // `audit reanchor` (issue #45) is the one mutation that runs on a
            // broken chain: the command validates, then the live log is set
            // aside as an archived segment and its line starts the new one.
            if (vault is not null && invocation.Name == AuditLog.ReanchorCommand)
                return Reanchor(AuditLog.For(vault), invocation, next);

            if (vault is not null)
            {
                writer = AuditLog.For(vault).OpenForAppend();
                openedRoot = vault.Root;
            }

            int exit;
            try
            {
                exit = next();
            }
            catch (Exception ex) when (ex is not ValidationException and not StateConflictException)
            {
                if (writer is not null)
                {
                    try { Append(writer, invocation, $"error={AuditLine.Escape(ex.GetType().Name)}"); }
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
                writer = AuditLog.For(declared).OpenForAppend();
            }

            if (writer is null)
            {
                if (exit == 0)
                    throw new InvalidOperationException(
                        $"'{invocation.Name}' succeeded without a resolvable vault, so it cannot be audited");
                return exit;
            }

            Append(writer, invocation, "exit=" + exit.ToString(CultureInfo.InvariantCulture));
            return exit;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    // Nothing is opened before the command runs (OpenForAppend would refuse
    // the broken chain); the command refuses an intact chain and a missing
    // reason itself, and only once it has returned 0 is the live log archived
    // and the re-anchor line appended, still under the vault lock.
    private static int Reanchor(AuditLog log, CommandInvocation invocation, Func<int> next)
    {
        var exit = next();
        if (exit != 0)
            return exit;
        var writer = log.Reanchor();
        using (writer)
            Append(writer, invocation, "exit=0");
        return exit;
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

    private static void Append(AuditWriter writer, CommandInvocation invocation, string outcome)
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
        writer.Append(segments);
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
