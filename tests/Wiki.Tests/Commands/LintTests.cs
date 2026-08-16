using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wiki.Core;
using Wiki.Tests.Support;
using Xunit;

namespace Wiki.Tests.Commands;

// Task 22: `wiki lint` - runs every advisory check in spec §11's table,
// files/refreshes findings in .wiki/issues.json (Issues.Upsert's
// occurrence-merging semantics), and writes .wiki/lint.json's `lastRun`
// (amendment D). `--fix-links` additionally repairs mechanical wikilink
// targets/idmap entries broken by a detected rename-drift.
//
// Several checks (stale, needs-review-backlog, pending-backlog) need a page
// dated more than a threshold in the past; PageService always stamps
// created/updated with the real clock and has no CLI knob for backdating, so
// these tests hand-rewrite a page's `updated` frontmatter field directly on
// disk after creating it through the CLI - the same "hand-write a raw file"
// technique SourceQueryTests/IssuesCommandTests already use to reach states
// the CLI itself can't produce.
public class LintTests
{
    private static CliResult Init(TempVault tv) => tv.Run("init", tv.Path, "--name", "t", "--json");

    private static string LintJsonPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "lint.json");
    private static string IdMapPath(TempVault tv) => Path.Combine(tv.Path, ".wiki", "idmap.json");
    private static string IndexPath(TempVault tv) => Path.Combine(tv.Path, "wiki", "index.md");
    private static string EntityPath(TempVault tv, string slug) => Path.Combine(tv.Path, "wiki", "entities", slug + ".md");
    private static string SummaryPath(TempVault tv, string slug) => Path.Combine(tv.Path, "wiki", "summaries", slug + ".md");

    private static JsonElement Data(CliResult r)
    {
        var line = r.Stdout.Trim().Split('\n')[^1];
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static string ExtractId(CliResult r) => Data(r).GetProperty("id").GetString()!;

    // Rewrites a page file's frontmatter `updated` field in place, everything
    // else preserved - simulates "this page was last touched N days ago"
    // without going through any CLI write path.
    private static void BackdateUpdated(string filePath, string newUpdatedYyyyMmDd)
    {
        var doc = PageDoc.Parse(File.ReadAllText(filePath));
        var f = doc.Front;
        var backdated = new PageFrontmatter
        {
            Id = f.Id,
            Type = f.Type,
            Title = f.Title,
            Status = f.Status,
            Created = f.Created,
            Updated = newUpdatedYyyyMmDd,
            Summary = f.Summary,
            Sources = f.Sources,
            Tags = f.Tags,
        };
        File.WriteAllText(filePath, new PageDoc(backdated, doc.Body).Serialize());
    }

    private static string DaysAgo(int days) => DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

    private static JsonElement IssuesList(TempVault tv, string? kind = null)
    {
        var args = kind is null
            ? new[] { "issues", "list", "--json" }
            : new[] { "issues", "list", "--kind", kind, "--json" };
        var r = tv.Run(args);
        Assert.Equal(0, r.ExitCode);
        return Data(r);
    }

    // -------------------- brief's headline scenario --------------------

    [Fact]
    public void Lint_FilesOrphanAndOversizeIssues_WritesLintJson()
    {
        using var tv = new TempVault(); Init(tv);

        // Overview is excluded from the orphan check by definition, so a
        // huge overview body isolates the oversize finding from orphan -
        // exactly one page triggers each check.
        var hugeBody = string.Join("\n", Enumerable.Repeat("filler line", 401));
        tv.RunStdin(hugeBody, "page", "upsert", "--type", "overview", "--title", "Overview", "--summary", "s", "--json");

        tv.RunStdin("Just a page, nothing links here.", "page", "upsert", "--type", "entity",
            "--title", "Solo", "--summary", "s2", "--json");

        var r = tv.Run("lint", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(2, data.GetProperty("filed").GetInt32());
        Assert.Equal(0, data.GetProperty("refreshed").GetInt32());

        Assert.True(File.Exists(LintJsonPath(tv)));
        var lintJson = File.ReadAllText(LintJsonPath(tv));
        Assert.Contains("lastRun", lintJson);
        using (var doc = JsonDocument.Parse(lintJson))
        {
            var lastRun = doc.RootElement.GetProperty("lastRun").GetString();
            Assert.True(DateTimeOffset.TryParse(lastRun, out _));
        }

        var orphanIssues = IssuesList(tv, "orphan");
        Assert.Equal(1, orphanIssues.GetArrayLength());
        Assert.Equal("solo", orphanIssues[0].GetProperty("subject").GetString());

        var oversizeIssues = IssuesList(tv, "oversize");
        Assert.Equal(1, oversizeIssues.GetArrayLength());
        Assert.Equal("overview", oversizeIssues[0].GetProperty("subject").GetString());
    }

    [Fact]
    public void Lint_Rerun_RefreshesOccurrences_NotDuplicated()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Solo page, no links in or out.", "page", "upsert", "--type", "entity",
            "--title", "Solo", "--summary", "s", "--json");

        var first = tv.Run("lint", "--json");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(1, Data(first).GetProperty("filed").GetInt32());

        var second = tv.Run("lint", "--json");
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(0, Data(second).GetProperty("filed").GetInt32());
        Assert.Equal(1, Data(second).GetProperty("refreshed").GetInt32());

        var orphanIssues = IssuesList(tv, "orphan");
        Assert.Equal(1, orphanIssues.GetArrayLength()); // one row, not two
        Assert.Equal(2, orphanIssues[0].GetProperty("occurrences").GetInt32());
    }

    // -------------------- dangling-link --------------------

    [Fact]
    public void Lint_DanglingLink_ViaAllowDangling_FilesIssue()
    {
        using var tv = new TempVault(); Init(tv);
        var r = tv.RunStdin("See [[ghost]] for details.", "page", "upsert", "--type", "entity",
            "--title", "Haunted", "--summary", "s", "--allow-dangling", "--json");
        Assert.Equal(0, r.ExitCode);

        var lint = tv.Run("lint", "--json");
        Assert.Equal(0, lint.ExitCode);

        var dangling = IssuesList(tv, "dangling-link");
        Assert.Equal(1, dangling.GetArrayLength());
        Assert.Equal("haunted", dangling[0].GetProperty("subject").GetString());
        Assert.Contains("ghost", dangling[0].GetProperty("detail").GetString());
    }

    // amendment L: the upsert itself files the issue. §11.4 always said
    // --allow-dangling links "are then filed automatically ... rather than
    // silently ignored", but the filing used to wait for the next lint - so
    // an agent that upserted and then read `issues list` saw nothing, while
    // the envelope's `danglingFiled` field claimed otherwise.
    [Fact]
    public void AllowDangling_FilesIssueAtWriteTime_WithoutWaitingForLint()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.RunStdin("See [[ghost]] and [[phantom]].", "page", "upsert", "--type", "entity",
            "--title", "Haunted", "--summary", "s", "--allow-dangling", "--json");
        Assert.Equal(0, r.ExitCode);

        // No lint run in between.
        var dangling = IssuesList(tv, "dangling-link");
        Assert.Equal(1, dangling.GetArrayLength());
        Assert.Equal("haunted", dangling[0].GetProperty("subject").GetString());
        var detail = dangling[0].GetProperty("detail").GetString()!;
        Assert.Contains("ghost", detail);
        Assert.Contains("phantom", detail);
        Assert.Equal(1, dangling[0].GetProperty("occurrences").GetInt32());
    }

    // The upsert-filed issue and the lint-filed one must be ONE record: both
    // file under (DanglingLink, <slug>), so a link that stays dangling
    // accumulates occurrences instead of forking into two issues and
    // corrupting the reflect-loop signal.
    [Fact]
    public void AllowDangling_UpsertFiledIssue_MergesWithLaterLint_NotDuplicated()
    {
        using var tv = new TempVault(); Init(tv);

        tv.RunStdin("See [[ghost]].", "page", "upsert", "--type", "entity",
            "--title", "Haunted", "--summary", "s", "--allow-dangling", "--json");
        Assert.Equal(0, tv.Run("lint", "--json").ExitCode);

        var dangling = IssuesList(tv, "dangling-link");
        Assert.Equal(1, dangling.GetArrayLength());
        Assert.Equal(2, dangling[0].GetProperty("occurrences").GetInt32());
    }

    // An update that introduces a NEW forward reference files against the
    // same page subject, same as create.
    [Fact]
    public void AllowDangling_OnUpdate_FilesIssueAtWriteTime()
    {
        using var tv = new TempVault(); Init(tv);

        var created = tv.RunStdin("No links yet.", "page", "upsert", "--type", "entity",
            "--title", "Haunted", "--summary", "s", "--json");
        var id = ExtractId(created);
        Assert.Equal(0, IssuesList(tv, "dangling-link").GetArrayLength());

        var updated = tv.RunStdin("Now see [[ghost]].", "page", "upsert", "--id", id, "--type", "entity",
            "--title", "Haunted", "--summary", "s", "--allow-dangling", "--json");
        Assert.Equal(0, updated.ExitCode);

        var dangling = IssuesList(tv, "dangling-link");
        Assert.Equal(1, dangling.GetArrayLength());
        Assert.Equal("haunted", dangling[0].GetProperty("subject").GetString());
        Assert.Contains("ghost", dangling[0].GetProperty("detail").GetString());
    }

    // A clean upsert files nothing - the filing is scoped to actual dangling
    // targets, not to every use of the flag.
    [Fact]
    public void AllowDangling_WithNoDanglingTargets_FilesNothing()
    {
        using var tv = new TempVault(); Init(tv);

        var r = tv.RunStdin("Plain body, no links.", "page", "upsert", "--type", "entity",
            "--title", "Haunted", "--summary", "s", "--allow-dangling", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, IssuesList(tv, "dangling-link").GetArrayLength());
    }

    // -------------------- rename-drift + --fix-links --------------------

    [Fact]
    public void Lint_RenameDrift_FilesIssue_AndFixLinksRepairsIdmapAndInboundLinks()
    {
        using var tv = new TempVault(); Init(tv);

        var bravo = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Bravo", "--summary", "s1", "--json");
        var bravoId = ExtractId(bravo);

        tv.RunStdin("See [[bravo]] for more.", "page", "upsert", "--type", "entity",
            "--title", "Alpha", "--summary", "s2", "--json");

        // Simulate an Obsidian-side rename: the file moves on disk, but
        // nothing tells the CLI, so idmap.json still points bravoId at the
        // old path. This is exactly the rename-drift scenario spec §11
        // describes, and it also makes Alpha's [[bravo]] link dangling.
        File.Move(EntityPath(tv, "bravo"), EntityPath(tv, "charlie"));

        var beforeIdmap = File.ReadAllText(IdMapPath(tv));
        var beforeAlphaBody = File.ReadAllText(EntityPath(tv, "alpha"));

        var lint = tv.Run("lint", "--json");
        Assert.Equal(0, lint.ExitCode);

        var renameDrift = IssuesList(tv, "rename-drift");
        Assert.Equal(1, renameDrift.GetArrayLength());
        Assert.Equal(bravoId, renameDrift[0].GetProperty("subject").GetString());

        var dangling = IssuesList(tv, "dangling-link");
        Assert.Equal(1, dangling.GetArrayLength());
        Assert.Equal("alpha", dangling[0].GetProperty("subject").GetString());
        Assert.Contains("bravo", dangling[0].GetProperty("detail").GetString());

        // Plain lint (no --fix-links) must not have touched anything.
        Assert.Equal(beforeIdmap, File.ReadAllText(IdMapPath(tv)));
        Assert.Equal(beforeAlphaBody, File.ReadAllText(EntityPath(tv, "alpha")));

        var fixed_ = tv.Run("lint", "--fix-links", "--json");
        Assert.Equal(0, fixed_.ExitCode);
        var fixedData = Data(fixed_);
        Assert.Equal(1, fixedData.GetProperty("fixLinksIdmapRepaired").GetInt32());
        Assert.Equal(1, fixedData.GetProperty("fixLinksBodiesRewritten").GetInt32());

        var idmapJson = File.ReadAllText(IdMapPath(tv));
        var idmap = JsonSerializer.Deserialize(idmapJson, Wiki.Json.WikiJsonContext.Default.DictionaryStringString)!;
        Assert.Equal("wiki/entities/charlie.md", idmap[bravoId]);

        var alphaBody = File.ReadAllText(EntityPath(tv, "alpha"));
        Assert.Contains("[[charlie]]", alphaBody);
        Assert.DoesNotContain("[[bravo]]", alphaBody);
    }

    // -------------------- index-drift (auto-fix) --------------------

    [Fact]
    public void Lint_IndexDrift_AutoFixesIndex_ButStillFilesIssue()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Body.", "page", "upsert", "--type", "entity", "--title", "Contoso", "--summary", "s", "--json");

        var correctIndex = File.ReadAllText(IndexPath(tv));
        File.WriteAllText(IndexPath(tv), "not a real index\n");

        // No --fix-links: index-drift's auto-fix is unconditional per spec §11.
        var r = tv.Run("lint", "--json");
        Assert.Equal(0, r.ExitCode);
        Assert.True(Data(r).GetProperty("indexRegenerated").GetBoolean());

        Assert.Equal(correctIndex, File.ReadAllText(IndexPath(tv)));

        var drift = IssuesList(tv, "index-drift");
        Assert.Equal(1, drift.GetArrayLength());
    }

    // -------------------- needs-review / pending backlog --------------------

    [Fact]
    public void Lint_NeedsReviewAndPendingBacklog_FlagsOnlyAgedPages()
    {
        using var tv = new TempVault(); Init(tv);

        var agedNeedsReview = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Aged Needs Review", "--summary", "s", "--json");
        var agedNeedsReviewId = ExtractId(agedNeedsReview);
        tv.Run("page", "set-status", agedNeedsReviewId, "needs-review", "--json");
        BackdateUpdated(EntityPath(tv, "aged-needs-review"), DaysAgo(20));

        var freshNeedsReview = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Fresh Needs Review", "--summary", "s", "--json");
        tv.Run("page", "set-status", ExtractId(freshNeedsReview), "needs-review", "--json");
        // Left at "today" - must NOT be flagged.

        var agedPending = tv.RunStdin("Body.", "page", "upsert", "--type", "entity",
            "--title", "Aged Pending", "--summary", "s", "--json");
        var agedPendingId = ExtractId(agedPending);
        tv.Run("page", "set-status", agedPendingId, "pending-review", "--json");
        BackdateUpdated(EntityPath(tv, "aged-pending"), DaysAgo(20));

        var r = tv.Run("lint", "--json");
        Assert.Equal(0, r.ExitCode);

        var needsReviewBacklog = IssuesList(tv, "needs-review-backlog");
        Assert.Equal(1, needsReviewBacklog.GetArrayLength());
        Assert.Equal("aged-needs-review", needsReviewBacklog[0].GetProperty("subject").GetString());

        var pendingBacklog = IssuesList(tv, "pending-backlog");
        Assert.Equal(1, pendingBacklog.GetArrayLength());
        Assert.Equal("aged-pending", pendingBacklog[0].GetProperty("subject").GetString());
    }

    // -------------------- stale --------------------

    [Fact]
    public void Lint_Stale_SummaryOlderThanStalenessDays_WithNewerCitedSource()
    {
        using var tv = new TempVault(); Init(tv);

        var srcFile = Path.Combine(tv.Path, "input.md");
        File.WriteAllText(srcFile, "raw notes");
        var sourceResult = tv.Run("source", "add", srcFile, "--category", "article", "--title", "Src", "--json");
        Assert.Equal(0, sourceResult.ExitCode);
        var sourceId = Data(sourceResult).GetProperty("id").GetString()!;

        var summary = tv.RunStdin("Summary body.", "page", "upsert", "--type", "summary",
            "--title", "Sum One", "--summary", "s", "--sources", sourceId, "--json");
        Assert.Equal(0, summary.ExitCode);

        // Default staleness_days is 90 (wiki.yaml template); the source's
        // `added` stays "today" (real clock), so backdating the summary's
        // `updated` past the threshold makes the source look newer.
        BackdateUpdated(SummaryPath(tv, "sum-one"), DaysAgo(200));

        var r = tv.Run("lint", "--json");
        Assert.Equal(0, r.ExitCode);

        var stale = IssuesList(tv, "stale");
        Assert.Equal(1, stale.GetArrayLength());
        Assert.Equal("sum-one", stale[0].GetProperty("subject").GetString());
    }

    // -------------------- coverage-gap --------------------

    [Fact]
    public void Lint_CoverageGap_TermIn3PlusPages_AsPlainText_NoPageOfItsOwn()
    {
        using var tv = new TempVault(); Init(tv);
        tv.RunStdin("Foo Bar Corp is a vendor we track.", "page", "upsert", "--type", "entity",
            "--title", "Alpha", "--summary", "s1", "--json");
        tv.RunStdin("Another mention of Foo Bar Corp here.", "page", "upsert", "--type", "entity",
            "--title", "Beta", "--summary", "s2", "--json");
        tv.RunStdin("Foo Bar Corp shows up a third time.", "page", "upsert", "--type", "entity",
            "--title", "Gamma", "--summary", "s3", "--json");

        var r = tv.Run("lint", "--json");
        Assert.Equal(0, r.ExitCode);

        var gaps = IssuesList(tv, "coverage-gap");
        Assert.Contains(gaps.EnumerateArray(), e => e.GetProperty("subject").GetString() == "Foo Bar Corp");
    }

    // -------------------- content-immutability invariant --------------------

    [Fact]
    public void Lint_PlainRun_NeverEditsAnyPageContent_AcrossWholeVault()
    {
        // The core "lint never edits page content" invariant (Global
        // Constraints), asserted broadly: a multi-page vault spanning every
        // page type, with real findings filed (orphan + oversize), must come
        // out of a PLAIN `wiki lint` (no --fix-links) with every page file
        // byte-identical - even though .wiki/issues.json, .wiki/lint.json, and
        // possibly index.md all change. Locks the invariant so a future check
        // that carelessly rewrites a body gets caught here.
        using var tv = new TempVault(); Init(tv);

        tv.RunStdin("Welcome to the vault.", "page", "upsert", "--type", "overview",
            "--title", "Overview", "--summary", "s", "--json");
        // Orphan: active entity with no inbound links.
        tv.RunStdin("Contoso is a vendor. No one links here.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        // Oversize concept (> default max_page_lines 400). Also links to the
        // entity so at least the entity isn't the only orphan trigger, and so
        // there's real inter-page linking in the snapshot.
        var bigBody = "See [[contoso]].\n" + string.Join("\n", Enumerable.Repeat("filler", 401));
        tv.RunStdin(bigBody, "page", "upsert", "--type", "concept",
            "--title", "Big Concept", "--summary", "s", "--json");
        tv.RunStdin("A short summary page.", "page", "upsert", "--type", "summary",
            "--title", "Sum", "--summary", "s", "--json");

        // Snapshot the exact bytes of EVERY page file in the vault.
        var pageFiles = Directory
            .EnumerateFiles(Path.Combine(tv.Path, "wiki"), "*.md", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "index.md" && Path.GetFileName(p) != "log.md")
            .ToArray();
        Assert.Equal(4, pageFiles.Length); // overview + entity + concept + summary
        var snapshot = pageFiles.ToDictionary(p => p, File.ReadAllText);

        var r = tv.Run("lint", "--json");
        Assert.Equal(0, r.ExitCode);
        // Real findings were filed (proves lint actually did work, not a no-op).
        // big-concept and sum are both orphans (contoso is linked from
        // big-concept, so it isn't); big-concept is also the oversize page.
        Assert.True(Data(r).GetProperty("filed").GetInt32() >= 2);
        Assert.True(IssuesList(tv, "orphan").GetArrayLength() >= 1);
        Assert.Equal(1, IssuesList(tv, "oversize").GetArrayLength());

        // The invariant: every page file is byte-identical to its snapshot.
        foreach (var (path, before) in snapshot)
            Assert.Equal(before, File.ReadAllText(path));
    }

    // -------------------- --fix-links "no prior idmap entry" branch --------------------

    [Fact]
    public void Lint_FixLinks_NoPriorIdmapEntry_RepairsIdmap_LeavesBodyUnchanged()
    {
        // ApplyFixLinks' `d.OldSlug is null` branch: a page file that exists
        // with valid frontmatter but whose id is NOT in idmap (e.g. dropped in
        // directly via Obsidian, never indexed). --fix-links must Put the
        // id->correct-path mapping but touch NO page body, since nothing could
        // have linked to a slug that never existed in idmap.
        using var tv = new TempVault(); Init(tv);

        var created = tv.RunStdin("Contoso body, self-contained.", "page", "upsert", "--type", "entity",
            "--title", "Contoso", "--summary", "s", "--json");
        var contosoId = ExtractId(created);

        // Drop the id's idmap entry, simulating "file present, never indexed".
        var idmapBefore = JsonSerializer.Deserialize(
            File.ReadAllText(IdMapPath(tv)), Wiki.Json.WikiJsonContext.Default.DictionaryStringString)!;
        idmapBefore.Remove(contosoId);
        File.WriteAllText(IdMapPath(tv), JsonSerializer.Serialize(
            idmapBefore, Wiki.Json.WikiJsonContext.Default.DictionaryStringString));

        var bodySnapshot = File.ReadAllText(EntityPath(tv, "contoso"));

        var r = tv.Run("lint", "--fix-links", "--json");
        Assert.Equal(0, r.ExitCode);
        var data = Data(r);
        Assert.Equal(1, data.GetProperty("fixLinksIdmapRepaired").GetInt32());
        // The no-old-slug branch rewrites zero bodies.
        Assert.Equal(0, data.GetProperty("fixLinksBodiesRewritten").GetInt32());

        // idmap now maps the id to its correct path...
        var idmapAfter = JsonSerializer.Deserialize(
            File.ReadAllText(IdMapPath(tv)), Wiki.Json.WikiJsonContext.Default.DictionaryStringString)!;
        Assert.Equal("wiki/entities/contoso.md", idmapAfter[contosoId]);

        // ...and the page body is byte-unchanged.
        Assert.Equal(bodySnapshot, File.ReadAllText(EntityPath(tv, "contoso")));
    }
}
