using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Microsoft.Extensions.Logging;

namespace GithubSync.Api.Sync.Ingestion;

// The v1 GitHub-issue-activity action types and their canonical EventKind are enumerated by
// the TryMapKind switch below. Any value outside that table is treated as unknown and
// skip-and-logged per CLAUDE.md. BodyEdited is the only kind that may carry a null
// SourceEventId (see docs/idempotency.md).
public class CanonicalEventMapper(
    IActorResolver actorResolver,
    ILogger<CanonicalEventMapper> logger,
    TimeProvider timeProvider) : ICanonicalEventMapper
{
    private const string SourceName = "github";

    public async ValueTask<CanonicalEvent?> MapAsync(
        GitHubIssueEvent source,
        Guid syncConfigurationId,
        CancellationToken ct)
    {
        var canonicalKind = TryMapKind(source.Kind);
        if (canonicalKind is null)
        {
            logger.LogWarning(
                "Skipping unrecognised GitHub event kind {Source} {ExternalId} {Reason}",
                SourceName, source.SourceEntityId,
                $"unknown GitHubEventKind value {(int)source.Kind}");
            return null;
        }

        if (source.SourceEventId is null && canonicalKind != EventKind.IssueEdited)
        {
            throw new InvalidOperationException(
                $"GitHub event {source.Kind} for issue {source.SourceEntityId} has a null SourceEventId; " +
                "only IssueEdited may persist with a null SourceEventId (see docs/idempotency.md).");
        }

        var actorId = await actorResolver.ResolveAsync(source.Actor, ct);

        return new CanonicalEvent
        {
            Id = Guid.NewGuid(),
            SyncConfigurationId = syncConfigurationId,
            Source = Source.GitHub,
            SourceEntityType = SourceEntityType.Issue,
            SourceEntityId = source.SourceEntityId,
            SourceEventId = source.SourceEventId,
            EventKind = canonicalKind.Value,
            EventTime = source.EventTime.ToUniversalTime(),
            ActorId = actorId,
            PayloadJson = source.PayloadJson,
            IngestedAt = timeProvider.GetUtcNow(),
        };
    }

    private static EventKind? TryMapKind(GitHubEventKind kind) => kind switch
    {
        GitHubEventKind.IssueOpened => EventKind.IssueCreated,
        GitHubEventKind.BodyEdited => EventKind.IssueEdited,
        GitHubEventKind.Labeled => EventKind.IssueLabeled,
        GitHubEventKind.Unlabeled => EventKind.IssueUnlabeled,
        GitHubEventKind.Assigned => EventKind.IssueAssigned,
        GitHubEventKind.Unassigned => EventKind.IssueUnassigned,
        GitHubEventKind.Typed => EventKind.IssueTyped,
        GitHubEventKind.Untyped => EventKind.IssueUntyped,
        GitHubEventKind.ParentAdded => EventKind.IssueParentAdded,
        GitHubEventKind.ParentRemoved => EventKind.IssueParentRemoved,
        GitHubEventKind.Commented => EventKind.IssueCommented,
        GitHubEventKind.Closed => EventKind.IssueClosed,
        GitHubEventKind.Reopened => EventKind.IssueReopened,
        _ => null,
    };
}
