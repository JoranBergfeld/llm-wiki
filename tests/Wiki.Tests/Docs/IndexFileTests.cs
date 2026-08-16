using System.Collections.Generic;
using Xunit;
using Wiki.Core;
using Wiki.Docs;

namespace Wiki.Tests.Docs;

public class IndexFileTests
{
    private static PageFrontmatter Make(PageType type, string title, PageStatus status, string summary, string[] sources)
        => new PageFrontmatter
        {
            Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            Type = type,
            Title = title,
            Status = status,
            Created = "2026-07-14T00:00:00Z",
            Updated = "2026-07-14T00:00:00Z",
            Summary = summary,
            Sources = sources,
            Tags = System.Array.Empty<string>(),
        };

    [Fact]
    public void Render_GroupsExcludesArchivedAndMarksPendingReview()
    {
        var pages = new List<(string Slug, PageFrontmatter Front)>
        {
            ("zebra-concept", Make(PageType.Concept, "Zebra Concept", PageStatus.Active, "A concept about zebras.", new[] { "src-a", "src-b" })),
            ("apple-concept", Make(PageType.Concept, "Apple Concept", PageStatus.PendingReview, "A concept about apples.", new[] { "src-a" })),
            ("archived-entity", Make(PageType.Entity, "Archived Entity", PageStatus.Archived, "Should not appear.", new[] { "src-a", "src-b", "src-c" })),
        };

        var actual = IndexFile.Render(pages);

        var expected =
            "## Concepts\n" +
            "- [[apple-concept]] — Apple Concept — A concept about apples. (sources: 1) [pending-review]\n" +
            "- [[zebra-concept]] — Zebra Concept — A concept about zebras. (sources: 2)\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Render_OrdersGroupsAsOverviewConceptEntitySummary()
    {
        var pages = new List<(string Slug, PageFrontmatter Front)>
        {
            ("s-one", Make(PageType.Summary, "Summary One", PageStatus.Active, "A summary.", System.Array.Empty<string>())),
            ("e-one", Make(PageType.Entity, "Entity One", PageStatus.Active, "An entity.", new[] { "src-a" })),
            ("c-one", Make(PageType.Concept, "Concept One", PageStatus.Active, "A concept.", System.Array.Empty<string>())),
            ("o-one", Make(PageType.Overview, "Overview One", PageStatus.Active, "An overview.", System.Array.Empty<string>())),
        };

        var actual = IndexFile.Render(pages);

        var expected =
            "## Overview\n" +
            "- [[o-one]] — Overview One — An overview. (sources: 0)\n" +
            "\n" +
            "## Concepts\n" +
            "- [[c-one]] — Concept One — A concept. (sources: 0)\n" +
            "\n" +
            "## Entities\n" +
            "- [[e-one]] — Entity One — An entity. (sources: 1)\n" +
            "\n" +
            "## Summaries\n" +
            "- [[s-one]] — Summary One — A summary. (sources: 0)\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Render_EmptyPagesProducesEmptyString()
    {
        var actual = IndexFile.Render(new List<(string Slug, PageFrontmatter Front)>());
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void Regenerate_WritesRenderedTextToIndexPath()
    {
        using var tv = new Wiki.Tests.Support.TempVault();
        System.IO.File.WriteAllText(System.IO.Path.Combine(tv.Path, "wiki.yaml"), "version: 1");
        var v = Vault.At(tv.Path);

        var pages = new List<(string Slug, PageFrontmatter Front)>
        {
            ("only-entity", Make(PageType.Entity, "Only Entity", PageStatus.Active, "Just one.", new[] { "src-a" })),
        };

        IndexFile.Regenerate(v, pages);

        var written = System.IO.File.ReadAllText(v.IndexPath);
        Assert.Equal(IndexFile.Render(pages), written);
    }
}
