using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #44: the hash-chained audit log. Every mutating CLI command appends
// exactly one `|`-separated line ending in `prev=` and `hash=`; read commands
// append none. Observed only through files in the vault and `wiki audit`.
public class AuditLogTests
{
    private static readonly Regex Tail = new(@" \| prev=(?<prev>[0-9a-f]{64}) \| hash=(?<hash>[0-9a-f]{64})$");

    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "audit", "audit.log");

    private static string[] Lines(TempVault tv)
        => File.Exists(LogPath(tv))
            ? File.ReadAllText(LogPath(tv)).Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            : [];

    private static string[] Fields(string line) => line.Split(" | ");

    [Fact]
    public void Init_WritesTheFirstLine_ChainedFromGenesis()
    {
        using var tv = new TempVault();
        Assert.Equal(0, Init(tv).ExitCode);

        var lines = Lines(tv);
        var line = Assert.Single(lines);
        var m = Tail.Match(line);
        Assert.True(m.Success, line);
        Assert.Equal(new string('0', 64), m.Groups["prev"].Value);
        Assert.Equal("init", Fields(line)[1]);
        Assert.Contains("via=cli", Fields(line));
    }

    [Fact]
    public void EachMutation_AppendsOneLine_WhosePrevIsTheLastHash()
    {
        using var tv = new TempVault();
        Init(tv);

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, r.ExitCode);

        var lines = Lines(tv);
        Assert.Equal(2, lines.Length);
        Assert.Equal("page.upsert", Fields(lines[1])[1]);
        Assert.Equal(Tail.Match(lines[0]).Groups["hash"].Value, Tail.Match(lines[1]).Groups["prev"].Value);
        Assert.NotEqual(Tail.Match(lines[0]).Groups["hash"].Value, Tail.Match(lines[1]).Groups["hash"].Value);
    }

    // The five mutations that write nothing to wiki/log.md are still audited.
    [Fact]
    public void MutationsThatSkipLogMd_AreAudited()
    {
        using var tv = new TempVault();
        Init(tv);

        Assert.Equal(0, tv.RunStdin("New conventions.", "schema", "propose", "--section", "Conventions", "--json").ExitCode);
        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);
        Assert.Equal(0, tv.Run("reindex", "--json").ExitCode);

        var actions = Lines(tv).Select(l => Fields(l)[1]).ToArray();
        Assert.Equal(new[] { "init", "schema.propose", "category.add", "reindex" }, actions);
        Assert.Equal("paper", Fields(Lines(tv)[2])[2]);
    }

    [Fact]
    public void LintDriftRewrite_IsAudited()
    {
        using var tv = new TempVault();
        Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Bravo", "--summary", "s1", "--json");
        tv.RunStdin("See [[bravo]] for more.", "page", "upsert", "--type", "entity", "--title", "Alpha", "--summary", "s2", "--json");
        var entities = Path.Combine(tv.Path, "wiki", "entities");
        File.Move(Path.Combine(entities, "bravo.md"), Path.Combine(entities, "charlie.md"));

        var before = Lines(tv).Length;
        var r = tv.Run("lint", "--fix-links", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("[[charlie]]", File.ReadAllText(Path.Combine(entities, "alpha.md")));

        var lines = Lines(tv);
        Assert.Equal(before + 1, lines.Length);
        Assert.Equal("lint", Fields(lines[^1])[1]);
    }

    [Theory]
    [InlineData("page", "list")]
    [InlineData("search", "contoso")]
    [InlineData("source", "list")]
    [InlineData("issues", "list")]
    [InlineData("review", "list")]
    [InlineData("schema", "proposals")]
    [InlineData("category", "list")]
    [InlineData("audit", "next")]
    [InlineData("audit", "list")]
    [InlineData("index", "show")]
    [InlineData("ingest", "status")]
    [InlineData("page", "show", "contoso")]
    public void ReadCommands_AppendNothing(params string[] command)
    {
        using var tv = new TempVault();
        Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");
        var before = File.ReadAllText(LogPath(tv));

        tv.Run([.. command, "--json"]);

        Assert.Equal(before, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void RejectedMutation_AppendsNothing()
    {
        using var tv = new TempVault();
        Init(tv);
        var before = File.ReadAllText(LogPath(tv));

        var r = tv.Run("category", "add", "Not Kebab", "--description", "x", "--json");
        Assert.Equal(1, r.ExitCode);

        Assert.Equal(before, File.ReadAllText(LogPath(tv)));
    }

    private static void ConfigureAuditPath(TempVault tv, string path)
        => File.AppendAllText(Path.Combine(tv.Path, "wiki.yaml"), $"audit:\n  path: \"{path}\"\n");

    // Every file under the vault, with its bytes, for byte-identity checks.
    private static System.Collections.Generic.Dictionary<string, string> Snapshot(TempVault tv)
        => Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(tv.Path, f), f => System.Convert.ToBase64String(File.ReadAllBytes(f)));

    [Fact]
    public void AuditPath_IsConfigurable()
    {
        using var tv = new TempVault();
        Init(tv);
        var defaultLog = File.ReadAllText(LogPath(tv));
        ConfigureAuditPath(tv, "history/vault-audit.log");

        Assert.Equal(0, tv.Run("category", "add", "paper", "--description", "Papers", "--json").ExitCode);

        var moved = File.ReadAllText(Path.Combine(tv.Path, "history", "vault-audit.log")).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(moved);
        Assert.Equal("category.add", Fields(line)[1]);
        Assert.Equal(defaultLog, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void AuditPath_InsideTheStateCache_IsRejected()
    {
        using var tv = new TempVault();
        Init(tv);
        ConfigureAuditPath(tv, ".wiki/audit.log");
        var before = Snapshot(tv);

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("config", r.Envelope.Errors[0].Code);
        Assert.Equal(before, Snapshot(tv));
    }

    [Fact]
    public void UnwritableAuditLog_FailsTheMutation_WithNoVaultChange()
    {
        using var tv = new TempVault();
        Init(tv);
        // A path under a regular file can never be created.
        ConfigureAuditPath(tv, "wiki.yaml/audit.log");
        var before = Snapshot(tv);

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");

        Assert.Equal(2, r.ExitCode);
        Assert.False(r.Envelope.Ok);
        Assert.Contains("audit log", r.Envelope.Errors[0].Message);
        Assert.Equal(before, Snapshot(tv));
    }

    [Fact]
    public void Reindex_LeavesEarlierAuditLinesUntouched()
    {
        using var tv = new TempVault();
        Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");
        var before = File.ReadAllText(LogPath(tv));

        Assert.Equal(0, tv.Run("reindex", "--json").ExitCode);

        var after = File.ReadAllText(LogPath(tv));
        Assert.StartsWith(before, after);
        Assert.Equal(3, Lines(tv).Length);
    }
}
