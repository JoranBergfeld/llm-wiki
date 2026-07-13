using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class VaultTests
{
    [Fact]
    public void Resolve_WalksUpForWikiYaml()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        System.IO.File.WriteAllText(System.IO.Path.Combine(tv.Path, "wiki.yaml"), "version: 1");
        var nested = System.IO.Path.Combine(tv.Path, "a", "b");
        System.IO.Directory.CreateDirectory(nested);
        var v = Wiki.Core.Vault.Resolve(null, _ => null, nested);
        Assert.Equal(tv.Path, v.Root);
    }
}
