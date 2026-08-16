using System.IO;
using System.Text;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #7 (--body-file as an alternative to --stdin) and issue #6 (the
// process entrypoint forces UTF-8 on both standard streams).
public class BodyInputTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string WriteTemp(TempVault tv, string name, string content)
    {
        // Deliberately OUTSIDE the vault's managed trees: --body-file input is
        // input, like the file `wiki source add` takes, not vault content.
        var path = Path.Combine(Path.GetTempPath(), "wiki-body-" + System.Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void PageUpsert_BodyFile_MatchesStdinResult()
    {
        const string body = "Body from a file.\n\nSecond paragraph.";

        using var viaStdin = new TempVault(); Init(viaStdin);
        var a = viaStdin.RunStdin(body, "page", "upsert", "--type", "concept",
            "--title", "Routing", "--summary", "How routing works", "--stdin", "--json");
        Assert.Equal(0, a.ExitCode);

        using var viaFile = new TempVault(); Init(viaFile);
        var file = WriteTemp(viaFile, "body.md", body);
        var b = viaFile.Run("page", "upsert", "--type", "concept",
            "--title", "Routing", "--summary", "How routing works", "--body-file", file, "--json");
        Assert.Equal(0, b.ExitCode);

        var slugA = ((JsonElement)a.Envelope.Data!).GetProperty("slug").GetString();
        var slugB = ((JsonElement)b.Envelope.Data!).GetProperty("slug").GetString();
        Assert.Equal(slugA, slugB);

        // Ids and timestamps are minted per run, so compare everything that
        // is a function of the INPUT: the stored body, plus the frontmatter
        // fields the caller supplied.
        var (scalarsA, _, bodyA) = Wiki.Core.Frontmatter.ReadBlock(
            File.ReadAllText(Path.Combine(viaStdin.Path, "wiki", "concepts", slugA + ".md")));
        var (scalarsB, _, bodyB) = Wiki.Core.Frontmatter.ReadBlock(
            File.ReadAllText(Path.Combine(viaFile.Path, "wiki", "concepts", slugB + ".md")));

        Assert.Equal(body, bodyA);
        Assert.Equal(bodyA, bodyB);
        Assert.Equal(scalarsA["title"], scalarsB["title"]);
        Assert.Equal(scalarsA["summary"], scalarsB["summary"]);
        Assert.Equal(scalarsA["type"], scalarsB["type"]);
        Assert.Equal(scalarsA["status"], scalarsB["status"]);

        File.Delete(file);
    }

    [Fact]
    public void SchemaPropose_BodyFile_MatchesStdinResult()
    {
        const string text = "Rewritten conventions section.\nSecond line.";

        using var tv = new TempVault(); Init(tv);
        var file = WriteTemp(tv, "section.md", text);

        var r = tv.Run("schema", "propose", "--section", "Conventions", "--body-file", file, "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(text, ((JsonElement)r.Envelope.Data!).GetProperty("newText").GetString());

        File.Delete(file);
    }

    [Fact]
    public void BothStdinAndBodyFile_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var file = WriteTemp(tv, "conflict.md", "body");

        var r = tv.RunStdin("other body", "page", "upsert", "--type", "concept",
            "--title", "Conflict", "--summary", "s", "--stdin", "--body-file", file, "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "body-source-conflict");
        // Nothing landed.
        Assert.False(Directory.Exists(Path.Combine(tv.Path, "wiki", "concepts"))
            && Directory.GetFiles(Path.Combine(tv.Path, "wiki", "concepts"), "*.md").Length > 0);

        File.Delete(file);
    }

    [Fact]
    public void MissingBodyFile_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var missing = Path.Combine(Path.GetTempPath(), "wiki-body-missing-" + System.Guid.NewGuid().ToString("N") + ".md");

        var r = tv.Run("page", "upsert", "--type", "concept",
            "--title", "Gone", "--summary", "s", "--body-file", missing, "--json");

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "body-file-not-found");
    }

    // The whole point of the flag: a body carrying every character that is
    // live in a shell (quotes, $, backticks) plus CRLF line endings survives
    // intact, because it never passes through a shell.
    [Fact]
    public void BodyFile_ShellHostileContent_RoundTrips()
    {
        using var tv = new TempVault(); Init(tv);

        var body = "He said \"hello\" and $HOME and `backticks`.\r\nSecond line — em dash, café, 東京.\r\n";
        var path = Path.Combine(Path.GetTempPath(), "wiki-body-" + System.Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, body, new UTF8Encoding(false));

        var r = tv.Run("page", "upsert", "--type", "concept",
            "--title", "Hostile", "--summary", "quoting torture test", "--body-file", path, "--json");
        Assert.Equal(0, r.ExitCode);

        var id = ((JsonElement)r.Envelope.Data!).GetProperty("id").GetString();
        var show = tv.Run("page", "show", id!, "--json");
        var shown = ((JsonElement)show.Envelope.Data!).GetProperty("body").GetString();

        // CRLF normalises to LF on the way through the frontmatter parser (it
        // does for every body, whatever the input channel); every other
        // character is byte-identical.
        Assert.Equal(body.Replace("\r\n", "\n"), shown);

        File.Delete(path);
    }

    [Fact]
    public void BodyFile_WithUtf8Bom_DoesNotLeakTheBomIntoTheBody()
    {
        using var tv = new TempVault(); Init(tv);
        var path = Path.Combine(Path.GetTempPath(), "wiki-body-" + System.Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, "clean first character", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var r = tv.Run("page", "upsert", "--type", "concept",
            "--title", "Bom", "--summary", "s", "--body-file", path, "--json");
        Assert.Equal(0, r.ExitCode);

        var id = ((JsonElement)r.Envelope.Data!).GetProperty("id").GetString();
        var shown = ((JsonElement)tv.Run("page", "show", id!, "--json").Envelope.Data!)
            .GetProperty("body").GetString();
        Assert.Equal("clean first character", shown);

        File.Delete(path);
    }

    // Issue #6: the process entrypoint's stream contract. The in-proc Main
    // overload takes its own streams, so this asserts the factory the real
    // entrypoint uses rather than going through a command.
    [Fact]
    public void StandardStreams_AreUtf8_WithoutABom()
    {
        var (stdout, stdin) = Wiki.App.OpenStandardStreams();
        using (stdout)
        using (stdin)
        {
            Assert.Equal("utf-8", stdout.Encoding.WebName);
            Assert.Equal("utf-8", stdin.CurrentEncoding.WebName);
            // A BOM in front of the --json envelope breaks strict parsers.
            Assert.Empty(stdout.Encoding.GetPreamble());
        }
    }
}
