using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.E2E;

// Task 27 (M4, final): the full-lifecycle integration proof spec §16 calls
// for - "an end-to-end test that scripts the full lifecycle (init -> add ->
// ingest all states -> lint -> retract -> repair) against a temp vault" -
// plus the reindex property test that extends Task 15's idmap byte-identity
// to *structural* ledger state (amendment A: §3, §16, Appendix B.A). Every
// prior task's command surface gets exercised at least once here, chained
// exactly the way a real agent session would chain it (ids/slugs pulled out
// of each command's own `--json` envelope, never hand-constructed).
public class LifecycleTests
{
    // Same "pull `data` out of the last JSON line" helper every other
    // Commands test file uses (Envelope.Data is `object?` and doesn't
    // round-trip into a typed DTO through TempVault's generic deserializer).
    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public void FullLifecycle_InitThroughRetract_EveryStepExitsCleanAndLandsExpectedState()
    {
        using var tv = new TempVault();

        // 1. init
        var init = tv.Run("init", tv.Path, "--name", "Lifecycle vault", "--json");
        Assert.Equal(0, init.ExitCode);

        // 2. source add -> registered
        var srcFile = Path.Combine(tv.Path, "meeting.md");
        File.WriteAllText(srcFile, "# Meeting transcript\nContoso wants a security addendum on the MSA.");
        var addSrc = tv.Run("source", "add", srcFile, "--category", "meeting-transcript",
            "--title", "Contoso security call", "--json");
        Assert.Equal(0, addSrc.ExitCode);
        var sourceId = Data(addSrc).GetProperty("id").GetString()!;

        var statusRegistered = tv.Run("ingest", "status", sourceId, "--json");
        Assert.Equal(0, statusRegistered.ExitCode);
        Assert.Equal("registered", Data(statusRegistered).GetProperty("state").GetString());

        // 3. page upsert --type summary --sources <id>, then advance -> summarized
        var summary = tv.RunStdin("Contoso asked for a security addendum on the MSA.",
            "page", "upsert", "--type", "summary", "--title", "Contoso security call summary",
            "--summary", "Security addendum request", "--sources", sourceId, "--json");
        Assert.Equal(0, summary.ExitCode);
        var summarySlug = Data(summary).GetProperty("slug").GetString()!;

        var advanceSummarized = tv.Run("ingest", "advance", sourceId, "--to", "summarized", "--json");
        Assert.Equal(0, advanceSummarized.ExitCode);

        var statusSummarized = tv.Run("ingest", "status", sourceId, "--json");
        Assert.Equal(0, statusSummarized.ExitCode);
        Assert.Equal("summarized", Data(statusSummarized).GetProperty("state").GetString());

        // 4. entity + concept upserts citing the source, then advance ->
        // integrated (--touched records both for audit).
        var entity = tv.RunStdin("Contoso is a customer negotiating a security addendum.",
            "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "Customer account", "--sources", sourceId, "--json");
        Assert.Equal(0, entity.ExitCode);
        var entityId = Data(entity).GetProperty("id").GetString()!;
        var entitySlug = Data(entity).GetProperty("slug").GetString()!;

        var concept = tv.RunStdin("A security addendum extends the MSA with additional security terms.",
            "page", "upsert", "--type", "concept", "--title", "Security addendum",
            "--summary", "Contract concept", "--sources", sourceId, "--json");
        Assert.Equal(0, concept.ExitCode);
        var conceptId = Data(concept).GetProperty("id").GetString()!;
        var conceptSlug = Data(concept).GetProperty("slug").GetString()!;

        var advanceIntegrated = tv.Run("ingest", "advance", sourceId, "--to", "integrated",
            "--touched", $"{entityId},{conceptId}", "--json");
        Assert.Equal(0, advanceIntegrated.ExitCode);

        var statusIntegrated = tv.Run("ingest", "status", sourceId, "--json");
        Assert.Equal(0, statusIntegrated.ExitCode);
        Assert.Equal("integrated", Data(statusIntegrated).GetProperty("state").GetString());

        // 5. wiki lint, THEN advance -> linted. The `linted` precondition
        // (spec §10, amendments D + J) requires .wiki/lint.json's `lastRun`
        // at-or-after this entry's `integratedAt` (`>=`, amendment J). Both
        // are second-precision ISO timestamps off the real wall clock, so a
        // lint landing in the SAME wall-clock second as the integrated
        // advance above - exactly what integrate-then-lint back-to-back
        // does - is ACCEPTED, not rejected. No sleep needed: the canonical
        // flow advances to `linted` cleanly on a single wall-clock second.
        var lint = tv.Run("lint", "--json");
        Assert.Equal(0, lint.ExitCode);

        var advanceLinted = tv.Run("ingest", "advance", sourceId, "--to", "linted", "--json");
        Assert.Equal(0, advanceLinted.ExitCode);

        var statusLinted = tv.Run("ingest", "status", sourceId, "--json");
        Assert.Equal(0, statusLinted.ExitCode);
        Assert.Equal("linted", Data(statusLinted).GetProperty("state").GetString());

        // 6. source retract -> cascade (spec §14, amendment I): summary page
        // -> archived (no issue); every OTHER citing, non-archived page
        // (entity + concept here) -> needs-review + a filed retraction issue.
        var retract = tv.Run("source", "retract", sourceId, "--reason",
            "customer withdrew the security addendum request", "--json");
        Assert.Equal(0, retract.ExitCode);
        var retractData = Data(retract);
        Assert.Equal("retracted", retractData.GetProperty("status").GetString());
        Assert.Equal(1, retractData.GetProperty("archivedSummaries").GetArrayLength());
        Assert.Equal(summarySlug, retractData.GetProperty("archivedSummaries")[0].GetString());
        var affected = retractData.GetProperty("affectedPages").EnumerateArray()
            .Select(x => x.GetString()!).ToArray();
        Assert.Equal(2, affected.Length);
        Assert.Contains(entitySlug, affected);
        Assert.Contains(conceptSlug, affected);

        var sourceShow = tv.Run("source", "show", sourceId, "--json");
        Assert.Equal(0, sourceShow.ExitCode);
        Assert.Equal("retracted", Data(sourceShow).GetProperty("status").GetString());

        var summaryShow = tv.Run("page", "show", summarySlug, "--json");
        Assert.Equal(0, summaryShow.ExitCode);
        Assert.Equal("archived", Data(summaryShow).GetProperty("status").GetString());

        var entityShow = tv.Run("page", "show", entitySlug, "--json");
        Assert.Equal(0, entityShow.ExitCode);
        Assert.Equal("needs-review", Data(entityShow).GetProperty("status").GetString());

        var conceptShow = tv.Run("page", "show", conceptSlug, "--json");
        Assert.Equal(0, conceptShow.ExitCode);
        Assert.Equal("needs-review", Data(conceptShow).GetProperty("status").GetString());

        // The retraction punch list: one filed issue per affected (non-summary)
        // citing page - the repair loop's entry point (spec §14, "readable via
        // wiki issues list --kind retraction").
        var retractionIssues = tv.Run("issues", "list", "--kind", "retraction", "--json");
        Assert.Equal(0, retractionIssues.ExitCode);
        var issueRows = Data(retractionIssues);
        var issueSubjects = issueRows.EnumerateArray().Select(i => i.GetProperty("subject").GetString()!).ToArray();
        Assert.Equal(2, issueSubjects.Length);
        Assert.Contains(entitySlug, issueSubjects);
        Assert.Contains(conceptSlug, issueSubjects);
        Assert.All(issueRows.EnumerateArray(),
            i => Assert.Contains("customer withdrew the security addendum request", i.GetProperty("detail").GetString()));

        // 7. repair (spec §14: retraction is a tracked repair job, not a
        // silent delete). Take the concept off its needs-review shelf: rewrite
        // the body to drop the claim resting on the retracted source, drop the
        // retracted source id from `--sources` (passing none here leaves an
        // empty sources set), restore it to active, and resolve its retraction
        // issue. This is the agent's punch-list workflow the retraction
        // cascade exists to hand off.
        var conceptIssueId = issueRows.EnumerateArray()
            .Single(i => i.GetProperty("subject").GetString() == conceptSlug)
            .GetProperty("id").GetString()!;

        var repair = tv.RunStdin("A security addendum extends the MSA with additional security terms.",
            "page", "upsert", "--id", conceptId, "--type", "concept", "--title", "Security addendum",
            "--summary", "Contract concept", "--json");
        Assert.Equal(0, repair.ExitCode);

        var reactivate = tv.Run("page", "set-status", conceptId, "active", "--json");
        Assert.Equal(0, reactivate.ExitCode);

        var resolveIssue = tv.Run("issues", "resolve", conceptIssueId, "--note", "rewrote page, dropped retracted source", "--json");
        Assert.Equal(0, resolveIssue.ExitCode);

        // Concept is back to active, no longer citing the retracted source.
        var repairedShow = tv.Run("page", "show", conceptSlug, "--json");
        Assert.Equal(0, repairedShow.ExitCode);
        var repairedData = Data(repairedShow);
        Assert.Equal("active", repairedData.GetProperty("status").GetString());
        var repairedSources = repairedData.GetProperty("sources").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.DoesNotContain(sourceId, repairedSources);

        // The retraction issue is resolved (off the open punch list).
        var resolvedData = Data(resolveIssue);
        Assert.Equal(conceptIssueId, resolvedData.GetProperty("id").GetString());
        Assert.Equal("resolved", resolvedData.GetProperty("status").GetString());
    }

    // -------------------- Part 3: reindex-with-ledger property --------------------

    // The Task 15 property (idmap.json byte-identical after delete+reindex)
    // extended to *structural* ledger state per amendment A. Three sources
    // cover all three structurally-derivable states in one pass:
    //   - A: fully ingested through a REAL registered -> summarized ->
    //     integrated history (so post-reindex we can assert that history -
    //     the `touched` audit list, `integratedAt` - is genuinely GONE, not
    //     reconstructed; amendment A explicitly disclaims byte-identity for
    //     it).
    //   - B: cited only by a `summary`-type page -> structurally `summarized`.
    //   - C: registered, cited by nothing -> structurally `registered`.
    [Fact]
    public void Reindex_FromScratch_ReproducesIdmapByteIdentically_AndRecomputesStructuralLedgerState()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");

        // --- Source A: real ingest history through `integrated`. ---
        var fileA = Path.Combine(tv.Path, "a.md");
        File.WriteAllText(fileA, "source A content");
        var addA = tv.Run("source", "add", fileA, "--category", "article", "--title", "Source A", "--json");
        Assert.Equal(0, addA.ExitCode);
        var sourceA = Data(addA).GetProperty("id").GetString()!;

        tv.RunStdin("Summary of A.", "page", "upsert", "--type", "summary",
            "--title", "Summary A", "--summary", "s", "--sources", sourceA, "--json");
        Assert.Equal(0, tv.Run("ingest", "advance", sourceA, "--to", "summarized", "--json").ExitCode);

        var entityA = tv.RunStdin("Entity citing A.", "page", "upsert", "--type", "entity",
            "--title", "Entity A", "--summary", "s", "--sources", sourceA, "--json");
        Assert.Equal(0, entityA.ExitCode);
        var entityAId = Data(entityA).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("ingest", "advance", sourceA, "--to", "integrated", "--touched", entityAId, "--json").ExitCode);

        // --- Source B: cited only by a summary page. ---
        var fileB = Path.Combine(tv.Path, "b.md");
        File.WriteAllText(fileB, "source B content");
        var addB = tv.Run("source", "add", fileB, "--category", "article", "--title", "Source B", "--json");
        Assert.Equal(0, addB.ExitCode);
        var sourceB = Data(addB).GetProperty("id").GetString()!;

        tv.RunStdin("Summary of B.", "page", "upsert", "--type", "summary",
            "--title", "Summary B", "--summary", "s", "--sources", sourceB, "--json");

        // --- Source C: registered, cited by nothing. ---
        var fileC = Path.Combine(tv.Path, "c.md");
        File.WriteAllText(fileC, "source C content");
        var addC = tv.Run("source", "add", fileC, "--category", "article", "--title", "Source C", "--json");
        Assert.Equal(0, addC.ExitCode);
        var sourceC = Data(addC).GetProperty("id").GetString()!;

        // Sanity: before deletion, the REAL ledger has A at `integrated` with
        // a non-empty `touched` list and a stamped `integratedAt`.
        var ledgerBefore = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "ledger.json"));
        Assert.Contains($"\"sourceId\":\"{sourceA}\",\"state\":\"integrated\"", ledgerBefore);
        Assert.Contains($"\"touched\":[\"{entityAId}\"]", ledgerBefore);
        Assert.Contains("\"integratedAt\":", ledgerBefore);

        var idmapBefore = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "idmap.json"));

        // Delete .wiki/ ENTIRELY - idmap, ledger, issues, lint state, config
        // cache, everything - then rebuild purely from raw/ + wiki/ markdown.
        Directory.Delete(Path.Combine(tv.Path, ".wiki"), recursive: true);

        var reindex = tv.Run("reindex", "--json");
        Assert.Equal(0, reindex.ExitCode);

        // idmap.json: byte-identical (Task 15's property; still holds with
        // both sources and pages in the mix).
        var idmapAfter = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "idmap.json"));
        Assert.Equal(idmapBefore, idmapAfter);

        // Structural ledger state: recomputed correctly per source.
        var statusA = tv.Run("ingest", "status", sourceA, "--json");
        Assert.Equal(0, statusA.ExitCode);
        var dataA = Data(statusA);
        Assert.Equal("integrated", dataA.GetProperty("state").GetString());
        // History is NOT reproduced from scratch (amendment A): `touched` is
        // back to empty and `integratedAt`/`registeredAt` are absent (the
        // WhenWritingNull default drops null fields entirely) - reindex never
        // fabricates a timestamp/audit-list it never witnessed, and the state
        // is not "linted" either, since lint history isn't markdown-derivable.
        Assert.Equal(0, dataA.GetProperty("touched").GetArrayLength());
        Assert.False(dataA.TryGetProperty("integratedAt", out _));
        Assert.False(dataA.TryGetProperty("registeredAt", out _));

        var statusB = tv.Run("ingest", "status", sourceB, "--json");
        Assert.Equal(0, statusB.ExitCode);
        Assert.Equal("summarized", Data(statusB).GetProperty("state").GetString());

        var statusC = tv.Run("ingest", "status", sourceC, "--json");
        Assert.Equal(0, statusC.ExitCode);
        Assert.Equal("registered", Data(statusC).GetProperty("state").GetString());

        // No source is recomputed (or fabricated) as `linted` - amendment A
        // leaves the recomputed state at the highest structurally-derivable
        // level, never guessing lint history from markdown alone.
        var ledgerAfter = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "ledger.json"));
        Assert.DoesNotContain("\"state\":\"linted\"", ledgerAfter);
    }

    // The subtle half of Ledger.Reconcile's merge rule (amendment A): reindex
    // sets state = max(existing, structural), so a source that has genuinely
    // reached `linted` must NOT be dragged back down to `integrated` just
    // because markdown can't prove the lint ran - and its history (Touched,
    // IntegratedAt) must survive untouched. This is a REINDEX-OVER-EXISTING
    // run (.wiki/ is NOT deleted), the opposite of the from-scratch property
    // above: here the existing ledger IS the thing being merge-preserved.
    [Fact]
    public void Reindex_OverExistingLedger_DoesNotDowngradeLinted_PreservesHistory()
    {
        using var tv = new TempVault();
        tv.Run("init", tv.Path, "--name", "t", "--json");

        var srcFile = Path.Combine(tv.Path, "s.md");
        File.WriteAllText(srcFile, "source content");
        var addSrc = tv.Run("source", "add", srcFile, "--category", "article", "--title", "Src", "--json");
        Assert.Equal(0, addSrc.ExitCode);
        var sourceId = Data(addSrc).GetProperty("id").GetString()!;

        // Drive it all the way to `linted`.
        tv.RunStdin("Summary.", "page", "upsert", "--type", "summary",
            "--title", "Sum", "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, tv.Run("ingest", "advance", sourceId, "--to", "summarized", "--json").ExitCode);
        var entity = tv.RunStdin("Entity.", "page", "upsert", "--type", "entity",
            "--title", "Ent", "--summary", "s", "--sources", sourceId, "--json");
        var entityId = Data(entity).GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("ingest", "advance", sourceId, "--to", "integrated", "--touched", entityId, "--json").ExitCode);
        // Same-second lint is fine now (amendment J) - no sleep.
        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);
        Assert.Equal(0, tv.Run("ingest", "advance", sourceId, "--to", "linted", "--json").ExitCode);

        // Snapshot the linted entry's state + history.
        var before = Data(tv.Run("ingest", "status", sourceId, "--json"));
        Assert.Equal("linted", before.GetProperty("state").GetString());
        var touchedBefore = before.GetProperty("touched").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(new[] { entityId }, touchedBefore);
        var integratedAtBefore = before.GetProperty("integratedAt").GetString()!;

        // Reindex over the EXISTING .wiki/ (not deleted). Structural recompute
        // can only prove this source reached `integrated` (an entity cites
        // it); the merge must keep it at the higher `linted`.
        Assert.Equal(0, tv.Run("reindex", "--json").ExitCode);

        var after = Data(tv.Run("ingest", "status", sourceId, "--json"));
        // Still linted - NOT downgraded to integrated.
        Assert.Equal("linted", after.GetProperty("state").GetString());
        // History preserved verbatim.
        var touchedAfter = after.GetProperty("touched").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(touchedBefore, touchedAfter);
        Assert.Equal(integratedAtBefore, after.GetProperty("integratedAt").GetString());
    }
}
