using Sentry;

namespace GithubSync.Api.Sync;

public sealed class SyncRunMetricsEmitter(ILogger<SyncRunMetricsEmitter> logger)
{
    internal const string SyncRunCompletedTemplate =
        "Sync run {RunId} ({Source}) completed: " +
        "fetched={Fetched} mapped={Mapped} persisted={Persisted} " +
        "deduped={Deduped} skipped={Skipped} failed={Failed} durationMs={DurationMs}";

    public void Emit(SyncRunMetrics metrics)
    {
        logger.LogInformation(
            SyncRunCompletedTemplate,
            metrics.RunId,
            metrics.Source,
            metrics.Fetched,
            metrics.Mapped,
            metrics.Persisted,
            metrics.Deduped,
            metrics.Skipped,
            metrics.Failed,
            metrics.DurationMs);

        CaptureSentryEvent(metrics);
    }

    private static void CaptureSentryEvent(SyncRunMetrics metrics)
    {
        // No-op when the SDK is uninitialized (dev with no DSN, tests). SentrySdk routes
        // through HubAdapter.Instance, which short-circuits before allocating an event.
        if (!SentrySdk.IsEnabled)
        {
            return;
        }

        // Message excludes RunId so Sentry's default fingerprint groups all sync runs from
        // the same source together; per-run granularity comes from the sync.run_id tag.
        SentrySdk.CaptureEvent(
            new SentryEvent
            {
                Level = SentryLevel.Info,
                Message = $"Sync run completed ({metrics.Source})",
            },
            scope =>
            {
                scope.SetTag("sync.run_id", metrics.RunId.ToString());
                scope.SetTag("sync.source", metrics.Source.ToString());
                scope.SetExtra("sync.fetched", metrics.Fetched);
                scope.SetExtra("sync.mapped", metrics.Mapped);
                scope.SetExtra("sync.persisted", metrics.Persisted);
                scope.SetExtra("sync.deduped", metrics.Deduped);
                scope.SetExtra("sync.skipped", metrics.Skipped);
                scope.SetExtra("sync.failed", metrics.Failed);
                scope.SetExtra("sync.duration_ms", metrics.DurationMs);
            });
    }
}
