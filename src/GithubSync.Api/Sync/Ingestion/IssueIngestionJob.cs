using GithubSync.Api.Sync;
using GithubSync.Data;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GithubSync.Api.Sync.Ingestion;

// Hangfire serialises the public job method's arguments. Keep public parameters to primitive
// types (Guid, CancellationToken). All other dependencies come through DI.
public class IssueIngestionJob(
    AppDbContext db,
    IGitHubIssueFetcher fetcher,
    IIssueEventPersister persister,
    SyncRunMetricsEmitter emitter,
    TimeProvider timeProvider,
    ILogger<IssueIngestionJob> logger,
    IBackgroundJobClient backgroundJobClient)
{
    // Recurring scheduler: enumerate enabled GitHub configs and fan out one fire-and-forget
    // job per config. Each per-config job carries [DisableConcurrentExecution] on its own
    // method signature so two scheduler ticks colliding still cannot overlap a single config.
    public async Task RunSchedulerAsync(CancellationToken ct)
    {
        var configIds = await db.SyncConfigurations
            .Where(c => c.Enabled && c.Source == Source.GitHub)
            .Select(c => c.Id)
            .ToListAsync(ct);

        logger.LogInformation(
            "Ingestion scheduler tick: enqueuing {ConfigCount} GitHub configurations",
            configIds.Count);

        foreach (var configId in configIds)
        {
            // Enqueue is fire-and-forget. Hangfire calls back into RunForConfigurationAsync,
            // which builds a fresh DI scope per invocation.
            backgroundJobClient.Enqueue<IssueIngestionJob>(
                j => j.RunForConfigurationAsync(configId, CancellationToken.None));
        }
    }

    public Task RunForConfigurationAsync(Guid syncConfigurationId, CancellationToken ct)
    {
        // Implementation lands in Task 7. The scheduler's Enqueue<T> expression needs this method
        // to exist at the symbol level today, but the test never actually executes it.
        throw new NotImplementedException("Task 7 implements this method.");
    }
}
