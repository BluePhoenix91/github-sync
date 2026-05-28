using System.Runtime.CompilerServices;
using System.Text.Json;
using GithubSync.Sources.GitHub.GraphQL;
using GithubSync.Sources.GitHub.GraphQL.Dto;
using GithubSync.Sources.GitHub.RateLimiting;
using Microsoft.Extensions.Logging;

namespace GithubSync.Sources.GitHub;

internal sealed class GitHubIssueFetcher(
    GitHubGraphQLClient client,
    GitHubRateLimitBudget budget,
    ILogger<GitHubIssueFetcher> logger) : IGitHubIssueFetcher
{
    public async IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner, string repo, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation(
            "GitHub fetch started {Source} {Owner} {Repo} {Since}",
            "github", owner, repo, since);

        var issuesYielded = 0;
        var eventsYielded = 0;
        var startedAt = DateTimeOffset.UtcNow;
        int lastRemaining = -1;

        string? cursor = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await budget.WaitIfLowAsync(ct);

            var response = await client.QueryIssuesPageAsync(owner, repo, since, cursor, ct);
            if (response.Data?.RateLimit is { } rl)
            {
                budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);
                lastRemaining = rl.Remaining;
            }

            var issues = response.Data?.Repository?.Issues;
            if (issues is null) yield break;

            foreach (var issue in issues.Nodes)
            {
                issuesYielded++;
                foreach (var ev in ExtractEvents(issue, since))
                {
                    eventsYielded++;
                    yield return ev;
                }
            }

            if (!issues.PageInfo.HasNextPage) break;
            cursor = issues.PageInfo.EndCursor;
        }

        logger.LogInformation(
            "GitHub fetch completed {Source} {Owner} {Repo} {IssuesYielded} {EventsYielded} {DurationMs} {RateLimitRemaining}",
            "github", owner, repo, issuesYielded, eventsYielded,
            (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, lastRemaining);
    }

    private static IEnumerable<GitHubIssueEvent> ExtractEvents(IssueNode issue, DateTimeOffset? since)
    {
        var sourceEntityId = issue.Number.ToString();
        var issueUpdatedAt = issue.UpdatedAt;

        // Build an event list, then sort by event time so within-issue ordering is stable.
        var events = new List<GitHubIssueEvent>(16);

        // 1. Synthesise IssueOpened from createdAt (if in window).
        if (since is null || issue.CreatedAt >= since)
        {
            events.Add(new GitHubIssueEvent(
                SourceEntityId: sourceEntityId,
                SourceEventId: issue.Id,
                Kind: GitHubEventKind.IssueOpened,
                EventTime: issue.CreatedAt,
                IssueUpdatedAt: issueUpdatedAt,
                Actor: ToActor(issue.Author),
                PayloadJson: SerializeIssueOpenedPayload(issue)));
        }

        // 2. Body edits.
        if (issue.UserContentEdits is { } edits)
        {
            foreach (var edit in edits.Nodes)
            {
                if (since is not null && edit.EditedAt < since) continue;
                events.Add(new GitHubIssueEvent(
                    SourceEntityId: sourceEntityId,
                    SourceEventId: null, // body edits do not carry a stable per-event ID we treat as canonical
                    Kind: GitHubEventKind.BodyEdited,
                    EventTime: edit.EditedAt,
                    IssueUpdatedAt: issueUpdatedAt,
                    Actor: ToActor(edit.Editor),
                    PayloadJson: JsonSerializer.Serialize(edit)));
            }
        }

        // 3. Comments.
        if (issue.Comments is { } comments)
        {
            foreach (var c in comments.Nodes)
            {
                if (since is not null && c.CreatedAt < since) continue;
                events.Add(new GitHubIssueEvent(
                    SourceEntityId: sourceEntityId,
                    SourceEventId: c.Id,
                    Kind: GitHubEventKind.Commented,
                    EventTime: c.CreatedAt,
                    IssueUpdatedAt: issueUpdatedAt,
                    Actor: ToActor(c.Author),
                    PayloadJson: JsonSerializer.Serialize(c)));
            }
        }

        // 4. Timeline items.
        if (issue.TimelineItems is { } timeline)
        {
            foreach (var t in timeline.Nodes)
            {
                if (since is not null && t.CreatedAt < since) continue;
                var kind = MapTimelineKind(t.TypeName);
                if (kind is null) continue; // skip unknown __typename — mapper handles unknown-canonical-kind logging later
                events.Add(new GitHubIssueEvent(
                    SourceEntityId: sourceEntityId,
                    SourceEventId: t.Id,
                    Kind: kind.Value,
                    EventTime: t.CreatedAt,
                    IssueUpdatedAt: issueUpdatedAt,
                    Actor: ToActor(t.Actor),
                    PayloadJson: JsonSerializer.Serialize(t)));
            }
        }

        // Within-issue: order by event time, then by node id for ties.
        events.Sort((a, b) =>
        {
            var c = a.EventTime.CompareTo(b.EventTime);
            return c != 0 ? c : string.CompareOrdinal(a.SourceEventId ?? "", b.SourceEventId ?? "");
        });

        return events;
    }

    private static string SerializeIssueOpenedPayload(IssueNode issue) =>
        JsonSerializer.Serialize(new
        {
            issue.Id, issue.Number, issue.DatabaseId, issue.CreatedAt, issue.Author,
        });

    private static GitHubActor? ToActor(ActorDto? dto)
    {
        if (dto is null) return null;
        var kind = dto.TypeName switch
        {
            "User" => GitHubActorKind.User,
            "Bot" => GitHubActorKind.Bot,
            "Mannequin" => GitHubActorKind.Mannequin,
            _ => GitHubActorKind.Other,
        };
        return new GitHubActor(dto.Login, dto.DatabaseId.ToString(), kind);
    }

    private static GitHubEventKind? MapTimelineKind(string typeName) => typeName switch
    {
        "LabeledEvent" => GitHubEventKind.Labeled,
        "UnlabeledEvent" => GitHubEventKind.Unlabeled,
        "AssignedEvent" => GitHubEventKind.Assigned,
        "UnassignedEvent" => GitHubEventKind.Unassigned,
        "ClosedEvent" => GitHubEventKind.Closed,
        "ReopenedEvent" => GitHubEventKind.Reopened,
        "TypedEvent" => GitHubEventKind.Typed,
        "UntypedEvent" => GitHubEventKind.Untyped,
        "ParentIssueAddedEvent" => GitHubEventKind.ParentAdded,
        "ParentIssueRemovedEvent" => GitHubEventKind.ParentRemoved,
        _ => null,
    };
}
