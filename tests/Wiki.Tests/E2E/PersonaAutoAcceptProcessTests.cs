using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.E2E;

// Issue #51: WIKI_PERSONA_AUTO_ACCEPT turns on persona auto-accept, so a
// valid changeset is accepted at propose time instead of waiting in
// `wiki review`. Environment variables are process-wide, so the proposing
// `wiki` runs in its own process (the parallel in-process suite must never
// see the variable); the vault is set up in-process as usual.
public class PersonaAutoAcceptProcessTests
{
    private const string Env = "WIKI_PERSONA_AUTO_ACCEPT";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static void Init(TempVault tv)
    {
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var text = File.ReadAllText(path);
        var anchor = "description: \"Web articles and blog posts\"";
        Assert.Contains(anchor, text);
        File.WriteAllText(path, text.Replace(anchor, anchor + "\n    persona-source: true"));
    }

    // An endorsed (persona-source) summary page; returns its id.
    private static string Page(TempVault tv, string title)
    {
        var src = Path.Combine(tv.Path, title.Replace(' ', '-') + "-src.md");
        File.WriteAllText(src, "Content of " + title);
        var s = tv.Run("source", "add", src, "--category", "article", "--title", title, "--json");
        Assert.Equal(0, s.ExitCode);
        var r = tv.RunStdin("Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
            "--summary", title + " summary", "--sources", Data(s).GetProperty("id").GetString()!, "--stdin", "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    private static string AddValue(string text, params string[] pages)
        => "{\"reason\":\"two notes say it\",\"changes\":[{\"op\":\"add\",\"section\":\"values\",\"text\":"
           + JsonSerializer.Serialize(text) + ",\"evidence\":["
           + string.Join(",", pages.Select(p => "{\"page\":\"" + p + "\"}")) + "]}]}";

    // `wiki persona propose --stdin --json` in a separate process, with the
    // auto-accept variable set to `value` (null: removed).
    private static async Task<(int Exit, JsonElement Envelope)> ProposeInProcess(TempVault tv, string changeset, string? value)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = tv.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add(typeof(App).Assembly.Location);
        foreach (var a in new[] { "persona", "propose", "--stdin", "--json", "--vault", tv.Path })
            start.ArgumentList.Add(a);
        start.Environment.Remove("WIKI_VAULT");
        start.Environment.Remove(Env);
        if (value is not null)
            start.Environment[Env] = value;

        using var p = Process.Start(start)!;
        await p.StandardInput.WriteAsync(changeset);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(Wait).Token);
        var last = (await stdout).Trim().Split('\n')[^1];
        return (p.ExitCode, JsonDocument.Parse(last).RootElement.Clone());
    }

    private static string[] AuditLines(TempVault tv)
        => File.ReadAllText(Path.Combine(tv.Path, "audit", "audit.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Segments(string line) => line.Split(" | ");

    private static string? AuditField(string line, string key)
        => Segments(line).FirstOrDefault(s => s.StartsWith(key + "="))?[(key.Length + 1)..];

    [Fact]
    public async Task WithTheVariableSet_AValidProposalIsAcceptedImmediately_AndLoggedAsAutoAccept()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");

        var (exit, envelope) = await ProposeInProcess(tv, AddValue("I value careful craft.", a, b), "1");

        Assert.Equal(0, exit);
        var changeset = envelope.GetProperty("data").GetProperty("changeset");
        var id = changeset.GetProperty("id").GetString()!;
        Assert.Equal("approved", changeset.GetProperty("status").GetString());
        Assert.True(envelope.GetProperty("data").GetProperty("autoAccepted").GetBoolean());

        var persona = Data(tv.Run("persona", "read", "--json"));
        Assert.Equal(AuditField(AuditLines(tv)[^1], "hash"), persona.GetProperty("auditEntry").GetString()); // amendment AY
        Assert.Equal(new[] { "I value careful craft." },
            persona.GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
        Assert.DoesNotContain(Data(tv.Run("review", "list", "--json")).EnumerateArray(),
            row => row.GetProperty("kind").GetString() == "persona");

        // One line for the whole step: the accept, marked as auto-accepted.
        var line = AuditLines(tv)[^1];
        Assert.Equal("persona.accept", Segments(line)[1]);
        Assert.Equal("PERSONA.md", Segments(line)[2]);
        Assert.Equal(id, AuditField(line, "changeset"));
        Assert.Equal("+1 ~0 -0", AuditField(line, "lines"));
        Assert.Equal("auto-accept", AuditField(line, "decidedVia"));
        Assert.Equal("two notes say it", AuditField(line, "reason"));
        Assert.Null(AuditField(line, "retired")); // nothing to retire on a first accept

        // The file is lint-clean: lint finds the evidence ledger on that line.
        tv.Run("lint", "--json");
        Assert.DoesNotContain(Data(tv.Run("issues", "list", "--status", "open", "--json")).EnumerateArray(),
            i => i.GetProperty("kind").GetString()!.StartsWith("persona-"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("off")]
    public async Task WithTheVariableUnsetOrOff_TheProposalStaysPending(string? value)
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");

        var (exit, envelope) = await ProposeInProcess(tv, AddValue("I value careful craft.", a, b), value);

        Assert.Equal(0, exit);
        var id = envelope.GetProperty("data").GetProperty("changeset").GetProperty("id").GetString()!;
        Assert.Equal("open", envelope.GetProperty("data").GetProperty("changeset").GetProperty("status").GetString());
        Assert.False(envelope.GetProperty("data").GetProperty("autoAccepted").GetBoolean());
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
        Assert.Contains(Data(tv.Run("review", "list", "--json")).EnumerateArray(),
            row => row.GetProperty("changeset").ValueKind == JsonValueKind.Object
                   && row.GetProperty("changeset").GetProperty("id").GetString() == id);
        Assert.Equal("persona.propose", Segments(AuditLines(tv)[^1])[1]);
    }

    [Fact]
    public async Task WithTheVariableSet_AProposalThatFailsLint_IsStillRejected()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var auditBefore = AuditLines(tv).Length;

        var (exit, envelope) = await ProposeInProcess(tv, AddValue("I value [[craft]].", a, b), "true");

        Assert.Equal(1, exit);
        Assert.Equal("persona-lint", envelope.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(tv.Path, "PERSONA.md")));
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
        Assert.Equal(auditBefore, AuditLines(tv).Length);
    }

    [Fact]
    public async Task WithTheVariableSet_SupersedingAPendingChangeset_IsRecordedOnTheAcceptLine()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var (_, first) = await ProposeInProcess(tv, AddValue("I value careful craft.", a, b), null);
        var pending = first.GetProperty("data").GetProperty("changeset").GetProperty("id").GetString()!;

        var (exit, envelope) = await ProposeInProcess(tv, AddValue("I value patient craft.", a, b), "yes");

        Assert.Equal(0, exit);
        Assert.Equal(pending, envelope.GetProperty("data").GetProperty("superseded").GetString());
        var line = AuditLines(tv)[^1];
        Assert.Equal("persona.accept", Segments(line)[1]);
        Assert.Equal("auto-accept", AuditField(line, "decidedVia"));
        Assert.Equal(pending, AuditField(line, "supersedes"));
        Assert.DoesNotContain(Data(tv.Run("review", "list", "--json")).EnumerateArray(),
            row => row.GetProperty("kind").GetString() == "persona");
    }

    // Issue #52: an accept records the evidence it retires, and auto-accept
    // writes the same `retired=` field as `review approve`.
    [Fact]
    public async Task WithTheVariableSet_RewordingALine_RecordsTheRetiredEvidenceOnTheAcceptLine()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var c = Page(tv, "Patience one");
        var d = Page(tv, "Patience two");
        var (firstExit, _) = await ProposeInProcess(tv, AddValue("I value careful craft.", a, b), "1");
        Assert.Equal(0, firstExit);
        var firstEvidence = AuditField(AuditLines(tv)[^1], "evidence")!.Split(',');
        Assert.Equal(2, firstEvidence.Length);

        var reword = "{\"reason\":\"newer notes say it better\",\"changes\":[{\"op\":\"change\",\"section\":\"values\","
                     + "\"from\":\"I value careful craft.\",\"text\":\"I value patient craft.\",\"evidence\":["
                     + "{\"page\":\"" + c + "\"},{\"page\":\"" + d + "\"}]}]}";
        var (exit, envelope) = await ProposeInProcess(tv, reword, "1");

        Assert.Equal(0, exit);
        Assert.True(envelope.GetProperty("data").GetProperty("autoAccepted").GetBoolean());
        var line = AuditLines(tv)[^1];
        Assert.Equal("persona.accept", Segments(line)[1]);
        Assert.Equal("auto-accept", AuditField(line, "decidedVia"));
        var retired = AuditField(line, "retired");
        Assert.NotNull(retired);
        Assert.Equal(firstEvidence.OrderBy(x => x, StringComparer.Ordinal),
            retired!.Split(',').OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnUnrecognisedValue_IsRefused_BeforeAnythingIsWritten()
    {
        using var tv = new TempVault(); Init(tv);
        var a = Page(tv, "Craft one");
        var b = Page(tv, "Craft two");
        var auditBefore = AuditLines(tv).Length;

        var (exit, envelope) = await ProposeInProcess(tv, AddValue("I value careful craft.", a, b), "sometimes");

        Assert.Equal(1, exit);
        Assert.Equal("invalid-persona-auto-accept", envelope.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(tv.Path, "persona", "changeset.md")));
        Assert.Equal(auditBefore, AuditLines(tv).Length);
    }
}
