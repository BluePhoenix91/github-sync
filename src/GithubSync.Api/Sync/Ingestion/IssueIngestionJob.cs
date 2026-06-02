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
    // job per config. Per-config concurrency is enforced by
    // [DisableConcurrentExecutionByArgs] on RunForConfigurationAsync.
    //
    // Guard against overlapping ticks (next cron fires before the previous tick finished its
    // DB read + enqueue loop). 60s timeout: the body is a single SELECT + N enqueues, well
    // under that ceiling — anything slower is a misconfig worth surfacing.
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
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

    // Custom filter keys the distributed lock by argument values (configId), so concurrency is
    // per-config rather than global. The stock [DisableConcurrentExecution] locks on type+method
    // alone, which would serialise all configs through one worker — see
    // DisableConcurrentExecutionByArgsAttribute.
    //
    // 900s timeout (= one 15-minute cron interval) lets a single slow run queue the next tick
    // rather than dropping it; a run exceeding ~30 minutes surfaces as a Hangfire timeout —
    // the right "this repo is too big for the current cron" signal. If cron changes via
    // Ingestion:CronExpression, revisit this number to stay at "≈ one interval".
    [DisableConcurrentExecutionByArgs(timeoutSeconds: 900)]
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

        // Re-check enrolment at execution time. The scheduler filters by Enabled + Source at
        // enqueue, but a config can flip between enqueue and dequeue. Without this guard we'd
        // still ingest once after disable/source-change.
        if (!config.Enabled || config.Source != Source.GitHub)
        {
            logger.LogWarning(
                "SyncConfiguration {ConfigId} no longer eligible (Enabled={Enabled}, Source={Source}) — skipping",
                syncConfigurationId, config.Enabled, config.Source);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cooperative shutdown — bail out without further DB work. The persister advances
            // the cursor per-issue, so progress is durable; skipping the SyncRun row is the
            // correct response to "the host is going down".
            logger.LogInformation(
                "Ingestion run cancelled for SyncConfiguration {ConfigId}", syncConfigurationId);
            return;
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

        await db.SaveChangesAsync(ct);

        // Deliberately do NOT rethrow. Hangfire's default retry policy is fine for transient
        // infrastructure errors via the recurring tick — but a per-issue mapper exception is a
        // structural bug and a retry-storm would spam Sentry. Letting the SyncRun row carry
        // Status = Failed is the correct surface; the operator notices via the dashboard or
        // by querying SyncRuns.
    }
}
