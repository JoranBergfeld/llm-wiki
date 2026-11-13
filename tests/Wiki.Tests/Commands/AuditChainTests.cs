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

    private static string Decompress(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        return r.ReadToEnd();
    }

    [Fact]
    public void Reanchor_RestoresWrites_AndRecordsItsReasonInTheChain()
    {
        using var tv = new TempVault();
        TamperedVault(tv);
        var broken = File.ReadAllText(LogPath(tv));

        var r = tv.Run("audit", "reanchor", "--reason", "restored from backup after disk fault", "--json");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("audit.000001.log.gz", Data(r).GetProperty("segment").GetString());
        // The broken bytes are kept exactly as found, in an archived segment.
        Assert.Equal(broken, Decompress(Path.Combine(AuditDir(tv), "audit.000001.log.gz")));
        var line = Assert.Single(Lines(tv));
        Assert.Equal("audit.reanchor", Fields(line)[1]);
        Assert.Equal("restored from backup after disk fault", Field(line, "reason"));
        // It chains from the SHA-256 of the broken content it sets aside
        // (known-good: hashed here independently of the CLI).
        using var sha = System.Security.Cryptography.SHA256.Create();
        Assert.Equal(System.Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(broken))).ToLowerInvariant(),
            Field(line, "prev"));

        Assert.True(Data(tv.Run("audit", "--json")).GetProperty("chain").GetProperty("intact").GetBoolean());
        Assert.Equal(0, AddCategory(tv, "memo").ExitCode);
        Assert.Equal(2, Lines(tv).Length);
    }

    [Fact]
    public void Reanchor_WithoutAReason_IsRejected_AndChangesNothing()
    {
        using var tv = new TempVault();
        TamperedVault(tv);
        var before = Snapshot(tv);

        var r = tv.Run("audit", "reanchor", "--reason", "  ", "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("missing-reason", r.Envelope.Errors[0].Code);
        Assert.Equal(before, Snapshot(tv));
    }

    [Fact]
    public void Reanchor_OnAnIntactChain_IsRefused_AndChangesNothing()
    {
        using var tv = new TempVault();
        Init(tv);
        var before = Snapshot(tv);

        var r = tv.Run("audit", "reanchor", "--reason", "just because", "--json");

        Assert.Equal(3, r.ExitCode);
        Assert.Equal("audit-chain-intact", r.Envelope.Errors[0].Code);
        Assert.Equal(before, Snapshot(tv));
    }
}
