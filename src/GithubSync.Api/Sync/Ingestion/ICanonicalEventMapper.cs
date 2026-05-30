using GithubSync.Data.Entities;
using GithubSync.Sources.GitHub;

namespace GithubSync.Api.Sync.Ingestion;

public interface ICanonicalEventMapper
{
    // Translates a source-shaped GitHub event into a canonical event row ready for persistence.
    // Returns null when the source EventKind is unrecognised — already logged as skip-and-log.
    // Throws InvalidOperationException when a non-IssueEdited event arrives with a null SourceEventId
    // (per docs/idempotency.md, that's a producer-side bug, not a tolerable per-row gap).
    ValueTask<CanonicalEvent?> MapAsync(
        GitHubIssueEvent source,
        Guid syncConfigurationId,
        CancellationToken ct);
}
