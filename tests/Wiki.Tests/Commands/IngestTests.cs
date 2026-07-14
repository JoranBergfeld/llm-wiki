using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 17: `wiki ingest status/advance/resume` - the CLI wiring around the
// ledger state machine (spec §10). Every precondition-rejection test asserts
// exit code + error code; the idempotent-readvance and out-of-order tests
// assert exit 3 / exit 1 respectively per the state-conflict-vs-blocking-
// validation split in spec §8's exit code table.
public class IngestTests
{
    private static string LedgerPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "ledger.json");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");

    // Registers a fresh vault + one source, in `registered` state. Returns
    // the vault and the new source's id (read out of `source add --json`'s
    // envelope data, mirroring how a real agent would chain these calls).
    private static (TempVault, string) Seeded()
    {
        var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var src = Path.Combine(tv.Path, "i.md");
        File.WriteAllText(src, "hello");
        var add = tv.Run("source", "add", src, "--category", "meeting-transcript", "--title", "M", "--json");
        var id = ((JsonElement)add.Envelope.Data!).GetProperty("id").GetString()!;
        return (tv, id);
    }

    private static string SummarizeSource(TempVault tv, string sourceId)
    {
        var r = tv.RunStdin("Summary body", "page", "upsert", "--type", "summary",
            "--title", "M summary", "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, r.ExitCode);
        return ((JsonElement)r.Envelope.Data!).GetProperty("id").GetString()!;
    }

    [Fact]
    public void Advance_ToSummarized_RequiresSummaryPage()
    {
        var (tv, id) = Seeded();
        var early = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(1, early.ExitCode);
        Assert.Contains(early.Envelope.Errors, e => e.Code == "precondition-summary");

        SummarizeSource(tv, id);

        var ok = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("\"state\":\"summarized\"", File.ReadAllText(LedgerPath(tv)));
        Assert.Contains("ingest-advance", File.ReadAllText(LogPath(tv)));
        tv.Dispose();
    }

    [Fact]
    public void Resume_ListsRemainingStates()
    {
        var (tv, id) = Seeded();
        var r = tv.Run("ingest", "resume", id, "--json");
        Assert.Equal(0, r.ExitCode);
        var data = (JsonElement)r.Envelope.Data!;
        Assert.Equal("registered", data.GetProperty("current").GetString());
        var remaining = data.GetProperty("remainingStates").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "summarized", "integrated", "linted" }, remaining);
        var artifacts = data.GetProperty("expectedArtifacts").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(3, artifacts.Length);
        Assert.All(artifacts, a => Assert.False(string.IsNullOrWhiteSpace(a)));
        tv.Dispose();
    }

    [Fact]
    public void Resume_UnknownSource_Rejected()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var r = tv.Run("ingest", "resume", "01JBOGUSSOURCEIDXXXXXXXXX", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-source");
    }

    [Fact]
    public void Advance_UnknownSource_Rejected()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var r = tv.Run("ingest", "advance", "01JBOGUSSOURCEIDXXXXXXXXX", "--to", "summarized", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-source");
    }

    [Fact]
    public void Advance_OutOfOrder_Rejected()
    {
        var (tv, id) = Seeded();
        // registered -> integrated skips summarized entirely.
        var r = tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-order");
        tv.Dispose();
    }

    [Fact]
    public void Advance_Reordered_Backwards_Rejected()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json").ExitCode);

        // Now at `integrated`; advancing "back" to `summarized` is not the
        // current state (idempotent case) and not the next state either -
        // out-of-order, exit 1.
        var r = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-order");
        tv.Dispose();
    }

    [Fact]
    public void Advance_Idempotent_ReadvanceToCurrentState_IsStateConflictNoOp()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);

        var again = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(3, again.ExitCode);
        Assert.Contains(again.Envelope.Errors, e => e.Code == "state-conflict");

        // Nothing changed underneath the no-op.
        var ledgerJson = File.ReadAllText(LedgerPath(tv));
        Assert.Contains("\"state\":\"summarized\"", ledgerJson);
        tv.Dispose();
    }

    [Fact]
    public void Advance_ToIntegrated_RecordsTouched()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);

        var r = tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "a,b,c", "--json");
        Assert.Equal(0, r.ExitCode);

        var ledgerJson = File.ReadAllText(LedgerPath(tv));
        Assert.Contains("\"touched\":[\"a\",\"b\",\"c\"]", ledgerJson);
        Assert.Contains("\"integratedAt\":", ledgerJson);
        tv.Dispose();
    }

    [Fact]
    public void Advance_ToLinted_WithoutLintRun_RejectedWithPreconditionLint()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json").ExitCode);

        // .wiki/lint.json doesn't exist yet (`wiki lint` is Task 22) - the
        // `linted` precondition must fail, documenting amendment D's wiring
        // ahead of Task 22 completing the loop.
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "lint.json")));

        var r = tv.Run("ingest", "advance", id, "--to", "linted", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-lint");
        tv.Dispose();
    }

    [Fact]
    public void Status_WithId_ReturnsThatEntry()
    {
        var (tv, id) = Seeded();
        var r = tv.Run("ingest", "status", id, "--json");
        Assert.Equal(0, r.ExitCode);
        var data = (JsonElement)r.Envelope.Data!;
        Assert.Equal(id, data.GetProperty("sourceId").GetString());
        Assert.Equal("registered", data.GetProperty("state").GetString());
        tv.Dispose();
    }

    [Fact]
    public void Status_WithoutId_ExcludesLintedOnly()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);

        var r = tv.Run("ingest", "status", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = (JsonElement)r.Envelope.Data!;
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal(id, data[0].GetProperty("sourceId").GetString());
        tv.Dispose();
    }

    [Fact]
    public void Status_UnknownSource_Rejected()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var r = tv.Run("ingest", "status", "01JBOGUSSOURCEIDXXXXXXXXX", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-source");
    }
}
