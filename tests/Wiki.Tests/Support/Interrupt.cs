using System;
using Wiki.Core;

namespace Wiki.Tests.Support;

// Fault injection for write recovery (issue #47). Installs the internal
// WriteJournal.FaultHook for the calling test's flow only (it is an
// AsyncLocal, so tests running App.Main in parallel never see each other's
// faults) and removes it on Dispose.
//
// The fault points a mutation passes, in order:
//   "written"   - after each change to a vault file, while the command runs
//   "committed" - the command finished and its journal is sealed, before the
//                 audit line is appended
//   "appended"  - the audit line is durable, before the journal is removed
//
// At(): the process "dies" there. A SimulatedInterruption unwinds the stack
// without running any recovery or audit code, and the lock handle is released
// the way the OS releases it for a killed process. FailAt(): an ordinary
// exception is raised there instead, as a full disk would.
internal sealed class Interrupt : IDisposable
{
    private Interrupt(string point, int occurrence, Func<Exception> fault)
    {
        var seen = 0;
        WriteJournal.FaultHook.Value = p =>
        {
            if (p == point && ++seen == occurrence)
                throw fault();
        };
    }

    public static Interrupt At(string point, int occurrence = 1)
        => new(point, occurrence, () => new SimulatedInterruption(point));

    public static Interrupt FailAt(string point, Exception exception, int occurrence = 1)
        => new(point, occurrence, () => exception);

    public void Dispose() => WriteJournal.FaultHook.Value = null;
}
