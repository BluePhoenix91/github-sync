using System.Diagnostics;
using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data.Enums;

namespace GithubSync.Api.Sync;

// Not thread-safe. Single-writer per run; if v2 fans work out across worker threads,
// switch counters to long + Interlocked.Add (memory: project_orchestration_topology).
public sealed class SyncRunMetrics(Source source)
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private bool completed;

    public Guid RunId { get; } = Guid.NewGuid();

    public Source Source { get; } = source;

    public int Fetched { get; private set; }
    public int Mapped { get; private set; }
    public int Persisted { get; private set; }
    public int Deduped { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public long DurationMs { get; private set; }

    public void IncrementFetched(int n = 1) => Fetched += n;
    public void IncrementMapped(int n = 1) => Mapped += n;
    public void IncrementPersisted(int n = 1) => Persisted += n;
    public void IncrementDeduped(int n = 1) => Deduped += n;
    public void IncrementSkipped(int n = 1) => Skipped += n;
    public void IncrementFailed(int n = 1) => Failed += n;

    // Pins the PersistResult → metrics mapping in one place so the export orchestrator (#72)
    // doesn't re-derive `Deduped = Attempted - Inserted` from scratch and risk drift.
    public void RecordPersistResult(PersistResult result)
    {
        IncrementPersisted(result.EventsInserted);
        IncrementDeduped(result.EventsAttempted - result.EventsInserted);
        IncrementSkipped(result.EventsSkippedUnknownKind);
    }

    public void Complete()
    {
        if (completed)
        {
            return;
        }

        stopwatch.Stop();
        DurationMs = stopwatch.ElapsedMilliseconds;
        completed = true;
    }
}
