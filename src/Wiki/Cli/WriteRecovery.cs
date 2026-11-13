using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Wiki.Audit;
using Wiki.Core;

namespace Wiki.Cli;

// Write recovery (issue #47, design in #36 "Recoverable mutations" and "After
// an interruption"). Three boundary steps around the write journal
// (Wiki.Core.WriteJournal, which documents the journal's life):
//
//   Step       mutation boundary, between VaultLock and AuditStep. With the
//              exclusive lock held: recover any interrupted write, then run
//              the command with a fresh journal, and remove the journal once
//              the audit line is durable. Success is reported only then.
//   BeforeRead read boundary, BEFORE VaultLock. A reader must not see a half
//              change, so if the journal says files may be half changed it
//              takes the lock exclusively for a moment and puts the
//              before-images back (it writes no audit line: the next writer
//              does). It cannot do this under the shared lock it is about to
//              take, and a live writer's journal is never touched: waiting
//              for the exclusive lock waits that writer out.
//   ReadGuard  read boundary, AFTER VaultLock. Under the shared lock no
//              writer is alive, so a dirty journal here is one whose writer
//              died between BeforeRead and the lock; refuse with the
//              retryable vault-busy rather than read through it.
//
// Recovery decisions, for the journal the dead writer left:
//   pending    -> roll back to the before-images, audit a `recover` line
//                 (outcome=rolled-back) naming the interrupted action.
//   committed  -> the change is complete: verify every touched file still
//                 has its after-hash, then append the owed audit line unless
//                 the log already ends with it.
//   anything else -> repair-needed; writes are refused, reads go on.
//
// Stateless like every boundary step: all of it comes from the invocation
// and the files on disk.
public static class WriteRecovery
{
    public static int Step(CommandInvocation invocation, Func<int> next) => Step(invocation, next, Timestamp.NowUnixMs);

    // The clock stamps a rollback's `recover` audit line (CONTRIBUTING:
    // "Inject the clock and RNG").
    public static int Step(CommandInvocation invocation, Func<int> next, Func<long> nowUnixMs)
    {
        var vault = TryResolve(invocation.Context);
        if (vault is null)
            return next();   // `init`: no vault, nothing journaled

        Recover(vault, invocation.Context.Origin, nowUnixMs);

        var journal = new WriteJournal(vault.JournalPath, invocation.Name, nowUnixMs);
        int exit;
        using (WriteJournal.Enter(journal))
        {
            try
            {
                exit = next();
                WriteJournal.Fault("appended");
            }
            catch (SimulatedInterruption)
            {
                // A "dead" process runs no cleanup: the journal stays as is.
                journal.Abandon();
                throw;
            }
            catch (Exception)
            {
                if (journal.Phase == JournalPhase.Pending)
                    journal.RollBack();   // the command failed half way: undo now (AuditStep usually already has)
                else
                    journal.Abandon();    // committed, but the audit append failed: the next writer appends it
                throw;
            }
        }
        journal.Complete();
        return exit;
    }

    public static int BeforeRead(CommandInvocation invocation, Func<int> next)
    {
        var vault = TryResolve(invocation.Context);
        if (vault is not null && JournalFile.Read(vault.JournalPath) is { IsDirty: true })
        {
            using (VaultLock.AcquireExclusive(invocation, vault))
            {
                var journal = JournalFile.Read(vault.JournalPath);
                if (journal is { IsDirty: true })
                    RestoreBeforeImages(vault, journal);
            }
        }
        return next();
    }

    public static int ReadGuard(CommandInvocation invocation, Func<int> next)
    {
        var vault = TryResolve(invocation.Context);
        if (vault is not null && JournalFile.Read(vault.JournalPath) is { IsDirty: true })
            throw new VaultBusyException(
                "an interrupted write is being recovered and the vault is mid-change; nothing was read, retry the command",
                vault.JournalPath);
        return next();
    }

    // Brings the vault back to a state with no interrupted write, or throws
    // repair-needed. Caller holds the exclusive lock.
    private static void Recover(Vault vault, InvocationOrigin origin, Func<long> nowUnixMs)
    {
        var journal = JournalFile.Read(vault.JournalPath);
        if (journal is null)
            return;
        if (journal.IsEmpty)
        {
            JournalFile.Remove(vault.JournalPath);
            return;
        }

        if (journal.Phase is JournalPhase.Pending or JournalPhase.Corrupt)
        {
            RestoreBeforeImages(vault, journal);
            journal = JournalFile.Read(vault.JournalPath)!;
        }

        if (journal.Phase == JournalPhase.Restored)
        {
            using var writer = AuditLog.For(vault, nowUnixMs).OpenForAppend();
            var segments = RecoverSegments(journal, origin, nowUnixMs);
            JournalFile.AppendDurably(vault.JournalPath,
                JournalFile.EncodeSeal(JournalFile.RollbackRecord, writer.Prev, segments));
            writer.Append(segments);
            JournalFile.Remove(vault.JournalPath);
            return;
        }

        // Committed or RolledBack: the vault side is settled; what is owed is
        // the sealed audit line.
        var seal = journal.Seal!;
        if (journal.Phase == JournalPhase.Committed)
        {
            foreach (var (path, hash) in journal.After)
            {
                if (JournalFile.HashOf(path) != hash)
                    throw RepairNeeded(vault, journal,
                        $"'{path}' changed after the interrupted '{journal.Action}' finished writing it, so it is no longer the state that write was going to record");
            }
        }

        using (var writer = AuditLog.For(vault, nowUnixMs).OpenForAppend())
        {
            if (writer.Prev == seal.Prev)
                writer.Append(seal.Segments);
            else if (AuditLine.Seal(seal.Segments, seal.Prev).Hash != writer.Prev)
                throw RepairNeeded(vault, journal,
                    $"the audit log '{writer.LogPath}' gained other lines after the interrupted '{journal.Action}', so its audit line can no longer be chained where it belongs (is the log shared with another vault?)");
        }
        JournalFile.Remove(vault.JournalPath);
    }

    private static void RestoreBeforeImages(Vault vault, ParsedJournal journal)
    {
        if (journal.Phase == JournalPhase.Corrupt)
            throw RepairNeeded(vault, journal,
                "the write journal is damaged (an unreadable record is followed by readable ones), so what the interrupted write changed cannot be trusted");
        JournalFile.Restore(journal.Images);
        JournalFile.AppendDurably(vault.JournalPath, JournalFile.EncodeRestored());
    }

    private static RepairNeededException RepairNeeded(Vault vault, ParsedJournal journal, string why)
        => new($"an interrupted '{journal.Action}' (started {journal.Started}) cannot be recovered automatically: {why}. " +
               $"Writes are refused until it is repaired; reads still work. Inspect the journal at '{vault.JournalPath}', " +
               "put the vault and audit log right by hand, then delete the journal to resume writing",
            vault.JournalPath);

    // The audit line recording a rollback. It names the interrupted action
    // and when it started, so a rolled-back change is never a silent one.
    private static IReadOnlyList<string> RecoverSegments(ParsedJournal journal, InvocationOrigin origin, Func<long> nowUnixMs) =>
    [
        Timestamp.Iso(nowUnixMs()),
        "recover",
        AuditLine.Escape(journal.Action.Replace(' ', '.')),
        "outcome=rolled-back",
        "started=" + AuditLine.Escape(journal.Started),
        "files=" + new HashSet<string>(journal.Images.ConvertAll(i => i.Path), WriteJournal.PathComparer).Count.ToString(CultureInfo.InvariantCulture),
        "exit=0",
        "via=" + origin.Via,
    ];

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
}
