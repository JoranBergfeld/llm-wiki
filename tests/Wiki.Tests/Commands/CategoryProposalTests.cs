using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #9: `wiki category propose/proposals/approve/reject`. Same envelope
// and exit-code contract as `wiki schema propose`, but its own store and its
// own approve action (a `category add` against wiki.yaml, not a section
// replacement against AGENTS.md).
public class CategoryProposalTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static string AddSource(TempVault tv, string name, string content)
    {
        var src = Path.Combine(tv.Path, name);
        File.WriteAllText(src, content);
        var r = tv.Run("source", "add", src, "--category", "article", "--title", name, "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    [Fact]
    public void Propose_RecordsAnOpenProposal_ListableAndCitingItsSources()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "one.md", "content one");
        var s2 = AddSource(tv, "two.md", "content two");

        var r = tv.Run("category", "propose", "research-paper",
            "--description", "Peer-reviewed papers",
            "--rationale", "Neither article nor meeting-transcript fits these two",
            "--sources", s1 + "," + s2, "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("research-paper", Data(r).GetProperty("categoryId").GetString());
        Assert.Equal("open", Data(r).GetProperty("status").GetString());

        var listed = tv.Run("category", "proposals", "--status", "open", "--json");
        var rows = ((JsonElement)listed.Envelope.Data!).EnumerateArray().ToArray();
        Assert.Single(rows);
        var sources = rows[0].GetProperty("sources").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { s1, s2 }, sources);

        // Proposing does not add the category.
        var cats = tv.Run("category", "list", "--json");
        Assert.DoesNotContain("research-paper", cats.Stdout);
    }

    [Fact]
    public void Approve_AddsTheCategoryExactlyAsCategoryAddWould()
    {
        using var tv = new TempVault(); Init(tv);

        var proposed = tv.Run("category", "propose", "research-paper",
            "--description", "Peer-reviewed papers", "--json");
        var proposalId = Data(proposed).GetProperty("id").GetString()!;

        var approved = tv.Run("category", "approve", proposalId, "--json");
        Assert.Equal(0, approved.ExitCode);
        Assert.Equal("approved", Data(approved).GetProperty("status").GetString());

        // wiki.yaml now carries it, and it is usable as a real category.
        var yaml = File.ReadAllText(Path.Combine(tv.Path, "wiki.yaml"));
        Assert.Contains("- id: research-paper", yaml);
        Assert.Contains("description: \"Peer-reviewed papers\"", yaml);

        var src = Path.Combine(tv.Path, "paper.md");
        File.WriteAllText(src, "a paper");
        var add = tv.Run("source", "add", src, "--category", "research-paper", "--title", "P", "--json");
        Assert.Equal(0, add.ExitCode);
    }

    [Fact]
    public void Reject_LeavesConfigUntouched()
    {
        using var tv = new TempVault(); Init(tv);
        var yamlBefore = File.ReadAllText(Path.Combine(tv.Path, "wiki.yaml"));

        var proposed = tv.Run("category", "propose", "research-paper", "--description", "d", "--json");
        var proposalId = Data(proposed).GetProperty("id").GetString()!;

        var rejected = tv.Run("category", "reject", proposalId, "--note", "use 'article'", "--json");
        Assert.Equal(0, rejected.ExitCode);
        Assert.Equal("rejected", Data(rejected).GetProperty("status").GetString());
        Assert.Equal("use 'article'", Data(rejected).GetProperty("note").GetString());

        Assert.Equal(yamlBefore, File.ReadAllText(Path.Combine(tv.Path, "wiki.yaml")));
    }

    [Fact]
    public void DecidingTwice_IsAStateConflict()
    {
        using var tv = new TempVault(); Init(tv);
        var proposalId = Data(tv.Run("category", "propose", "x-cat", "--description", "d", "--json"))
            .GetProperty("id").GetString()!;

        Assert.Equal(0, tv.Run("category", "approve", proposalId, "--json").ExitCode);

        var again = tv.Run("category", "approve", proposalId, "--json");
        Assert.Equal(3, again.ExitCode);
        Assert.Contains(again.Envelope.Errors, e => e.Code == "state-conflict");

        var rejectAfter = tv.Run("category", "reject", proposalId, "--json");
        Assert.Equal(3, rejectAfter.ExitCode);
    }

    [Fact]
    public void Propose_ValidatesIdAndDuplicateAtProposeTime()
    {
        using var tv = new TempVault(); Init(tv);

        var badId = tv.Run("category", "propose", "Research Paper", "--description", "d", "--json");
        Assert.Equal(1, badId.ExitCode);
        Assert.Contains(badId.Envelope.Errors, e => e.Code == "invalid-category-id");

        // 'article' ships in the scaffold.
        var dup = tv.Run("category", "propose", "article", "--description", "d", "--json");
        Assert.Equal(1, dup.ExitCode);
        Assert.Contains(dup.Envelope.Errors, e => e.Code == "duplicate-category");

        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "category-proposals.json")));
    }

    [Fact]
    public void Propose_UnknownSourceId_Rejected()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.Run("category", "propose", "research-paper", "--description", "d",
            "--sources", "01M0NOTAREALSOURCEID000000", "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "unknown-source");
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "category-proposals.json")));
    }

    [Fact]
    public void UnknownProposalId_IsNotFound()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.Run("category", "approve", "01M0NOSUCHPROPOSAL0000000", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    // Approve re-checks against the CURRENT config: a category added by hand
    // between propose and approve must not be added twice.
    [Fact]
    public void Approve_AfterTheCategoryWasAddedByHand_Fails_AndLeavesTheProposalOpen()
    {
        using var tv = new TempVault(); Init(tv);
        var proposalId = Data(tv.Run("category", "propose", "research-paper", "--description", "d", "--json"))
            .GetProperty("id").GetString()!;

        Assert.Equal(0, tv.Run("category", "add", "research-paper", "--description", "added by hand", "--json").ExitCode);

        var approve = tv.Run("category", "approve", proposalId, "--json");
        Assert.Equal(1, approve.ExitCode);
        Assert.Contains(approve.Envelope.Errors, e => e.Code == "duplicate-category");

        var open = ((JsonElement)tv.Run("category", "proposals", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray().ToArray();
        Assert.Single(open);
    }

    // A category proposal must never show up as an AGENTS.md amendment: the
    // two stores are separate precisely so `schema approve` can never be
    // handed a category id.
    [Fact]
    public void CategoryProposals_AreNotSchemaProposals()
    {
        using var tv = new TempVault(); Init(tv);
        var proposalId = Data(tv.Run("category", "propose", "research-paper", "--description", "d", "--json"))
            .GetProperty("id").GetString()!;

        var schemaList = tv.Run("schema", "proposals", "--json");
        Assert.Empty(((JsonElement)schemaList.Envelope.Data!).EnumerateArray());

        var crossApprove = tv.Run("schema", "approve", proposalId, "--json");
        Assert.Equal(1, crossApprove.ExitCode);
        Assert.Contains(crossApprove.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Proposals_BadStatusFilter_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.Run("category", "proposals", "--status", "pending", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "invalid-proposal-status");
    }
}
