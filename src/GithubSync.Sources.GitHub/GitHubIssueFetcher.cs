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

                await foreach (var ev in DrainOverflowingConnectionsAsync(owner, repo, issue, since, ct))
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

        // 2. Body edits from the initial page.
        if (issue.UserContentEdits is { } edits)
            events.AddRange(ExtractEditEvents(issue, edits.Nodes, since));

        // 3. Comments from the initial page.
        if (issue.Comments is { } comments)
            events.AddRange(ExtractCommentEvents(issue, comments.Nodes, since));

        // 4. Timeline items from the initial page.
        if (issue.TimelineItems is { } timeline)
            events.AddRange(ExtractTimelineEvents(issue, timeline.Nodes, since));

        // Within-issue: order by event time, then by SourceEventId for ties.
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
        // Timeline overflow
        if (issue.TimelineItems is { PageInfo.HasNextPage: true, PageInfo.EndCursor: { } tCursor })
        {
            string? cursor = tCursor;
            while (cursor is not null)
            {
                ct.ThrowIfCancellationRequested();
                await budget.WaitIfLowAsync(ct);
                var resp = await client.FollowUpTimelineAsync(owner, repo, issue.Number, cursor, ct);
                if (resp.Data?.RateLimit is { } rl) budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);

                var conn = resp.Data?.Repository?.Issue?.TimelineItems;
                if (conn is null) break;
                foreach (var ev in ExtractTimelineEvents(issue, conn.Nodes, since))
                    yield return ev;
                cursor = conn.PageInfo.HasNextPage ? conn.PageInfo.EndCursor : null;
            }
        }

        // Comments overflow
        if (issue.Comments is { PageInfo.HasNextPage: true, PageInfo.EndCursor: { } cCursor })
        {
            string? cursor = cCursor;
            while (cursor is not null)
            {
                ct.ThrowIfCancellationRequested();
                await budget.WaitIfLowAsync(ct);
                var resp = await client.FollowUpCommentsAsync(owner, repo, issue.Number, cursor, ct);
                if (resp.Data?.RateLimit is { } rl) budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);

                var conn = resp.Data?.Repository?.Issue?.Comments;
                if (conn is null) break;
                foreach (var ev in ExtractCommentEvents(issue, conn.Nodes, since))
                    yield return ev;
                cursor = conn.PageInfo.HasNextPage ? conn.PageInfo.EndCursor : null;
            }
        }

        // Body edits overflow
        if (issue.UserContentEdits is { PageInfo.HasNextPage: true, PageInfo.EndCursor: { } eCursor })
        {
            string? cursor = eCursor;
            while (cursor is not null)
            {
                ct.ThrowIfCancellationRequested();
                await budget.WaitIfLowAsync(ct);
                var resp = await client.FollowUpEditsAsync(owner, repo, issue.Number, cursor, ct);
                if (resp.Data?.RateLimit is { } rl) budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);

                var conn = resp.Data?.Repository?.Issue?.UserContentEdits;
                if (conn is null) break;
                foreach (var ev in ExtractEditEvents(issue, conn.Nodes, since))
                    yield return ev;
                cursor = conn.PageInfo.HasNextPage ? conn.PageInfo.EndCursor : null;
            }
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractTimelineEvents(
        IssueNode issue, IReadOnlyList<TimelineItemNode> nodes, DateTimeOffset? since)
    {
        foreach (var t in nodes)
        {
            if (since is not null && t.CreatedAt < since) continue;
            var kind = MapTimelineKind(t.TypeName);
            if (kind is null) continue;
            yield return new GitHubIssueEvent(
                issue.Number.ToString(), t.Id, kind.Value, t.CreatedAt, issue.UpdatedAt,
                ToActor(t.Actor), JsonSerializer.Serialize(t));
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractCommentEvents(
        IssueNode issue, IReadOnlyList<CommentNode> nodes, DateTimeOffset? since)
    {
        foreach (var c in nodes)
        {
            if (since is not null && c.CreatedAt < since) continue;
            yield return new GitHubIssueEvent(
                issue.Number.ToString(), c.Id, GitHubEventKind.Commented, c.CreatedAt, issue.UpdatedAt,
                ToActor(c.Author), JsonSerializer.Serialize(c));
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractEditEvents(
        IssueNode issue, IReadOnlyList<UserContentEditNode> nodes, DateTimeOffset? since)
    {
        foreach (var e in nodes)
        {
            if (since is not null && e.EditedAt < since) continue;
            yield return new GitHubIssueEvent(
                issue.Number.ToString(), null, GitHubEventKind.BodyEdited, e.EditedAt, issue.UpdatedAt,
                ToActor(e.Editor), JsonSerializer.Serialize(e));
        }
    }
}
