using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Wiki.Core;

// The write journal of one mutating CLI step (issue #47, design in #36
// "Recoverable mutations"). Every vault file change goes through AtomicFile
// (Write, Delete) or LogFile.Append, and each of those asks the ambient
// journal to record the file's BEFORE-image first: the journal record is
// flushed to disk before the file is touched, so recovery information always
// exists before any file changes.
//
// Life of a journal, `.wiki/journal`:
//   1. pending:   begin + one before-image per file the command touches. The
//                 file is created at the first change, so a command that
//                 writes nothing (and every rejected command) leaves no trace.
//   2. committed: the command returned. Every touched file is flushed to
//                 disk, its after-hash recorded, and the command's audit line
//                 (segments plus the hash it chains from) sealed into the
//                 journal. AuditStep does this immediately before appending.
//   3. removed:   once the audit line is durable. Only then has the step
//                 succeeded.
//
// An interruption leaves the journal behind and the next writer recovers it
// (Wiki.Cli.WriteRecovery):
//   - pending:   the command never finished, so there is no end state to
//                finish; its changes are rolled back to the before-images
//                and the rollback itself is audited (`recover` line). Never
//                silently: the interrupted action is named on that line.
//   - committed: the change is complete and durable; the next writer appends
//                the owed audit line (unless it already made it to the log)
//                and removes the journal.
//   - anything it cannot decide (a touched file changed since, an audit log
//     that moved on without the line, a corrupt journal) refuses writes with
//     `repair-needed` instead of guessing.
//
// Per-flow, not per-process: the journal travels in an AsyncLocal, set by the
// mutation boundary for exactly one CLI step, so App.Main calls running in
// parallel in one process (the MCP server, the test suite) each journal only
// their own writes.
public sealed class WriteJournal
{
    private static readonly AsyncLocal<WriteJournal?> Ambient = new();

    // Test-only fault injection (issue #47). Nothing a client can reach sets
    // it: no argv flag, no environment variable, and it is internal, visible
    // only to the test assembly. Called with a point name ("written",
    // "committed", "appended"); a hook that throws simulates an interruption
    // there. AsyncLocal so a test's hook never fires in a parallel test.
    internal static readonly AsyncLocal<Action<string>?> FaultHook = new();

    internal static void Fault(string point) => FaultHook.Value?.Invoke(point);

    internal static WriteJournal? Current => Ambient.Value;

    private readonly string _path;
    private readonly string _action;
    private readonly Func<long> _nowUnixMs;
    private readonly object _gate = new();
    private readonly List<string> _touched = new();
    private readonly HashSet<string> _imaged = new(PathComparer);
    private readonly HashSet<string> _fullyImaged = new(PathComparer);
    private FileStream? _file;
    private bool _wrote;
    private bool _rollBackFailed;
    private int _suspended;

    public JournalPhase Phase { get; private set; } = JournalPhase.Pending;

    // The clock stamps the journal's `begin` record, which a rollback's
    // `recover` audit line carries as `started=` (CONTRIBUTING: "Inject the
    // clock and RNG").
    public WriteJournal(string path, string action, Func<long> nowUnixMs)
    {
        _path = path;
        _action = action;
        _nowUnixMs = nowUnixMs;
    }

    // How many distinct files this step has changed so far.
    public int TouchedFiles
    {
        get
        {
            lock (_gate)
                return _touched.Count;
        }
    }

    internal static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Makes `journal` the ambient journal of this flow until disposed.
    public static IDisposable Enter(WriteJournal journal)
    {
        var previous = Ambient.Value;
        Ambient.Value = journal;
        return new Restore(() => Ambient.Value = previous);
    }

    // Changes made inside are not journaled. For the audit log's own files
    // (lines, detail files, rotated segments): they have their own integrity
    // and must never be rolled back with the vault change they record. This
    // is the documented exemption from the journaled write rule (audit log,
    // journal and lock file are the recovery infrastructure itself; see
    // CONTRIBUTING.md, "Every vault write goes through ...").
    public static IDisposable Suspend()
    {
        var journal = Ambient.Value;
        if (journal is null)
            return new Restore(() => { });
        Interlocked.Increment(ref journal._suspended);
        return new Restore(() => Interlocked.Decrement(ref journal._suspended));
    }

    // Called by the write primitives immediately BEFORE they change `path`.
    // `append` means the change only adds bytes at the end, so the original
    // length is enough to undo it.
    internal static void BeforeChange(string path, bool append) => Current?.Record(Path.GetFullPath(path), append);

    // Called by the write primitives right after a change landed.
    internal static void AfterChange()
    {
        var journal = Current;
        if (journal is { Phase: JournalPhase.Pending, _suspended: 0 })
            Fault("written");
    }

    private void Record(string path, bool append)
    {
        lock (_gate)
        {
            if (Phase != JournalPhase.Pending || _suspended > 0)
                return;
            if (_fullyImaged.Contains(path) || (append && _imaged.Contains(path)))
                return;

            // Undo runs the records backwards, so a file needs a new record
            // only when nothing recorded so far restores it completely: an
            // append after any record is undone by that record; a rewrite
            // after a length-only record is not.
            JournalImage image;
            if (!File.Exists(path))
                image = JournalImage.Absent(path);
            else if (append)
                image = JournalImage.Length(path, new FileInfo(path).Length);
            else
                image = JournalImage.Content(path, File.ReadAllBytes(path));

            var text = image.Encode();
            if (_file is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // CreateNew: a journal left by an earlier step must have been
                // recovered before this step started; never overwrite one.
                _file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                _wrote = true;
                text = JournalFile.EncodeBegin(_action, DateTimeOffset.FromUnixTimeMilliseconds(_nowUnixMs())) + text;
            }
            JournalFile.AppendDurably(_file, text);

            if (_imaged.Add(path))
                _touched.Add(path);
            if (image.Kind != JournalImageKind.Length)
                _fullyImaged.Add(path);
        }
    }

    // The command returned: flush every touched file, record its after-hash,
    // and seal the audit line the step is about to append. From here on the
    // change is complete; an interruption is finished by the next writer.
    // Changes after this point (the audit append itself) are not journaled.
    internal void Commit(IReadOnlyList<string> auditSegments, string auditPrev)
    {
        lock (_gate)
        {
            if (Phase != JournalPhase.Pending)
                throw new InvalidOperationException("the write journal was already committed");
            if (_file is null)
            {
                // Nothing was written, so there is nothing to recover.
                Phase = JournalPhase.Committed;
                return;
            }

            var text = new System.Text.StringBuilder();
            foreach (var path in _touched)
                text.Append(JournalFile.EncodeAfter(path, JournalFile.FlushAndHash(path)));
            text.Append(JournalFile.EncodeSeal(JournalFile.CommitRecord, auditPrev, auditSegments));
            JournalFile.AppendDurably(_file, text.ToString());
            Phase = JournalPhase.Committed;
        }
        Fault("committed");
    }

    // The step finished and its audit line is durable: the journal is done.
    public void Complete()
    {
        lock (_gate)
        {
            Close();
            Phase = JournalPhase.Done;
            JournalFile.Remove(_path);
        }
    }

    // The command failed while the journal was pending (an IO error, a
    // command breaking validate-then-write): undo what it changed, at once,
    // under the same lock. True when the vault is back as it was (including
    // when nothing was written). If the undo itself fails it returns false,
    // the journal stays, and the next writer rolls back instead (and audits
    // that with a `recover` line); a second call then does not try again, so
    // the audit line that said `rollback-pending` stays true.
    public bool RollBack()
    {
        lock (_gate)
        {
            if (Phase == JournalPhase.Done)
                return true;
            if (Phase != JournalPhase.Pending || _rollBackFailed)
                return false;
            Close();
            if (!_wrote)
            {
                Phase = JournalPhase.Done;
                return true;
            }
            try
            {
                var journal = JournalFile.Read(_path);
                if (journal is { Phase: JournalPhase.Pending })
                {
                    JournalFile.Restore(journal.Images);
                    JournalFile.Remove(_path);
                    Phase = JournalPhase.Done;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next writer.
            }
            _rollBackFailed = true;
            return false;
        }
    }

    // Leaves the journal on disk as it is, for the next writer (a committed
    // step whose audit append failed, or a simulated crash).
    public void Abandon()
    {
        lock (_gate)
            Close();
    }

    private void Close()
    {
        _file?.Dispose();
        _file = null;
    }

    private sealed class Restore(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}

public enum JournalPhase
{
    // Recording before-images; the command has not finished.
    Pending,
    // A pending journal whose before-images have been put back.
    Restored,
    // The command finished; its audit line is sealed in the journal.
    Committed,
    // A rollback whose `recover` audit line is sealed in the journal.
    RolledBack,
    Done,
    // Unreadable records followed by readable ones: not a torn tail, so
    // nothing in it can be trusted.
    Corrupt,
}

// Raised only by the test fault hook: unwinds like a process that died at
// that point, so no recovery or audit code runs on the way out.
internal sealed class SimulatedInterruption(string point)
    : Exception($"simulated interruption at '{point}'")
{
}
