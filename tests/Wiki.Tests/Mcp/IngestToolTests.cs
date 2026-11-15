using System.IO;
using System.Text;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #57: assistants ingest over MCP. `ingest_source` lands an upload in
// the server's inbox (`<inboxRoot>/<category>/<filename>`, inbox root fixed
// at startup, outside the vault), registers it with `source scan` and hands
// back each file's outcome, the `ingest resume` plan and any category
// proposal it filed. `continue_ingest` drives the ledger to `linted`.
public class IngestToolTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static JsonElement CliData(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string[] Codes(JsonElement s)
        => s.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();

    // An inbox root outside the vault, removed afterwards.
    private sealed class TempInbox : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wiki-inbox-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static Task<McpHarness> Start(TempVault tv, string transport, TempInbox inbox, params string[] more)
        => McpHarness.StartAsync(tv, transport, null, ["--inbox-root", inbox.Path, .. more]);

    private static Task<ModelContextProtocol.Protocol.CallToolResult> Ingest(McpHarness mcp, string category, string filename, string content,
        string? categoryDescription = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category,
            ["filename"] = filename,
            ["content"] = B64(content),
        };
        if (categoryDescription is not null)
            args["categoryDescription"] = categoryDescription;
        return mcp.CallAsync("ingest_source", args);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_LandsTheUploadInTheInbox_RegistersIt_AndReturnsThePlan(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox);

        var result = await Ingest(mcp, "article", "widgets.md", "# Widgets\n\nAll about widgets.\n");

        Assert.False(result.IsError, result.Text());
        var data = result.Structured().GetProperty("data");

        // Landed at <inboxRoot>/<category>/<filename>, byte for byte.
        var landed = Path.Combine(inbox.Path, "article", "widgets.md");
        Assert.Equal("# Widgets\n\nAll about widgets.\n", File.ReadAllText(landed));

        // Registered by the scan: hashed, in raw/, in the ledger.
        var upload = data.GetProperty("upload");
        Assert.Equal("registered", upload.GetProperty("outcome").GetString());
        var sourceId = upload.GetProperty("sourceId").GetString()!;
        Assert.Equal(1, data.GetProperty("scan").GetProperty("registered").GetInt32());
        var listed = CliData(tv.Run("source", "list", "--json"));
        Assert.Contains(listed.EnumerateArray(), s => s.GetProperty("id").GetString() == sourceId);

        // The deterministic resume plan for the new source.
        var plan = Assert.Single(data.GetProperty("plans").EnumerateArray());
        Assert.Equal(sourceId, plan.GetProperty("sourceId").GetString());
        Assert.Equal("registered", plan.GetProperty("current").GetString());
        Assert.Contains("summarized", plan.GetProperty("remainingStates").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("categoryProposal", data.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_SameContentAgain_IsReportedAsADuplicate(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox);

        var first = await Ingest(mcp, "article", "a.md", "Same words.\n");
        var firstId = first.Structured().GetProperty("data").GetProperty("upload").GetProperty("sourceId").GetString();

        // Same content under another name: deduplicated by hash.
        var again = await Ingest(mcp, "article", "b.md", "Same words.\n");

        Assert.False(again.IsError, again.Text());
        var data = again.Structured().GetProperty("data");
        var upload = data.GetProperty("upload");
        Assert.Equal("skipped-duplicate", upload.GetProperty("outcome").GetString());
        Assert.Equal(firstId, upload.GetProperty("sourceId").GetString());
        Assert.Equal(0, data.GetProperty("scan").GetProperty("registered").GetInt32());
        Assert.Single(CliData(tv.Run("source", "list", "--json")).EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_UnknownCategoryWithADescription_FilesACategoryProposal(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox);

        var result = await Ingest(mcp, "podcast", "ep1.md", "Episode one transcript.\n", categoryDescription: "Podcast transcripts");

        Assert.False(result.IsError, result.Text());
        var data = result.Structured().GetProperty("data");
        var proposal = data.GetProperty("categoryProposal");
        Assert.Equal("podcast", proposal.GetProperty("categoryId").GetString());
        var proposals = CliData(tv.Run("category", "proposals", "--json"));
        Assert.Contains(proposals.EnumerateArray(), p => p.GetProperty("categoryId").GetString() == "podcast");

        // Nothing registered until the category exists; the upload waits in the inbox.
        Assert.DoesNotContain("scan", data.EnumerateObject().Select(p => p.Name));
        Assert.Empty(CliData(tv.Run("source", "list", "--json")).EnumerateArray());
        Assert.True(File.Exists(Path.Combine(inbox.Path, "podcast", "ep1.md")));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_UnknownCategoryWithoutADescription_FailsWithTheScansError(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox);

        var result = await Ingest(mcp, "podcast", "ep1.md", "Episode one.\n");

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.Equal(1, s.GetProperty("exitCode").GetInt32());
        Assert.Contains("unknown-category", Codes(s));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_TheInboxRootCannotBeChangedByAnyArgument(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox);

        // No argument names a directory.
        var tool = (await mcp.Client.ListToolsAsync()).Single(t => t.Name == "ingest_source");
        var names = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["category", "filename", "content", "categoryDescription", "categoryRationale"], names);

        // A filename or category that would leave <inboxRoot>/<category> is refused before anything lands.
        var escape = Path.Combine(Path.GetTempPath(), "wiki-escape-" + Guid.NewGuid().ToString("N"));
        foreach (var (category, filename, code) in new[]
        {
            ("article", "../../escape.md", "invalid-filename"),
            ("article", escape + ".md", "invalid-filename"),
            ("article", "sub/escape.md", "invalid-filename"),
            ("article", "..", "invalid-filename"),
            ("article", ".hidden.md", "invalid-filename"),
            ("../escape", "x.md", "invalid-category-id"),
            ("Article", "x.md", "invalid-category-id"),
        })
        {
            var result = await Ingest(mcp, category, filename, "escape attempt\n");
            Assert.True(result.IsError, $"{category}/{filename}");
            var s = result.Structured();
            Assert.Equal(1, s.GetProperty("exitCode").GetInt32());
            Assert.Contains(code, Codes(s));
            Assert.Empty(s.GetProperty("completedSteps").EnumerateArray());
        }

        Assert.False(File.Exists(escape + ".md"));
        Assert.False(Directory.Exists(inbox.Path) && Directory.EnumerateFileSystemEntries(inbox.Path, "*", SearchOption.AllDirectories).Any());
        Assert.Empty(CliData(tv.Run("source", "list", "--json")).EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_ContentThatIsNotBase64_IsRefused(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox);

        var result = await mcp.CallAsync("ingest_source", new Dictionary<string, object?>
        {
            ["category"] = "article", ["filename"] = "x.md", ["content"] = "not base64 at all!",
        });

        Assert.True(result.IsError);
        Assert.Contains("invalid-upload", Codes(result.Structured()));
        Assert.False(Directory.Exists(Path.Combine(inbox.Path, "article")));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_LargerThanTheServersUploadLimit_IsRefused(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox, "--max-upload-bytes", "10");

        var atLimit = await Ingest(mcp, "article", "ok.md", "0123456789");
        Assert.False(atLimit.IsError, atLimit.Text());

        var result = await Ingest(mcp, "article", "big.md", "0123456789A");

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.Equal(1, s.GetProperty("exitCode").GetInt32());
        Assert.Contains("upload-too-large", Codes(s));
        Assert.False(File.Exists(Path.Combine(inbox.Path, "article", "big.md")));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task IngestSource_WithoutAConfiguredInbox_IsRefused(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await Ingest(mcp, "article", "x.md", "text\n");

        Assert.True(result.IsError);
        Assert.Contains("inbox-not-configured", Codes(result.Structured()));
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Server_RefusesToStart_WithAnInboxRootInsideTheVault(string transport)
    {
        using var tv = new TempVault(); Init(tv);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpHarness.StartAsync(tv, transport, null, "--inbox-root", Path.Combine(tv.Path, "inbox"), "--json"));

        Assert.Contains("server exited 1", ex.Message);
        Assert.Contains("\"inbox-in-vault\"", ex.Message);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ContinueIngest_AdvancesAnIngestToCompletion(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        using var inbox = new TempInbox();
        await using var mcp = await Start(tv, transport, inbox);

        var ingested = await Ingest(mcp, "article", "widgets.md", "Widgets are small.\n");
        var sourceId = ingested.Structured().GetProperty("data").GetProperty("upload").GetProperty("sourceId").GetString()!;

        // Pending work lists the new source.
        var status = await mcp.CallAsync("continue_ingest", new Dictionary<string, object?> { ["action"] = "status" });
        Assert.Contains(status.Structured().GetProperty("data").GetProperty("status").EnumerateArray(),
            e => e.GetProperty("sourceId").GetString() == sourceId);

        // The agent writes the summary page, then advances step by step.
        var page = await mcp.CallAsync("write_page", new Dictionary<string, object?>
        {
            ["type"] = "summary", ["title"] = "Widgets summary", ["summary"] = "s", ["body"] = "Widgets are small.", ["sources"] = sourceId,
        });
        Assert.False(page.IsError, page.Text());

        var summarized = await Advance(mcp, sourceId, "summarized");
        Assert.Equal("summarized", summarized.GetProperty("advance").GetProperty("state").GetString());
        Assert.Equal("summarized", summarized.GetProperty("plan").GetProperty("current").GetString());

        await Advance(mcp, sourceId, "integrated", touched: "");
        var lint = await mcp.CallAsync("check_health", new Dictionary<string, object?> { ["links"] = false });
        Assert.False(lint.IsError, lint.Text());
        var linted = await Advance(mcp, sourceId, "linted");

        Assert.Empty(linted.GetProperty("plan").GetProperty("remainingStates").EnumerateArray());
        var resume = await mcp.CallAsync("continue_ingest", new Dictionary<string, object?> { ["action"] = "resume", ["sourceId"] = sourceId });
        Assert.Equal("linted", resume.Structured().GetProperty("data").GetProperty("plan").GetProperty("current").GetString());
        var after = await mcp.CallAsync("continue_ingest", new Dictionary<string, object?> { ["action"] = "status" });
        Assert.Empty(after.Structured().GetProperty("data").GetProperty("status").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ContinueIngest_AdvanceWithoutASourceId_IsAProtocolError(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(() =>
            mcp.CallAsync("continue_ingest", new Dictionary<string, object?> { ["action"] = "advance", ["to"] = "summarized" }));
        Assert.Equal(ModelContextProtocol.McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    private static async Task<JsonElement> Advance(McpHarness mcp, string sourceId, string to, string? touched = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = "advance", ["sourceId"] = sourceId, ["to"] = to };
        if (touched is not null)
            args["touched"] = touched;
        var result = await mcp.CallAsync("continue_ingest", args);
        Assert.False(result.IsError, result.Text());
        return result.Structured().GetProperty("data");
    }
}
