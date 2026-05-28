namespace GithubSync.Sources.GitHub;

// Source-side event discriminator. Translated to GithubSync.Data.Enums.EventKind by the mapper (#12).
public enum GitHubEventKind
{
    IssueOpened = 1,
    Renamed = 2,
    BodyEdited = 3,
    Labeled = 4,
    Unlabeled = 5,
    Assigned = 6,
    Unassigned = 7,
    Typed = 8,
    Untyped = 9,
    ParentAdded = 10,
    ParentRemoved = 11,
    Commented = 12,
    Closed = 13,
    Reopened = 14,
}
