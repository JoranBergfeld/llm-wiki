using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #47: recovering from an interrupted write (design in #36,
// "Recoverable mutations" / "After an interruption"). Interruptions are
// injected through the test-only fault hook (Support/Interrupt.cs); every
// observation goes through the CLI seam and the files a vault keeps on disk.
public class WriteRecoveryTests
{
    private static TempVault NewVault()
    {
        var tv = new TempVault();
        Assert.Equal(0, tv.Run("init", tv.Path, "--name", "t", "--json").ExitCode);
        return tv;
    }

    private static CliResult Upsert(TempVault tv, string title, string body = "Body.")
        => tv.RunStdin(body, "page", "upsert", "--type", "entity", "--title", title, "--summary", "s", "--stdin", "--json");

    private static string JournalPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "journal");

    [Fact]
    public void InterruptionMidWrite_LeavesRecoveryInformationOnDisk()
    {
        using var tv = NewVault();

        using (Interrupt.At("written"))
            Assert.NotEqual(0, Upsert(tv, "Contoso").ExitCode);

        Assert.True(File.Exists(JournalPath(tv)), "no journal was left behind");
        Assert.NotEmpty(File.ReadAllBytes(JournalPath(tv)));
    }

    // The command finished writing but died before its audit line landed:
    // the next writer appends that line (with its original timestamp and
    // exit), keeps the change, and only then runs.
    [Fact]
    public void NextWriter_CompletesACommittedInterruption_AndItsAuditLine()
    {
        using var tv = NewVault();

        using (Interrupt.At("committed"))
            Upsert(tv, "Contoso");
        Assert.Equal(new[] { "init" }, Actions(tv));

        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);

        Assert.Equal(new[] { "init", "page.upsert", "category.add" }, Actions(tv));
        Assert.Contains("exit=0", Fields(AuditLines(tv)[1]));
        Assert.False(File.Exists(JournalPath(tv)));
        Assert.True(ChainIntact(tv));
        Assert.Equal(0, tv.Run("page", "show", "contoso", "--json").ExitCode);
    }

    // #44's fail-closed window: the command wrote, then the audit append
    // failed (a full disk). The step must not report success, and the change
    // must not stay unaudited: the next writer appends the owed line.
    [Fact]
    public void AuditAppendFailingAfterTheCommandWrote_IsNotASuccess_AndTheNextWriterAuditsIt()
    {
        using var tv = NewVault();

        CliResult failed;
        using (Interrupt.FailAt("committed", new IOException("disk full")))
            failed = Upsert(tv, "Contoso");

        Assert.Equal(2, failed.ExitCode);
        Assert.False(failed.Envelope.Ok);
        Assert.Equal(new[] { "init" }, Actions(tv));
        Assert.True(File.Exists(JournalPath(tv)));

        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);

        Assert.Equal(new[] { "init", "page.upsert", "category.add" }, Actions(tv));
        Assert.True(ChainIntact(tv));
        Assert.Equal(0, tv.Run("page", "show", "contoso", "--json").ExitCode);
    }

    // Died after the audit line was durable but before the journal was
    // removed: the next writer sees the line is already there and does not
    // append it twice.
    [Fact]
    public void InterruptionAfterTheAuditLine_IsNotAuditedTwice()
    {
        using var tv = NewVault();

        using (Interrupt.At("appended"))
            Upsert(tv, "Contoso");
        Assert.True(File.Exists(JournalPath(tv)));

        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);

        Assert.Equal(new[] { "init", "page.upsert", "category.add" }, Actions(tv));
        Assert.True(ChainIntact(tv));
        Assert.False(File.Exists(JournalPath(tv)));
    }

    // Died part way through its writes: there is no end state to finish, so
    // the next writer puts every touched file back and audits the rollback,
    // naming what was interrupted. Not silent, and no half change survives.
    [Fact]
    public void NextWriter_RollsBackAnInterruptionBeforeTheCommandFinished_AndAuditsIt()
    {
        using var tv = NewVault();
        Assert.Equal(0, Upsert(tv, "Fabrikam").ExitCode);
        var before = Snapshot(tv);

        using (Interrupt.At("written", occurrence: 2))
            Upsert(tv, "Contoso");
        Assert.NotEqual(before, Snapshot(tv));

        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);

        var after = Snapshot(tv);
        after.Remove("wiki.yaml");
        before.Remove("wiki.yaml");
        Assert.Equal(before, after);
        Assert.Equal(new[] { "init", "page.upsert", "recover", "category.add" }, Actions(tv));
        var recover = Fields(AuditLines(tv)[2]);
        Assert.Equal("page.upsert", recover[2]);
        Assert.Contains("outcome=rolled-back", recover);
        Assert.True(ChainIntact(tv));
        Assert.False(File.Exists(JournalPath(tv)));
    }

    // A rename is a write plus a delete; interrupted between them, both are
    // undone, so neither a duplicate nor a lost page is left behind.
    [Fact]
    public void InterruptedRename_IsRolledBack()
    {
        using var tv = NewVault();
        var id = ((System.Text.Json.JsonElement)Upsert(tv, "Contoso").Envelope.Data!).GetProperty("id").GetString()!;
        var before = Snapshot(tv);

        using (Interrupt.At("written"))
            tv.Run("page", "rename", id, "acme", "--json");

        Assert.Equal(0, tv.Run("reindex", "--json").ExitCode);
        Assert.Equal(0, tv.Run("page", "show", "contoso", "--json").ExitCode);
        Assert.Equal(1, tv.Run("page", "show", "acme", "--json").ExitCode);
        Assert.Equal(before["wiki/entities/contoso.md"], Snapshot(tv)["wiki/entities/contoso.md"]);
    }

    // Readers must not see a half change. The first reader after the
    // interruption puts the before-images back itself (it writes no audit
    // line; the next writer audits the rollback).
    [Fact]
    public void ReadAfterAnInterruptedWrite_SeesTheStateBeforeIt()
    {
        using var tv = NewVault();
        Assert.Equal(0, Upsert(tv, "Fabrikam").ExitCode);
        var before = Snapshot(tv);

        using (Interrupt.At("written"))
            Upsert(tv, "Contoso");
        Assert.True(File.Exists(Path.Combine(tv.Path, "wiki", "entities", "contoso.md")), "the interruption came too late to leave a half change");

        Assert.Equal(1, tv.Run("page", "show", "contoso", "--json").ExitCode);
        Assert.Equal(before, Snapshot(tv));
        Assert.Equal(new[] { "init", "page.upsert" }, Actions(tv));

        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);
        Assert.Equal(new[] { "init", "page.upsert", "recover", "category.add" }, Actions(tv));
    }

    // A command that fails part way (an IO error, not a crash) is undone at
    // once by the step that ran it: nothing is left for the next writer.
    [Fact]
    public void CommandFailingMidWrite_IsRolledBackAtOnce()
    {
        using var tv = NewVault();
        var before = Snapshot(tv);

        CliResult r;
        using (Interrupt.FailAt("written", new IOException("disk full")))
            r = Upsert(tv, "Contoso");

        Assert.Equal(2, r.ExitCode);
        Assert.False(File.Exists(JournalPath(tv)));
        Assert.Equal(before, Snapshot(tv));
    }

    // Ambiguous: the audit log is shared with a second vault (outside the
    // guarantee, #36), which appended to it while the interrupted line was
    // still owed. Recovery cannot place the line, so every write is refused
    // with repair-needed; reads still return the (complete) change.
    [Fact]
    public void AmbiguousInterruption_RefusesWrites_WithRepairNeeded_ButReadsStillWork()
    {
        using var tv = NewVault();
        using (Interrupt.At("committed"))
            Upsert(tv, "Contoso");

        using var other = NewVault();
        File.AppendAllText(Path.Combine(other.Path, "wiki.yaml"), $"audit:\n  path: \"{AuditLog(tv).Replace('\\', '/')}\"\n");
        Assert.Equal(0, other.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refused = tv.Run("category", "add", "memo", "--description", "Memos", "--json");
            Assert.Equal(2, refused.ExitCode);
            Assert.Equal("repair-needed", Assert.Single(refused.Envelope.Errors!).Code);
        }

        var read = tv.Run("page", "show", "contoso", "--json");
        Assert.Equal(0, read.ExitCode);
        Assert.True(File.Exists(JournalPath(tv)));
    }

    // Vault content as recovery sees it: everything but the audit log (which
    // only ever grows) and the lock and journal themselves.
    private static Dictionary<string, string> Snapshot(TempVault tv)
        => Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(tv.Path, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("audit/") && f != ".wiki/journal" && f != ".wiki/lock")
            .ToDictionary(f => f, f => System.Convert.ToBase64String(File.ReadAllBytes(Path.Combine(tv.Path, f))));

    private static string AuditLog(TempVault tv) => Path.Combine(tv.Path, "audit", "audit.log");

    private static string[] AuditLines(TempVault tv)
        => File.ReadAllText(AuditLog(tv)).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

    private static string[] Fields(string line) => line.Split(" | ");

    private static string[] Actions(TempVault tv) => AuditLines(tv).Select(l => Fields(l)[1]).ToArray();

    private static bool ChainIntact(TempVault tv)
    {
        var r = tv.Run("audit", "--json");
        Assert.Equal(0, r.ExitCode);
        return ((System.Text.Json.JsonElement)r.Envelope.Data!).GetProperty("chain").GetProperty("intact").GetBoolean();
    }
}
