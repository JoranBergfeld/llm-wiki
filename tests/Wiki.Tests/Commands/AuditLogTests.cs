using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #44: the hash-chained audit log. Every mutating CLI command appends
// exactly one `|`-separated line ending in `prev=` and `hash=`; read commands
// append none. Observed only through files in the vault and `wiki audit`.
public class AuditLogTests
{
    private static readonly Regex Tail = new(@" \| prev=(?<prev>[0-9a-f]{64}) \| hash=(?<hash>[0-9a-f]{64})$");

    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "audit", "audit.log");

    private static string[] Lines(TempVault tv)
        => File.Exists(LogPath(tv))
            ? File.ReadAllText(LogPath(tv)).Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            : [];

    private static string[] Fields(string line) => line.Split(" | ");

    [Fact]
    public void Init_WritesTheFirstLine_ChainedFromGenesis()
    {
        using var tv = new TempVault();
        Assert.Equal(0, Init(tv).ExitCode);

        var lines = Lines(tv);
        var line = Assert.Single(lines);
        var m = Tail.Match(line);
        Assert.True(m.Success, line);
        Assert.Equal(new string('0', 64), m.Groups["prev"].Value);
        Assert.Equal("init", Fields(line)[1]);
        Assert.Contains("via=cli", Fields(line));
    }

    [Fact]
    public void EachMutation_AppendsOneLine_WhosePrevIsTheLastHash()
    {
        using var tv = new TempVault();
        Init(tv);

        var r = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        Assert.Equal(0, r.ExitCode);

        var lines = Lines(tv);
        Assert.Equal(2, lines.Length);
        Assert.Equal("page.upsert", Fields(lines[1])[1]);
        Assert.Equal(Tail.Match(lines[0]).Groups["hash"].Value, Tail.Match(lines[1]).Groups["prev"].Value);
        Assert.NotEqual(Tail.Match(lines[0]).Groups["hash"].Value, Tail.Match(lines[1]).Groups["hash"].Value);
    }
}
