using System.IO;
using System.Text.Json;
using ModelContextProtocol;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #58: the review loop over MCP. `propose_change` files a category,
// schema or persona proposal; `review_queue` lists everything pending
// (pages, categories, schema, persona); `decide` approves or rejects any of
// them. A decision made over MCP records `decidedVia=mcp` next to the
// transport on its audit line - derived from the server's origin, never
// from an argument.
public class ReviewToolTests
{
    private static JsonElement CliData(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static void Init(TempVault tv, bool gated = false)
    {
        if (gated)
            tv.Run("init", tv.Path, "--name", "t", "--review-gate", "--json");
        else
            tv.Run("init", tv.Path, "--name", "t", "--json");
    }

    // Init with the scaffolded `article` category endorsed (persona-source),
    // so summary pages citing article sources can be persona evidence.
    private static void InitPersona(TempVault tv)
    {
        Init(tv);
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var text = File.ReadAllText(path);
        var anchor = "description: \"Web articles and blog posts\"";
        Assert.Contains(anchor, text);
        File.WriteAllText(path, text.Replace(anchor, anchor + "\n    persona-source: true"));
    }

    private static string AddSource(TempVault tv, string name = "input")
    {
        var src = Path.Combine(tv.Path, name + ".md");
        File.WriteAllText(src, "Content of " + name);
        var r = tv.Run("source", "add", src, "--category", "article", "--title", name, "--json");
        Assert.Equal(0, r.ExitCode);
        return CliData(r).GetProperty("id").GetString()!;
    }

    private static string SummaryPage(TempVault tv, string title)
    {
        var source = AddSource(tv, title.ToLowerInvariant().Replace(' ', '-') + "-src");
        var r = tv.RunStdin("Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
            "--summary", title + " summary", "--sources", source, "--stdin", "--json");
        Assert.Equal(0, r.ExitCode);
        return CliData(r).GetProperty("id").GetString()!;
    }

    private static string PersonaChangeset(string reason, string text, params string[] pages)
        => "{\"reason\":" + JsonSerializer.Serialize(reason) + ",\"considered\":[],\"changes\":[{\"op\":\"add\"," +
           "\"section\":\"values\",\"text\":" + JsonSerializer.Serialize(text) + ",\"evidence\":[" +
           string.Join(",", pages.Select(p => "{\"page\":\"" + p + "\"}")) + "]}]}";

    private static string[] AuditLines(TempVault tv)
        => File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log"));

    private static string? AuditField(string line, string key)
        => line.Split(" | ").FirstOrDefault(s => s.StartsWith(key + "="))?[(key.Length + 1)..];

    private static string[] CompletedSteps(JsonElement s)
        => s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Codes(JsonElement s)
        => s.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();

    private static Task<ModelContextProtocol.Protocol.CallToolResult> Propose(McpHarness mcp, Dictionary<string, object?> args)
        => mcp.CallAsync("propose_change", args);

    // ---- propose_change ---------------------------------------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ToolsList_ListsTheReviewTools(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var tools = (await mcp.Client.ListToolsAsync()).ToDictionary(t => t.Name);

        Assert.True(tools["review_queue"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["propose_change"].ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.False(tools["decide"].ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        // decide takes no argument that could claim who decided.
        var decideArgs = tools["decide"].ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name);
        Assert.DoesNotContain(decideArgs, n => n.Contains("via", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ProposeChange_Category_FilesAnOpenCategoryProposal(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var source = AddSource(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await Propose(mcp, new()
        {
            ["kind"] = "category", ["category"] = "podcast", ["description"] = "Podcast episodes",
            ["rationale"] = "no audio category", ["sources"] = source,
        });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["category propose"], CompletedSteps(s));
        var proposal = s.GetProperty("data").GetProperty("category");
        Assert.Equal("podcast", proposal.GetProperty("categoryId").GetString());
        var listed = Assert.Single(CliData(tv.Run("category", "proposals", "--status", "open", "--json")).EnumerateArray());
        Assert.Equal(proposal.GetProperty("id").GetString(), listed.GetProperty("id").GetString());
        Assert.Equal([source], listed.GetProperty("sources").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ProposeChange_Schema_FilesTheSectionTextAsAnOpenProposal(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var before = File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await Propose(mcp, new()
        {
            ["kind"] = "schema", ["section"] = "Session start", ["text"] = "1. Read PERSONA.md first.\n",
            ["rationale"] = "persona matters",
        });

        Assert.False(result.IsError, result.Text());
        var proposal = result.Structured().GetProperty("data").GetProperty("schema");
        Assert.Equal("Session start", proposal.GetProperty("section").GetString());
        Assert.Equal("open", proposal.GetProperty("status").GetString());
        var listed = Assert.Single(CliData(tv.Run("schema", "proposals", "--status", "open", "--json")).EnumerateArray());
        Assert.Contains("Read PERSONA.md first.", listed.GetProperty("newText").GetString());
        // Proposing never applies.
        Assert.Equal(before, File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md")));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ProposeChange_Persona_QueuesTheChangesetForReview(string transport)
    {
        using var tv = new TempVault(); InitPersona(tv);
        var a = SummaryPage(tv, "Craft one");
        var b = SummaryPage(tv, "Craft two");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await Propose(mcp, new()
        {
            ["kind"] = "persona", ["changeset"] = PersonaChangeset("two notes say it", "I value careful craft.", a, b),
        });

        Assert.False(result.IsError, result.Text());
        var data = result.Structured().GetProperty("data").GetProperty("persona");
        Assert.Equal("open", data.GetProperty("changeset").GetProperty("status").GetString());
        Assert.False(data.GetProperty("autoAccepted").GetBoolean());
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
        Assert.Contains(CliData(tv.Run("review", "list", "--json")).EnumerateArray(),
            r => r.GetProperty("kind").GetString() == "persona");
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ProposeChange_Persona_InvalidChangeset_IsTheCliError(string transport)
    {
        using var tv = new TempVault(); InitPersona(tv);
        var a = SummaryPage(tv, "Craft one");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        // One page is not enough evidence for a distilled line.
        var result = await Propose(mcp, new()
        {
            ["kind"] = "persona", ["changeset"] = PersonaChangeset("one note", "I value careful craft.", a),
        });

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.Equal(1, s.GetProperty("exitCode").GetInt32());
        Assert.Contains("persona-evidence", Codes(s));
        Assert.Empty(CompletedSteps(s));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ProposeChange_MissingTheArgumentAKindNeeds_IsInvalidParams(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        foreach (var args in new Dictionary<string, object?>[]
                 {
                     new() { ["kind"] = "category", ["description"] = "d" },
                     new() { ["kind"] = "category", ["category"] = "podcast" },
                     new() { ["kind"] = "schema", ["text"] = "t" },
                     new() { ["kind"] = "schema", ["section"] = "Session start" },
                     new() { ["kind"] = "persona" },
                     new() { ["kind"] = "page", ["category"] = "podcast", ["description"] = "d" },
                 })
        {
            var ex = await Assert.ThrowsAsync<McpProtocolException>(() => Propose(mcp, args));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
        Assert.Empty(CliData(tv.Run("category", "proposals", "--json")).EnumerateArray());
    }

    // ---- review_queue -----------------------------------------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReviewQueue_ReturnsEveryPendingKind(string transport)
    {
        using var tv = new TempVault(); InitPersona(tv);
        var a = SummaryPage(tv, "Craft one");
        var b = SummaryPage(tv, "Craft two");
        GateOn(tv);
        var pending = PendingPage(tv, "Contoso");
        var category = CliData(tv.Run("category", "propose", "podcast", "--description", "Podcasts", "--json"))
            .GetProperty("id").GetString();
        var schema = CliData(tv.RunStdin("1. New step.\n", "schema", "propose", "--section", "Session start", "--stdin", "--json"))
            .GetProperty("id").GetString();
        var persona = CliData(tv.RunStdin(PersonaChangeset("two notes", "I value careful craft.", a, b),
            "persona", "propose", "--stdin", "--json")).GetProperty("changeset").GetProperty("id").GetString();
        // Already decided items are not pending.
        var rejected = CliData(tv.Run("category", "propose", "radio", "--description", "Radio", "--json")).GetProperty("id").GetString()!;
        tv.Run("category", "reject", rejected, "--json");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("review_queue");

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["review list", "category proposals", "schema proposals"], CompletedSteps(s));
        var data = s.GetProperty("data");
        var review = data.GetProperty("review").EnumerateArray().ToArray();
        Assert.Contains(review, r => r.GetProperty("kind").GetString() == "page" && r.GetProperty("id").GetString() == pending);
        Assert.Contains(review, r => r.GetProperty("kind").GetString() == "persona" && r.GetProperty("changeset").GetProperty("id").GetString() == persona);
        Assert.Equal([category], data.GetProperty("categories").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal([schema], data.GetProperty("schema").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ReviewQueue_EmptyVault_ReturnsEmptyLists(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var data = (await mcp.CallAsync("review_queue")).Structured().GetProperty("data");

        Assert.Equal(0, data.GetProperty("review").GetArrayLength());
        Assert.Equal(0, data.GetProperty("categories").GetArrayLength());
        Assert.Equal(0, data.GetProperty("schema").GetArrayLength());
    }

    // ---- decide -----------------------------------------------------------

    private static void GateOn(TempVault tv)
    {
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var text = File.ReadAllText(path);
        Assert.Contains("review_gate: false", text);
        File.WriteAllText(path, text.Replace("review_gate: false", "review_gate: true"));
    }

    private static string PendingPage(TempVault tv, string title)
    {
        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", title, "--summary", "s", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("pending-review", CliData(r).GetProperty("status").GetString());
        return CliData(r).GetProperty("id").GetString()!;
    }

    private static string PageStatus(TempVault tv, string id)
        => CliData(tv.Run("page", "show", id, "--frontmatter-only", "--json")).GetProperty("status").GetString()!;

    private static Task<ModelContextProtocol.Protocol.CallToolResult> Decide(McpHarness mcp, string kind, string id, string decision, string? reason = null)
    {
        var args = new Dictionary<string, object?> { ["kind"] = kind, ["id"] = id, ["decision"] = decision };
        if (reason is not null)
            args["reason"] = reason;
        return mcp.CallAsync("decide", args);
    }

    private static void AssertDecidedOverMcp(TempVault tv, string action, string transport)
    {
        var line = AuditLines(tv)[^1];
        Assert.Equal(action, line.Split(" | ")[1]);
        Assert.Equal("mcp", AuditField(line, "decidedVia"));
        Assert.Equal(transport, AuditField(line, "via"));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Decide_Page_ApproveAndReject(string transport)
    {
        using var tv = new TempVault(); Init(tv, gated: true);
        var approved = PendingPage(tv, "Contoso");
        var rejected = PendingPage(tv, "Fabrikam");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var r1 = await Decide(mcp, "page", approved, "approve");
        Assert.False(r1.IsError, r1.Text());
        Assert.Equal(["review approve"], CompletedSteps(r1.Structured()));
        Assert.Equal("active", r1.Structured().GetProperty("data").GetProperty("decision").GetProperty("status").GetString());
        Assert.Equal("active", PageStatus(tv, approved));
        AssertDecidedOverMcp(tv, "review.approve", transport);

        var r2 = await Decide(mcp, "page", rejected, "reject", "off topic");
        Assert.False(r2.IsError, r2.Text());
        Assert.Equal("archived", PageStatus(tv, rejected));
        AssertDecidedOverMcp(tv, "review.reject", transport);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Decide_Category_ApproveAddsIt_RejectLeavesWikiYaml(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var approve = CliData(tv.Run("category", "propose", "podcast", "--description", "Podcasts", "--json")).GetProperty("id").GetString()!;
        var reject = CliData(tv.Run("category", "propose", "radio", "--description", "Radio", "--json")).GetProperty("id").GetString()!;
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var r1 = await Decide(mcp, "category", approve, "approve");
        Assert.False(r1.IsError, r1.Text());
        Assert.Equal(["category approve"], CompletedSteps(r1.Structured()));
        Assert.Contains(CliData(tv.Run("category", "list", "--json")).EnumerateArray(), c => c.GetProperty("id").GetString() == "podcast");
        AssertDecidedOverMcp(tv, "category.approve", transport);

        var r2 = await Decide(mcp, "category", reject, "reject", "too narrow");
        Assert.False(r2.IsError, r2.Text());
        Assert.Equal("rejected", r2.Structured().GetProperty("data").GetProperty("decision").GetProperty("status").GetString());
        Assert.DoesNotContain(CliData(tv.Run("category", "list", "--json")).EnumerateArray(), c => c.GetProperty("id").GetString() == "radio");
        AssertDecidedOverMcp(tv, "category.reject", transport);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Decide_Schema_ApproveAppliesTheSection_RejectDoesNot(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var approve = CliData(tv.RunStdin("1. Approved step.\n", "schema", "propose", "--section", "Session start", "--stdin", "--json")).GetProperty("id").GetString()!;
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var r1 = await Decide(mcp, "schema", approve, "approve");
        Assert.False(r1.IsError, r1.Text());
        Assert.Equal(["schema approve"], CompletedSteps(r1.Structured()));
        Assert.Contains("1. Approved step.", File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md")));
        AssertDecidedOverMcp(tv, "schema.approve", transport);

        var reject = CliData(tv.RunStdin("1. Rejected step.\n", "schema", "propose", "--section", "Ingest", "--stdin", "--json")).GetProperty("id").GetString()!;
        var r2 = await Decide(mcp, "schema", reject, "reject", "no");
        Assert.False(r2.IsError, r2.Text());
        Assert.DoesNotContain("1. Rejected step.", File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md")));
        AssertDecidedOverMcp(tv, "schema.reject", transport);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Decide_Persona_ApproveWritesPersona_AndRecordsDecidedViaMcp(string transport)
    {
        using var tv = new TempVault(); InitPersona(tv);
        var a = SummaryPage(tv, "Craft one");
        var b = SummaryPage(tv, "Craft two");
        var id = CliData(tv.RunStdin(PersonaChangeset("two notes", "I value careful craft.", a, b),
            "persona", "propose", "--stdin", "--json")).GetProperty("changeset").GetProperty("id").GetString()!;
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await Decide(mcp, "persona", id, "approve");

        Assert.False(result.IsError, result.Text());
        Assert.Equal(["review approve"], CompletedSteps(result.Structured()));
        Assert.Contains("I value careful craft.", File.ReadAllText(Path.Combine(tv.Path, "PERSONA.md")));
        var line = AuditLines(tv)[^1];
        Assert.Equal(id, AuditField(line, "changeset"));
        AssertDecidedOverMcp(tv, "persona.accept", transport);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Decide_Persona_RejectRequiresAReason(string transport)
    {
        using var tv = new TempVault(); InitPersona(tv);
        var a = SummaryPage(tv, "Craft one");
        var b = SummaryPage(tv, "Craft two");
        var id = CliData(tv.RunStdin(PersonaChangeset("two notes", "I value careful craft.", a, b),
            "persona", "propose", "--stdin", "--json")).GetProperty("changeset").GetProperty("id").GetString()!;
        await using var mcp = await McpHarness.StartAsync(tv, transport);
        var linesBefore = AuditLines(tv).Length;

        var without = await Decide(mcp, "persona", id, "reject");
        Assert.True(without.IsError);
        Assert.Contains("reason-required", Codes(without.Structured()));
        Assert.Equal(linesBefore, AuditLines(tv).Length);

        var with = await Decide(mcp, "persona", id, "reject", "not me");
        Assert.False(with.IsError, with.Text());
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
        AssertDecidedOverMcp(tv, "persona.reject", transport);
        Assert.Equal("not me", AuditField(AuditLines(tv)[^1], "reason"));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Decide_AlreadyDecided_IsExit3_NotAnError(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var id = CliData(tv.Run("category", "propose", "podcast", "--description", "Podcasts", "--json")).GetProperty("id").GetString()!;
        tv.Run("category", "approve", id, "--json");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await Decide(mcp, "category", id, "approve");

        Assert.False(result.IsError);
        Assert.Equal(3, result.Structured().GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Decide_UnknownKindOrDecision_IsInvalidParams(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        foreach (var (kind, decision) in new[] { ("source", "approve"), ("page", "defer") })
        {
            var ex = await Assert.ThrowsAsync<McpProtocolException>(() => Decide(mcp, kind, "01J0000000000000000000000", decision));
            Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        }
    }

    // ---- the CLI records who decided too ----------------------------------

    [Fact]
    public void CliDecisions_RecordDecidedViaCli()
    {
        using var tv = new TempVault(); Init(tv, gated: true);
        var page = PendingPage(tv, "Contoso");
        var category = CliData(tv.Run("category", "propose", "podcast", "--description", "Podcasts", "--json")).GetProperty("id").GetString()!;
        var schema = CliData(tv.RunStdin("1. New step.\n", "schema", "propose", "--section", "Session start", "--stdin", "--json")).GetProperty("id").GetString()!;

        foreach (var (argv, action) in new[]
                 {
                     (new[] { "review", "approve", page, "--json" }, "review.approve"),
                     (new[] { "category", "reject", category, "--note", "n", "--json" }, "category.reject"),
                     (new[] { "schema", "approve", schema, "--json" }, "schema.approve"),
                 })
        {
            Assert.Equal(0, tv.Run(argv).ExitCode);
            var line = AuditLines(tv)[^1];
            Assert.Equal(action, line.Split(" | ")[1]);
            Assert.Equal("cli", AuditField(line, "decidedVia"));
            Assert.Equal("cli", AuditField(line, "via"));
        }
    }
}
