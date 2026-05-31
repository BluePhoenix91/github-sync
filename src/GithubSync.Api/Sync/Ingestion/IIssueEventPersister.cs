using GithubSync.Sources.GitHub;

namespace GithubSync.Api.Sync.Ingestion;

public interface IIssueEventPersister
{
    Task<PersistResult> PersistAsync(
        Guid syncConfigurationId,
        IAsyncEnumerable<GitHubIssueEvent> source,
        CancellationToken ct);
}

// IssuesCommitted   — count of issues whose transaction reached COMMIT (includes empty/all-deduped issues).
// EventsAttempted   — count of mapped CanonicalEvent rows sent into an INSERT batch (excludes unknown-kind).
// EventsInserted    — of EventsAttempted, the count that the DB actually wrote (rest absorbed by ON CONFLICT).
// EventsSkippedUnknownKind — count of source events the mapper returned null for.
// FinalCursor       — SyncCursor.LastEventTime after the last successful commit, or null if no issue committed.
public sealed record PersistResult(
    int IssuesCommitted,
    int EventsAttempted,
    int EventsInserted,
    int EventsSkippedUnknownKind,
    DateTimeOffset? FinalCursor);
