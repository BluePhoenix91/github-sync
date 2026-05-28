namespace GithubSync.Sources.GitHub;

// Source-side event discriminator. The mapper translates these into the canonical GithubSync.Data.Enums.EventKind.
// Title renames are not a distinct kind here: GitHub fires `UserContentEdit` for both title and body changes,
// so title-only edits surface as BodyEdited. Adding RenamedTitleEvent to the query would double-emit.
public enum GitHubEventKind
{
    IssueOpened = 1,
    BodyEdited = 2,
    Labeled = 3,
    Unlabeled = 4,
    Assigned = 5,
    Unassigned = 6,
    Typed = 7,
    Untyped = 8,
    ParentAdded = 9,
    ParentRemoved = 10,
    Commented = 11,
    Closed = 12,
    Reopened = 13,
}
