using System.IO;
using System.Text.Json;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 24: `wiki source retract <id> --reason "…" [--purge]` - the
// retraction cascade (spec §14), the last M3 task. In order: source
// frontmatter -> retracted; the source's summary-type page(s) -> archived
// (no issue); every OTHER citing page -> needs-review + a filed `retraction`
// issue (one per page); index regenerated; a `retract` log line written.
// `--purge` additionally strips the raw file's body, leaving a metadata
// stub (frontmatter only) that still resolves via the idmap.
public class RetractTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string RawDir(TempVault tv) => Path.Combine(tv.Path, "raw");
    private static string LogPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "log.md");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string AddSource(TempVault tv, string fileName, string content, string category, string title)
    {
        var src = Path.Combine(tv.Path, fileName);
        File.WriteAllText(src, content);
        var r = tv.Run("source", "add", src, "--category", category, "--title", title, "--json");
        Assert.Equal(0, r.ExitCode);
        return Data(r).GetProperty("id").GetString()!;
    }

    private static string RawFilePath(TempVault tv, string sourceId) => Path.Combine(RawDir(tv), sourceId + ".md");

    // -------------------- the cascade, per spec §14 --------------------

    [Fact]
    public void Retract_Cascade_RetractsSource_ArchivesSummary_FlagsOtherCitingPage_FilesOneIssue()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "# transcript\nhello", "meeting-transcript", "Contoso mtg");

        var summary = tv.RunStdin("Summary body.", "page", "upsert", "--type", "summary",
            "--title", "Contoso mtg summary", "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, summary.ExitCode);
        var summarySlug = Data(summary).GetProperty("slug").GetString()!;

        var concept = tv.RunStdin("[[contoso-mtg-summary]] talks about pricing.", "page", "upsert",
            "--type", "concept", "--title", "Pricing model", "--summary", "s",
            "--sources", sourceId, "--json");
        Assert.Equal(0, concept.ExitCode);
        var conceptId = Data(concept).GetProperty("id").GetString()!;
        var conceptSlug = Data(concept).GetProperty("slug").GetString()!;

        var retract = tv.Run("source", "retract", sourceId, "--reason", "author retracted the claim", "--json");
        Assert.Equal(0, retract.ExitCode);
        var data = Data(retract);
        Assert.Equal(sourceId, data.GetProperty("id").GetString());
        Assert.Equal("retracted", data.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("archivedSummaries").GetArrayLength());
        Assert.Equal(summarySlug, data.GetProperty("archivedSummaries")[0].GetString());
        Assert.Equal(1, data.GetProperty("affectedPages").GetArrayLength());
        Assert.Equal(conceptSlug, data.GetProperty("affectedPages")[0].GetString());
        Assert.False(data.GetProperty("purged").GetBoolean());

        // Source itself: retracted.
        var show = tv.Run("source", "show", sourceId, "--json");
        Assert.Equal("retracted", Data(show).GetProperty("status").GetString());
        Assert.Equal("# transcript\nhello", Data(show).GetProperty("body").GetString());

        // Summary page: archived, no issue filed for it.
        var summaryShow = tv.Run("page", "show", summarySlug, "--json");
        Assert.Equal("archived", Data(summaryShow).GetProperty("status").GetString());

        // Concept page: needs-review.
        var conceptShow = tv.Run("page", "show", conceptSlug, "--json");
        Assert.Equal("needs-review", Data(conceptShow).GetProperty("status").GetString());

        // Exactly one retraction issue, filed against the concept (not the summary).
        var issues = tv.Run("issues", "list", "--kind", "retraction", "--json");
        Assert.Equal(0, issues.ExitCode);
        var issueData = Data(issues);
        Assert.Equal(1, issueData.GetArrayLength());
        Assert.Equal(conceptSlug, issueData[0].GetProperty("subject").GetString());
        Assert.Contains("author retracted the claim", issueData[0].GetProperty("detail").GetString());
        Assert.DoesNotContain(issueData.EnumerateArray(), i => i.GetProperty("subject").GetString() == summarySlug);

        // Index regenerated: archived summary drops out, concept stays (needs-review isn't archived).
        var index = File.ReadAllText(IndexPath(tv));
        Assert.DoesNotContain(summarySlug, index);
        Assert.Contains(conceptSlug, index);

        // Log line records the reason.
        var log = File.ReadAllText(LogPath(tv));
        Assert.Contains("retract", log);
        Assert.Contains("author retracted the claim", log);

        _ = conceptId; // asserted via slug above; kept for clarity of what was created
    }

    [Fact]
    public void Retract_NoCitingPages_ArchivesNothing_FlagsNothing_StillRetractsSource()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "content", "article", "Lonely source");

        var retract = tv.Run("source", "retract", sourceId, "--reason", "bad data", "--json");
        Assert.Equal(0, retract.ExitCode);
        var data = Data(retract);
        Assert.Equal(0, data.GetProperty("archivedSummaries").GetArrayLength());
        Assert.Equal(0, data.GetProperty("affectedPages").GetArrayLength());

        var show = tv.Run("source", "show", sourceId, "--json");
        Assert.Equal("retracted", Data(show).GetProperty("status").GetString());
    }

    // -------------------- --purge --------------------

    [Fact]
    public void Retract_WithPurge_StripsRawBody_KeepsResolvableStub_RecordsRetraction()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "sensitive content that must go away", "article", "Purge me");

        var retract = tv.Run("source", "retract", sourceId, "--reason", "deletion request", "--purge", "--json");
        Assert.Equal(0, retract.ExitCode);
        Assert.True(Data(retract).GetProperty("purged").GetBoolean());

        // The raw file still exists at the same path (id still resolves).
        var rawPath = RawFilePath(tv, sourceId);
        Assert.True(File.Exists(rawPath));
        var rawText = File.ReadAllText(rawPath);
        Assert.DoesNotContain("sensitive content", rawText);

        // Stub still parses as valid source frontmatter: id/status survive.
        var (scalars, lists, body) = Wiki.Core.Frontmatter.ReadBlock(rawText);
        var front = Wiki.Core.SourceFrontmatter.FromRaw(scalars, lists);
        Assert.Equal(sourceId, front.Id);
        Assert.Equal(Wiki.Core.SourceStatus.Retracted, front.Status);
        Assert.Equal("Purge me", front.Title);
        Assert.Equal(string.Empty, body);

        // `source show` still resolves the id post-purge.
        var show = tv.Run("source", "show", sourceId, "--json");
        Assert.Equal(0, show.ExitCode);
        Assert.Equal("retracted", Data(show).GetProperty("status").GetString());
        Assert.Equal(string.Empty, Data(show).GetProperty("body").GetString());
    }

    [Fact]
    public void Retract_WithoutPurge_RawBodyStaysIntact()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "content that stays", "article", "Kept");

        var retract = tv.Run("source", "retract", sourceId, "--reason", "just retract", "--json");
        Assert.Equal(0, retract.ExitCode);
        Assert.False(Data(retract).GetProperty("purged").GetBoolean());

        var rawText = File.ReadAllText(RawFilePath(tv, sourceId));
        Assert.Contains("content that stays", rawText);
    }

    // -------------------- rejections: nothing lands --------------------

    [Fact]
    public void Retract_UnknownId_NotFound_NothingChanges()
    {
        using var tv = new TempVault(); Init(tv);

        var filesBefore = Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length;
        var logBefore = File.Exists(LogPath(tv)) ? File.ReadAllText(LogPath(tv)) : "";

        var r = tv.Run("source", "retract", "01AAAAAAAAAAAAAAAAAAAAAAAA", "--reason", "x", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");

        Assert.Equal(filesBefore, Directory.GetFiles(tv.Path, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(logBefore, File.Exists(LogPath(tv)) ? File.ReadAllText(LogPath(tv)) : "");
    }

    [Fact]
    public void Retract_IdResolvesToPage_NotSource_NotFound()
    {
        using var tv = new TempVault(); Init(tv);
        var created = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var pageId = Data(created).GetProperty("id").GetString()!;

        var r = tv.Run("source", "retract", pageId, "--reason", "x", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "not-found");
    }

    [Fact]
    public void Retract_AlreadyRetracted_Rejected_NothingChangesAgain()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "content", "article", "T");

        var first = tv.Run("source", "retract", sourceId, "--reason", "first reason", "--json");
        Assert.Equal(0, first.ExitCode);

        var rawBefore = File.ReadAllText(RawFilePath(tv, sourceId));
        var logBefore = File.ReadAllText(LogPath(tv));

        var second = tv.Run("source", "retract", sourceId, "--reason", "second reason", "--json");
        Assert.Equal(1, second.ExitCode);
        Assert.Contains(second.Envelope.Errors, e => e.Code == "already-retracted");

        // Nothing changed on the second (rejected) call.
        Assert.Equal(rawBefore, File.ReadAllText(RawFilePath(tv, sourceId)));
        Assert.Equal(logBefore, File.ReadAllText(LogPath(tv)));
    }

    [Fact]
    public void Retract_EmptyReason_Rejected()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "content", "article", "T");

        var r = tv.Run("source", "retract", sourceId, "--reason", "  ", "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Envelope.Errors, e => e.Code == "reason-required");

        var show = tv.Run("source", "show", sourceId, "--json");
        Assert.Equal("active", Data(show).GetProperty("status").GetString());
    }

    [Fact]
    public void Retract_MissingReasonFlag_CliRejectsBeforeService()
    {
        using var tv = new TempVault(); Init(tv);
        var sourceId = AddSource(tv, "input.md", "content", "article", "T");

        var r = tv.Run("source", "retract", sourceId, "--json");
        Assert.NotEqual(0, r.ExitCode);
    }
}
