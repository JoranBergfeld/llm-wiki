using System.IO;
using Xunit;
using Wiki.Core;
using Wiki.State;

namespace Wiki.Tests.State;

// Task 21: the .wiki/issues.json store (spec §12) - occurrence-merging
// lifecycle. Mirrors IdMapTests/LedgerEntry-style direct unit tests: no CLI
// involved, exercises Issues.Load/Upsert/Resolve/List/Save straight against a
// scratch Vault.
public class IssuesTests
{
    // A flag-rooted Vault doesn't require wiki.yaml to exist (same trick
    // IdMapTests uses) - enough to get a Vault pointed at a scratch directory
    // for issues.json round-trips.
    static Vault MakeVault(string root) => Vault.Resolve(root, _ => null, root);

    private const string T1 = "2024-01-01T00:00:00Z";
    private const string T2 = "2024-01-02T00:00:00Z";

    // -------------------- Upsert merge behavior --------------------

    [Fact]
    public void Upsert_SameKindAndSubject_MergesIntoOneIssue_BumpsOccurrences_PreservesFirstSeen()
    {
        var issues = new Issues();

        var first = issues.Upsert(IssueKind.Orphan, "page-1", "no inbound links", T1);
        var second = issues.Upsert(IssueKind.Orphan, "page-1", "still no inbound links", T2);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(issues.List(null, null));

        var stored = issues.Get(first.Id)!;
        Assert.Equal(2, stored.Occurrences);
        Assert.Equal(T1, stored.FirstSeen);
        Assert.Equal(T2, stored.LastSeen);
        Assert.Equal("still no inbound links", stored.Detail);
        Assert.Equal("open", stored.Status);
    }

    [Fact]
    public void Upsert_DifferentSubject_SameKind_CreatesSeparateIssue()
    {
        var issues = new Issues();

        var a = issues.Upsert(IssueKind.Orphan, "page-1", "d", T1);
        var b = issues.Upsert(IssueKind.Orphan, "page-2", "d", T1);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, issues.List(null, null).Count);
    }

    [Fact]
    public void Upsert_DifferentKind_SameSubject_CreatesSeparateIssue()
    {
        var issues = new Issues();

        var a = issues.Upsert(IssueKind.Orphan, "page-1", "d", T1);
        var b = issues.Upsert(IssueKind.Stale, "page-1", "d", T1);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, issues.List(null, null).Count);
    }

    // -------------------- Resolve --------------------

    [Fact]
    public void Resolve_SetsStatusResolved_AndStoresNote()
    {
        var issues = new Issues();
        var issue = issues.Upsert(IssueKind.Orphan, "page-1", "d", T1);

        issues.Resolve(issue.Id, "fixed by adding a link");

        var stored = issues.Get(issue.Id)!;
        Assert.Equal("resolved", stored.Status);
        Assert.Equal("fixed by adding a link", stored.ResolveNote);
    }

    [Fact]
    public void Resolve_UnknownIssueId_ThrowsNotFound()
    {
        var issues = new Issues();

        var ex = Assert.Throws<ValidationException>(() => issues.Resolve("01AAAAAAAAAAAAAAAAAAAAAAAA", null));
        Assert.Equal("not-found", ex.Code);
    }

    // The chosen semantic (documented on Issues.Upsert): merging only ever
    // considers OPEN issues. A resolved issue does not block - and is not
    // reopened by - a fresh recurrence of the same (kind, subject); a brand
    // new open issue is filed instead, and the resolved one is left alone as
    // history.
    [Fact]
    public void ResolveThenReupsert_Recurrence_FilesNewOpenIssue_OldStaysResolved()
    {
        var issues = new Issues();
        var original = issues.Upsert(IssueKind.Orphan, "page-1", "first occurrence", T1);
        issues.Resolve(original.Id, "fixed");

        var recurrence = issues.Upsert(IssueKind.Orphan, "page-1", "recurred", T2);

        Assert.NotEqual(original.Id, recurrence.Id);

        var stillResolved = issues.Get(original.Id)!;
        Assert.Equal("resolved", stillResolved.Status);
        Assert.Equal("fixed", stillResolved.ResolveNote);
        Assert.Equal(1, stillResolved.Occurrences);

        var fresh = issues.Get(recurrence.Id)!;
        Assert.Equal("open", fresh.Status);
        Assert.Equal(1, fresh.Occurrences);
        Assert.Equal(T2, fresh.FirstSeen);

        Assert.Equal(2, issues.List(null, null).Count);
    }

    // -------------------- List filters --------------------

    [Fact]
    public void List_FilterByStatusOpen_ExcludesResolved()
    {
        var issues = new Issues();
        var open = issues.Upsert(IssueKind.Orphan, "page-1", "d", T1);
        var toResolve = issues.Upsert(IssueKind.Stale, "page-2", "d", T1);
        issues.Resolve(toResolve.Id, null);

        var result = issues.List(null, "open");

        Assert.Single(result);
        Assert.Equal(open.Id, result[0].Id);
    }

    [Fact]
    public void List_FilterByKind_OnlyMatchingKind()
    {
        var issues = new Issues();
        var orphan = issues.Upsert(IssueKind.Orphan, "page-1", "d", T1);
        issues.Upsert(IssueKind.Stale, "page-2", "d", T1);

        var result = issues.List(IssueKind.Orphan, null);

        Assert.Single(result);
        Assert.Equal(orphan.Id, result[0].Id);
    }

    // -------------------- Load/Save round-trip + determinism --------------------

    [Fact]
    public void Load_MissingFile_YieldsEmptyStore()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var vault = MakeVault(tv.Path);

        var issues = new Issues();
        issues.Load(vault);

        Assert.Empty(issues.List(null, null));
    }

    [Fact]
    public void Save_Load_RoundTrips_AllFields()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var vault = MakeVault(tv.Path);

        var issues = new Issues();
        var issue = issues.Upsert(IssueKind.CoverageGap, "term-x", "mentioned 3x, no page", T1);
        issues.Upsert(IssueKind.CoverageGap, "term-x", "still mentioned, no page", T2);
        issues.Resolve(issue.Id, "created the page");
        issues.Save(vault);

        var reloaded = new Issues();
        reloaded.Load(vault);
        var stored = reloaded.Get(issue.Id)!;

        Assert.Equal(IssueKind.CoverageGap, stored.Kind);
        Assert.Equal("term-x", stored.Subject);
        Assert.Equal("still mentioned, no page", stored.Detail);
        Assert.Equal(T1, stored.FirstSeen);
        Assert.Equal(T2, stored.LastSeen);
        Assert.Equal(2, stored.Occurrences);
        Assert.Equal("resolved", stored.Status);
        Assert.Equal("created the page", stored.ResolveNote);
    }

    // Regression guard mirroring IdMapTests.Save_WritesKeysInSortedOrdinalOrder:
    // Save() must always emit issues in Id-sorted order regardless of the
    // order Upsert was called in, so issues.json doesn't churn from
    // insertion-order noise. Fixed (all-zero) random bytes isolate the sort
    // to the ULID's leading time component: upserting the LATER-timestamped
    // issue FIRST still must land it AFTER the earlier one on disk.
    [Fact]
    public void Save_WritesIssuesInIdSortedOrder_RegardlessOfUpsertOrder()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var vault = MakeVault(tv.Path);

        var issues = new Issues(() => new byte[10]);
        var later = issues.Upsert(IssueKind.Orphan, "z-subject", "d", "2024-06-01T00:00:00Z");
        var earlier = issues.Upsert(IssueKind.Stale, "a-subject", "d", "2024-01-01T00:00:00Z");
        issues.Save(vault);

        Assert.NotEqual(earlier.Id, later.Id);

        var text = File.ReadAllText(Path.Combine(vault.StateDir, "issues.json"));
        var idxEarlier = text.IndexOf(earlier.Id, System.StringComparison.Ordinal);
        var idxLater = text.IndexOf(later.Id, System.StringComparison.Ordinal);

        Assert.True(idxEarlier >= 0 && idxLater >= 0, "expected both issue ids in issues.json");
        Assert.True(idxEarlier < idxLater, "expected earlier-timestamped (lexically smaller) id first");
    }
}
