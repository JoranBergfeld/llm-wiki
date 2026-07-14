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
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");

    // Snapshot ledger.json so a rejected advance can be asserted to have
    // landed *nothing* - the "blocking validation, nothing lands" invariant.
    // Returns "" if the file doesn't exist yet, matching File.ReadAllText's
    // "unchanged" semantics for a never-created file.
    private static string LedgerSnapshot(TempVault tv)
        => File.Exists(LedgerPath(tv)) ? File.ReadAllText(LedgerPath(tv)) : "";

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
        var snapshot = LedgerSnapshot(tv);
        var early = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(1, early.ExitCode);
        Assert.Contains(early.Envelope.Errors, e => e.Code == "precondition-summary");
        Assert.Equal(snapshot, LedgerSnapshot(tv));

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
        var snapshot = LedgerSnapshot(tv);
        // registered -> integrated skips summarized entirely.
        var r = tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-order");
        Assert.Equal(snapshot, LedgerSnapshot(tv));
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
        var snapshot = LedgerSnapshot(tv);
        var r = tv.Run("ingest", "advance", id, "--to", "summarized", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-order");
        Assert.Equal(snapshot, LedgerSnapshot(tv));
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

    // Regression test for the Critical fix: Ledger.Advance used to clobber
    // `Touched` on every transition (it was only ever set from the caller's
    // `touched` argument, which is `[]` for every `--to` except `integrated`
    // since `--touched` isn't passed on the `linted` advance). That wiped
    // the `integrated` audit trail the moment a source moved to `linted`.
    // The fix carries `existing.Touched` forward on any transition that
    // isn't itself `--to integrated`. This asserts the audit list set at
    // `integrated` is still there after the later `linted` advance.
    [Fact]
    public void Advance_ToLinted_PreservesTouchedFromIntegrated()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);

        var integrate = tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "a,b,c", "--json");
        Assert.Equal(0, integrate.ExitCode);
        Assert.Contains("\"touched\":[\"a\",\"b\",\"c\"]", File.ReadAllText(LedgerPath(tv)));

        // Satisfy the `linted` precondition: a lint run recorded strictly
        // after this entry's `integratedAt` timestamp, in `.wiki/lint.json`
        // (`LintStateData.LastRun` -> wire field `lastRun`, camelCase per
        // WikiJsonContext). A day in the future is safely newer than
        // whatever `integratedAt` the real clock just stamped.
        var lintPath = Path.Combine(tv.Path, ".wiki", "lint.json");
        var lastRun = System.DateTimeOffset.UtcNow.AddDays(1)
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(lintPath, $"{{\"lastRun\":\"{lastRun}\"}}");

        // No `--touched` on this advance - exactly the case that used to
        // wipe the list.
        var linted = tv.Run("ingest", "advance", id, "--to", "linted", "--json");
        Assert.Equal(0, linted.ExitCode);
        Assert.Contains("\"touched\":[\"a\",\"b\",\"c\"]", File.ReadAllText(LedgerPath(tv)));
        tv.Dispose();
    }

    // Amendment J: a lint run in the SAME wall-clock second as the
    // `integrated` transition satisfies the `linted` precondition. Both
    // timestamps are second-granularity, and the canonical flow (spec §10
    // step 5) integrates then immediately lints, so a same-second lint really
    // did run after integration - the precondition accepts `lastRun >=
    // integratedAt`, not strictly `>`. This is the production-code fix that
    // replaced the E2E test's old Thread.Sleep workaround.
    [Fact]
    public void Advance_ToLinted_SameSecondLint_Accepted()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json").ExitCode);

        // Read back the exact `integratedAt` the integrate advance stamped,
        // then write lint.json's `lastRun` to the SAME value - simulating a
        // lint that landed in the same second as the integration.
        var status = tv.Run("ingest", "status", id, "--json");
        var integratedAt = ((JsonElement)status.Envelope.Data!).GetProperty("integratedAt").GetString()!;
        var lintPath = Path.Combine(tv.Path, ".wiki", "lint.json");
        File.WriteAllText(lintPath, $"{{\"lastRun\":\"{integratedAt}\"}}");

        var linted = tv.Run("ingest", "advance", id, "--to", "linted", "--json");
        Assert.Equal(0, linted.ExitCode);
        Assert.Contains("\"state\":\"linted\"", File.ReadAllText(LedgerPath(tv)));
        tv.Dispose();
    }

    [Fact]
    public void Advance_ToIntegrated_IndexDrift_RejectedWithPreconditionIndex()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);

        // Simulate something writing wiki/index.md outside the CLI - the
        // one thing spec §9 says must never happen, and exactly the drift
        // `precondition-index` exists to catch.
        File.WriteAllText(IndexPath(tv), "drifted\n");

        var snapshot = LedgerSnapshot(tv);
        var r = tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-index");
        Assert.Equal(snapshot, LedgerSnapshot(tv));
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

        var snapshot = LedgerSnapshot(tv);
        var r = tv.Run("ingest", "advance", id, "--to", "linted", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-lint");
        Assert.Equal(snapshot, LedgerSnapshot(tv));
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
