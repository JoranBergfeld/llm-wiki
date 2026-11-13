using System.IO;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #55: `browse_wiki`, `check_health`, `work_issues` and `audit_page`
// are listed and return the underlying CLI results (each step's data under
// its key); `check_health` cannot pass --fail-under; CLI failures surface per
// the error contract. A selector argument (view / action) picks the CLI
// command; an argument that selector needs but did not get is a protocol
// error, like any other schema failure.
public class BrowseAndMaintainToolTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string Id(CliResult r)
    {
        Assert.Equal(0, r.ExitCode);
        return CliData(r).GetProperty("id").GetString()!;
    }

    private static JsonElement CliData(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string AddSource(TempVault tv, string name = "input.md")
    {
        var src = Path.Combine(tv.Path, name);
        File.WriteAllText(src, "source content of " + name);
        return Id(tv.Run("source", "add", src, "--category", "article", "--title", name, "--json"));
    }

    private static string Page(TempVault tv, string type, string title, string body, string? sources = null)
    {
        var args = new List<string> { "page", "upsert", "--type", type, "--title", title, "--summary", title + " summary", "--allow-dangling", "--json" };
        if (sources is not null) { args.Add("--sources"); args.Add(sources); }
        return Id(tv.RunStdin(body, [.. args]));
    }

    private static string[] Steps(CallToolResult r)
        => r.Structured().GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static JsonElement Data(CallToolResult r, string key)
    {
        Assert.False(r.IsError, r.Text());
        var s = r.Structured();
        Assert.True(s.GetProperty("ok").GetBoolean());
        Assert.Equal(0, s.GetProperty("exitCode").GetInt32());
        return s.GetProperty("data").GetProperty(key);
    }

    private static async Task AssertInvalidParams(McpHarness mcp, string tool, Dictionary<string, object?> args)
    {
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync(tool, args));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    // -------------------- listing --------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Tools_AreListed_WithTheirSelectorsAdvertised(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var tools = (await mcp.Client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        string[] Enum(string tool, string prop) => tools[tool].InputSchema.GetProperty("properties").GetProperty(prop)
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["pages", "sources", "source", "categories"], Enum("browse_wiki", "view"));
        Assert.Equal(["list", "show", "resolve"], Enum("work_issues", "action"));
        Assert.Equal(["next", "record", "list"], Enum("audit_page", "action"));
        Assert.Contains("check_health", tools.Keys);

        Assert.True(tools["browse_wiki"].Annotations?.ReadOnlyHint);
        Assert.False(tools["work_issues"].Annotations?.DestructiveHint);
        Assert.False(tools["audit_page"].Annotations?.DestructiveHint);
    }

    // -------------------- browse_wiki --------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task BrowseWiki_DefaultsToThePageList(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        Page(tv, "entity", "Contoso", "Contoso body.");
        Page(tv, "concept", "Widgets", "Made by [[contoso]].");
        var expected = CliData(tv.Run("page", "list", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("browse_wiki");

        Assert.Equal(expected.GetRawText(), Data(result, "pages").GetRawText());
        Assert.Equal(["page list"], Steps(result));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task BrowseWiki_Pages_PassesFiltersThrough(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        Page(tv, "entity", "Contoso", "Contoso body.");
        Page(tv, "concept", "Widgets", "Made by [[contoso]].");
        var expected = CliData(tv.Run("page", "list", "--type", "concept", "--status", "active", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("browse_wiki", new Dictionary<string, object?>
        {
            ["view"] = "pages", ["type"] = "concept", ["status"] = "active",
        });

        var pages = Data(result, "pages");
        Assert.Equal(expected.GetRawText(), pages.GetRawText());
        Assert.Equal(1, pages.GetArrayLength());

        var orphans = await mcp.CallAsync("browse_wiki", new Dictionary<string, object?> { ["orphans"] = true });
        Assert.Equal(CliData(tv.Run("page", "list", "--orphans", "--json")).GetRawText(), Data(orphans, "pages").GetRawText());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task BrowseWiki_Sources_ReturnsTheSourceList(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        AddSource(tv, "a.md");
        AddSource(tv, "b.md");
        var expected = CliData(tv.Run("source", "list", "--status", "active", "--category", "article", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("browse_wiki", new Dictionary<string, object?>
        {
            ["view"] = "sources", ["status"] = "active", ["category"] = "article",
        });

        Assert.Equal(expected.GetRawText(), Data(result, "sources").GetRawText());
        Assert.Equal(2, expected.GetArrayLength());
        Assert.Equal(["source list"], Steps(result));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task BrowseWiki_Source_ReturnsTheSourceAndItsImpact(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var source = AddSource(tv);
        Page(tv, "summary", "Input summary", "Summarises it.", source);
        var show = CliData(tv.Run("source", "show", source, "--frontmatter-only", "--json"));
        var impact = CliData(tv.Run("source", "impact", source, "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("browse_wiki", new Dictionary<string, object?>
        {
            ["view"] = "source", ["source"] = source, ["frontmatterOnly"] = true,
        });

        Assert.Equal(show.GetRawText(), Data(result, "source").GetRawText());
        Assert.Equal(impact.GetRawText(), Data(result, "impact").GetRawText());
        Assert.Equal(["source show", "source impact"], Steps(result));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task BrowseWiki_Categories_ReturnsTheCategoryList(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var expected = CliData(tv.Run("category", "list", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("browse_wiki", new Dictionary<string, object?> { ["view"] = "categories" });

        Assert.Equal(expected.GetRawText(), Data(result, "categories").GetRawText());
        Assert.Equal(["category list"], Steps(result));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task BrowseWiki_UnknownSource_IsAnErrorResult_BeforeTheImpactStep(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("browse_wiki", new Dictionary<string, object?> { ["view"] = "source", ["source"] = "01NOPE" });

        Assert.True(result.IsError);
        Assert.Equal(1, result.Structured().GetProperty("exitCode").GetInt32());
        Assert.Empty(Steps(result));
    }

    // -------------------- check_health --------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task CheckHealth_RunsLintAndLinksCheck_ByDefault(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        Page(tv, "entity", "Contoso", "See https://example.com/contoso for more.");
        var links = CliData(tv.Run("links", "check", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("check_health");

        Assert.Equal(["lint", "links check"], Steps(result));
        Assert.Equal(JsonValueKind.Object, Data(result, "lint").ValueKind);
        Assert.Equal(links.GetRawText(), Data(result, "links").GetRawText());
        Assert.False(result.Structured().GetProperty("data").TryGetProperty("eval", out _));
        // lint ran for real: it records its run.
        Assert.True(File.Exists(Path.Combine(tv.Path, ".wiki", "lint.json")));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task CheckHealth_FixLinks_RepairsARenameDrift(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        Page(tv, "entity", "Bravo", "Body.");
        Page(tv, "entity", "Alpha", "See [[bravo]] for more.");
        var entities = Path.Combine(tv.Path, "wiki", "entities");
        File.Move(Path.Combine(entities, "bravo.md"), Path.Combine(entities, "charlie.md"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("check_health", new Dictionary<string, object?> { ["fixLinks"] = true, ["links"] = false });

        Assert.Equal(["lint"], Steps(result));
        Assert.Equal(1, Data(result, "lint").GetProperty("fixLinksBodiesRewritten").GetInt32());
        Assert.Contains("[[charlie]]", File.ReadAllText(Path.Combine(entities, "alpha.md")));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task CheckHealth_WithEval_ScoresTheGoldenQuestions(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        Page(tv, "entity", "Contoso", "Contoso ships things.");
        File.WriteAllText(Path.Combine(tv.Path, "eval.yaml"), """
            version: 1
            questions:
              - ask: "What did Contoso ship?"
                expect: contoso, contoso-roadmap
            """);
        var expected = CliData(tv.Run("eval", "--k", "3", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("check_health", new Dictionary<string, object?> { ["eval"] = true, ["k"] = 3 });

        Assert.Equal(["lint", "links check", "eval"], Steps(result));
        // A score under any bar is still a plain success: there is no --fail-under, so no exit 4.
        Assert.Equal(expected.GetRawText(), Data(result, "eval").GetRawText());
        Assert.Equal(50, Data(result, "eval").GetProperty("score").GetInt32());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task CheckHealth_CannotPassFailUnder(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var tool = (await mcp.Client.ListToolsAsync()).Single(t => t.Name == "check_health");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name);
        Assert.DoesNotContain(properties, p => p.Contains("fail", StringComparison.OrdinalIgnoreCase));

        await AssertInvalidParams(mcp, "check_health", new() { ["eval"] = true, ["failUnder"] = 90 });
        await AssertInvalidParams(mcp, "check_health", new() { ["eval"] = true, ["fail-under"] = 90 });
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task CheckHealth_EvalWithoutEvalFile_StopsThereAsAnErrorResult(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("check_health", new Dictionary<string, object?> { ["eval"] = true });

        Assert.True(result.IsError);
        Assert.Equal(1, result.Structured().GetProperty("exitCode").GetInt32());
        Assert.Equal(["lint", "links check"], Steps(result));
        Assert.StartsWith("eval-file-missing: ", result.Text());
    }

    // -------------------- work_issues --------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task WorkIssues_ListsShowsAndResolves(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        Page(tv, "entity", "Contoso", "Lonely page.");
        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);
        var listed = CliData(tv.Run("issues", "list", "--kind", "orphan", "--status", "open", "--json"));
        Assert.Equal(1, listed.GetArrayLength());
        var issue = listed[0].GetProperty("id").GetString()!;
        var shown = CliData(tv.Run("issues", "show", issue, "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var list = await mcp.CallAsync("work_issues", new Dictionary<string, object?> { ["kind"] = "orphan", ["status"] = "open" });
        Assert.Equal(listed.GetRawText(), Data(list, "issues").GetRawText());
        Assert.Equal(["issues list"], Steps(list));

        var show = await mcp.CallAsync("work_issues", new Dictionary<string, object?> { ["action"] = "show", ["issue"] = issue });
        Assert.Equal(shown.GetRawText(), Data(show, "issue").GetRawText());
        Assert.Equal(["issues show"], Steps(show));

        var resolve = await mcp.CallAsync("work_issues", new Dictionary<string, object?>
        {
            ["action"] = "resolve", ["issue"] = issue, ["note"] = "linked from the overview",
        });
        Assert.Equal("resolved", Data(resolve, "issue").GetProperty("status").GetString());
        Assert.Equal(["issues resolve"], Steps(resolve));
        Assert.Equal("resolved", CliData(tv.Run("issues", "show", issue, "--json")).GetProperty("status").GetString());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task WorkIssues_CliFailures_SurfacePerTheErrorContract(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var badStatus = await mcp.CallAsync("work_issues", new Dictionary<string, object?> { ["status"] = "opne" });
        Assert.True(badStatus.IsError);
        Assert.Equal(1, badStatus.Structured().GetProperty("exitCode").GetInt32());
        Assert.StartsWith("invalid-issue-status: ", badStatus.Text());

        var missing = await mcp.CallAsync("work_issues", new Dictionary<string, object?> { ["action"] = "show", ["issue"] = "01NOPE" });
        Assert.True(missing.IsError);
        Assert.Equal(1, missing.Structured().GetProperty("exitCode").GetInt32());
    }

    // -------------------- audit_page --------------------

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task AuditPage_SelectsRecordsAndLists(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var source = AddSource(tv);
        var page = Page(tv, "concept", "Billing", "Claims about billing.", source);
        var next = CliData(tv.Run("audit", "next", "--json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var selected = await mcp.CallAsync("audit_page");
        Assert.Equal(next.GetRawText(), Data(selected, "audit").GetRawText());
        Assert.Equal(page, Data(selected, "audit").GetProperty("pageId").GetString());
        Assert.Equal(["audit next"], Steps(selected));

        var recorded = await mcp.CallAsync("audit_page", new Dictionary<string, object?>
        {
            ["action"] = "record", ["page"] = page, ["verdict"] = "unsupported", ["note"] = "claim X is not in the source",
        });
        Assert.Equal(JsonValueKind.Object, Data(recorded, "audit").ValueKind);
        Assert.Equal(["audit record"], Steps(recorded));

        var expected = CliData(tv.Run("audit", "list", "--verdict", "unsupported", "--json"));
        var listed = await mcp.CallAsync("audit_page", new Dictionary<string, object?> { ["action"] = "list", ["verdict"] = "unsupported" });
        Assert.Equal(expected.GetRawText(), Data(listed, "audit").GetRawText());
        Assert.Equal(1, expected.GetArrayLength());
        Assert.Equal(["audit list"], Steps(listed));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task AuditPage_UnsupportedWithoutNote_IsAnErrorResult(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var source = AddSource(tv);
        var page = Page(tv, "concept", "Billing", "Claims about billing.", source);
        var cli = tv.Run("audit", "record", page, "--verdict", "unsupported", "--json");
        Assert.Equal(1, cli.ExitCode);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("audit_page", new Dictionary<string, object?>
        {
            ["action"] = "record", ["page"] = page, ["verdict"] = "unsupported",
        });

        Assert.True(result.IsError);
        Assert.Equal(1, result.Structured().GetProperty("exitCode").GetInt32());
        Assert.Equal(cli.Envelope.Errors[0].Code, result.Structured().GetProperty("errors")[0].GetProperty("code").GetString());
    }

    // -------------------- schema failures --------------------

    public static TheoryData<string, string, Dictionary<string, object?>> InvalidArguments()
    {
        var data = new TheoryData<string, string, Dictionary<string, object?>>();
        foreach (var transport in McpTransports.Names)
        {
            data.Add(transport, "browse_wiki", new() { ["view"] = "everything" });                        // not in the enum
            data.Add(transport, "browse_wiki", new() { ["view"] = "source" });                            // the view needs `source`
            data.Add(transport, "browse_wiki", new() { ["orphans"] = "yes" });                            // wrong type
            data.Add(transport, "check_health", new() { ["k"] = "ten" });                                 // wrong type
            data.Add(transport, "work_issues", new() { ["action"] = "delete" });                          // not in the enum
            data.Add(transport, "work_issues", new() { ["action"] = "show" });                            // needs `issue`
            data.Add(transport, "work_issues", new() { ["action"] = "resolve" });                         // needs `issue`
            data.Add(transport, "audit_page", new() { ["action"] = "record", ["verdict"] = "supported" }); // needs `page`
            data.Add(transport, "audit_page", new() { ["action"] = "record", ["page"] = "x" });           // needs `verdict`
            data.Add(transport, "audit_page", new() { ["vault"] = "/elsewhere" });                        // undeclared
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task SchemaInvalidArguments_AreAProtocolError(string transport, string tool, Dictionary<string, object?> args)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        await AssertInvalidParams(mcp, tool, args);
    }
}
