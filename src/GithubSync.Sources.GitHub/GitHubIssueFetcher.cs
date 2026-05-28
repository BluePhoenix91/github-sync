using System.Runtime.CompilerServices;

namespace GithubSync.Sources.GitHub;

internal sealed class GitHubIssueFetcher : IGitHubIssueFetcher
{
    // Stub — implemented in Task 8.
    public async IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner, string repo, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
