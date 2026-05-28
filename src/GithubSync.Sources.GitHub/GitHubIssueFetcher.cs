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
    private const string SourceName = "github";

    public async IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner, string repo, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation(
            "GitHub fetch started {Source} {Owner} {Repo} {Since}",
            SourceName, owner, repo, since);

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

                // Buffer initial-page + follow-up events for this issue, then sort across all of them
                // before yielding. Sorting only the initial page (or sorting connections in isolation)
                // would break the within-issue event-time ordering contract whenever multiple
                // connections overflow with interleaved event times.
                var issueEvents = ExtractInitialPageEvents(issue, since);

                await foreach (var ev in DrainOverflowingConnectionsAsync(owner, repo, issue, since, ct))
                {
                    issueEvents.Add(ev);
                }

                issueEvents.Sort(CompareByEventTimeThenId);

                foreach (var ev in issueEvents)
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
            SourceName, owner, repo, issuesYielded, eventsYielded,
            (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, lastRemaining);
    }

    private static List<GitHubIssueEvent> ExtractInitialPageEvents(IssueNode issue, DateTimeOffset? since)
    {
        var sourceEntityId = issue.Number.ToString();
        var issueUpdatedAt = issue.UpdatedAt;
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

        if (issue.UserContentEdits is { } edits)
            events.AddRange(ExtractEditEvents(sourceEntityId, issueUpdatedAt, edits.Nodes, since));
        if (issue.Comments is { } comments)
            events.AddRange(ExtractCommentEvents(sourceEntityId, issueUpdatedAt, comments.Nodes, since));
        if (issue.TimelineItems is { } timeline)
            events.AddRange(ExtractTimelineEvents(sourceEntityId, issueUpdatedAt, timeline.Nodes, since));

        return events;
    }

    private static int CompareByEventTimeThenId(GitHubIssueEvent a, GitHubIssueEvent b)
    {
        var c = a.EventTime.CompareTo(b.EventTime);
        return c != 0 ? c : string.CompareOrdinal(a.SourceEventId ?? "", b.SourceEventId ?? "");
    }

    private static string SerializeIssueOpenedPayload(IssueNode issue) =>
        JsonSerializer.Serialize(new
        {
            issue.Id, issue.Number, issue.DatabaseId, issue.CreatedAt, issue.Title, issue.Body, issue.Author,
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

    private async IAsyncEnumerable<GitHubIssueEvent> DrainOverflowingConnectionsAsync(
        string owner, string repo, IssueNode issue, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sourceEntityId = issue.Number.ToString();
        var issueUpdatedAt = issue.UpdatedAt;

        if (issue.TimelineItems is { PageInfo: var tPage })
        {
            await foreach (var ev in DrainConnectionAsync(
                tPage,
                (cursor, c) => client.FollowUpTimelineAsync(owner, repo, issue.Number, cursor, c),
                resp => resp.Data?.Repository?.Issue?.TimelineItems,
                conn => ExtractTimelineEvents(sourceEntityId, issueUpdatedAt, conn.Nodes, since),
                ct))
            {
                yield return ev;
            }
        }

        if (issue.Comments is { PageInfo: var cPage })
        {
            await foreach (var ev in DrainConnectionAsync(
                cPage,
                (cursor, c) => client.FollowUpCommentsAsync(owner, repo, issue.Number, cursor, c),
                resp => resp.Data?.Repository?.Issue?.Comments,
                conn => ExtractCommentEvents(sourceEntityId, issueUpdatedAt, conn.Nodes, since),
                ct))
            {
                yield return ev;
            }
        }

        if (issue.UserContentEdits is { PageInfo: var ePage })
        {
            await foreach (var ev in DrainConnectionAsync(
                ePage,
                (cursor, c) => client.FollowUpEditsAsync(owner, repo, issue.Number, cursor, c),
                resp => resp.Data?.Repository?.Issue?.UserContentEdits,
                conn => ExtractEditEvents(sourceEntityId, issueUpdatedAt, conn.Nodes, since),
                ct))
            {
                yield return ev;
            }
        }
    }

    private async IAsyncEnumerable<GitHubIssueEvent> DrainConnectionAsync<TConn>(
        PageInfoDto initialPage,
        Func<string, CancellationToken, Task<IssueFollowUpResponse>> fetchPage,
        Func<IssueFollowUpResponse, TConn?> extractConnection,
        Func<TConn, IEnumerable<GitHubIssueEvent>> extractEvents,
        [EnumeratorCancellation] CancellationToken ct)
        where TConn : class
    {
        if (!initialPage.HasNextPage || initialPage.EndCursor is not { } startCursor)
            yield break;

        string? cursor = startCursor;
        while (cursor is not null)
        {
            ct.ThrowIfCancellationRequested();
            await budget.WaitIfLowAsync(ct);

            var resp = await fetchPage(cursor, ct);
            if (resp.Data?.RateLimit is { } rl) budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);

            var conn = extractConnection(resp);
            if (conn is null) break;

            foreach (var ev in extractEvents(conn))
                yield return ev;

            // All three TConn types expose PageInfo via their first property; the extractor functions above
            // return the same connection types whose PageInfo we read here using pattern matching.
            cursor = conn switch
            {
                TimelineItemsConnection t => t.PageInfo.HasNextPage ? t.PageInfo.EndCursor : null,
                CommentsConnection c => c.PageInfo.HasNextPage ? c.PageInfo.EndCursor : null,
                EditsConnection e => e.PageInfo.HasNextPage ? e.PageInfo.EndCursor : null,
                _ => null,
            };
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractTimelineEvents(
        string sourceEntityId, DateTimeOffset issueUpdatedAt, IReadOnlyList<TimelineItemNode> nodes, DateTimeOffset? since)
    {
        foreach (var t in nodes)
        {
            if (since is not null && t.CreatedAt < since) continue;
            var kind = MapTimelineKind(t.TypeName);
            if (kind is null) continue;
            yield return new GitHubIssueEvent(
                sourceEntityId, t.Id, kind.Value, t.CreatedAt, issueUpdatedAt,
                ToActor(t.Actor), JsonSerializer.Serialize(t));
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractCommentEvents(
        string sourceEntityId, DateTimeOffset issueUpdatedAt, IReadOnlyList<CommentNode> nodes, DateTimeOffset? since)
    {
        foreach (var c in nodes)
        {
            if (since is not null && c.CreatedAt < since) continue;
            yield return new GitHubIssueEvent(
                sourceEntityId, c.Id, GitHubEventKind.Commented, c.CreatedAt, issueUpdatedAt,
                ToActor(c.Author), JsonSerializer.Serialize(c));
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractEditEvents(
        string sourceEntityId, DateTimeOffset issueUpdatedAt, IReadOnlyList<UserContentEditNode> nodes, DateTimeOffset? since)
    {
        foreach (var e in nodes)
        {
            if (since is not null && e.EditedAt < since) continue;
            yield return new GitHubIssueEvent(
                sourceEntityId, null, GitHubEventKind.BodyEdited, e.EditedAt, issueUpdatedAt,
                ToActor(e.Editor), JsonSerializer.Serialize(e));
        }
    }
}
