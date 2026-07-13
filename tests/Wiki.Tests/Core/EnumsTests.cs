using Xunit;
using Wiki.Core;

namespace Wiki.Tests.Core;

public class EnumsTests
{
    [Fact]
    public void PageStatus_RoundTripsKebabWire()
    {
        Assert.Equal("pending-review", PageStatusX.ToWire(PageStatus.PendingReview));
        Assert.Equal(PageStatus.PendingReview, PageStatusX.Parse("pending-review"));
        Assert.Throws<Wiki.Core.ValidationException>(() => PageStatusX.Parse("bogus"));
    }

    [Theory]
    [InlineData(PageStatus.Active, "active")]
    [InlineData(PageStatus.PendingReview, "pending-review")]
    [InlineData(PageStatus.NeedsReview, "needs-review")]
    [InlineData(PageStatus.Archived, "archived")]
    public void PageStatus_AllMembersRoundTrip(PageStatus value, string wire)
    {
        Assert.Equal(wire, PageStatusX.ToWire(value));
        Assert.Equal(value, PageStatusX.Parse(wire));
    }

    [Theory]
    [InlineData(PageType.Summary, "summary")]
    [InlineData(PageType.Entity, "entity")]
    [InlineData(PageType.Concept, "concept")]
    [InlineData(PageType.Overview, "overview")]
    public void PageType_AllMembersRoundTrip(PageType value, string wire)
    {
        Assert.Equal(wire, PageTypeX.ToWire(value));
        Assert.Equal(value, PageTypeX.Parse(wire));
    }

    [Fact]
    public void PageType_Parse_UnknownThrowsValidationException()
        => Assert.Throws<ValidationException>(() => PageTypeX.Parse("bogus"));

    [Theory]
    [InlineData(SourceStatus.Active, "active")]
    [InlineData(SourceStatus.Retracted, "retracted")]
    public void SourceStatus_AllMembersRoundTrip(SourceStatus value, string wire)
    {
        Assert.Equal(wire, SourceStatusX.ToWire(value));
        Assert.Equal(value, SourceStatusX.Parse(wire));
    }

    [Fact]
    public void SourceStatus_Parse_UnknownThrowsValidationException()
        => Assert.Throws<ValidationException>(() => SourceStatusX.Parse("bogus"));

    [Theory]
    [InlineData(LedgerState.Registered, "registered")]
    [InlineData(LedgerState.Summarized, "summarized")]
    [InlineData(LedgerState.Integrated, "integrated")]
    [InlineData(LedgerState.Linted, "linted")]
    public void LedgerState_AllMembersRoundTrip(LedgerState value, string wire)
    {
        Assert.Equal(wire, LedgerStateX.ToWire(value));
        Assert.Equal(value, LedgerStateX.Parse(wire));
    }

    [Fact]
    public void LedgerState_Parse_UnknownThrowsValidationException()
        => Assert.Throws<ValidationException>(() => LedgerStateX.Parse("bogus"));

    [Theory]
    [InlineData(IssueKind.Orphan, "orphan")]
    [InlineData(IssueKind.DanglingLink, "dangling-link")]
    [InlineData(IssueKind.Stale, "stale")]
    [InlineData(IssueKind.CoverageGap, "coverage-gap")]
    [InlineData(IssueKind.IndexDrift, "index-drift")]
    [InlineData(IssueKind.Oversize, "oversize")]
    [InlineData(IssueKind.RenameDrift, "rename-drift")]
    [InlineData(IssueKind.NeedsReviewBacklog, "needs-review-backlog")]
    [InlineData(IssueKind.PendingBacklog, "pending-backlog")]
    public void IssueKind_AllMembersRoundTrip(IssueKind value, string wire)
    {
        Assert.Equal(wire, IssueKindX.ToWire(value));
        Assert.Equal(value, IssueKindX.Parse(wire));
    }

    [Fact]
    public void IssueKind_Parse_UnknownThrowsValidationException()
        => Assert.Throws<ValidationException>(() => IssueKindX.Parse("bogus"));

    [Fact]
    public void ValidationException_CarriesCodeAndPath()
    {
        var ex = new ValidationException("some-code", "some message", "some.path");
        Assert.Equal("some-code", ex.Code);
        Assert.Equal("some.path", ex.Path);
        Assert.Equal("some message", ex.Message);
    }

    [Fact]
    public void ValidationException_PathDefaultsToNull()
    {
        var ex = new ValidationException("some-code", "some message");
        Assert.Null(ex.Path);
    }
}
