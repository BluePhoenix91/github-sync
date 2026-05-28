using System.Runtime.CompilerServices;
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
        // Implemented in Task 9.
        yield break;
    }
}
