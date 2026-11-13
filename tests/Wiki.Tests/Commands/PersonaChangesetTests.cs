using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Issue #50: `wiki persona distill` (read side of a pass), `wiki persona
// propose` (one changeset, validated, queued; clears the candidate flags the
// pass considered) and the all-or-nothing review in `wiki review`.
// Observed only through the CLI seam: envelopes, exit codes, files in the
// vault and lines in the audit log.
public class PersonaChangesetTests
{
    private static JsonElement Data(CliResult r) => (JsonElement)r.Envelope.Data!;

    // Init with the scaffolded `article` category endorsed (persona-source)
    // and `note` left alone.
    private static void Init(TempVault tv)
    {
        tv.Run("init", tv.Path, "--name", "t", "--json");
        var path = Path.Combine(tv.Path, "wiki.yaml");
        var text = File.ReadAllText(path);
        var anchor = "description: \"Web articles and blog posts\"";
        Assert.Contains(anchor, text);
        File.WriteAllText(path, text.Replace(anchor, anchor + "\n    persona-source: true"));
    }

    private static string AddSource(TempVault tv, string category, string name)
    {
        var src = Path.Combine(tv.Path, name + ".md");
        File.WriteAllText(src, "Content of " + name);
        var r = tv.Run("source", "add", src, "--category", category, "--title", name, "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    // A summary page citing a new source in `category`; returns (id, path).
    private static (string Id, string Path) Page(TempVault tv, string title, string category = "article", string? body = null)
    {
        var source = AddSource(tv, category, title.ToLowerInvariant().Replace(' ', '-') + "-src");
        var r = tv.RunStdin(body ?? "Body of " + title, "page", "upsert", "--type", "summary", "--title", title,
            "--summary", title + " summary", "--sources", source, "--stdin", "--json");
        Assert.Equal(0, r.ExitCode);
        return (Data(r).GetProperty("id").GetString()!, Data(r).GetProperty("path").GetString()!);
    }

    private static string Read(TempVault tv, string rel) => File.ReadAllText(Path.Combine(tv.Path, rel));

    [Fact]
    public void Distill_ListsCandidatePages_AndTheCurrentPersona()
    {
        using var tv = new TempVault(); Init(tv);
        var endorsed = Page(tv, "Craft note");
        Page(tv, "Plain note", category: "meeting-transcript");

        var r = tv.Run("persona", "distill", "--json");

        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        var candidate = Assert.Single(data.GetProperty("candidates").EnumerateArray());
        Assert.Equal(endorsed.Id, candidate.GetProperty("id").GetString());
        Assert.Equal("craft-note", candidate.GetProperty("slug").GetString());
        Assert.Equal(endorsed.Path, candidate.GetProperty("path").GetString());
        Assert.False(data.GetProperty("persona").GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("pending").ValueKind);
    }
}
