using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #43: the CLI is hosted in-process by the MCP server, where argv can
// carry values supplied by a remote client. System.CommandLine 2.0.0 expands
// `@file` tokens into the contents of that file by default, which would let a
// client make the server read an arbitrary file and splice it into argv
// (including `--vault`). Response files are therefore off for the whole CLI:
// an `@token` is always a literal value.
public class ResponseFileTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string WriteResponseFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "wiki-rsp-" + System.Guid.NewGuid().ToString("N") + ".rsp");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void AtToken_AsPositionalValue_IsLiteralNotExpanded()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("This line mentions platform engineering.",
            "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        var rsp = WriteResponseFile("platform");
        try
        {
            var r = tv.Run("search", "@" + rsp, "--json");

            // Expanded, the search term would be "platform" and hit Contoso.
            // Literal, the term is "@<path>", which appears nowhere.
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(0, Data(r).GetProperty("hits").GetArrayLength());
        }
        finally { File.Delete(rsp); }
    }

    [Fact]
    public void AtToken_AsOptionValue_IsLiteralNotExpanded()
    {
        using var tv = new TempVault(); Init(tv);
        var rsp = WriteResponseFile("entity");
        try
        {
            // Expanded, --type would be "entity" and the search would succeed.
            // Literal, "@<path>" is not a page type and is rejected.
            var r = tv.Run("search", "anything", "--type", "@" + rsp, "--json");

            Assert.Equal(1, r.ExitCode);
            Assert.False(r.Envelope.Ok);
        }
        finally { File.Delete(rsp); }
    }

    [Fact]
    public void AtToken_CarryingVaultOption_CannotRebindTheVault()
    {
        using var tv = new TempVault(); Init(tv);
        using var other = new TempVault(); other.Run("init", other.Path, "--name", "other", "--json");
        other.RunStdin("This line mentions platform engineering.",
            "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        var rsp = WriteResponseFile("platform\n--vault\n" + other.Path);
        try
        {
            // Expanded, argv would become `search platform --vault <other>`
            // and find Contoso in the other vault. Literal, `@<path>` is just
            // the search term against the bound vault.
            var r = tv.Run("search", "@" + rsp, "--vault", tv.Path, "--json");

            Assert.Equal(0, r.ExitCode);
            Assert.Equal(0, Data(r).GetProperty("hits").GetArrayLength());
        }
        finally { File.Delete(rsp); }
    }
}
