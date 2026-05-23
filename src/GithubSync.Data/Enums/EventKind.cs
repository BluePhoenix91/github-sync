namespace GithubSync.Data.Enums;

public enum EventKind
{
    IssueCreated = 1,
    IssueEdited = 2,
    IssueLabeled = 3,
    IssueUnlabeled = 4,
    IssueAssigned = 5,
    IssueUnassigned = 6,
    IssueTyped = 7,
    IssueUntyped = 8,
    IssueParentAdded = 9,
    IssueParentRemoved = 10,
    IssueCommented = 11,
    IssueClosed = 12,
    IssueReopened = 13,
}
