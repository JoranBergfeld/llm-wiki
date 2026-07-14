using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 15: `wiki reindex` - rebuilds idmap.json and index.md from the
// markdown alone. The headline property is byte-identity: deleting
// idmap.json and running reindex must reproduce the exact same bytes,
// because IdMap.Save (Task 9) always serializes a fresh, ordinal-sorted
// Dictionary<string,string> regardless of insertion order. M1 scope only -
// ledger/issues state rebuild is deferred to Task 27.
public class ReindexTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");

    // Envelope.Data is `object?` and doesn't round-trip back into a typed
    // DTO through the generic deserializer TempVault uses - pull fields
    // straight out of the raw JSON line instead (same technique as
    // PageShowListTests/PageUpsertUpdateTests).
    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public void Reindex_ReproducesIdmapByteIdentically()
    {
        // Brief's given snippet omits --json, but TempVault.Run/RunStdin always
        // parse the last output line as a JSON envelope - without --json, init's
        // human-readable Spectre line ("OK Initialized vault ...") fails to parse
        // (same fix PageUpsertCreateTests applies to this exact brief pattern).
        using var tv = new TempVault(); tv.Run("init", tv.Path, "--name", "t", "--json");
        tv.RunStdin("a", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");
        tv.RunStdin("b", "page", "upsert", "--type", "concept", "--title", "Deal", "--summary", "s", "--json");
        var before = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "idmap.json"));
        File.Delete(Path.Combine(tv.Path, ".wiki", "idmap.json"));
        var r = tv.Run("reindex", "--json");
        Assert.Equal(0, r.ExitCode);
        var after = File.ReadAllText(Path.Combine(tv.Path, ".wiki", "idmap.json"));
        Assert.Equal(before, after); // byte-identical (amendment A)
    }

    [Fact]
    public void Reindex_PicksUpRawSourceId_InIdmap()
    {
        using var tv = new TempVault(); Init(tv);

        // No `source add` yet (Task 16) - hand-write a minimal raw/<ulid>.md
        // with valid source frontmatter, the same shape FrontmatterTests uses.
        const string sourceId = "01J9ZKM1E8W1R2X3Y4Z5A6B7C8";
        var sourceText = """
            ---
            id: 01J9ZKM1E8W1R2X3Y4Z5A6B7C8
            type: source
            title: "Vendor security questionnaire"
            category: document
            added: 2026-07-13
            sha256: deadbeefcafef00d
            origin: https://example.com/contoso/vsq.pdf
            status: active
            ---
            Raw notes about the source.
            """;
        var rawDir = Path.Combine(tv.Path, "raw");
        Directory.CreateDirectory(rawDir);
        File.WriteAllText(Path.Combine(rawDir, sourceId + ".md"), sourceText);

        // A stray non-source file under raw/assets/ must not be scanned as a
        // source (it has no source frontmatter and would throw if it were).
        var assetsDir = Path.Combine(rawDir, "assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "logo.png"), "not a real image, just a placeholder");

        var r = tv.Run("reindex", "--json");
        Assert.Equal(0, r.ExitCode);

        var idmapText = File.ReadAllText(IdMapPath(tv));
        Assert.Contains(sourceId, idmapText);
        Assert.Contains("raw/" + sourceId + ".md", idmapText);
    }

    [Fact]
    public void Reindex_RegeneratesIndexMd_MatchingNormalUpsertOutput()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body one.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        tv.RunStdin("Body two.", "page", "upsert", "--type", "concept", "--title", "Deal", "--summary", "s2", "--json");

        // index.md already reflects both pages after the upserts above (each
        // upsert regenerates it). Wipe idmap.json AND index.md, then reindex
        // must regenerate index.md to the same content from the markdown alone.
        var before = File.ReadAllText(IndexPath(tv));
        File.Delete(IdMapPath(tv));
        File.Delete(IndexPath(tv));

        var r = tv.Run("reindex", "--json");
        Assert.Equal(0, r.ExitCode);

        var after = File.ReadAllText(IndexPath(tv));
        Assert.Equal(before, after);
    }

    [Fact]
    public void Reindex_IsDeterministic_RunningTwiceYieldsIdenticalIdmap()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body one.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");
        tv.RunStdin("Body two.", "page", "upsert", "--type", "concept", "--title", "Deal", "--summary", "s2", "--json");

        var first = tv.Run("reindex", "--json");
        Assert.Equal(0, first.ExitCode);
        var afterFirst = File.ReadAllText(IdMapPath(tv));

        var second = tv.Run("reindex", "--json");
        Assert.Equal(0, second.ExitCode);
        var afterSecond = File.ReadAllText(IdMapPath(tv));

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Reindex_ReportsCounts()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body one.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s1", "--json");

        const string sourceId = "01J9ZKM1E8W1R2X3Y4Z5A6B7C8";
        var sourceText = """
            ---
            id: 01J9ZKM1E8W1R2X3Y4Z5A6B7C8
            type: source
            title: "Vendor security questionnaire"
            category: document
            added: 2026-07-13
            sha256: deadbeefcafef00d
            origin: https://example.com/contoso/vsq.pdf
            status: active
            ---
            Raw notes about the source.
            """;
        File.WriteAllText(Path.Combine(tv.Path, "raw", sourceId + ".md"), sourceText);

        var r = tv.Run("reindex", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Envelope.Ok);

        var data = Data(r);
        Assert.Equal(1, data.GetProperty("pages").GetInt32());
        Assert.Equal(1, data.GetProperty("sources").GetInt32());
        Assert.Equal(2, data.GetProperty("idmapEntries").GetInt32());
    }
}
