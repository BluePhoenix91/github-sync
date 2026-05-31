using System.Text.Json;
using GithubSync.Sources.GitHub;

namespace GithubSync.Data.Tests;

// Compact builder for GitHubIssueEvent test data. All fields default to plausible values;
// override only what the test cares about. Null Actor is intentional — most #13 tests do not
// exercise actor resolution, which keeps the seed surface small.
public static class GitHubIssueEventBuilder
{
    public static GitHubIssueEvent Build(
        string sourceEntityId = "1",
        string? sourceEventId = "evt-1",
        GitHubEventKind kind = GitHubEventKind.IssueOpened,
        DateTimeOffset? eventTime = null,
        DateTimeOffset? issueUpdatedAt = null,
        GitHubActor? actor = null,
        string? payloadJson = null)
    {
        var et = eventTime ?? new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        return new GitHubIssueEvent(
            SourceEntityId: sourceEntityId,
            SourceEventId: sourceEventId,
            Kind: kind,
            EventTime: et,
            IssueUpdatedAt: issueUpdatedAt ?? et,
            Actor: actor,
            PayloadJson: payloadJson ?? JsonSerializer.Serialize(new { stub = true }));
    }

    // Materialises a sequence of events as an IAsyncEnumerable so it can flow into PersistAsync
    // without needing a real fetcher.
    public static async IAsyncEnumerable<GitHubIssueEvent> AsStream(params GitHubIssueEvent[] events)
    {
        foreach (var e in events)
        {
            yield return e;
            await Task.Yield();
        }
    }
}
