using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class AtomicFileTests
{
    [Fact]
    public void Write_CreatesFile_AndIsAtomicViaTempRename()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var p = System.IO.Path.Combine(dir, "x.md");
        AtomicFile.Write(p, "hello");
        Assert.Equal("hello", System.IO.File.ReadAllText(p));
        Assert.Empty(System.IO.Directory.GetFiles(dir, "*.tmp")); // temp cleaned up
    }

    [Fact]
    public void GuardWritable_RejectsRawAndGeneratedDocs()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var v = Wiki.Core.Vault.Resolve(tv.Path, _ => null, tv.Path);
        Assert.Throws<ValidationException>(() => AtomicFile.GuardWritable(v, System.IO.Path.Combine(v.RawDir, "a.md")));
        Assert.Throws<ValidationException>(() => AtomicFile.GuardWritable(v, v.IndexPath));
    }

    [Fact]
    public void GuardWritable_RejectsLogPath()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        var v = Wiki.Core.Vault.Resolve(tv.Path, _ => null, tv.Path);
        var ex = Assert.Throws<ValidationException>(() => AtomicFile.GuardWritable(v, v.LogPath));
        Assert.Equal("protected-path", ex.Code);
    }

    [Fact]
    public void GuardWritable_RejectsCaseVariantPaths()
    {
        // On APFS/NTFS these resolve to the same protected files as their lowercase
        // spellings, so the guard must refuse them regardless of casing.
        using var tv = new Wiki.Tests.Support.TempVault();
        var v = Wiki.Core.Vault.Resolve(tv.Path, _ => null, tv.Path);

        var rawVariant = System.IO.Path.Combine(v.Root, "RAW", "a.md");
        var indexVariant = System.IO.Path.Combine(v.WikiDir, "INDEX.md");

        Assert.Equal("protected-path", Assert.Throws<ValidationException>(() => AtomicFile.GuardWritable(v, rawVariant)).Code);
        Assert.Equal("protected-path", Assert.Throws<ValidationException>(() => AtomicFile.GuardWritable(v, indexVariant)).Code);
    }
}
