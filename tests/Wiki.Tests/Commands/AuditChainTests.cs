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

        Assert.Equal(6, r.ExitCode);
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

    private static void RotateAt(TempVault tv, int bytes)
        => File.AppendAllText(Path.Combine(tv.Path, "wiki.yaml"), $"audit:\n  rotate_bytes: {bytes}\n");

    private static string ManifestPath(TempVault tv) => Path.Combine(AuditDir(tv), "audit.manifest.json");

    private static JsonElement ManifestSegments(TempVault tv)
        => JsonDocument.Parse(File.ReadAllText(ManifestPath(tv))).RootElement.GetProperty("segments");

    private static string Hash(string line) => Field(line, "hash");

    // A threshold of 1 byte rolls the live log over before every mutation
    // once it holds anything.
    [Fact]
    public void AtTheThreshold_TheLiveLogRollsIntoACompressedSegment_ThatStaysInTheChain()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 1);
        var first = File.ReadAllText(LogPath(tv));

        Assert.Equal(0, AddCategory(tv, "paper").ExitCode);

        Assert.Equal(first, Decompress(Path.Combine(AuditDir(tv), "audit.000001.log.gz")));
        var seg = Assert.Single(ManifestSegments(tv).EnumerateArray());
        Assert.Equal("audit.000001.log.gz", seg.GetProperty("file").GetString());
        Assert.Equal(Hash(first.TrimEnd('\n')), seg.GetProperty("lastHash").GetString());
        var live = Assert.Single(Lines(tv));
        Assert.Equal("category.add", Fields(live)[1]);
        Assert.Equal(Hash(first.TrimEnd('\n')), Field(live, "prev"));
        Assert.True(Data(tv.Run("audit", "--json")).GetProperty("chain").GetProperty("intact").GetBoolean());
    }

    [Fact]
    public void BelowTheThreshold_NothingRolls()
    {
        using var tv = new TempVault();
        Init(tv);
        AddCategory(tv, "paper");

        Assert.Equal(2, Lines(tv).Length);
        Assert.False(File.Exists(ManifestPath(tv)));
    }

    [Fact]
    public void RotationNeverDeletesASegment()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 1);
        AddCategory(tv, "paper");
        AddCategory(tv, "talk");
        AddCategory(tv, "memo");

        var files = Directory.GetFiles(AuditDir(tv), "audit.*.log.gz").Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Assert.Equal(new[] { "audit.000001.log.gz", "audit.000002.log.gz", "audit.000003.log.gz" }, files);
        Assert.Equal(3, ManifestSegments(tv).GetArrayLength());
    }

    [Fact]
    public void InvalidRotationThreshold_IsAConfigError()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 0);

        var r = AddCategory(tv, "paper");

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("config", r.Envelope.Errors[0].Code);
    }

    private static JsonElement Deep(TempVault tv)
    {
        var r = tv.Run("audit", "--deep", "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("deep");
    }

    [Fact]
    public void Audit_WithoutDeep_ChecksOnlyTheLiveSegment()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 1);
        AddCategory(tv, "paper");
        AddCategory(tv, "talk");
        File.WriteAllBytes(Path.Combine(AuditDir(tv), "audit.000001.log.gz"), [1, 2, 3]);

        var r = tv.Run("audit", "--json");

        Assert.True(Data(r).GetProperty("chain").GetProperty("intact").GetBoolean());
        Assert.False(Data(r).TryGetProperty("deep", out _));
    }

    [Fact]
    public void AuditDeep_VerifiesEverySegmentAndTheLinksBetweenThem()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 1);
        AddCategory(tv, "paper");
        AddCategory(tv, "talk");

        var deep = Deep(tv);

        Assert.True(deep.GetProperty("intact").GetBoolean());
        var segments = deep.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("segment").GetString()).ToArray();
        Assert.Equal(new[] { "audit.000001.log.gz", "audit.000002.log.gz", "live" }, segments);
    }

    [Fact]
    public void AuditDeep_DetectsAnEditedArchivedSegment()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 1);
        AddCategory(tv, "paper");
        AddCategory(tv, "talk");
        var path = Path.Combine(AuditDir(tv), "audit.000002.log.gz");
        var edited = Decompress(path).Replace("| paper |", "| article |");
        using (var gz = new GZipStream(File.Create(path), CompressionLevel.Optimal))
            gz.Write(System.Text.Encoding.UTF8.GetBytes(edited));

        var deep = Deep(tv);

        Assert.False(deep.GetProperty("intact").GetBoolean());
        var seg = deep.GetProperty("segments")[1];
        Assert.False(seg.GetProperty("intact").GetBoolean());
        Assert.Contains(seg.GetProperty("problems").EnumerateArray(), p => p.GetProperty("code").GetString() == "hash-mismatch");
    }

    [Fact]
    public void AuditDeep_DetectsABrokenLinkBetweenSegments()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 1);
        AddCategory(tv, "paper");
        AddCategory(tv, "talk");
        // Drop the first archived segment's entry: the second no longer
        // chains from anything the manifest knows.
        var manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath(tv))).RootElement;
        var kept = manifest.GetProperty("segments").EnumerateArray().Skip(1).Select(s => s.GetRawText());
        File.WriteAllText(ManifestPath(tv), "{\"segments\":[" + string.Join(",", kept) + "]}");

        var deep = Deep(tv);

        Assert.False(deep.GetProperty("intact").GetBoolean());
        var first = deep.GetProperty("segments")[0];
        Assert.Equal("audit.000002.log.gz", first.GetProperty("segment").GetString());
        var p = Assert.Single(first.GetProperty("problems").EnumerateArray());
        Assert.Equal("prev-mismatch", p.GetProperty("code").GetString());
        Assert.Equal(1, p.GetProperty("line").GetInt32());
    }

    [Fact]
    public void AuditDeep_AcceptsAReanchoredSegment_ButNotOneEditedAfterwards()
    {
        using var tv = new TempVault();
        TamperedVault(tv);
        tv.Run("audit", "reanchor", "--reason", "known edit", "--json");

        var deep = Deep(tv);
        Assert.True(deep.GetProperty("intact").GetBoolean());
        var seg = deep.GetProperty("segments")[0];
        Assert.True(seg.GetProperty("reanchored").GetBoolean());

        var path = Path.Combine(AuditDir(tv), "audit.000001.log.gz");
        var edited = Decompress(path).Replace("| talk |", "| video |");
        using (var gz = new GZipStream(File.Create(path), CompressionLevel.Optimal))
            gz.Write(System.Text.Encoding.UTF8.GetBytes(edited));

        Assert.False(Deep(tv).GetProperty("intact").GetBoolean());
    }

    // A crash after the manifest named the new segment but before the live
    // log was emptied leaves the live log holding that segment's bytes. The
    // chain is not broken by it, and the next mutation finishes the job.
    [Fact]
    public void AnInterruptedRotation_IsFinishedByTheNextMutation()
    {
        using var tv = new TempVault();
        Init(tv);
        RotateAt(tv, 1);
        AddCategory(tv, "paper");
        File.WriteAllText(LogPath(tv), Decompress(Path.Combine(AuditDir(tv), "audit.000001.log.gz")));

        Assert.True(Data(tv.Run("audit", "--json")).GetProperty("chain").GetProperty("intact").GetBoolean());
        Assert.Equal(0, AddCategory(tv, "talk").ExitCode);

        Assert.True(Deep(tv).GetProperty("intact").GetBoolean());
        Assert.Equal("category.add", Fields(Assert.Single(Lines(tv)))[1]);
    }
}
