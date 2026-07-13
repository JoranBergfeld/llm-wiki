using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class WikilinksTests
{
    [Fact] public void Extract_FindsTargets_IgnoresCodeFences_HandlesDisplay()
    {
        var body = "See [[contoso]] and [[deal-x|the deal]].\n```\n[[not-a-link]]\n```\n";
        var links = Wikilinks.Extract(body);
        Assert.Equal(2, links.Count);
        Assert.Equal("contoso", links[0].Target);
        Assert.Equal("the deal", links[1].Display);
    }

    [Fact] public void Rewrite_RenamesTargetPreservingDisplay()
    {
        var body = "[[contoso]] and [[contoso|Contoso Inc]]";
        Assert.Equal("[[acme]] and [[acme|Contoso Inc]]", Wikilinks.Rewrite(body, "contoso", "acme"));
    }

    [Fact] public void Rewrite_SkipsCodeFences()
    {
        var body = "[[contoso]]\n```\n[[contoso]]\n```\n";
        var result = Wikilinks.Rewrite(body, "contoso", "acme");
        Assert.Equal("[[acme]]\n```\n[[contoso]]\n```\n", result);
    }

    [Fact] public void Rewrite_DoesNotRewriteSharedPrefixTarget()
    {
        var body = "[[contoso]] and [[contoso-deal]] and [[contoso|Contoso Inc]]";
        var result = Wikilinks.Rewrite(body, "contoso", "acme");
        Assert.Equal("[[acme]] and [[contoso-deal]] and [[acme|Contoso Inc]]", result);
    }
}
