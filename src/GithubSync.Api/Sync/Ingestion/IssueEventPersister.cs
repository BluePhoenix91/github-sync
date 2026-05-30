using GithubSync.Data;
using GithubSync.Sources.GitHub;
using Microsoft.Extensions.Logging;

namespace GithubSync.Api.Sync.Ingestion;

public class IssueEventPersister(
    AppDbContext db,
    ICanonicalEventMapper mapper,
    ILogger<IssueEventPersister> logger) : IIssueEventPersister
{
    public Task<PersistResult> PersistAsync(
        Guid syncConfigurationId,
        IAsyncEnumerable<GitHubIssueEvent> source,
        CancellationToken ct) =>
        throw new NotImplementedException();
}
