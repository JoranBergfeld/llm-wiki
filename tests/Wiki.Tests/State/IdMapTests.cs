using Xunit;
using Wiki.State;
using Wiki.Core;

namespace Wiki.Tests.State;

public class IdMapTests
{
    // A flag-rooted Vault doesn't require wiki.yaml to exist, so this is enough
    // to get a Vault pointed at a scratch directory for idmap.json round-trips.
    static Vault MakeVault(string root) => Vault.Resolve(root, _ => null, root);

    [Fact]
    public void Save_Load_RoundTrips_And_NormalizesSlashes()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var vault = MakeVault(tv.Path);

        var map = new IdMap();
        map.Put("01ABC", "wiki/entities/foo.md");
        map.Put("01XYZ", "wiki\\entities\\bar.md");
        map.Save(vault);

        var reloaded = new IdMap();
        reloaded.Load(vault);

        Assert.Equal("wiki/entities/foo.md", reloaded.PathFor("01ABC"));
        Assert.Equal("wiki/entities/bar.md", reloaded.PathFor("01XYZ"));
        Assert.Equal("01ABC", reloaded.IdFor("wiki/entities/foo.md"));
        Assert.Equal("01XYZ", reloaded.IdFor("wiki/entities/bar.md"));
    }

    [Fact]
    public void Load_MissingFile_YieldsEmptyMap()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var vault = MakeVault(tv.Path);

        var map = new IdMap();
        map.Load(vault);

        Assert.Empty(map.All);
        Assert.Null(map.PathFor("anything"));
    }

    [Fact]
    public void Put_ThenRemove_DropsTheId()
    {
        var map = new IdMap();
        map.Put("id1", "a.md");
        Assert.Equal("a.md", map.PathFor("id1"));

        map.Remove("id1");

        Assert.Null(map.PathFor("id1"));
        Assert.Null(map.IdFor("a.md"));
    }

    [Fact]
    public void Save_WritesKeysInSortedOrdinalOrder()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var vault = MakeVault(tv.Path);

        var map = new IdMap();
        map.Put("zzz", "z.md");
        map.Put("aaa", "a.md");
        map.Put("mmm", "m.md");
        map.Save(vault);

        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(vault.StateDir, "idmap.json"));
        var idxAaa = text.IndexOf("\"aaa\"", System.StringComparison.Ordinal);
        var idxMmm = text.IndexOf("\"mmm\"", System.StringComparison.Ordinal);
        var idxZzz = text.IndexOf("\"zzz\"", System.StringComparison.Ordinal);

        Assert.True(idxAaa >= 0, "expected 'aaa' key in idmap.json");
        Assert.True(idxMmm >= 0, "expected 'mmm' key in idmap.json");
        Assert.True(idxZzz >= 0, "expected 'zzz' key in idmap.json");
        Assert.True(idxAaa < idxMmm, "expected 'aaa' before 'mmm'");
        Assert.True(idxMmm < idxZzz, "expected 'mmm' before 'zzz'");
    }
}
