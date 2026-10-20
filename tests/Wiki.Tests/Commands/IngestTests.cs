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

    // --- Amendment Z: unwitnessed integration (issue #23) ------------------

    // Creates an `entity` page citing `sourceId` - the structural evidence
    // ReindexService.RebuildLedger reads to prove a source reached
    // `integrated`. A summary page alone only proves `summarized`, so without
    // this a reindexed entry lands back at `summarized` and never exercises
    // the null-`integratedAt` case at all.
    private static void IntegrateSource(TempVault tv, string sourceId)
    {
        var r = tv.RunStdin("Entity body", "page", "upsert", "--type", "entity",
            "--title", "M entity", "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, r.ExitCode);
    }

    // Drives issue #23's repro up to the wedge: a source taken to
    // `integrated` through the real flow, then `.wiki/` deleted and rebuilt by
    // `wiki reindex` - the documented recovery path. Reindex re-derives
    // `state: integrated` from the entity page's citation but leaves
    // `integratedAt` null rather than fabricate a transition it never
    // witnessed (amendment A), so the returned vault holds exactly the entry
    // that used to be permanently unadvanceable.
    private static (TempVault, string) ReindexDerivedIntegrated()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);
        IntegrateSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json").ExitCode);

        Directory.Delete(Path.Combine(tv.Path, ".wiki"), recursive: true);
        Assert.Equal(0, tv.Run("reindex", "--json").ExitCode);

        // Precondition of every test below: back at `integrated`, with no
        // `integratedAt` (absent from the envelope, or present as null).
        var data = (JsonElement)tv.Run("ingest", "status", id, "--json").Envelope.Data!;
        Assert.Equal("integrated", data.GetProperty("state").GetString());
        Assert.True(!data.TryGetProperty("integratedAt", out var integratedAt)
            || integratedAt.ValueKind == JsonValueKind.Null);
        return (tv, id);
    }

    // The wedge itself. `advance --to linted` on a reindex-derived
    // `integrated` entry used to fail `precondition-lint` however many times
    // lint was re-run, because a null `integratedAt` was read as "no lint ran
    // after integration" - a comparison with nothing on the other side of it.
    // A recorded lint run now satisfies the precondition.
    [Fact]
    public void Advance_ToLinted_UnwitnessedIntegration_AcceptedAfterLint()
    {
        var (tv, id) = ReindexDerivedIntegrated();

        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);

        var linted = tv.Run("ingest", "advance", id, "--to", "linted", "--json");
        Assert.Equal(0, linted.ExitCode);
        Assert.Contains("\"state\":\"linted\"", File.ReadAllText(LedgerPath(tv)));

        // The entry is finished, so it drops out of the work queue
        // `ingest status` (no args) reports - amendment G. This is the half of
        // the bug that cost real money: a wedged row kept the queue non-empty
        // forever, waking a scheduled agent every hour indefinitely.
        var queue = (JsonElement)tv.Run("ingest", "status", "--json").Envelope.Data!;
        Assert.DoesNotContain(queue.EnumerateArray(),
            e => e.GetProperty("sourceId").GetString() == id);
        tv.Dispose();
    }

    // Tolerating the null must not tolerate a vault that has never linted:
    // the "a lint actually ran" half of the precondition is unconditional.
    [Fact]
    public void Advance_ToLinted_UnwitnessedIntegration_StillRequiresALintRun()
    {
        var (tv, id) = ReindexDerivedIntegrated();

        // Deleting `.wiki/` took lint.json with it, and reindex does not
        // recreate it - the last-lint timestamp is history, not structure
        // (amendment A).
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "lint.json")));

        var snapshot = LedgerSnapshot(tv);
        var r = tv.Run("ingest", "advance", id, "--to", "linted", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-lint");
        Assert.Equal(snapshot, LedgerSnapshot(tv));
        tv.Dispose();
    }

    // The ordering claim is dropped ONLY where there is no timestamp to order
    // against. A WITNESSED integration whose newest lint predates it is still
    // rejected - that lint ran against a body the integration then changed,
    // so it proves nothing (amendment J's "strictly older" reject).
    [Fact]
    public void Advance_ToLinted_WitnessedIntegration_StaleLint_StillRejected()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json").ExitCode);

        var lastRun = System.DateTimeOffset.UtcNow.AddDays(-1)
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(tv.Path, ".wiki", "lint.json"), $"{{\"lastRun\":\"{lastRun}\"}}");

        var snapshot = LedgerSnapshot(tv);
        var r = tv.Run("ingest", "advance", id, "--to", "linted", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "precondition-lint");
        Assert.Equal(snapshot, LedgerSnapshot(tv));
        tv.Dispose();
    }

    // `ingest resume` is what an agent reads after losing context, so it must
    // not name the unsatisfiable goal either - "newer than this source's
    // 'integrated' timestamp" is the instruction that had agents re-running
    // lint forever against an entry that has no such timestamp.
    [Fact]
    public void Resume_UnwitnessedIntegration_DescribesALintRunWithoutOrdering()
    {
        var (tv, id) = ReindexDerivedIntegrated();

        var data = (JsonElement)tv.Run("ingest", "resume", id, "--json").Envelope.Data!;
        var artifacts = data.GetProperty("expectedArtifacts").EnumerateArray()
            .Select(a => a.GetString()!).ToList();
        var lintArtifact = Assert.Single(artifacts);
        Assert.Contains("reconstructed by reindex", lintArtifact);
        // The ordering INSTRUCTION must be gone. Matching the full phrase, not
        // a bare "newer than" - the replacement text uses those words itself,
        // in the clause explaining that there is nothing to be newer than.
        Assert.DoesNotContain("newer than this source's", lintArtifact);
        tv.Dispose();
    }

    // The ordinary case keeps the ordering wording - a witnessed integration
    // really does have a timestamp the lint must be newer than.
    [Fact]
    public void Resume_WitnessedIntegration_KeepsOrderingWording()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json").ExitCode);

        var data = (JsonElement)tv.Run("ingest", "resume", id, "--json").Envelope.Data!;
        var lintArtifact = Assert.Single(data.GetProperty("expectedArtifacts").EnumerateArray()
            .Select(a => a.GetString()!));
        Assert.Contains("newer than this source's", lintArtifact);
        tv.Dispose();
    }

    // A source not integrated YET also has a null `integratedAt`, but its
    // transition is still ahead of it and will stamp one - so it must get the
    // ordinary ordering wording, not the unwitnessed-integration wording.
    [Fact]
    public void Resume_NotYetIntegrated_KeepsOrderingWording()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);

        var data = (JsonElement)tv.Run("ingest", "resume", id, "--json").Envelope.Data!;
        var artifacts = data.GetProperty("expectedArtifacts").EnumerateArray()
            .Select(a => a.GetString()!).ToList();
        Assert.Equal(2, artifacts.Count);
        Assert.Contains("newer than this source's", artifacts[^1]);
        Assert.DoesNotContain("reconstructed by reindex", artifacts[^1]);
        tv.Dispose();
    }

    // A non-null but UNPARSEABLE `integratedAt` is corrupt state, not absent
    // history, so it stays rejected rather than riding the null tolerance in.
    [Fact]
    public void Advance_ToLinted_UnparseableIntegratedAt_Rejected()
    {
        var (tv, id) = Seeded();
        SummarizeSource(tv, id);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "summarized", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", id, "--to", "integrated", "--touched", "", "--json").ExitCode);

        var status = (JsonElement)tv.Run("ingest", "status", id, "--json").Envelope.Data!;
        var integratedAt = status.GetProperty("integratedAt").GetString()!;

        // Corrupt the timestamp in place - the one thing no CLI path writes,
        // which is why it has to be simulated by hand here.
        var ledgerPath = LedgerPath(tv);
        File.WriteAllText(ledgerPath, File.ReadAllText(ledgerPath).Replace(integratedAt, "not-a-date"));

        var lastRun = System.DateTimeOffset.UtcNow.AddDays(1)
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(tv.Path, ".wiki", "lint.json"), $"{{\"lastRun\":\"{lastRun}\"}}");

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
