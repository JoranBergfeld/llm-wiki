using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Amendment AX (spec story 88): a pending proposal can be withdrawn with a
// reason - the proposer taking it back, as opposed to the reviewer rejecting
// it. Persona changesets through `wiki review withdraw`, category and schema
// proposals through `wiki category|schema withdraw`. Each is logged in the
// audit log with its reason, and nothing else changes.
public class WithdrawTests
{
    private static JsonElement Data(CliResult r)
    {
        using var doc = JsonDocument.Parse(r.Stdout.Trim().Split('\n')[^1]);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static void Init(TempVault tv)
    {
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var anchor = "description: \"Web articles and blog posts\"";
        File.WriteAllText(path, File.ReadAllText(path).Replace(anchor, anchor + "\n    persona-source: true"));
    }

    private static string Page(TempVault tv, string title)
    {
        var src = Path.Combine(tv.Path, title.Replace(' ', '-') + "-src.md");
        File.WriteAllText(src, "Content of " + title);
        var s = tv.Run("source", "add", src, "--category", "article", "--title", title, "--json");
        var r = tv.RunStdin("Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
            "--summary", title + " summary", "--sources", Data(s).GetProperty("id").GetString()!, "--stdin", "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    private static string PendingChangeset(TempVault tv)
    {
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var p = tv.RunStdin("{\"reason\":\"two notes\",\"changes\":[{\"op\":\"add\",\"section\":\"values\",\"text\":\"I value craft.\","
                            + "\"evidence\":[{\"page\":\"" + a + "\"},{\"page\":\"" + b + "\"}]}]}",
            "persona", "propose", "--stdin", "--json");
        Assert.Equal(0, p.ExitCode);
        return Data(p).GetProperty("changeset").GetProperty("id").GetString()!;
    }

    private static string[] AuditLines(TempVault tv)
        => File.ReadAllLines(Path.Combine(tv.Path, "audit", "audit.log"));

    private static string? Field(string line, string key)
        => line.Split(" | ").FirstOrDefault(s => s.StartsWith(key + "="))?[(key.Length + 1)..];

    // ---- persona changeset --------------------------------------------------

    [Fact]
    public void WithdrawingAPendingPersonaChangeset_TakesItOutOfReview_AndLogsTheReason()
    {
        using var tv = new TempVault(); Init(tv);
        var id = PendingChangeset(tv);

        var r = tv.Run("review", "withdraw", id, "--reason", "superseded by a better pass", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("withdrawn", Data(r).GetProperty("changeset").GetProperty("status").GetString());
        Assert.Equal("superseded by a better pass", Data(r).GetProperty("changeset").GetProperty("note").GetString());
        Assert.DoesNotContain(Data(tv.Run("review", "list", "--json")).EnumerateArray(),
            row => row.GetProperty("kind").GetString() == "persona");
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
        Assert.Equal(JsonValueKind.Null, Data(tv.Run("persona", "distill", "--json")).GetProperty("pending").ValueKind);

        var line = AuditLines(tv)[^1];
        Assert.Equal("persona.withdraw", line.Split(" | ")[1]);
        Assert.Equal(id, line.Split(" | ")[2]);
        Assert.Equal("superseded by a better pass", Field(line, "reason"));
        Assert.Equal("cli", Field(line, "decidedVia"));
    }

    [Fact]
    public void WithdrawingWithoutAReason_IsRejected_AndChangesNothing()
    {
        using var tv = new TempVault(); Init(tv);
        var id = PendingChangeset(tv);
        var auditBefore = AuditLines(tv).Length;

        var r = tv.Run("review", "withdraw", id, "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("reason-required", r.Envelope.Errors[0].Code);
        Assert.Contains(Data(tv.Run("review", "list", "--json")).EnumerateArray(),
            row => row.GetProperty("kind").GetString() == "persona");
        Assert.Equal(auditBefore, AuditLines(tv).Length);
    }

    [Fact]
    public void WithdrawingADecidedChangeset_IsAStateConflict()
    {
        using var tv = new TempVault(); Init(tv);
        var id = PendingChangeset(tv);
        Assert.Equal(0, tv.Run("review", "withdraw", id, "--reason", "r", "--json").ExitCode);

        var again = tv.Run("review", "withdraw", id, "--reason", "r", "--json");
        var approve = tv.Run("review", "approve", id, "--json");

        Assert.Equal(3, again.ExitCode);
        Assert.Equal(3, approve.ExitCode);
    }

    [Fact]
    public void APendingPage_IsRejected_NotWithdrawn()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.Run("review", "withdraw", "01JC0000000000000000000000", "--reason", "r", "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("not-found", r.Envelope.Errors[0].Code);
    }

    // ---- category and schema proposals ---------------------------------------

    [Fact]
    public void WithdrawingACategoryProposal_MarksItWithdrawn_AndLogsTheReason()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(tv.Run("category", "propose", "research-paper", "--description", "Papers", "--json"))
            .GetProperty("id").GetString()!;
        var yaml = File.ReadAllText(Path.Combine(tv.Path, "wiki.yaml"));

        var r = tv.Run("category", "withdraw", id, "--reason", "article fits after all", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("withdrawn", Data(r).GetProperty("status").GetString());
        Assert.Equal(yaml, File.ReadAllText(Path.Combine(tv.Path, "wiki.yaml")));
        Assert.Empty(Data(tv.Run("category", "proposals", "--status", "open", "--json")).EnumerateArray());
        Assert.Single(Data(tv.Run("category", "proposals", "--status", "withdrawn", "--json")).EnumerateArray());
        var line = AuditLines(tv)[^1];
        Assert.Equal("category.withdraw", line.Split(" | ")[1]);
        Assert.Equal("article fits after all", Field(line, "reason"));
        Assert.Equal(3, tv.Run("category", "approve", id, "--json").ExitCode);
        Assert.Equal("reason-required", tv.Run("category", "withdraw", id, "--json").Envelope.Errors[0].Code);
    }

    [Fact]
    public void WithdrawingASchemaProposal_MarksItWithdrawn_AndLogsTheReason()
    {
        using var tv = new TempVault(); Init(tv);
        var id = Data(tv.RunStdin("1. A new step.\n", "schema", "propose", "--section", "Session start", "--stdin", "--json"))
            .GetProperty("id").GetString()!;
        var agents = File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md"));

        var r = tv.Run("schema", "withdraw", id, "--reason", "not needed", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("withdrawn", Data(r).GetProperty("status").GetString());
        Assert.Equal(agents, File.ReadAllText(Path.Combine(tv.Path, "AGENTS.md")));
        Assert.Single(Data(tv.Run("schema", "proposals", "--status", "withdrawn", "--json")).EnumerateArray());
        var line = AuditLines(tv)[^1];
        Assert.Equal("schema.withdraw", line.Split(" | ")[1]);
        Assert.Equal("not needed", Field(line, "reason"));
        Assert.Equal(3, tv.Run("schema", "approve", id, "--json").ExitCode);
    }
}
