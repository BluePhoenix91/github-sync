namespace GithubSync.Sources.GitHub;

public interface IGitHubIssueFetcher
{
    // Yields events grouped by issue, in non-decreasing issue.updatedAt order, within this invocation.
    // 'since' = null means "from now"; the caller decides cursor semantics.
    IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner,
        string repo,
        DateTimeOffset? since,
        CancellationToken ct);
}
