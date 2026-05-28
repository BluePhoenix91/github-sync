namespace GithubSync.Sources.GitHub;

public sealed record GitHubIssueEvent(
    string SourceEntityId,         // GitHub issue number, as string (scoped per repo).
    string? SourceEventId,         // GraphQL node id; null only for body edits — matches CanonicalEvent rule.
    GitHubEventKind Kind,
    DateTimeOffset EventTime,
    DateTimeOffset IssueUpdatedAt, // Watermark hint used by the persister to advance the cursor crash-safely.
    GitHubActor? Actor,            // Null for deleted-user / system / "ghost" actors. Not skipped.
    string PayloadJson);           // Raw GitHub payload slice for downstream mapping + persistence.
