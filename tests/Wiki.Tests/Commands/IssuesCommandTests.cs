using System.IO;
using System.Text.Json;
using Wiki.Core;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 21: `wiki issues list/show/resolve` over the .wiki/issues.json store.
// Nothing files a real issue yet (that's lint, a later M3 task), so these
// tests seed issues.json directly through the production Issues store API
// (same technique SourceQueryTests uses to hand-write a raw file to reach a
// status not otherwise wired up to the CLI) rather than through a CLI verb.
public class IssuesCommandTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    // A flag-rooted Vault doesn't require wiki.yaml (same trick IdMapTests/
    // IssuesTests use) - enough to point Issues.Save at the vault the CLI
    // will later resolve via --vault.
    private static Vault MakeVault(string root) => Vault.Resolve(root, _ => null, root);

    private static string SeedOpenIssue(TempVault tv, IssueKind kind, string subject, string detail, string utcIso)
    {
        var vault = MakeVault(tv.Path);
        var store = new Wiki.State.Issues();
        store.Load(vault);
        var issue = store.Upsert(kind, subject, detail, utcIso);
        store.Save(vault);
        return issue.Id;
    }

    // -------------------- issues list --------------------

    [Fact]
    public void List_NoFilter_ReturnsAllIssues()
    {
        using var tv = new TempVault(); Init(tv);
        SeedOpenIssue(tv, IssueKind.Orphan, "page-1", "no inbound links", "2024-01-01T00:00:00Z");
        SeedOpenIssue(tv, IssueKind.Stale, "page-2", "stale summary", "2024-01-01T00:00:00Z");

        var r = tv.Run("issues", "list", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(2, Data(r).GetArrayLength());
    }

    [Fact]
    public void List_FilterByKindAndStatus()
    {
        using var tv = new TempVault(); Init(tv);
        var orphanId = SeedOpenIssue(tv, IssueKind.Orphan, "page-1", "d", "2024-01-01T00:00:00Z");
        SeedOpenIssue(tv, IssueKind.Stale, "page-2", "d", "2024-01-01T00:00:00Z");

        var r = tv.Run("issues", "list", "--kind", "orphan", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal(orphanId, data[0].GetProperty("id").GetString());
        Assert.Equal("orphan", data[0].GetProperty("kind").GetString());

        var resolveResult = tv.Run("issues", "resolve", orphanId, "--json");
        Assert.Equal(0, resolveResult.ExitCode);

        var openOnly = tv.Run("issues", "list", "--status", "open", "--json");
        Assert.Equal(1, Data(openOnly).GetArrayLength());
        Assert.Equal("stale", Data(openOnly)[0].GetProperty("kind").GetString());
    }

    // -------------------- issues show --------------------

    [Fact]
    public void Show_ReturnsFullIssue()
    {
        using var tv = new TempVault(); Init(tv);
        var id = SeedOpenIssue(tv, IssueKind.CoverageGap, "term-x", "mentioned 3x, no page", "2024-01-01T00:00:00Z");

        var r = tv.Run("issues", "show", id, "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(id, data.GetProperty("id").GetString());
        Assert.Equal("coverage-gap", data.GetProperty("kind").GetString());
        Assert.Equal("term-x", data.GetProperty("subject").GetString());
        Assert.Equal("open", data.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("occurrences").GetInt32());
    }

    [Fact]
    public void Show_UnknownId_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("issues", "show", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    // -------------------- issues resolve --------------------

    [Fact]
    public void Resolve_SetsStatusResolved_AndPersistsAcrossInvocations()
    {
        using var tv = new TempVault(); Init(tv);
        var id = SeedOpenIssue(tv, IssueKind.Orphan, "page-1", "d", "2024-01-01T00:00:00Z");

        var r = tv.Run("issues", "resolve", id, "--note", "added a link", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("resolved", Data(r).GetProperty("status").GetString());
        Assert.Equal("added a link", Data(r).GetProperty("resolveNote").GetString());

        // Re-fetch through a fresh `show` invocation to prove the resolve
        // was actually persisted to issues.json, not just returned in-memory.
        var show = tv.Run("issues", "show", id, "--json");
        Assert.Equal("resolved", Data(show).GetProperty("status").GetString());
        Assert.Equal("added a link", Data(show).GetProperty("resolveNote").GetString());
    }

    [Fact]
    public void Resolve_UnknownId_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("issues", "resolve", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Resolve_AppendsLogEntry()
    {
        using var tv = new TempVault(); Init(tv);
        var id = SeedOpenIssue(tv, IssueKind.Orphan, "page-1", "d", "2024-01-01T00:00:00Z");

        var r = tv.Run("issues", "resolve", id, "--note", "fixed it", "--json");
        Assert.Equal(0, r.ExitCode);

        var log = File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md"));
        Assert.Contains("issue-resolve", log);
        Assert.Contains(id, log);
        Assert.Contains("fixed it", log);
    }
}
