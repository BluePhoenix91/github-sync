namespace GithubSync.Sources.GitHub;

public interface IGitHubIssueFetcher
{
    // Yields events grouped by issue, in non-decreasing issue.updatedAt order, within this invocation.
    // 'since' = null passes no lower-bound filter to GitHub — the caller owns cursor initialisation.
    IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner,
        string repo,
        DateTimeOffset? since,
        CancellationToken ct);
}
