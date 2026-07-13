using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class SlugTests
{
    [Theory]
    [InlineData("Contoso", "contoso")]
    [InlineData("Contoso platform review — 2026", "contoso-platform-review-2026")]
    [InlineData("  A/B  Test!! ", "a-b-test")]
    public void From_ProducesKebab(string title, string expected)
        => Assert.Equal(expected, Slug.From(title));

    [Fact]
    public void Ensure_SuffixesOnCollision()
    {
        var taken = new System.Collections.Generic.HashSet<string> { "contoso", "contoso-2" };
        Assert.Equal("contoso-3", Slug.Ensure("contoso", taken.Contains));
    }

    [Fact]
    public void Ensure_ReturnsOriginal_WhenNoCollision()
        => Assert.Equal("contoso", Slug.Ensure("contoso", _ => false));
}
