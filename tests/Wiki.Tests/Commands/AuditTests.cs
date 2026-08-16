using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #12: `wiki audit next|record|list`. The CLI selects and records; the
// agent judges. Nothing here calls a model, and none of it touches lint.
public class AuditTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static string AddSource(TempVault tv, string name, string content)
    {
        var src = Path.Combine(tv.Path, name);
        File.WriteAllText(src, content);
        return Data(tv.Run("source", "add", src, "--category", "article", "--title", name, "--json"))
            .GetProperty("id").GetString()!;
    }

    private static string CreatePage(TempVault tv, string title, string body, string? sources)
    {
        var args = new System.Collections.Generic.List<string>
        {
            "page", "upsert", "--type", "concept", "--title", title, "--summary", title + " summary",
            "--stdin", "--allow-dangling", "--json",
        };
        if (sources is not null) { args.Add("--sources"); args.Add(sources); }
        var r = tv.RunStdin(body, args.ToArray());
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    [Fact]
    public void Next_EmitsThePageAndItsCitedSourceIds()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one body");
        var s2 = AddSource(tv, "s2.md", "source two body");
        var id = CreatePage(tv, "Billing", "Claims about billing.", s1 + "," + s2);

        var r = tv.Run("audit", "next", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(id, Data(r).GetProperty("pageId").GetString());
        Assert.Equal("Claims about billing.", Data(r).GetProperty("body").GetString());
        Assert.Equal("never audited; cites 2 source(s)", Data(r).GetProperty("why").GetString());

        var sourceIds = Data(r).GetProperty("sources").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { s1, s2 }, sourceIds);

        // Ids and titles only - never inlined source bodies. The auditor
        // fetches those with `wiki source show`.
        Assert.False(Data(r).GetProperty("sources").EnumerateArray().First().TryGetProperty("body", out _));
    }

    [Fact]
    public void Next_IsReadOnly()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        CreatePage(tv, "Billing", "Claims.", s1);

        var logBefore = File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md"));
        tv.Run("audit", "next", "--json");

        Assert.Equal(logBefore, File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "audits.json")));
    }

    [Fact]
    public void Next_SkipsPagesWithNoCitedSources()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Sourceless", "Asserts things from nowhere.", null);

        var r = tv.Run("audit", "next", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Envelope.Ok);
        Assert.False(Data(r).GetProperty("hasTarget").GetBoolean());
        Assert.Contains("nothing to check claims against", Data(r).GetProperty("reason").GetString()!);
    }

    // Priority 1: the cheap structural signal points the expensive semantic
    // check at the right page. This is why part C of #11 shipped first.
    [Fact]
    public void Next_PrefersPagesCarryingAnOpenContentLossIssue()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var s2 = AddSource(tv, "s2.md", "source two");
        var s3 = AddSource(tv, "s3.md", "source three");

        // 'Aaa' cites more sources, so it wins on the source-count heuristic...
        CreatePage(tv, "Aaa", "[[x]] [[y]]", s1 + "," + s2 + "," + s3);
        // ...unless 'Bbb' has lost references in a rewrite.
        var bbb = CreatePage(tv, "Bbb", "[[p]] [[q]] [[r]] [[s]]", s1);
        tv.RunStdin("[[p]]", "page", "upsert", "--type", "concept", "--title", "Bbb",
            "--summary", "Bbb summary", "--id", bbb, "--sources", s1, "--stdin", "--allow-dangling", "--json");

        Assert.Contains("content-loss",
            ((JsonElement)tv.Run("issues", "list", "--status", "open", "--json").Envelope.Data!)
                .EnumerateArray().Select(e => e.GetProperty("kind").GetString()));

        var r = tv.Run("audit", "next", "--json");
        Assert.Equal(bbb, Data(r).GetProperty("pageId").GetString());
        Assert.Contains("content-loss", Data(r).GetProperty("why").GetString()!);
    }

    [Fact]
    public void Next_PrefersMoreCitedSources_ThenLeastRecentlyAudited()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var s2 = AddSource(tv, "s2.md", "source two");

        var few = CreatePage(tv, "Few", "One source.", s1);
        var many = CreatePage(tv, "Many", "Two sources.", s1 + "," + s2);

        // Both never audited, so source count decides.
        Assert.Equal(many, Data(tv.Run("audit", "next", "--json")).GetProperty("pageId").GetString());

        // Once 'Many' has been audited, the never-audited page outranks it.
        Assert.Equal(0, tv.Run("audit", "record", many, "--verdict", "supported", "--json").ExitCode);
        Assert.Equal(few, Data(tv.Run("audit", "next", "--json")).GetProperty("pageId").GetString());
    }

    [Fact]
    public void Next_IsDeterministic()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        for (var i = 1; i <= 4; i++)
            CreatePage(tv, $"Page {i}", "Identical shape.", s1);

        Assert.Equal(
            tv.Run("audit", "next", "--json").Stdout,
            tv.Run("audit", "next", "--json").Stdout);
    }

    [Fact]
    public void Record_Supported_StoresTheVerdict_FilesNoIssue()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var id = CreatePage(tv, "Billing", "Claims.", s1);

        var r = tv.Run("audit", "record", id, "--verdict", "supported", "--note", "every claim traced", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("supported", Data(r).GetProperty("verdict").GetString());
        Assert.Equal(1, Data(r).GetProperty("audits").GetInt32());
        Assert.False(Data(r).GetProperty("issueFiled").GetBoolean());

        Assert.Empty(((JsonElement)tv.Run("issues", "list", "--status", "open", "--json").Envelope.Data!).EnumerateArray());
        Assert.Contains("audit", File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
    }

    [Fact]
    public void Record_Unsupported_FilesAnIssue_ThatAccumulatesOccurrences()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var id = CreatePage(tv, "Billing", "Claims.", s1);

        var first = tv.Run("audit", "record", id, "--verdict", "unsupported",
            "--note", "asserts a Q3 launch date no cited source mentions", "--json");
        Assert.Equal(0, first.ExitCode);
        Assert.True(Data(first).GetProperty("issueFiled").GetBoolean());

        var issues = ((JsonElement)tv.Run("issues", "list", "--kind", "unsupported-claim", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray().ToArray();
        Assert.Single(issues);
        Assert.Equal("billing", issues[0].GetProperty("subject").GetString());
        Assert.Contains("Q3 launch date", issues[0].GetProperty("detail").GetString()!);

        // A recurring semantic finding is exactly what the reflect loop reads,
        // so it must merge on (kind, subject) rather than fork.
        tv.Run("audit", "record", id, "--verdict", "unsupported", "--note", "still asserts it", "--json");
        var again = ((JsonElement)tv.Run("issues", "list", "--kind", "unsupported-claim", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray().ToArray();
        Assert.Single(again);
        Assert.Equal(2, again[0].GetProperty("occurrences").GetInt32());

        // And it is resolvable with a note like any other kind - a verdict is
        // a finding to weigh, not a fact.
        var issueId = again[0].GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("issues", "resolve", issueId, "--note", "claim is in the transcript, auditor missed it", "--json").ExitCode);
    }

    [Fact]
    public void Record_UnsupportedWithoutANote_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var id = CreatePage(tv, "Billing", "Claims.", s1);

        var r = tv.Run("audit", "record", id, "--verdict", "unsupported", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "note-required");
        Assert.False(File.Exists(Path.Combine(tv.Path, ".wiki", "audits.json")));
    }

    [Fact]
    public void Record_BadVerdictOrUnknownPage_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var id = CreatePage(tv, "Billing", "Claims.", s1);

        var badVerdict = tv.Run("audit", "record", id, "--verdict", "maybe", "--json");
        Assert.Equal(1, badVerdict.ExitCode);
        Assert.Contains(badVerdict.Envelope.Errors, e => e.Code == "invalid-verdict");

        var badPage = tv.Run("audit", "record", "01M0NOSUCHPAGEID000000000", "--verdict", "supported", "--json");
        Assert.Equal(1, badPage.ExitCode);
        Assert.Contains(badPage.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void List_ShowsTheLastVerdictPerPage()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var a = CreatePage(tv, "Alpha", "Claims a.", s1);
        var b = CreatePage(tv, "Beta", "Claims b.", s1);

        tv.Run("audit", "record", a, "--verdict", "supported", "--json");
        tv.Run("audit", "record", b, "--verdict", "unsupported", "--note", "no source says this", "--json");
        tv.Run("audit", "record", a, "--verdict", "unsupported", "--note", "second look disagrees", "--json");

        var all = ((JsonElement)tv.Run("audit", "list", "--json").Envelope.Data!).EnumerateArray().ToArray();
        Assert.Equal(2, all.Length);

        var alpha = all.Single(e => e.GetProperty("slug").GetString() == "alpha");
        Assert.Equal("unsupported", alpha.GetProperty("verdict").GetString());
        Assert.Equal(2, alpha.GetProperty("audits").GetInt32());

        var filtered = ((JsonElement)tv.Run("audit", "list", "--verdict", "unsupported", "--json").Envelope.Data!)
            .EnumerateArray().ToArray();
        Assert.Equal(2, filtered.Length);

        var bad = tv.Run("audit", "list", "--verdict", "nonsense", "--json");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains(bad.Envelope.Errors, e => e.Code == "invalid-verdict");
    }

    // A cited source can be retracted after the page was written, so a
    // dangling citation is a state the auditor must be told about rather than
    // a reason for the command to fail.
    [Fact]
    public void Next_ReportsRetractedAndUnresolvableCitations()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        CreatePage(tv, "Billing", "Claims.", s1);

        Assert.Equal(0, tv.Run("source", "retract", s1, "--reason", "wrong", "--json").ExitCode);

        var r = tv.Run("audit", "next", "--json");
        Assert.Equal(0, r.ExitCode);
        // Retraction flipped the page to needs-review, so it drops out of the
        // active candidate set entirely.
        Assert.False(Data(r).GetProperty("hasTarget").GetBoolean());
    }

    [Fact]
    public void Audit_IsNotPartOfLint()
    {
        using var tv = new TempVault(); Init(tv);
        var s1 = AddSource(tv, "s1.md", "source one");
        var id = CreatePage(tv, "Billing", "Claims.", s1);
        tv.Run("audit", "record", id, "--verdict", "unsupported", "--note", "unsupported claim", "--json");

        // Lint neither produces nor clears unsupported-claim findings; it just
        // leaves the existing one alone.
        var lint = tv.Run("lint", "--json");
        Assert.Equal(0, lint.ExitCode);
        var kinds = Data(lint).GetProperty("counts").EnumerateArray()
            .Select(e => e.GetProperty("kind").GetString()).ToArray();
        Assert.DoesNotContain("unsupported-claim", kinds);

        Assert.Single(((JsonElement)tv.Run("issues", "list", "--kind", "unsupported-claim", "--status", "open", "--json").Envelope.Data!)
            .EnumerateArray());
    }
}
