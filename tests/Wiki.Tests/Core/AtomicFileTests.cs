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
}
