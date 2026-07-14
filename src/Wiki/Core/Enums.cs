namespace Wiki.Core;

// Closed-vocabulary enums for the wiki data model. Every enum gets a matching
// static "X" helper with hand-written ToWire/Parse tables — no Enum.Parse,
// Enum.GetName, or attribute/reflection-based conversion, so this stays AOT-clean.

public enum PageType { Summary, Entity, Concept, Overview }

public static class PageTypeX
{
    public static string ToWire(PageType value) => value switch
    {
        PageType.Summary => "summary",
        PageType.Entity => "entity",
        PageType.Concept => "concept",
        PageType.Overview => "overview",
        _ => throw new ValidationException("invalid-page-type", $"unknown PageType '{value}'"),
    };

    public static PageType Parse(string wire) => wire switch
    {
        "summary" => PageType.Summary,
        "entity" => PageType.Entity,
        "concept" => PageType.Concept,
        "overview" => PageType.Overview,
        _ => throw new ValidationException("invalid-page-type", $"unknown page type '{wire}'"),
    };
}

public enum PageStatus { Active, PendingReview, NeedsReview, Archived }

public static class PageStatusX
{
    public static string ToWire(PageStatus value) => value switch
    {
        PageStatus.Active => "active",
        PageStatus.PendingReview => "pending-review",
        PageStatus.NeedsReview => "needs-review",
        PageStatus.Archived => "archived",
        _ => throw new ValidationException("invalid-page-status", $"unknown PageStatus '{value}'"),
    };

    public static PageStatus Parse(string wire) => wire switch
    {
        "active" => PageStatus.Active,
        "pending-review" => PageStatus.PendingReview,
        "needs-review" => PageStatus.NeedsReview,
        "archived" => PageStatus.Archived,
        _ => throw new ValidationException("invalid-page-status", $"unknown page status '{wire}'"),
    };
}

public enum SourceStatus { Active, Retracted }

public static class SourceStatusX
{
    public static string ToWire(SourceStatus value) => value switch
    {
        SourceStatus.Active => "active",
        SourceStatus.Retracted => "retracted",
        _ => throw new ValidationException("invalid-source-status", $"unknown SourceStatus '{value}'"),
    };

    public static SourceStatus Parse(string wire) => wire switch
    {
        "active" => SourceStatus.Active,
        "retracted" => SourceStatus.Retracted,
        _ => throw new ValidationException("invalid-source-status", $"unknown source status '{wire}'"),
    };
}

public enum LedgerState { Registered, Summarized, Integrated, Linted }

public static class LedgerStateX
{
    public static string ToWire(LedgerState value) => value switch
    {
        LedgerState.Registered => "registered",
        LedgerState.Summarized => "summarized",
        LedgerState.Integrated => "integrated",
        LedgerState.Linted => "linted",
        _ => throw new ValidationException("invalid-ledger-state", $"unknown LedgerState '{value}'"),
    };

    public static LedgerState Parse(string wire) => wire switch
    {
        "registered" => LedgerState.Registered,
        "summarized" => LedgerState.Summarized,
        "integrated" => LedgerState.Integrated,
        "linted" => LedgerState.Linted,
        _ => throw new ValidationException("invalid-ledger-state", $"unknown ledger state '{wire}'"),
    };
}

public enum IssueKind
{
    Orphan,
    DanglingLink,
    Stale,
    CoverageGap,
    IndexDrift,
    Oversize,
    RenameDrift,
    NeedsReviewBacklog,
    PendingBacklog,

    // Workflow kind, NOT a lint check (spec amendment H: IssueKind covers
    // non-lint workflow kinds too - cf. §14's `retraction`, likewise absent
    // from §11's lint table). Filed by `wiki review reject` (spec §15). It
    // gets its own kind precisely so Issues.Upsert's (kind, subject) merge
    // can never collide a rejection with the `pending-backlog` LINT finding
    // on the same page's slug - which would overwrite the lint issue's detail
    // and corrupt its occurrence count, the reflect-loop signal.
    ReviewRejected,

    // Workflow kind, NOT a lint check (spec amendment H, spec §14). Filed by
    // `wiki source retract` once per page that cites the retracted source id
    // (excluding that source's own summary page, which is archived instead -
    // see SourceService.Retract). Its own kind for the same collision reason
    // as ReviewRejected above: Issues.Upsert merges on (kind, subject), so a
    // retraction on a page that already carries some other open lint finding
    // for that slug must not overwrite that finding's detail/occurrences.
    Retraction,
}

public static class IssueKindX
{
    public static string ToWire(IssueKind value) => value switch
    {
        IssueKind.Orphan => "orphan",
        IssueKind.DanglingLink => "dangling-link",
        IssueKind.Stale => "stale",
        IssueKind.CoverageGap => "coverage-gap",
        IssueKind.IndexDrift => "index-drift",
        IssueKind.Oversize => "oversize",
        IssueKind.RenameDrift => "rename-drift",
        IssueKind.NeedsReviewBacklog => "needs-review-backlog",
        IssueKind.PendingBacklog => "pending-backlog",
        IssueKind.ReviewRejected => "review-rejected",
        IssueKind.Retraction => "retraction",
        _ => throw new ValidationException("invalid-issue-kind", $"unknown IssueKind '{value}'"),
    };

    public static IssueKind Parse(string wire) => wire switch
    {
        "orphan" => IssueKind.Orphan,
        "dangling-link" => IssueKind.DanglingLink,
        "stale" => IssueKind.Stale,
        "coverage-gap" => IssueKind.CoverageGap,
        "index-drift" => IssueKind.IndexDrift,
        "oversize" => IssueKind.Oversize,
        "rename-drift" => IssueKind.RenameDrift,
        "needs-review-backlog" => IssueKind.NeedsReviewBacklog,
        "pending-backlog" => IssueKind.PendingBacklog,
        "review-rejected" => IssueKind.ReviewRejected,
        "retraction" => IssueKind.Retraction,
        _ => throw new ValidationException("invalid-issue-kind", $"unknown issue kind '{wire}'"),
    };
}
