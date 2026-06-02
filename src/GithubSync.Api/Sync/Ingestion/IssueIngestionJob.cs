using System.Text.Json;
using GithubSync.Api.Sync;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Data.Locators;
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

    // [DisableConcurrentExecution] is keyed on argument values when the job has at least one
    // arg — Hangfire builds a per-argument lock so two scheduler ticks enqueuing the same
    // configId cannot overlap. The timeout is how long the second worker waits for the lock
    // before giving up and throwing. 900s (= one 15-minute cron interval) lets a single slow
    // run queue the next tick rather than dropping it; a run that exceeds 30 minutes will
    // surface as a TimeoutException on the next tick, which is the right "this repo is too
    // big for the current cron" signal. If/when cron changes via Ingestion:CronExpression,
    // revisit this number to stay at "≈ one interval".
    [DisableConcurrentExecution(timeoutInSeconds: 900)]
    public async Task RunForConfigurationAsync(Guid syncConfigurationId, CancellationToken ct)
    {
        var config = await db.SyncConfigurations
            .Include(c => c.Cursor)
            .SingleOrDefaultAsync(c => c.Id == syncConfigurationId, ct);

        if (config is null)
        {
            logger.LogWarning(
                "Ingestion job dispatched for missing SyncConfiguration {ConfigId} — skipping",
                syncConfigurationId);
            return;
        }

        var startedAt = timeProvider.GetUtcNow();
        var metrics = new SyncRunMetrics(config.Source);

        PersistResult? result = null;
        Exception? failure = null;

        try
        {
            var locator = JsonSerializer.Deserialize<GitHubSourceLocator>(
                config.SourceLocator, LocatorJsonOptions.Default)
                ?? throw new InvalidOperationException(
                    $"SyncConfiguration {syncConfigurationId} has unparseable GitHub SourceLocator");

            // The persister upserts the cursor on first commit — see IssueEventPersister.UpsertCursorAsync.
            var stream = fetcher.FetchAsync(locator.Owner, locator.Repo, config.Cursor?.LastEventTime, ct);
            result = await persister.PersistAsync(syncConfigurationId, stream, ct);

            // Expected v1 behaviour: the emitter's `fetched=0 mapped=0` reflects that the fetcher
            // and mapper don't surface counts today — they're filled by future instrumentation work.
            metrics.RecordPersistResult(result);
        }
        catch (OperationCanceledException ex)
        {
            // Cancellation is expected on host shutdown; don't page the operator. Status stays
            // Failed for v1 because the SyncRun row's job is "this run did not complete normally".
            failure = ex;
            metrics.IncrementFailed();
            logger.LogInformation(
                "Ingestion run cancelled for SyncConfiguration {ConfigId}", syncConfigurationId);
        }
        catch (Exception ex)
        {
            failure = ex;
            metrics.IncrementFailed();
            logger.LogError(ex,
                "Ingestion run failed for SyncConfiguration {ConfigId}", syncConfigurationId);
        }

        metrics.Complete();
        emitter.Emit(metrics);

        var completedAt = timeProvider.GetUtcNow();
        var status = failure is null ? SyncRunStatus.Success : SyncRunStatus.Failed;
        var message = failure?.Message;

        // Accepted v1 edge: if `result` is null because the persister threw mid-stream, the counts
        // below are zero even though earlier per-issue transactions may have committed. The cursor
        // and CanonicalEvents are the source of truth for what landed; SyncRun is best-effort summary.
        db.SyncRuns.Add(new SyncRun
        {
            Id = metrics.RunId,
            SyncConfigurationId = syncConfigurationId,
            Source = config.Source,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Status = status,
            IssuesCommitted = result?.IssuesCommitted ?? 0,
            EventsAttempted = result?.EventsAttempted ?? 0,
            EventsInserted = result?.EventsInserted ?? 0,
            EventsSkippedUnknownKind = result?.EventsSkippedUnknownKind ?? 0,
            DurationMs = metrics.DurationMs,
            Message = message,
        });

        // Deliberate CancellationToken.None: if the orchestrator was cancelled mid-run we still
        // want the run-history row to land so the operator sees evidence of the cancellation rather
        // than a silent gap.
        await db.SaveChangesAsync(CancellationToken.None);

        // Deliberately do NOT rethrow. Hangfire's default retry policy is fine for transient
        // infrastructure errors via the recurring tick — but a per-issue mapper exception is a
        // structural bug and a retry-storm would spam Sentry. Letting the SyncRun row carry
        // Status = Failed is the correct surface; the operator notices via the dashboard or
        // by querying SyncRuns.
    }
}
