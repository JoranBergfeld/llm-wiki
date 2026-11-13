using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wiki.Mcp;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #54, spec "Error contract": unknown tools and schema-invalid
// arguments are JSON-RPC protocol errors; CLI exit 1/2 is a tool result with
// isError and the CLI envelope unchanged plus exitCode (text: `code: message`
// lines); exit 3 is a non-error result carrying exitCode 3.
public class ErrorContractTests
{
    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string AddSource(TempVault tv)
    {
        var src = Path.Combine(tv.Path, "input.md");
        File.WriteAllText(src, "content");
        var r = tv.Run("source", "add", src, "--category", "article", "--title", "T", "--json");
        Assert.Equal(0, r.ExitCode);
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    private static string[] CompletedSteps(CallToolResult r)
        => r.Structured().GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task UnknownTool_IsAProtocolError(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync("init"));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    public static TheoryData<string, string, Dictionary<string, object?>> InvalidArguments()
    {
        var data = new TheoryData<string, string, Dictionary<string, object?>>();
        foreach (var transport in McpTransports.Names)
        {
            data.Add(transport, "read_page", new Dictionary<string, object?>());                                            // required missing
            data.Add(transport, "read_page", new Dictionary<string, object?> { ["page"] = 42 });                             // wrong type
            data.Add(transport, "read_page", new Dictionary<string, object?> { ["page"] = "x", ["backlinks"] = "yes" });     // wrong type
            data.Add(transport, "search_wiki", new Dictionary<string, object?> { ["query"] = "x", ["limit"] = "many" });     // wrong type
            data.Add(transport, "search_wiki", new Dictionary<string, object?> { ["query"] = "x", ["vault"] = "/elsewhere" }); // undeclared
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task SchemaInvalidArguments_AreAProtocolError(string transport, string tool, Dictionary<string, object?> args)
    {
        using var tv = new TempVault(); Init(tv);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallAsync(tool, args));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Exit1_IsAnErrorResult_CarryingTheEnvelopeUnchanged(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var cli = tv.Run("page", "show", "nope", "--json");
        Assert.Equal(1, cli.ExitCode);
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_page", new Dictionary<string, object?> { ["page"] = "nope", ["backlinks"] = true });

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.Equal(1, s.GetProperty("exitCode").GetInt32());
        Assert.Empty(CompletedSteps(result));
        // The CLI envelope, unchanged: same keys and values the CLI printed.
        using var envelope = JsonDocument.Parse(cli.Stdout.Trim().Split('\n')[^1]);
        foreach (var prop in envelope.RootElement.EnumerateObject())
            Assert.Equal(prop.Value.GetRawText(), s.GetProperty(prop.Name).GetRawText());
        Assert.Equal($"not-found: {cli.Envelope.Errors[0].Message}", result.Text());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Exit2_IsAnErrorResult(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");
        using var doc = JsonDocument.Parse(created.Stdout.Trim().Split('\n')[^1]);
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        File.WriteAllText(Path.Combine(tv.Path, ".wiki", "idmap.json"), "garbage{"); // unreadable state: an IO failure
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var result = await mcp.CallAsync("read_page", new Dictionary<string, object?> { ["page"] = id });

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.Equal(2, s.GetProperty("exitCode").GetInt32());
        Assert.False(s.GetProperty("ok").GetBoolean());
        Assert.Equal("io-error", s.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.StartsWith("io-error: ", result.Text());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task Exit3_IsANonErrorResult_CarryingExitCode3(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var source = AddSource(tv);
        Assert.Equal(0, tv.Run("source", "retract", source, "--reason", "first", "--json").ExitCode);
        await using var mcp = await McpHarness.StartAsync(tv, transport, (b, cli) => b.WithTools(new ProbeTools(cli)));

        var result = await mcp.CallAsync("probe_retract", new Dictionary<string, object?> { ["source"] = source });

        Assert.False(result.IsError);
        var s = result.Structured();
        Assert.Equal(3, s.GetProperty("exitCode").GetInt32());
        Assert.Equal("already-retracted", s.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.StartsWith("already-retracted: ", result.Text());
    }

    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task MidSequenceFailure_ReturnsTheFailingStep_AndTheStepsBeforeIt(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");
        await using var mcp = await McpHarness.StartAsync(tv, transport, (b, cli) => b.WithTools(new ProbeTools(cli)));

        var result = await mcp.CallAsync("probe_two_steps", new Dictionary<string, object?> { ["page"] = "contoso", ["source"] = "missing" });

        Assert.True(result.IsError);
        Assert.Equal(["page show"], CompletedSteps(result));
        Assert.Equal(1, result.Structured().GetProperty("exitCode").GetInt32());
        Assert.Equal("not-found", result.Structured().GetProperty("errors")[0].GetProperty("code").GetString());
    }

    // Test-only tools, built through the real adapter and registered with the
    // generic WithTools<T> like the catalogue's own.
    [McpServerToolType]
    public sealed class ProbeTools(CliAdapter cli)
    {
        [McpServerTool(Name = "probe_retract"), Description("test probe")]
        public CallToolResult Retract(string source, CancellationToken cancellationToken = default)
        {
            var run = cli.Begin(cancellationToken);
            run.Step(new CliStep("retract", "source", "retract").Option("--reason", "probe").Positional(source));
            return run.Result();
        }

        [McpServerTool(Name = "probe_two_steps"), Description("test probe")]
        public CallToolResult TwoSteps(string page, string source, CancellationToken cancellationToken = default)
        {
            var run = cli.Begin(cancellationToken);
            if (run.Step(new CliStep("page", "page", "show").Positional(page)))
                run.Step(new CliStep("retract", "source", "retract").Option("--reason", "probe").Positional(source));
            return run.Result();
        }
    }
}
