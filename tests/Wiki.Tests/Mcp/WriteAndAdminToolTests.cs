using System.IO;
using System.Text.Json;
using ModelContextProtocol;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #56: assistants change the vault over MCP. Each tool performs its
// change through the CLI (the result is what a manual run returns), the
// change is audited as arriving over the server's transport, and the
// multi-step `repair_vault` stops at the first failing step without rolling
// back the steps before it.
public class WriteAndAdminToolTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement CliData(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string CreatePage(TempVault tv, string title, string body = "Body.")
    {
        var r = tv.RunStdin(body, "page", "upsert", "--type", "entity", "--title", title, "--summary", "s", "--json");
        Assert.Equal(0, r.ExitCode);
        return CliData(r).GetProperty("id").GetString()!;
    }

    private static string AddSource(TempVault tv)
    {
        var src = Path.Combine(tv.Path, "input.md");
        File.WriteAllText(src, "content");
        var r = tv.Run("source", "add", src, "--category", "article", "--title", "T", "--json");
        Assert.Equal(0, r.ExitCode);
        return CliData(r).GetProperty("id").GetString()!;
    }

    private static string[] CompletedSteps(JsonElement s)
        => s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string LastAuditLine(TempVault tv)
        => File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log"))[^1];

    private static string Via(string transport) => transport == McpTransports.Stdio ? "via=stdio" : "via=" + transport;

    private static string PageStatus(TempVault tv, string id)
        => CliData(tv.Run("page", "show", id, "--frontmatter-only", "--json")).GetProperty("status").GetString()!;

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ToolsList_ListsTheWriteAndAdminTools_AndNeverInit(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var tools = (await mcp.Client.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in new[] { "write_page", "rename_page", "add_category", "retract_source", "repair_vault" })
        {
            Assert.True(tools.ContainsKey(name), name);
            Assert.False(tools[name].ProtocolTool.Annotations?.ReadOnlyHint ?? false, name);
        }
        Assert.True(tools["retract_source"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.DoesNotContain(tools.Keys, n => n.Contains("init", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task WritePage_CreatesThePage_WithTheBodyPassedAsAString(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Contoso");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("write_page", new Dictionary<string, object?>
        {
            ["type"] = "concept",
            ["title"] = "Widgets",
            ["summary"] = "A product",
            ["body"] = "Widgets are made by [[contoso]].\n",
            ["tags"] = "product,hardware",
        });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["page upsert"], CompletedSteps(s));
        var data = s.GetProperty("data").GetProperty("page");
        var id = data.GetProperty("id").GetString()!;
        // What the tool returned is what the CLI holds now.
        var shown = CliData(tv.Run("page", "show", id, "--json"));
        Assert.Equal("Widgets", shown.GetProperty("title").GetString());
        Assert.Equal("concept", shown.GetProperty("type").GetString());
        Assert.Contains("Widgets are made by [[contoso]].", shown.GetProperty("body").GetString());
        Assert.Equal(["widgets"], CliData(tv.Run("page", "backlinks", "contoso", "--json")).EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(" | page.upsert | ", LastAuditLine(tv));
        Assert.Contains($" | {Via(transport)} | ", LastAuditLine(tv));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task WritePage_MatchesAManualRun(string transport)
    {
        // Same request to a twin vault by hand and through the tool: the
        // result data agrees on everything but the freshly minted id.
        using var manual = new TempVault(); Init(manual);
        using var viaMcp = new TempVault(); Init(viaMcp);
        var cli = CliData(manual.RunStdin("Body text.", "page", "upsert", "--type", "entity", "--title", "Fabrikam",
            "--summary", "A company", "--stdin", "--json"));
        await using var mcp = await McpHarness.StartAsync(viaMcp, transport);

        var result = await mcp.CallAsync("write_page", new Dictionary<string, object?>
        {
            ["type"] = "entity", ["title"] = "Fabrikam", ["summary"] = "A company", ["body"] = "Body text.",
        });

        var tool = result.Structured().GetProperty("data").GetProperty("page");
        var expected = cli.EnumerateObject().Where(p => p.Name != "id").Select(p => (p.Name, p.Value.GetRawText()));
        var actual = tool.EnumerateObject().Where(p => p.Name != "id").Select(p => (p.Name, p.Value.GetRawText()));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task WritePage_WithId_UpdatesTheExistingPage(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var id = CreatePage(tv, "Contoso", "Old body.");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("write_page", new Dictionary<string, object?>
        {
            ["id"] = id, ["type"] = "entity", ["title"] = "Contoso", ["summary"] = "s", ["body"] = "New body.",
        });

        Assert.False(result.IsError, result.Text());
        var body = CliData(tv.Run("page", "show", id, "--json")).GetProperty("body").GetString();
        Assert.Contains("New body.", body);
        Assert.DoesNotContain("Old body.", body);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task WritePage_ValidationFailure_IsTheCliEnvelope(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("write_page", new Dictionary<string, object?>
        {
            ["type"] = "entity", ["title"] = "Contoso", ["summary"] = "s", ["body"] = "Links to [[nowhere]].",
        });

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.Equal(1, s.GetProperty("exitCode").GetInt32());
        Assert.Empty(CompletedSteps(s));
        Assert.Empty(CliData(tv.Run("page", "list", "--json")).EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RenamePage_RenamesAndRewritesInboundLinks(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var id = CreatePage(tv, "Contoso");
        CreatePage(tv, "Widgets", "Made by [[contoso]].");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("rename_page", new Dictionary<string, object?> { ["id"] = id, ["newSlug"] = "contoso-ltd" });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["page rename"], CompletedSteps(s));
        var data = s.GetProperty("data").GetProperty("rename");
        Assert.Equal("contoso", data.GetProperty("oldSlug").GetString());
        Assert.Equal("contoso-ltd", data.GetProperty("newSlug").GetString());
        Assert.Equal(1, data.GetProperty("linksRewritten").GetInt32());
        Assert.Contains("[[contoso-ltd]]", CliData(tv.Run("page", "show", "widgets", "--json")).GetProperty("body").GetString());
        Assert.Contains(" | page.rename | ", LastAuditLine(tv));
        Assert.Contains($" | {Via(transport)} | ", LastAuditLine(tv));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task AddCategory_MatchesAManualRun(string transport)
    {
        using var manual = new TempVault(); Init(manual);
        using var viaMcp = new TempVault(); Init(viaMcp);
        var cli = manual.Run("category", "add", "podcast", "--description", "Podcast transcripts", "--json");
        await using var mcp = await McpHarness.StartAsync(viaMcp, transport);

        var result = await mcp.CallAsync("add_category", new Dictionary<string, object?>
        {
            ["id"] = "podcast", ["description"] = "Podcast transcripts",
        });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["category add"], CompletedSteps(s));
        Assert.Equal(CliData(cli).GetRawText(), s.GetProperty("data").GetProperty("category").GetRawText());
        Assert.Equal(File.ReadAllText(Path.Combine(manual.Path, "wiki.yaml")), File.ReadAllText(Path.Combine(viaMcp.Path, "wiki.yaml")));
        Assert.Contains(" | category.add | ", LastAuditLine(viaMcp));
        Assert.Contains($" | {Via(transport)} | ", LastAuditLine(viaMcp));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RetractSource_RetractsTheSource(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var source = AddSource(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("retract_source", new Dictionary<string, object?>
        {
            ["source"] = source, ["reason"] = "wrong document",
        });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["source retract"], CompletedSteps(s));
        Assert.Equal(source, s.GetProperty("data").GetProperty("retract").GetProperty("id").GetString());
        Assert.Equal("retracted", CliData(tv.Run("source", "show", source, "--frontmatter-only", "--json")).GetProperty("status").GetString());
        Assert.Contains(" | source.retract | ", LastAuditLine(tv));
        Assert.Contains($" | {Via(transport)} | ", LastAuditLine(tv));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RetractSource_Twice_IsANonErrorExit3(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var source = AddSource(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);
        var args = new Dictionary<string, object?> { ["source"] = source, ["reason"] = "r" };
        await mcp.CallAsync("retract_source", args);

        var again = await mcp.CallAsync("retract_source", args);

        Assert.False(again.IsError);
        Assert.Equal(3, again.Structured().GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RepairVault_Reindexes_ByDefault(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        CreatePage(tv, "Contoso");
        File.Delete(Path.Combine(tv.Path, ".wiki", "idmap.json"));
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("repair_vault");

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["reindex"], CompletedSteps(s));
        Assert.True(s.GetProperty("data").TryGetProperty("reindex", out _));
        Assert.Equal(0, tv.Run("page", "show", "contoso", "--json").ExitCode);
        Assert.Contains(" | reindex | ", LastAuditLine(tv));
        Assert.Contains($" | {Via(transport)} | ", LastAuditLine(tv));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RepairVault_SetsTheStatusOfEachPage_AfterReindexing(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var a = CreatePage(tv, "Alpha");
        var b = CreatePage(tv, "Beta");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("repair_vault", new Dictionary<string, object?>
        {
            ["pages"] = $"{a},{b}", ["status"] = "archived",
        });

        Assert.False(result.IsError, result.Text());
        var s = result.Structured();
        Assert.Equal(["reindex", "page set-status", "page set-status"], CompletedSteps(s));
        var statuses = s.GetProperty("data").GetProperty("statuses");
        Assert.Equal([a, b], statuses.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal("archived", PageStatus(tv, a));
        Assert.Equal("archived", PageStatus(tv, b));
        Assert.Contains($" | {Via(transport)} | ", LastAuditLine(tv));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RepairVault_FailingMiddleStep_StopsThere_AndKeepsEarlierStepsApplied(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var a = CreatePage(tv, "Alpha");
        var c = CreatePage(tv, "Gamma");
        const string missing = "01JZZZZZZZZZZZZZZZZZZZZZZZ";
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("repair_vault", new Dictionary<string, object?>
        {
            ["reindex"] = false, ["pages"] = $"{a},{missing},{c}", ["status"] = "archived",
        });

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.Equal(["page set-status"], CompletedSteps(s));
        Assert.Equal(1, s.GetProperty("exitCode").GetInt32());
        Assert.Equal("not-found", s.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("archived", PageStatus(tv, a)); // no rollback
        Assert.Equal("active", PageStatus(tv, c));   // never ran
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task RepairVault_PagesWithoutStatus_IsAProtocolError(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var a = CreatePage(tv, "Alpha");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() =>
            mcp.CallAsync("repair_vault", new Dictionary<string, object?> { ["pages"] = a }));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }
}
