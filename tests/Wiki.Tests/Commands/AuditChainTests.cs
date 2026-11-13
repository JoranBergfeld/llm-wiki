using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #45: what happens once the audit chain is broken (writes refused,
// reads kept, an explicit re-anchor), and rotation of the live log into
// compressed segments that stay part of the chain. Observed only through the
// CLI and the files the audit log leaves in the vault.
public class AuditChainTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string AuditDir(TempVault tv) => Path.Combine(tv.Path, "audit");

    private static string LogPath(TempVault tv) => Path.Combine(AuditDir(tv), "audit.log");

    private static string[] Lines(TempVault tv)
        => File.Exists(LogPath(tv))
            ? File.ReadAllText(LogPath(tv)).Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            : [];

    private static string[] Fields(string line) => line.Split(" | ");

    private static string Field(string line, string key)
        => Fields(line).Single(f => f.StartsWith(key + "=", System.StringComparison.Ordinal))[(key.Length + 1)..];

    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    private static CliResult AddCategory(TempVault tv, string id)
        => tv.Run("category", "add", id, "--description", "d", "--json");

    private static Dictionary<string, string> Snapshot(TempVault tv)
        => Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(tv.Path, f), f => System.Convert.ToBase64String(File.ReadAllBytes(f)));

    // Init, two audited mutations, then an edited middle line.
    private static void TamperedVault(TempVault tv)
    {
        Init(tv);
        AddCategory(tv, "paper");
        AddCategory(tv, "talk");
        File.WriteAllText(LogPath(tv), File.ReadAllText(LogPath(tv)).Replace("| paper |", "| article |"));
    }

    [Fact]
    public void AfterAMidLogEdit_MutationsAreRefused_WithNoVaultChange()
    {
        using var tv = new TempVault();
        TamperedVault(tv);
        var before = Snapshot(tv);

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");

        Assert.Equal(3, r.ExitCode);
        Assert.Equal("audit-chain-broken", r.Envelope.Errors[0].Code);
        Assert.Contains("reanchor", r.Envelope.Errors[0].Message);
        Assert.Equal(before, Snapshot(tv));
    }

    [Fact]
    public void AfterAMidLogEdit_ReadsStillWork()
    {
        using var tv = new TempVault();
        TamperedVault(tv);

        Assert.Equal(0, tv.Run("category", "list", "--json").ExitCode);
        Assert.Equal(0, tv.Run("page", "list", "--json").ExitCode);
        var audit = tv.Run("audit", "--json");
        Assert.Equal(0, audit.ExitCode);
        Assert.False(Data(audit).GetProperty("chain").GetProperty("intact").GetBoolean());
    }
}
