using System.IO;
using System.Text.Json;
using ModelContextProtocol;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #59: the persona over MCP. `read_persona` returns the persona and,
// when asked, the interview guide; `distill_persona` is the read side of a
// distillation pass (candidate pages and candidate transcripts beside the
// current persona); `record_owner_statement` appends one answer, word for
// word, to an interview transcript. `recordedVia` comes from the server's
// origin, never from an argument.
public class PersonaToolTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement CliData(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string[] CompletedSteps(JsonElement s)
        => s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Codes(JsonElement s)
        => s.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();

    // ---- read_persona -----------------------------------------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReadPersona_ReturnsThePersona_WithoutTheGuideByDefault(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_persona");

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["persona read"], CompletedSteps(s));
        var persona = s.GetProperty("data").GetProperty("persona");
        Assert.False(persona.GetProperty("exists").GetBoolean());
        Assert.Equal(0, persona.GetProperty("values").GetArrayLength());
        Assert.False(s.GetProperty("data").TryGetProperty("guide", out _));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReadPersona_IncludeGuide_AlsoReturnsTheInterviewGuide(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_persona", new Dictionary<string, object?> { ["includeGuide"] = true });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["persona read", "persona interview guide"], CompletedSteps(s));
        var guide = s.GetProperty("data").GetProperty("guide");
        Assert.Equal("shipped", guide.GetProperty("source").GetString());
        Assert.Equal("1", guide.GetProperty("version").GetString());
        Assert.Contains("word for word", guide.GetProperty("text").GetString());
    }

    // ---- distill_persona --------------------------------------------------

    // Init with the scaffolded `article` category endorsed (persona-source),
    // so summary pages citing article sources are persona candidates.
    private static void InitPersona(TempVault tv)
    {
        Init(tv);
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var text = File.ReadAllText(path);
        var anchor = "description: \"Web articles and blog posts\"";
        Assert.Contains(anchor, text);
        File.WriteAllText(path, text.Replace(anchor, anchor + "\n    persona-source: true"));
    }

    private static string SummaryPage(TempVault tv, string title)
    {
        var src = Path.Combine(tv.Path, title.ToLowerInvariant().Replace(' ', '-') + "-src.md");
        File.WriteAllText(src, "Content of " + title);
        var source = CliData(tv.Run("source", "add", src, "--category", "article", "--title", title, "--json"))
            .GetProperty("id").GetString()!;
        var r = tv.RunStdin("Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
            "--summary", title + " summary", "--sources", source, "--stdin", "--json");
        Assert.Equal(0, r.ExitCode);
        return CliData(r).GetProperty("id").GetString()!;
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task DistillPersona_ReturnsCandidatePagesAndTranscripts_WithThePersona(string transport)
    {
        using var tv = new TempVault(); InitPersona(tv);
        var page = SummaryPage(tv, "Craft one");
        var transcript = CliData(tv.Run("persona", "interview", "record", "--section", "values",
            "--question", "What matters?", "--answer", "Careful craft.", "--json")).GetProperty("transcript").GetString()!;
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("distill_persona");

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["persona distill"], CompletedSteps(s));
        var distill = s.GetProperty("data").GetProperty("distill");
        Assert.True(distill.TryGetProperty("persona", out var persona));
        Assert.False(persona.GetProperty("exists").GetBoolean());
        Assert.Equal([page], distill.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetString()!));
        var t = Assert.Single(distill.GetProperty("transcripts").EnumerateArray());
        Assert.Equal(transcript, t.GetProperty("id").GetString());
        var entry = Assert.Single(t.GetProperty("entries").EnumerateArray());
        Assert.Equal("a1", entry.GetProperty("anchor").GetString());
        Assert.Equal("Careful craft.", entry.GetProperty("answer").GetString());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task DistillPersona_IsReadOnly_AndClearsNothing(string transport)
    {
        using var tv = new TempVault(); InitPersona(tv);
        SummaryPage(tv, "Craft one");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var tool = (await mcp.Client.ListToolsAsync()).Single(t => t.Name == "distill_persona");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);

        await mcp.CallAsync("distill_persona");
        var again = await mcp.CallAsync("distill_persona");
        Assert.Equal(1, again.Structured().GetProperty("data").GetProperty("distill").GetProperty("candidates").GetArrayLength());
    }

    // ---- record_owner_statement -------------------------------------------

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static Task<ModelContextProtocol.Protocol.CallToolResult> Record(McpHarness mcp, string section, string answer,
        string? question = null, string? transcript = null)
    {
        var args = new Dictionary<string, object?> { ["section"] = section, ["answer"] = answer };
        if (question is not null) args["question"] = question;
        if (transcript is not null) args["transcript"] = transcript;
        return mcp.CallAsync("record_owner_statement", args);
    }

    private static string AuditLast(TempVault tv)
        => File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log")).Last();

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RecordOwnerStatement_StartsATranscript_ThenAppendsToIt_RecordedViaMcp(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var first = await Record(mcp, "values", "Careful craft.", question: "What do you care about most?");
        Assert.False(first.IsError, first.Text());
        var s1 = first.Structured();
        Assert.Equal(["persona interview record"], CompletedSteps(s1));
        var r1 = s1.GetProperty("data").GetProperty("record");
        var id = r1.GetProperty("transcript").GetString()!;
        Assert.Equal(Today, id);
        Assert.True(r1.GetProperty("created").GetBoolean());
        Assert.Equal("a1", r1.GetProperty("anchor").GetString());
        Assert.Equal("mcp", r1.GetProperty("recordedVia").GetString());

        var second = await Record(mcp, "tone-of-voice", "Short and plain.", question: "How do you write?", transcript: id);
        Assert.False(second.IsError, second.Text());
        var r2 = second.Structured().GetProperty("data").GetProperty("record");
        Assert.Equal(id, r2.GetProperty("transcript").GetString());
        Assert.False(r2.GetProperty("created").GetBoolean());
        Assert.Equal("a2", r2.GetProperty("anchor").GetString());

        var text = File.ReadAllText(Path.Combine(tv.Path, "persona", "interviews", id + ".md"));
        Assert.Contains("**Question**\n\n> What do you care about most?", text);
        Assert.Contains("**Answer**\n\n> Careful craft.", text);
        Assert.Contains("**Answer**\n\n> Short and plain.", text);
        Assert.Contains("- recordedVia: mcp", text);
        Assert.Contains("- transport: stdio", text);
        Assert.DoesNotContain("- recordedVia: cli", text);
        var audit = AuditLast(tv);
        Assert.Contains("| persona.interview.record |", audit);
        Assert.Contains("recordedVia=mcp", audit);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RecordOwnerStatement_KeepsTheAnswerWordForWord(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);
        // Leading dashes, an @, quotes and a second paragraph: all plain answer text.
        const string answer = "--vault=elsewhere @file \"quoted\"\n\nSecond paragraph.";

        var result = await Record(mcp, "boundaries", answer);

        Assert.False(result.IsError, result.Text());
        var id = result.Structured().GetProperty("data").GetProperty("record").GetProperty("transcript").GetString()!;
        var transcriptView = Assert.Single(CliData(tv.Run("persona", "distill", "--json")).GetProperty("transcripts").EnumerateArray());
        Assert.Equal(id, transcriptView.GetProperty("id").GetString());
        var entry = Assert.Single(transcriptView.GetProperty("entries").EnumerateArray());
        Assert.Equal(answer, entry.GetProperty("answer").GetString());
        // A dictated line has no question.
        Assert.True(!entry.TryGetProperty("question", out var q) || q.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RecordOwnerStatement_BlankQuestionAndTranscript_MeanDictatedLineInANewTranscript(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await Record(mcp, "values", "Careful craft.", question: "", transcript: "");

        Assert.False(result.IsError, result.Text());
        Assert.True(result.Structured().GetProperty("data").GetProperty("record").GetProperty("created").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RecordOwnerStatement_CliRefusals_AreToolResults(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var empty = await Record(mcp, "values", "   ");
        Assert.True(empty.IsError);
        Assert.Equal(1, empty.Structured().GetProperty("exitCode").GetInt32());
        Assert.Contains("empty-answer", Codes(empty.Structured()));

        var missing = await Record(mcp, "values", "Careful craft.", transcript: "2001-01-01");
        Assert.True(missing.IsError);
        Assert.Contains("not-found", Codes(missing.Structured()));
        Assert.False(Directory.Exists(Path.Combine(tv.Path, "persona", "interviews"))
                     && Directory.EnumerateFiles(Path.Combine(tv.Path, "persona", "interviews")).Any());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RecordOwnerStatement_SectionOutsideThePersona_IsInvalidParams(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => Record(mcp, "hobbies", "Sailing."));

        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RecordOwnerStatement_TakesNoArgumentClaimingHowItWasRecorded(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var tool = (await mcp.Client.ListToolsAsync()).Single(t => t.Name == "record_owner_statement");

        Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        var props = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(props, n => n.Contains("via", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("transport", StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("token", StringComparison.OrdinalIgnoreCase));
        var required = tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["section", "answer"], required);
    }

    // ---- the whole loop ---------------------------------------------------

    // Acceptance: a full onboarding interview over MCP, then distill and
    // propose, produces a changeset the owner can review - every step a tool
    // call, nothing done at the CLI.
    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task AFullInterviewOverMcp_ThenDistillAndPropose_QueuesAReviewableChangeset(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        // The agent reads the persona and the guide before it starts.
        var read = (await mcp.CallAsync("read_persona", new Dictionary<string, object?> { ["includeGuide"] = true })).Structured();
        Assert.Contains("word for word", read.GetProperty("data").GetProperty("guide").GetProperty("text").GetString());

        // One answer per section; the first call starts the transcript.
        (string Section, string Question, string Answer)[] interview =
        [
            ("values", "What do you care about most in your work?", "Careful craft over speed."),
            ("principles", "How do you decide when to stop polishing?", "Ship when it is correct, not when it is perfect."),
            ("tone-of-voice", "How do you write to colleagues?", "Short sentences, no jargon."),
            ("boundaries", "What will you never do?", "I never publish anything I have not read myself."),
        ];
        string? transcript = null;
        var anchors = new List<string>();
        foreach (var (section, question, answer) in interview)
        {
            var r = await Record(mcp, section, answer, question, transcript);
            Assert.False(r.IsError, r.Text());
            var data = r.Structured().GetProperty("data").GetProperty("record");
            transcript ??= data.GetProperty("transcript").GetString();
            Assert.Equal(transcript, data.GetProperty("transcript").GetString());
            anchors.Add(data.GetProperty("anchor").GetString()!);
        }
        Assert.Equal(["a1", "a2", "a3", "a4"], anchors);

        // The pass reads the transcript as a candidate.
        var distill = (await mcp.CallAsync("distill_persona")).Structured().GetProperty("data").GetProperty("distill");
        var candidate = Assert.Single(distill.GetProperty("transcripts").EnumerateArray());
        Assert.Equal(transcript, candidate.GetProperty("id").GetString());
        var entries = candidate.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(interview.Select(i => i.Answer), entries.Select(e => e.GetProperty("answer").GetString()!));

        // ... and files one changeset, each line resting on the owner's own answer.
        var changes = entries.Zip(interview, (e, i) =>
            "{\"op\":\"add\",\"section\":\"" + i.Section + "\",\"text\":" + JsonSerializer.Serialize(i.Answer) +
            ",\"evidence\":[{\"kind\":\"owner-stated\",\"path\":\"" + transcript + "\",\"anchor\":\"" +
            e.GetProperty("anchor").GetString() + "\"}]}");
        var changeset = "{\"reason\":\"onboarding interview\",\"considered\":[\"" + transcript + "\"],\"changes\":[" +
                        string.Join(",", changes) + "]}";
        var proposed = await mcp.CallAsync("propose_change", new Dictionary<string, object?> { ["kind"] = "persona", ["changeset"] = changeset });
        Assert.False(proposed.IsError, proposed.Text());
        var persona = proposed.Structured().GetProperty("data").GetProperty("persona");
        var id = persona.GetProperty("changeset").GetProperty("id").GetString()!;
        Assert.Equal("open", persona.GetProperty("changeset").GetProperty("status").GetString());
        Assert.Contains(transcript, persona.GetProperty("cleared").EnumerateArray().Select(c => c.GetString()));

        // Reviewable: it is in the queue, and nothing is applied until decided.
        var queue = (await mcp.CallAsync("review_queue")).Structured().GetProperty("data").GetProperty("review");
        var row = Assert.Single(queue.EnumerateArray(), r => r.GetProperty("kind").GetString() == "persona");
        Assert.Equal(id, row.GetProperty("changeset").GetProperty("id").GetString());
        Assert.Equal(4, row.GetProperty("changeset").GetProperty("changes").GetArrayLength());
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
        // The considered transcript is no longer a candidate.
        var after = (await mcp.CallAsync("distill_persona")).Structured().GetProperty("data").GetProperty("distill");
        Assert.Equal(0, after.GetProperty("transcripts").GetArrayLength());

        // The owner approves it, and the persona holds the owner's own words.
        var decided = await mcp.CallAsync("decide", new Dictionary<string, object?>
            { ["kind"] = "persona", ["id"] = id, ["decision"] = "approve" });
        Assert.False(decided.IsError, decided.Text());
        var values = (await mcp.CallAsync("read_persona")).Structured().GetProperty("data").GetProperty("persona");
        Assert.Equal(["Careful craft over speed."], values.GetProperty("values").EnumerateArray().Select(v => v.GetString()!));
        Assert.Equal(["Short sentences, no jargon."], values.GetProperty("toneOfVoice").EnumerateArray().Select(v => v.GetString()!));
        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);
    }
}
