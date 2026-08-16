using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #11 part A: `wiki eval` — golden-question retrieval scoring
// (recall@k) against the human-owned eval.yaml.
public class EvalTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static void WriteEval(TempVault tv, string yaml)
        => File.WriteAllText(Path.Combine(tv.Path, "eval.yaml"), yaml, new UTF8Encoding(false));

    private static void CreatePage(TempVault tv, string type, string title, string summary, string body)
    {
        var r = tv.RunStdin(body, "page", "upsert", "--type", type, "--title", title,
            "--summary", summary, "--stdin", "--allow-dangling", "--json");
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Eval_ScoresRecallAgainstTheGoldenQuestions()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "entity", "Contoso", "Platform vendor evaluated in Q2", "Contoso ships things.");
        CreatePage(tv, "concept", "Billing engine", "How billing is metered and invoiced", "Metering and invoices.");
        CreatePage(tv, "entity", "Fabrikam", "Unrelated vendor", "Nothing to do with the question.");

        WriteEval(tv, """
            version: 1
            questions:
              - ask: "What did Contoso ship?"
                expect: contoso
              - ask: "How does the billing engine work?"
                expect: billing-engine
            """);

        var r = tv.Run("eval", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(2, Data(r).GetProperty("questions").GetInt32());
        Assert.Equal(10, Data(r).GetProperty("k").GetInt32());
        Assert.Equal(100, Data(r).GetProperty("score").GetInt32());
        Assert.Equal(2, Data(r).GetProperty("passed").GetInt32());
    }

    [Fact]
    public void Eval_ReportsMissingExpectations_AndScoresPartialRecall()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "entity", "Contoso", "Platform vendor evaluated in Q2", "Contoso ships things.");

        WriteEval(tv, """
            version: 1
            questions:
              - ask: "What did Contoso ship?"
                expect: contoso, contoso-roadmap
            """);

        var r = tv.Run("eval", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(50, Data(r).GetProperty("score").GetInt32());
        Assert.Equal(0, Data(r).GetProperty("passed").GetInt32());

        var q = Data(r).GetProperty("results").EnumerateArray().Single();
        Assert.Equal(new[] { "contoso" }, q.GetProperty("found").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "contoso-roadmap" }, q.GetProperty("missing").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // The headline failure the metric exists to catch: a page whose summary
    // stops describing what it is becomes invisible to routing even though
    // the page itself is fine.
    [Fact]
    public void Eval_CatchesSummaryRot()
    {
        using var tv = new TempVault(); Init(tv);

        // Nine decoys that all mention the term in their bodies, so the
        // routing surface is what decides who makes the top k.
        for (var i = 1; i <= 9; i++)
            CreatePage(tv, "concept", $"Decoy {i}", $"Decoy {i} about billing", "billing engine mentioned here");

        CreatePage(tv, "concept", "Metering", "How the billing engine meters usage", "Details.");

        WriteEval(tv, """
            version: 1
            questions:
              - ask: "How does the billing engine meter usage?"
                expect: metering
            """);

        // k=1: only the single best-routed page counts, and the page whose
        // TITLE+SUMMARY carry the terms wins over bodies that merely mention them.
        var sharp = tv.Run("eval", "--k", "1", "--json");
        Assert.Equal(100, Data(sharp).GetProperty("score").GetInt32());
    }

    [Fact]
    public void Eval_KIsConfigurable_AndConstrainsTheCandidateSet()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "entity", "Alpha", "alpha thing", "shared term");
        CreatePage(tv, "entity", "Beta", "beta thing", "shared term");

        WriteEval(tv, """
            version: 1
            questions:
              - ask: "shared term"
                expect: alpha, beta
            """);

        var wide = tv.Run("eval", "--k", "10", "--json");
        Assert.Equal(100, Data(wide).GetProperty("score").GetInt32());

        var narrow = tv.Run("eval", "--k", "1", "--json");
        Assert.Equal(50, Data(narrow).GetProperty("score").GetInt32());
        Assert.Single(Data(narrow).GetProperty("results").EnumerateArray().Single().GetProperty("surfaced").EnumerateArray());
    }

    // Decided explicitly: a failing eval is not "your input was rejected", so
    // it must not reuse exit 1. Reporting is exit 0; --fail-under exits 4.
    [Fact]
    public void Eval_FailUnder_ExitsFour_ButStillReports()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "entity", "Contoso", "Platform vendor", "Contoso.");

        WriteEval(tv, """
            version: 1
            questions:
              - ask: "What did Contoso ship?"
                expect: contoso, contoso-roadmap
            """);

        var withoutBar = tv.Run("eval", "--json");
        Assert.Equal(0, withoutBar.ExitCode);

        var underBar = tv.Run("eval", "--fail-under", "80", "--json");
        Assert.Equal(4, underBar.ExitCode);
        // The report is still emitted - a caller that set a bar needs to know
        // WHICH questions missed, not just that the run failed.
        Assert.True(underBar.Envelope.Ok);
        Assert.Equal(50, Data(underBar).GetProperty("score").GetInt32());

        var overBar = tv.Run("eval", "--fail-under", "40", "--json");
        Assert.Equal(0, overBar.ExitCode);
    }

    [Fact]
    public void Eval_WritesNothing()
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "entity", "Contoso", "Platform vendor", "Contoso.");
        WriteEval(tv, """
            version: 1
            questions:
              - ask: "Contoso"
                expect: contoso
            """);

        var logBefore = File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md"));
        var issuesBefore = File.Exists(Path.Combine(tv.Path, ".wiki", "issues.json"));

        tv.Run("eval", "--json");

        Assert.Equal(logBefore, File.ReadAllText(Path.Combine(tv.Path, "wiki", "log.md")));
        Assert.Equal(issuesBefore, File.Exists(Path.Combine(tv.Path, ".wiki", "issues.json")));
    }

    [Fact]
    public void Eval_MissingFile_IsRejectedWithGuidance()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.Run("eval", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "eval-file-missing");
        Assert.Contains("human-owned", r.Envelope.Errors[0].Message);
    }

    [Fact]
    public void Eval_MalformedFile_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);

        WriteEval(tv, "questions:\n  - ask: \"no version key\"\n    expect: x\n");
        var noVersion = tv.Run("eval", "--json");
        Assert.Equal(1, noVersion.ExitCode);
        Assert.Contains(noVersion.Envelope.Errors, e => e.Code == "eval-file");

        WriteEval(tv, "version: 1\nquestions:\n  - ask: \"missing expect\"\n");
        var noExpect = tv.Run("eval", "--json");
        Assert.Equal(1, noExpect.ExitCode);
        Assert.Contains(noExpect.Envelope.Errors, e => e.Code == "eval-file");

        WriteEval(tv, "version: 1\n");
        var noQuestions = tv.Run("eval", "--json");
        Assert.Equal(1, noQuestions.ExitCode);
        Assert.Contains(noQuestions.Envelope.Errors, e => e.Code == "eval-file");
    }

    [Fact]
    public void Eval_ArchivedPagesAreNotCandidates()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("Contoso body.", "page", "upsert", "--type", "entity", "--title", "Contoso",
            "--summary", "Platform vendor", "--stdin", "--json");
        var id = Data(r).GetProperty("id").GetString()!;

        WriteEval(tv, """
            version: 1
            questions:
              - ask: "Contoso vendor"
                expect: contoso
            """);

        Assert.Equal(100, Data(tv.Run("eval", "--json")).GetProperty("score").GetInt32());

        Assert.Equal(0, tv.Run("page", "set-status", id, "archived", "--json").ExitCode);
        Assert.Equal(0, Data(tv.Run("eval", "--json")).GetProperty("score").GetInt32());
    }

    [Fact]
    public void Eval_IsDeterministic()
    {
        using var tv = new TempVault(); Init(tv);
        for (var i = 1; i <= 5; i++)
            CreatePage(tv, "entity", $"Thing {i}", $"thing number {i}", "shared body term");

        WriteEval(tv, """
            version: 1
            questions:
              - ask: "shared body term"
                expect: thing-1
            """);

        var first = tv.Run("eval", "--k", "2", "--json");
        var second = tv.Run("eval", "--k", "2", "--json");
        Assert.Equal(first.Stdout, second.Stdout);
    }
}
