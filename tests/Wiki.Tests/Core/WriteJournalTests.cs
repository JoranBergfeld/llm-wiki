using System.IO;
using Wiki.Core;
using Xunit;

namespace Wiki.Tests.Core;

// The write journal on its own (issue #47). End-to-end recovery is covered in
// Commands/WriteRecoveryTests; these pin what the journal itself records.
public class WriteJournalTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wiki-journal-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // The `begin` record's time comes from the injected clock, not the wall
    // clock: it is what a rollback's `recover` audit line reports as started=.
    [Fact]
    public void BeginRecord_IsStampedByTheInjectedClock()
    {
        var dir = NewDir();
        try
        {
            var journalPath = Path.Combine(dir, "journal");
            var page = Path.Combine(dir, "page.md");
            File.WriteAllText(page, "before");
            const long pinned = 946_684_800_000;   // 2000-01-01T00:00:00Z

            var journal = new WriteJournal(journalPath, "page upsert", () => pinned);
            using (WriteJournal.Enter(journal))
                WriteJournal.BeforeChange(page, append: false);
            journal.Abandon();

            Assert.Equal("2000-01-01T00:00:00Z", JournalFile.Read(journalPath)!.Started);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RollBack_WithNothingWritten_SucceedsAndLeavesNoJournal()
    {
        var dir = NewDir();
        try
        {
            var journalPath = Path.Combine(dir, "journal");
            var journal = new WriteJournal(journalPath, "page upsert", Timestamp.NowUnixMs);

            Assert.Equal(0, journal.TouchedFiles);
            Assert.True(journal.RollBack());
            Assert.Equal(JournalPhase.Done, journal.Phase);
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
