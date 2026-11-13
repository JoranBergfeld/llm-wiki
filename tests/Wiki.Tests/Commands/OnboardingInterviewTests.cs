using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #53: the onboarding interview. `wiki persona interview guide` prints
// the interview guide; `wiki persona interview record` appends one question
// and answer, word for word, to an interview transcript. Transcripts are
// owner-stated evidence for persona lines. Observed only through the CLI
// seam: envelopes, exit codes, files in the vault and the audit log.
public class OnboardingInterviewTests
{
    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string Read(TempVault tv, string rel) => File.ReadAllText(Path.Combine(tv.Path, rel));

    // ---- guide ------------------------------------------------------------

    [Fact]
    public void Guide_PrintsTheShippedGuide_WithItsVersion()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.Run("persona", "interview", "guide", "--json");

        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal("shipped", data.GetProperty("source").GetString());
        Assert.Equal("1", data.GetProperty("version").GetString());
        var text = data.GetProperty("text").GetString()!;
        Assert.Contains("word for word", text);
        foreach (var heading in new[] { "## Values", "## Principles", "## Tone of voice", "## Boundaries" })
            Assert.Contains(heading, text);
    }

    [Fact]
    public void Guide_PrintsTheVaultOverride_WithItsHash_WhenPresent()
    {
        using var tv = new TempVault(); Init(tv);
        Directory.CreateDirectory(Path.Combine(tv.Path, "persona"));
        File.WriteAllText(Path.Combine(tv.Path, "persona", "interview-guide.md"), "# Our guide\n\nAsk about sailing.\n");

        var r = tv.Run("persona", "interview", "guide", "--json");

        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal("vault", data.GetProperty("source").GetString());
        Assert.Equal("persona/interview-guide.md", data.GetProperty("path").GetString());
        Assert.Equal("# Our guide\n\nAsk about sailing.\n", data.GetProperty("text").GetString());
        // sha256("# Our guide\n\nAsk about sailing.\n")
        Assert.Equal("sha256:" + Sha256("# Our guide\n\nAsk about sailing.\n"), data.GetProperty("version").GetString());
    }

    // ---- record -----------------------------------------------------------

    private static string Today => System.DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static CliResult Record(TempVault tv, string section, string? question, string answer, string? transcript = null)
    {
        var args = new System.Collections.Generic.List<string> { "persona", "interview", "record", "--section", section, "--answer", answer, "--json" };
        if (question is not null) { args.Add("--question"); args.Add(question); }
        if (transcript is not null) { args.Add("--transcript"); args.Add(transcript); }
        return tv.Run(args.ToArray());
    }

    [Fact]
    public void Record_WithoutATranscript_StartsTodaysTranscript_AndReturnsItsId()
    {
        using var tv = new TempVault(); Init(tv);

        var r = Record(tv, "values", "What do you care about most?", "Careful craft.");

        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(Today, data.GetProperty("transcript").GetString());
        Assert.Equal($"persona/interviews/{Today}.md", data.GetProperty("path").GetString());
        Assert.Equal("a1", data.GetProperty("anchor").GetString());
        Assert.True(data.GetProperty("created").GetBoolean());
        Assert.True(File.Exists(Path.Combine(tv.Path, "persona", "interviews", Today + ".md")));
    }

    [Fact]
    public void Record_WithATranscript_AppendsToIt()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(Record(tv, "values", "Q1?", "First.")).GetProperty("transcript").GetString()!;

        var r = Record(tv, "principles", "Q2?", "Second.", transcript: id);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(id, Data(r).GetProperty("transcript").GetString());
        Assert.Equal("a2", Data(r).GetProperty("anchor").GetString());
        Assert.False(Data(r).GetProperty("created").GetBoolean());
        var text = Read(tv, $"persona/interviews/{id}.md");
        Assert.Contains("> First.", text);
        Assert.Contains("> Second.", text);
        Assert.Single(Directory.GetFiles(Path.Combine(tv.Path, "persona", "interviews")));
    }

    [Fact]
    public void LaterSessionsTheSameDay_GetV2V3AndSoOn_WithNoCap()
    {
        using var tv = new TempVault(); Init(tv);

        var ids = Enumerable.Range(0, 11).Select(_ => Data(Record(tv, "values", null, "A line.")).GetProperty("transcript").GetString()).ToArray();

        Assert.Equal(Today, ids[0]);
        Assert.Equal(Today + "-v2", ids[1]);
        Assert.Equal(Today + "-v3", ids[2]);
        Assert.Equal(Today + "-v11", ids[10]);
    }

    [Fact]
    public void AnEntry_RecordsQuestionAnswerSectionGuideVersionAndRecordedVia()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(Record(tv, "tone-of-voice", "How do you write to colleagues?", "Short and plain.")).GetProperty("transcript").GetString()!;

        var text = Read(tv, $"persona/interviews/{id}.md");

        Assert.Contains("persona: candidate", text);
        Assert.Contains("## a1", text);
        Assert.Contains("- section: tone-of-voice", text);
        Assert.Contains("- guide: 1", text);
        Assert.Contains("- recordedVia: cli", text);
        Assert.Contains("**Question**\n\n> How do you write to colleagues?", text);
        Assert.Contains("**Answer**\n\n> Short and plain.", text);
    }

    [Fact]
    public void AnEntry_RecordsTheOverridesHash_WhenTheVaultHasItsOwnGuide()
    {
        using var tv = new TempVault(); Init(tv);
        Directory.CreateDirectory(Path.Combine(tv.Path, "persona"));
        File.WriteAllText(Path.Combine(tv.Path, "persona", "interview-guide.md"), "Ask about sailing.\n");

        var r = Record(tv, "values", "Why sailing?", "Wind.");

        var id = Data(r).GetProperty("transcript").GetString()!;
        Assert.Equal("sha256:" + Sha256("Ask about sailing.\n"), Data(r).GetProperty("guide").GetString());
        Assert.Contains("- guide: sha256:" + Sha256("Ask about sailing.\n"), Read(tv, $"persona/interviews/{id}.md"));
    }

    [Fact]
    public void ADictatedLine_IsARecordWithoutAQuestion()
    {
        using var tv = new TempVault(); Init(tv);

        var r = Record(tv, "boundaries", null, "Never sign anything for me.");

        Assert.Equal(0, r.ExitCode);
        var text = Read(tv, Data(r).GetProperty("path").GetString()!);
        Assert.DoesNotContain("**Question**", text);
        Assert.Contains("> Never sign anything for me.", text);
    }

    // Everything an owner might say that looks like transcript structure.
    private const string Tricky =
        "  Leading spaces, trailing spaces   \n\n## a2\n---\n> already quoted\n```\ncode\n```\n**Answer**\nUnicode: café — ünïcödé ✓\n\n\nlast line without newline";

    [Fact]
    public void ALongAnswerViaStdin_IsRecordedByteForByte()
    {
        using var tv = new TempVault(); Init(tv);
        var answer = string.Concat(Enumerable.Repeat(Tricky + "\n", 200));

        var r = tv.RunStdin(answer, "persona", "interview", "record", "--section", "values", "--question", "Tell me everything.", "--stdin", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(Sha256(answer), Data(r).GetProperty("answerHash").GetString());
        var entry = Assert.Single(Assert.Single(Data(tv.Run("persona", "distill", "--json")).GetProperty("transcripts").EnumerateArray())
            .GetProperty("entries").EnumerateArray());
        Assert.Equal(answer, entry.GetProperty("answer").GetString());
        Assert.Equal("Tell me everything.", entry.GetProperty("question").GetString());
        Assert.Equal("a1", entry.GetProperty("anchor").GetString());
        Assert.Equal(Sha256(answer), entry.GetProperty("hash").GetString());
    }

    [Fact]
    public void Record_RejectsAnUnknownSection_AnEmptyAnswer_AndAnUnknownTranscript_WritingNothing()
    {
        using var tv = new TempVault(); Init(tv);

        var badSection = Record(tv, "hobbies", "Q?", "A.");
        var empty = Record(tv, "values", "Q?", "   ");
        var unknown = Record(tv, "values", "Q?", "A.", transcript: "2001-01-01");
        var malformed = Record(tv, "values", "Q?", "A.", transcript: "../../wiki/index");

        Assert.Equal(1, badSection.ExitCode);
        Assert.Equal("invalid-persona-section", badSection.Envelope.Errors[0].Code);
        Assert.Equal(1, empty.ExitCode);
        Assert.Equal("empty-answer", empty.Envelope.Errors[0].Code);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Equal("not-found", unknown.Envelope.Errors[0].Code);
        Assert.Equal(1, malformed.ExitCode);
        Assert.Equal("invalid-transcript", malformed.Envelope.Errors[0].Code);
        Assert.False(Directory.Exists(Path.Combine(tv.Path, "persona", "interviews")));
    }

    [Fact]
    public void Record_WritesOneAuditLine_NamingTheEntryAndHowItArrived()
    {
        using var tv = new TempVault(); Init(tv);
        var r = Record(tv, "values", "Q?", "Careful craft.");
        var path = Data(r).GetProperty("path").GetString()!;

        var line = File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log")).Last();

        Assert.Contains("| persona.interview.record | " + path + "#a1 |", line);
        Assert.Contains("section=values", line);
        Assert.Contains("guide=1", line);
        Assert.Contains("recordedVia=cli", line);
        Assert.Contains("answer-hash=" + Sha256("Careful craft."), line);
    }

    // ---- not a knowledge page ----------------------------------------------

    [Fact]
    public void Transcripts_StayOutOfSearchTheIndexAndLint()
    {
        using var tv = new TempVault(); Init(tv);
        Record(tv, "values", "Q?", "Zymurgical honesty matters to me.");

        Assert.Equal(0, tv.Run("reindex", "--json").ExitCode);
        var search = tv.Run("search", "Zymurgical", "--json");
        Assert.Equal(0, search.ExitCode);
        Assert.DoesNotContain("Zymurgical", search.Stdout);
        Assert.DoesNotContain("interviews", tv.Run("index", "show", "--json").Stdout);
        Assert.DoesNotContain("interviews", Read(tv, "wiki/index.md"));
        Assert.DoesNotContain("interviews", tv.Run("page", "list", "--json").Stdout);
        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);
        Assert.DoesNotContain("interviews", tv.Run("issues", "list", "--json").Stdout);
    }

    // ---- append only --------------------------------------------------------

    [Fact]
    public void Appending_KeepsEveryExistingByte_IncludingTheOwnersHandEdits()
    {
        using var tv = new TempVault(); Init(tv);
        var r = Record(tv, "values", "Q1?", "First.");
        var full = Path.Combine(tv.Path, Data(r).GetProperty("path").GetString()!);
        File.WriteAllText(full, File.ReadAllText(full).Replace("> First.", "> First, edited by hand."));
        var before = File.ReadAllBytes(full);

        Record(tv, "values", "Q2?", "Second.", transcript: Data(r).GetProperty("transcript").GetString());

        var after = File.ReadAllBytes(full);
        Assert.True(after.Length > before.Length);
        Assert.Equal(before, after.Take(before.Length).ToArray());
    }

    // ---- owner-stated evidence ---------------------------------------------

    private static string OwnerStated(string transcript, string anchor)
        => "{\"kind\":\"owner-stated\",\"path\":\"" + transcript + "\",\"anchor\":\"" + anchor + "\"}";

    private static string Add(string section, string text, params string[] evidence)
        => "{\"op\":\"add\",\"section\":\"" + section + "\",\"text\":" + JsonSerializer.Serialize(text)
           + ",\"evidence\":[" + string.Join(",", evidence) + "]}";

    private static CliResult Propose(TempVault tv, string[] changes, params string[] considered)
        => tv.RunStdin("{\"reason\":\"interview pass\",\"considered\":[" + string.Join(",", considered.Select(c => "\"" + c + "\"")) + "]"
                       + ",\"changes\":[" + string.Join(",", changes) + "]}", "persona", "propose", "--stdin", "--json");

    private static string Accept(TempVault tv, params string[] changes)
    {
        var p = Propose(tv, changes);
        Assert.Equal(0, p.ExitCode);
        var id = Data(p).GetProperty("changeset").GetProperty("id").GetString()!;
        Assert.Equal(0, tv.Run("review", "approve", id, "--json").ExitCode);
        return id;
    }

    [Fact]
    public void ALineBackedByOneTranscriptAnswer_IsAccepted_AndPassesLint()
    {
        using var tv = new TempVault(); Init(tv);
        var t = Data(Record(tv, "tone-of-voice", "How do you write?", "Short sentences, no jargon.")).GetProperty("transcript").GetString()!;
        Record(tv, "values", "What matters?", "Careful craft.", transcript: t);

        Accept(tv, Add("tone-of-voice", "Writes in short sentences without jargon.", OwnerStated(t, "a1")),
                   Add("values", "Careful craft.", OwnerStated(t, "a2")));

        var lint = tv.Run("lint", "--json");
        Assert.Equal(0, lint.ExitCode);
        Assert.DoesNotContain("persona-evidence", tv.Run("issues", "list", "--json").Stdout);
        var accept = File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log")).Last(l => l.Contains("| persona.accept |"));
        var evidence = accept.Split(" | ").Single(f => f.StartsWith("evidence="))["evidence=".Length..];
        if (evidence.StartsWith('@')) // long values spill to a detail file
            evidence = File.ReadAllText(Path.Combine(tv.Path, "audit", "details", evidence[1..] + ".txt"));
        Assert.Contains($"persona/interviews/{t}.md#a1@" + Sha256("Short sentences, no jargon."), evidence);
        Assert.Contains($"persona/interviews/{t}.md#a2@" + Sha256("Careful craft."), evidence);
    }

    [Fact]
    public void OwnerStatedEvidence_ResolvesTheAnswer_ByTranscriptIdOrPath()
    {
        using var tv = new TempVault(); Init(tv);
        var r = Record(tv, "values", "Q?", "Careful craft.");
        var path = Data(r).GetProperty("path").GetString()!;

        var p = Propose(tv, new[] { Add("values", "Careful craft.", OwnerStated(path, "a1")) });

        Assert.Equal(0, p.ExitCode);
        var e = Assert.Single(Data(p).GetProperty("changeset").GetProperty("changes")[0].GetProperty("evidence").EnumerateArray());
        Assert.Equal("owner-stated", e.GetProperty("kind").GetString());
        Assert.Equal(path, e.GetProperty("path").GetString());
        Assert.Equal("a1", e.GetProperty("anchor").GetString());
        Assert.Equal(Sha256("Careful craft."), e.GetProperty("hash").GetString());
    }

    [Fact]
    public void OwnerStatedEvidence_NamingNoRecordedAnswer_IsRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var t = Data(Record(tv, "values", "Q?", "Careful craft.")).GetProperty("transcript").GetString()!;

        var noAnchor = Propose(tv, new[] { Add("values", "Careful craft.", OwnerStated(t, "a9")) });
        var noTranscript = Propose(tv, new[] { Add("values", "Careful craft.", OwnerStated("2001-01-01", "a1")) });

        Assert.Equal(1, noAnchor.ExitCode);
        Assert.Equal("not-found", noAnchor.Envelope.Errors[0].Code);
        Assert.Equal(1, noTranscript.ExitCode);
        Assert.Equal("not-found", noTranscript.Envelope.Errors[0].Code);
    }

    [Fact]
    public void ToneOfVoice_BackedOnlyByNotes_FailsTheEvidenceRule()
    {
        using var tv = new TempVault(); Init(tv);
        var wiki = Path.Combine(tv.Path, "wiki.yaml");
        var anchor = "description: \"Web articles and blog posts\"";
        File.WriteAllText(wiki, File.ReadAllText(wiki).Replace(anchor, anchor + "\n    persona-source: true"));
        string Page(string title)
        {
            var src = Path.Combine(tv.Path, title + "-src.md");
            File.WriteAllText(src, "Content of " + title);
            var s = tv.Run("source", "add", src, "--category", "article", "--title", title, "--json");
            var pg = tv.RunStdin("Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
                "--summary", title + " summary", "--sources", Data(s).GetProperty("id").GetString()!, "--stdin", "--json");
            return Data(pg).GetProperty("id").GetString()!;
        }
        var a = Page("one");
        var b = Page("two");

        var p = Propose(tv, new[] { Add("tone-of-voice", "Writes plainly.", "{\"page\":\"" + a + "\"}", "{\"page\":\"" + b + "\"}") });

        Assert.Equal(1, p.ExitCode);
        Assert.Equal("persona-evidence", p.Envelope.Errors[0].Code);
    }

    // ---- distillation -------------------------------------------------------

    private static string[] TranscriptIds(TempVault tv)
        => Data(tv.Run("persona", "distill", "--json")).GetProperty("transcripts").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public void Distill_ReturnsNewTranscriptsAsCandidates_AndThePassClearsTheirFlag()
    {
        using var tv = new TempVault(); Init(tv);
        var t1 = Data(Record(tv, "values", "Q?", "Careful craft.")).GetProperty("transcript").GetString()!;
        var t2 = Data(Record(tv, "boundaries", null, "Never sign for me.")).GetProperty("transcript").GetString()!;

        var distill = Data(tv.Run("persona", "distill", "--json"));
        var first = distill.GetProperty("transcripts")[0];
        Assert.Equal(new[] { t1, t2 }, TranscriptIds(tv));
        Assert.Equal($"persona/interviews/{t1}.md", first.GetProperty("path").GetString());
        var entry = Assert.Single(first.GetProperty("entries").EnumerateArray());
        Assert.Equal("values", entry.GetProperty("section").GetString());
        Assert.Equal("Q?", entry.GetProperty("question").GetString());

        // t1 cited as evidence, t2 only considered: both are cleared.
        var p = Propose(tv, new[] { Add("values", "Careful craft.", OwnerStated(t1, "a1")) }, t2);

        Assert.Equal(0, p.ExitCode);
        var cleared = Data(p).GetProperty("cleared").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(t1, cleared);
        Assert.Contains(t2, cleared);
        Assert.Empty(TranscriptIds(tv));
        Assert.DoesNotContain("persona: candidate", Read(tv, $"persona/interviews/{t1}.md"));
    }

    [Fact]
    public void ClearingTheFlag_ChangesNoAnswer_SoTheEvidenceStaysCurrent()
    {
        using var tv = new TempVault(); Init(tv);
        var t = Data(Record(tv, "values", "Q?", "Careful craft.")).GetProperty("transcript").GetString()!;
        var before = Read(tv, $"persona/interviews/{t}.md");

        Accept(tv, Add("values", "Careful craft.", OwnerStated(t, "a1")));

        Assert.Equal(before.Replace("persona: candidate\n", ""), Read(tv, $"persona/interviews/{t}.md"));
        tv.Run("lint", "--json");
        Assert.DoesNotContain("persona-stale-evidence", tv.Run("issues", "list", "--json").Stdout);
    }

    // Exit 1, not 3: exit 3 means "already done" and reads as success over
    // MCP, but here the answer was NOT recorded (amendment AT).
    [Fact]
    public void AFinishedTranscript_IsNeverAppendedTo_AndTheRefusalIsAnError()
    {
        using var tv = new TempVault(); Init(tv);
        var t = Data(Record(tv, "values", "Q?", "Careful craft.")).GetProperty("transcript").GetString()!;
        Assert.Equal(0, Propose(tv, System.Array.Empty<string>(), t).ExitCode);
        var before = File.ReadAllBytes(Path.Combine(tv.Path, "persona", "interviews", t + ".md"));

        var r = Record(tv, "values", "Q2?", "More.", transcript: t);

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("transcript-finished", r.Envelope.Errors[0].Code);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(tv.Path, "persona", "interviews", t + ".md")));
    }

    // ---- stale owner-stated evidence ---------------------------------------

    private static JsonElement[] StaleIssues(TempVault tv)
        => Data(tv.Run("issues", "list", "--status", "open", "--json")).EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString() == "persona-stale-evidence").ToArray();

    private const string Craft = "Careful craft.";
    private const string Plain = "Writes plainly.";

    private static string TwoLineInterview(TempVault tv)
    {
        var t = Data(Record(tv, "values", "What matters?", "Careful craft above all.")).GetProperty("transcript").GetString()!;
        Record(tv, "tone-of-voice", "How do you write?", "Plainly.", transcript: t);
        Accept(tv, Add("values", Craft, OwnerStated(t, "a1")), Add("tone-of-voice", Plain, OwnerStated(t, "a2")));
        tv.Run("lint", "--json");
        Assert.Empty(StaleIssues(tv));
        return t;
    }

    [Fact]
    public void EditingOneAnswer_MakesOnlyTheLinesRestingOnItStale()
    {
        using var tv = new TempVault(); Init(tv);
        var t = TwoLineInterview(tv);
        var full = Path.Combine(tv.Path, "persona", "interviews", t + ".md");
        File.WriteAllText(full, File.ReadAllText(full).Replace("> Careful craft above all.", "> Careful craft, mostly."));

        tv.Run("lint", "--json");

        var issue = Assert.Single(StaleIssues(tv));
        Assert.Equal("PERSONA.md#values: " + Craft, issue.GetProperty("subject").GetString());
        var detail = issue.GetProperty("detail").GetString()!;
        Assert.Contains($"persona/interviews/{t}.md#a1", detail);
        Assert.Contains("changed", detail);

        var recheck = Assert.Single(Data(tv.Run("persona", "distill", "--json")).GetProperty("recheck").EnumerateArray());
        var e = Assert.Single(recheck.GetProperty("evidence").EnumerateArray());
        Assert.Equal("owner-stated", e.GetProperty("kind").GetString());
        Assert.Equal("a1", e.GetProperty("anchor").GetString());
        Assert.Equal("changed", e.GetProperty("reason").GetString());
        Assert.Equal(Sha256("Careful craft above all."), e.GetProperty("recordedHash").GetString());
        Assert.Equal(Sha256("Careful craft, mostly."), e.GetProperty("currentHash").GetString());
    }

    [Fact]
    public void DeletingTheTranscript_RetractsIt_MakingAllItsLinesStale()
    {
        using var tv = new TempVault(); Init(tv);
        var t = TwoLineInterview(tv);
        File.Delete(Path.Combine(tv.Path, "persona", "interviews", t + ".md"));

        tv.Run("lint", "--json");

        var subjects = StaleIssues(tv).Select(i => i.GetProperty("subject").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "PERSONA.md#tone-of-voice: " + Plain, "PERSONA.md#values: " + Craft }, subjects);
        Assert.All(StaleIssues(tv), i => Assert.Contains("retracted", i.GetProperty("detail").GetString()));
        var reasons = Data(tv.Run("persona", "distill", "--json")).GetProperty("recheck").EnumerateArray()
            .SelectMany(l => l.GetProperty("evidence").EnumerateArray()).Select(e => e.GetProperty("reason").GetString()).Distinct();
        Assert.Equal(new[] { "retracted" }, reasons);
    }

    [Fact]
    public void ReconfirmingFromANewTranscript_RetiresTheStaleWarning()
    {
        using var tv = new TempVault(); Init(tv);
        var t = TwoLineInterview(tv);
        File.Delete(Path.Combine(tv.Path, "persona", "interviews", t + ".md"));
        tv.Run("lint", "--json");
        Assert.Equal(2, StaleIssues(tv).Length);

        var t2 = Data(Record(tv, "values", "What matters?", "Careful craft.")).GetProperty("transcript").GetString()!;
        Accept(tv, "{\"op\":\"change\",\"section\":\"values\",\"from\":\"" + Craft + "\",\"text\":\"" + Craft + "\",\"evidence\":[" + OwnerStated(t2, "a1") + "]}");
        tv.Run("lint", "--json");

        Assert.Equal("PERSONA.md#tone-of-voice: " + Plain, Assert.Single(StaleIssues(tv)).GetProperty("subject").GetString());
    }

    private static string Sha256(string s)
        => System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
