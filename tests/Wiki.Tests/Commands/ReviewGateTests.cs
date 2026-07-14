using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Core;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 23: the review gate (spec §15). When `review_gate: true`, every page
// upsert (create or update) lands `pending-review` instead of `active`;
// `wiki review list/approve/reject` drives it from there. Updates get a
// shadow copy of the previous body under `.wiki/review/<id>.prev.md` so
// `reject` can restore it and `list` can show a diff; a create has no prior
// version, so reject on a create archives it instead. Every test that
// exercises the gate inits WITH --review-gate; a couple of regression tests
// confirm the gate OFF (existing default) still lands pages `active` -
// dozens of pre-Task-23 tests already assume that, so this is a guardrail,
// not new coverage.
public class ReviewGateTests
{
    private static CliResult InitGated(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--review-gate", "--json");
    private static CliResult InitUngated(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string ShadowPath(TempVault tv, string pageId) => Path.Combine(tv.Path, ".wiki", "review", pageId + ".prev.md");

    private static string ExtractId(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    // -------------------- gate ON: create lands pending-review --------------------

    [Fact]
    public void Create_UnderGate_LandsPendingReview_AppearsInReviewList()
    {
        using var tv = new TempVault(); InitGated(tv);

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("pending-review", Data(r).GetProperty("status").GetString());

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var doc = PageDoc.Parse(File.ReadAllText(file));
        Assert.Equal(PageStatus.PendingReview, doc.Front.Status);

        var list = tv.Run("review", "list", "--json");
        Assert.Equal(0, list.ExitCode);
        var data = Data(list);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("contoso", data[0].GetProperty("slug").GetString());
        Assert.False(data[0].GetProperty("isUpdate").GetBoolean());
    }

    [Fact]
    public void Approve_PendingCreate_MovesToActive_ClearsShadowIfAny_RemovesFromReviewList()
    {
        using var tv = new TempVault(); InitGated(tv);

        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);

        var approve = tv.Run("review", "approve", id, "--json");
        Assert.Equal(0, approve.ExitCode);

        var file = Path.Combine(tv.Path, "wiki", "entities", "contoso.md");
        var doc = PageDoc.Parse(File.ReadAllText(file));
        Assert.Equal(PageStatus.Active, doc.Front.Status);

        var list = tv.Run("review", "list", "--json");
        Assert.Equal(0, Data(list).GetArrayLength());
    }

    // -------------------- gate ON: update saves shadow, reject restores --------------------

    [Fact]
    public void Update_UnderGate_LandsPendingReview_SavesShadowOfPreviousBody()
    {
        using var tv = new TempVault(); InitGated(tv);

        var created = tv.RunStdin("Original body.", "page", "upsert", "--type", "entity",
            "--title", "Fabrikam", "--summary", "s1", "--json");
        var id = ExtractId(created);
        // Gate is on, so the create itself lands pending-review; approve it
        // first so the update path below is exercising a genuine "revise an
        // already-active page" scenario, not a second pending create.
        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);

        var updated = tv.RunStdin("Revised body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Fabrikam", "--summary", "s2", "--json");
        Assert.Equal(0, updated.ExitCode);
        Assert.Equal("pending-review", Data(updated).GetProperty("status").GetString());

        var shadow = ShadowPath(tv, id);
        Assert.True(File.Exists(shadow));
        Assert.Equal("Original body.", File.ReadAllText(shadow));

        var file = Path.Combine(tv.Path, "wiki", "entities", "fabrikam.md");
        var doc = PageDoc.Parse(File.ReadAllText(file));
        Assert.Contains("Revised body.", doc.Body);
        Assert.Equal(PageStatus.PendingReview, doc.Front.Status);

        var list = tv.Run("review", "list", "--json");
        var data = Data(list);
        Assert.Equal(1, data.GetArrayLength());
        Assert.True(data[0].GetProperty("isUpdate").GetBoolean());
        var diff = data[0].GetProperty("diff").GetString();
        Assert.Contains("Original body.", diff);
        Assert.Contains("Revised body.", diff);
    }

    [Fact]
    public void Reject_PendingUpdate_RestoresPreviousBody_SetsActive_ClearsShadow_FilesIssue()
    {
        using var tv = new TempVault(); InitGated(tv);

        var created = tv.RunStdin("Original body.", "page", "upsert", "--type", "entity",
            "--title", "Fabrikam", "--summary", "s1", "--json");
        var id = ExtractId(created);
        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);

        var updated = tv.RunStdin("Revised body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Fabrikam", "--summary", "s2", "--json");
        Assert.Equal(0, updated.ExitCode);

        var reject = tv.Run("review", "reject", id, "--note", "not accurate", "--json");
        Assert.Equal(0, reject.ExitCode);

        var file = Path.Combine(tv.Path, "wiki", "entities", "fabrikam.md");
        var doc = PageDoc.Parse(File.ReadAllText(file));
        Assert.Contains("Original body.", doc.Body);
        Assert.DoesNotContain("Revised body.", doc.Body);
        Assert.Equal(PageStatus.Active, doc.Front.Status);

        Assert.False(File.Exists(ShadowPath(tv, id)));

        var issues = tv.Run("issues", "list", "--json");
        Assert.Equal(0, issues.ExitCode);
        var issueData = Data(issues);
        Assert.True(issueData.GetArrayLength() >= 1);
        Assert.Contains(issueData.EnumerateArray(), i =>
            i.GetProperty("subject").GetString() == "fabrikam" &&
            i.GetProperty("kind").GetString() == "review-rejected" &&
            i.GetProperty("detail").GetString()!.Contains("not accurate"));
    }

    // -------------------- gate ON: reject of a pending create archives (no shadow) --------------------

    [Fact]
    public void Reject_PendingCreate_NoShadow_ArchivesPage_FilesIssue()
    {
        using var tv = new TempVault(); InitGated(tv);

        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Initech", "--summary", "s", "--json");
        var id = ExtractId(created);
        Assert.False(File.Exists(ShadowPath(tv, id)));

        var reject = tv.Run("review", "reject", id, "--json");
        Assert.Equal(0, reject.ExitCode);

        var file = Path.Combine(tv.Path, "wiki", "entities", "initech.md");
        var doc = PageDoc.Parse(File.ReadAllText(file));
        Assert.Equal(PageStatus.Archived, doc.Front.Status);

        var issues = tv.Run("issues", "list", "--json");
        var issueData = Data(issues);
        Assert.Contains(issueData.EnumerateArray(), i =>
            i.GetProperty("subject").GetString() == "initech" &&
            i.GetProperty("kind").GetString() == "review-rejected");
    }

    // -------------------- reject files review-rejected, never collides with lint --------------------

    [Fact]
    public void Reject_FilesReviewRejectedKind_NotPendingBacklog()
    {
        using var tv = new TempVault(); InitGated(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);

        var reject = tv.Run("review", "reject", id, "--note", "no good", "--json");
        Assert.Equal(0, reject.ExitCode);

        var rr = tv.Run("issues", "list", "--kind", "review-rejected", "--json");
        Assert.Equal(0, rr.ExitCode);
        Assert.Equal(1, Data(rr).GetArrayLength());
        Assert.Equal("contoso", Data(rr)[0].GetProperty("subject").GetString());

        var pb = tv.Run("issues", "list", "--kind", "pending-backlog", "--json");
        Assert.Equal(0, Data(pb).GetArrayLength());
    }

    // The corruption regression: a REAL pending-backlog LINT issue already
    // exists on the same page's slug when reject fires. reject must file a
    // SEPARATE review-rejected issue and leave the lint record byte-for-byte
    // untouched (Issues.Upsert merges on (kind, subject), so a shared kind
    // would silently overwrite the lint issue's detail and bump its count).
    [Fact]
    public void Reject_DoesNotMergeInto_ExistingPendingBacklogLintIssue()
    {
        using var tv = new TempVault(); InitGated(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);

        // Simulate the lint pending-backlog finding on this exact slug, filed
        // directly through the production Issues store (same technique
        // IssuesCommandTests uses to seed issues.json).
        var vault = Vault.Resolve(tv.Path, _ => null, tv.Path);
        var store = new Wiki.State.Issues();
        store.Load(vault);
        var lintIssue = store.Upsert(IssueKind.PendingBacklog, "contoso",
            "page has been 'pending-review' for 20d (based on 'updated'; threshold 14d)",
            "2024-01-01T00:00:00Z");
        store.Save(vault);
        var lintId = lintIssue.Id;

        var reject = tv.Run("review", "reject", id, "--note", "rejected reason", "--json");
        Assert.Equal(0, reject.ExitCode);

        // The lint issue is untouched: same detail, occurrences still 1, still open.
        var show = tv.Run("issues", "show", lintId, "--json");
        Assert.Equal(0, show.ExitCode);
        var lintNow = Data(show);
        Assert.Equal("pending-backlog", lintNow.GetProperty("kind").GetString());
        Assert.Equal(1, lintNow.GetProperty("occurrences").GetInt32());
        Assert.Equal("open", lintNow.GetProperty("status").GetString());
        Assert.Contains("20d", lintNow.GetProperty("detail").GetString());
        Assert.DoesNotContain("rejected reason", lintNow.GetProperty("detail").GetString());

        // A separate review-rejected issue now exists on the same slug.
        var rr = tv.Run("issues", "list", "--kind", "review-rejected", "--json");
        Assert.Equal(1, Data(rr).GetArrayLength());
        Assert.NotEqual(lintId, Data(rr)[0].GetProperty("id").GetString());
        Assert.Equal("contoso", Data(rr)[0].GetProperty("subject").GetString());
    }

    // -------------------- gate OFF: regression --------------------

    [Fact]
    public void Create_GateOff_LandsActive()
    {
        using var tv = new TempVault(); InitUngated(tv);

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("active", Data(r).GetProperty("status").GetString());
        Assert.False(File.Exists(ShadowPath(tv, ExtractId(r))));
    }

    [Fact]
    public void Update_GateOff_LandsActive_NoShadow()
    {
        using var tv = new TempVault(); InitUngated(tv);

        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s1", "--json");
        var id = ExtractId(created);

        var updated = tv.RunStdin("New body.", "page", "upsert", "--id", id,
            "--type", "entity", "--title", "Contoso", "--summary", "s2", "--json");
        Assert.Equal(0, updated.ExitCode);
        Assert.Equal("active", Data(updated).GetProperty("status").GetString());
        Assert.False(File.Exists(ShadowPath(tv, id)));
    }

    // -------------------- approve/reject error paths --------------------

    [Fact]
    public void Approve_UnknownId_NotFound()
    {
        using var tv = new TempVault(); InitGated(tv);
        var r = tv.Run("review", "approve", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Approve_NonPendingPage_NotPending()
    {
        using var tv = new TempVault(); InitGated(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);
        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);

        var r = tv.Run("review", "approve", id, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-pending");
    }

    [Fact]
    public void Reject_UnknownId_NotFound()
    {
        using var tv = new TempVault(); InitGated(tv);
        var r = tv.Run("review", "reject", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Reject_NonPendingPage_NotPending()
    {
        using var tv = new TempVault(); InitGated(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);
        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);

        var r = tv.Run("review", "reject", id, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-pending");
    }

    // -------------------- lint interaction: approved page is subject to orphan lint --------------------

    [Fact]
    public void ApprovedPage_BecomesSubjectToOrphanLint()
    {
        using var tv = new TempVault(); InitGated(tv);

        var created = tv.RunStdin("Body with no links.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var id = ExtractId(created);

        // Still pending-review: orphan lint must NOT flag it yet.
        var lintBefore = tv.Run("lint", "--json");
        Assert.Equal(0, lintBefore.ExitCode);
        var issuesBefore = tv.Run("issues", "list", "--kind", "orphan", "--json");
        Assert.Equal(0, Data(issuesBefore).GetArrayLength());

        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);

        var lintAfter = tv.Run("lint", "--json");
        Assert.Equal(0, lintAfter.ExitCode);
        var issuesAfter = tv.Run("issues", "list", "--kind", "orphan", "--json");
        Assert.Equal(1, Data(issuesAfter).GetArrayLength());
        Assert.Equal("contoso", Data(issuesAfter)[0].GetProperty("subject").GetString());
    }
}
