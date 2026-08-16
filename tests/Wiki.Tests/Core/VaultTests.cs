using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class VaultTests
{
    [Fact]
    public void Resolve_WalksUpForWikiYaml()
    {
        using var tv = new TempVault();
        File.WriteAllText(Path.Combine(tv.Path, "wiki.yaml"), "version: 1");
        var nested = Path.Combine(tv.Path, "a", "b");
        Directory.CreateDirectory(nested);
        var v = Vault.Resolve(null, _ => null, nested);
        Assert.Equal(tv.Path, v.Root);
    }

    // -------------------- amendment M: an explicit path must be a vault --------------------

    // The walk-up branch stops at a wiki.yaml by construction, but --vault
    // and WIKI_VAULT used to accept any string. A typo then produced an
    // "empty vault" indistinguishable from a real one - the agent that is
    // told to trust `ok` got ok:true and zero results.
    [Fact]
    public void Resolve_ExplicitFlagWithoutWikiYaml_Throws()
    {
        using var tv = new TempVault();
        var missing = Path.Combine(tv.Path, "not-a-vault");
        Directory.CreateDirectory(missing);

        var ex = Assert.Throws<ValidationException>(() => Vault.Resolve(missing, _ => null, tv.Path));
        Assert.Equal("no-vault", ex.Code);
    }

    [Fact]
    public void Resolve_ExplicitFlagPointingNowhere_Throws()
    {
        using var tv = new TempVault();
        var ex = Assert.Throws<ValidationException>(
            () => Vault.Resolve(Path.Combine(tv.Path, "nope"), _ => null, tv.Path));
        Assert.Equal("no-vault", ex.Code);
    }

    [Fact]
    public void Resolve_EnvVarWithoutWikiYaml_Throws()
    {
        using var tv = new TempVault();
        var missing = Path.Combine(tv.Path, "not-a-vault");
        Directory.CreateDirectory(missing);

        var ex = Assert.Throws<ValidationException>(
            () => Vault.Resolve(null, k => k == "WIKI_VAULT" ? missing : null, tv.Path));
        Assert.Equal("no-vault", ex.Code);
    }

    [Fact]
    public void Resolve_ExplicitFlagWithWikiYaml_Succeeds()
    {
        using var tv = new TempVault();
        File.WriteAllText(Path.Combine(tv.Path, "wiki.yaml"), "version: 1");
        var v = Vault.Resolve(tv.Path, _ => null, Path.GetTempPath());
        Assert.Equal(tv.Path, v.Root);
    }

    // `wiki init` is the command that CREATES the wiki.yaml, so it takes the
    // At() path-model factory rather than Resolve()'s user-input validation -
    // otherwise no vault could ever be scaffolded.
    [Fact]
    public void At_AcceptsAnyDirectory_WithoutRequiringWikiYaml()
    {
        using var tv = new TempVault();
        var fresh = Path.Combine(tv.Path, "fresh");
        var v = Vault.At(fresh);
        Assert.Equal(Path.GetFullPath(fresh), v.Root);
    }

    // -------------------- amendment M at the CLI boundary --------------------

    [Fact]
    public void Cli_ReadCommandOnNonVaultPath_FailsInsteadOfReturningEmpty()
    {
        using var tv = new TempVault();
        var missing = Path.Combine(tv.Path, "not-a-vault");
        Directory.CreateDirectory(missing);

        var sw = new StringWriter();
        var exit = Wiki.App.Main(new[] { "page", "list", "--vault", missing, "--json" }, sw, new StringReader(""));

        Assert.Equal(1, exit);
        using var doc = JsonDocument.Parse(sw.ToString().Trim().Split('\n')[^1]);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("no-vault", doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void Cli_InitIntoFreshDirectory_StillWorks()
    {
        using var tv = new TempVault();
        var fresh = Path.Combine(tv.Path, "fresh");

        var sw = new StringWriter();
        var exit = Wiki.App.Main(new[] { "init", fresh, "--name", "t", "--json" }, sw, new StringReader(""));

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(fresh, "wiki.yaml")));
    }
}
