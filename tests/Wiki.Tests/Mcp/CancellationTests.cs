using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wiki.Mcp;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Mcp;

// Issue #56 / #37: a client cancel never interrupts a CLI step; it is checked
// between steps. The intent stops there, keeps the steps already done, and
// returns the failure shape (code `cancelled`, completedSteps, no exitCode).
public class CancellationTests
{
    private static JsonElement CliData(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static void Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string CreatePage(TempVault tv, string title)
        => CliData(tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", title, "--summary", "s", "--json"))
            .GetProperty("id").GetString()!;

    private static string PageStatus(TempVault tv, string id)
        => CliData(tv.Run("page", "show", id, "--frontmatter-only", "--json")).GetProperty("status").GetString()!;

    // A real `notifications/cancelled` from the client. The first step is
    // held inside the CLI: a manual writer is parked holding the vault lock,
    // so `page set-status` waits on it. The client cancels while that step
    // waits; the writer is then released, the step runs to completion, and
    // the cancel takes effect before the next step. Per MCP the client drops
    // any response to a request it cancelled, so the vault is what this test
    // observes; the returned shape is covered below.
    //
    // The notification is sent explicitly: with SDK 2.3.0 over this in-memory
    // pipe, cancelling CallToolAsync's token did not get the notification to
    // the server (verified: the request ran on to completion), while the same
    // notification sent by hand cancels the request's token server-side.
    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task ClientCancelDuringAStep_LetsThatStepFinish_AndRunsNoFurtherStep(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var a = CreatePage(tv, "Alpha");
        var b = CreatePage(tv, "Beta");
        await using var mcp = await McpHarness.StartAsync(tv, transport);

        var held = new HeldStdin("Body.");
        var writer = Task.Run(() => tv.RunWith(held, new StringWriter(),
            "page", "upsert", "--type", "entity", "--title", "Gamma", "--summary", "s", "--stdin", "--json"));
        Assert.True(held.WaitUntilEntered(TimeSpan.FromSeconds(30)));

        var requestId = new RequestId("repair-1");
        var call = mcp.Client.SendRequestAsync(new JsonRpcRequest
        {
            Id = requestId,
            Method = RequestMethods.ToolsCall,
            Params = new JsonObject
            {
                ["name"] = "repair_vault",
                ["arguments"] = new JsonObject { ["reindex"] = false, ["pages"] = $"{a},{b}", ["status"] = "archived" },
            },
        });
        await Task.Delay(TimeSpan.FromSeconds(1)); // the tool now waits on the lock inside its first step
        await mcp.Client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = requestId, Reason = "test" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await Task.Delay(TimeSpan.FromSeconds(1)); // the server has seen the cancel
        held.Release();
        await writer;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (PageStatus(tv, a) != "archived" && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal("archived", PageStatus(tv, a)); // the step under way ran to completion
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal("active", PageStatus(tv, b));   // the next one never started
    }

    // The shape a cancelled intent returns, through the real adapter: a probe
    // tool whose token is cancelled after its first step.
    [Theory]
    [MemberData(nameof(McpTransports.All), MemberType = typeof(McpTransports))]
    public async Task CancelBetweenSteps_ReturnsTheFailureShape_WithTheCompletedSteps(string transport)
    {
        using var tv = new TempVault(); Init(tv);
        var a = CreatePage(tv, "Alpha");
        var b = CreatePage(tv, "Beta");
        await using var mcp = await McpHarness.StartAsync(tv, transport, (builder, cli) => builder.WithTools(new CancelProbe(cli)));

        var result = await mcp.CallAsync("probe_cancel_after_first", new Dictionary<string, object?> { ["first"] = a, ["second"] = b });

        Assert.True(result.IsError);
        var s = result.Structured();
        Assert.False(s.GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", s.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(s.TryGetProperty("exitCode", out _));
        Assert.Equal(["page set-status"], s.GetProperty("completedSteps").EnumerateArray().Select(e => e.GetString()));
        Assert.StartsWith("cancelled: ", result.Text());
        Assert.Equal("archived", PageStatus(tv, a)); // no rollback
        Assert.Equal("active", PageStatus(tv, b));
    }

    [McpServerToolType]
    public sealed class CancelProbe(CliAdapter cli)
    {
        [McpServerTool(Name = "probe_cancel_after_first"), Description("test probe")]
        public CallToolResult Run(string first, string second, CancellationToken cancellationToken = default)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = cli.Begin(cancel.Token);
            if (run.Step(new CliStep("first", "page", "set-status").Positional(first).Positional("archived")))
            {
                cancel.Cancel();
                run.Step(new CliStep("second", "page", "set-status").Positional(second).Positional("archived"));
            }
            return run.Result();
        }
    }
}
