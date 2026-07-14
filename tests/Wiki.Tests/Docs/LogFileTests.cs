using Xunit;
using Wiki.Core;
using Wiki.Docs;

namespace Wiki.Tests.Docs;

public class LogFileTests
{
    [Fact]
    public void Append_CreatesLogFileWithCorrectFormat()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        System.IO.File.WriteAllText(System.IO.Path.Combine(tv.Path, "wiki.yaml"), "version: 1");
        var v = Vault.Resolve(tv.Path, _ => null, tv.Path);

        var utcIso = "2026-07-14T12:34:56Z";
        var op = "create";
        var subject = "page.md";
        var detail = "created new page";

        LogFile.Append(v, utcIso, op, subject, detail);

        var content = System.IO.File.ReadAllText(v.LogPath);
        Assert.Equal("## [2026-07-14T12:34:56Z] create | page.md | created new page\n", content);
    }

    [Fact]
    public void Append_AddsMultipleLinesInOrder()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        System.IO.File.WriteAllText(System.IO.Path.Combine(tv.Path, "wiki.yaml"), "version: 1");
        var v = Vault.Resolve(tv.Path, _ => null, tv.Path);

        LogFile.Append(v, "2026-07-14T12:34:56Z", "create", "page1.md", "first entry");
        LogFile.Append(v, "2026-07-14T12:34:57Z", "update", "page2.md", "second entry");

        var content = System.IO.File.ReadAllText(v.LogPath);
        var expected = "## [2026-07-14T12:34:56Z] create | page1.md | first entry\n## [2026-07-14T12:34:57Z] update | page2.md | second entry\n";
        Assert.Equal(expected, content);
    }
}
