using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace GithubSync.Api.Sync.Ingestion;

// Hangfire's stock [DisableConcurrentExecution] keys its distributed lock on the method's
// type+name only — every invocation of that method serialises globally regardless of args.
// For RunForConfigurationAsync we want one lock per SyncConfiguration so that one slow
// config doesn't block unrelated ones. This filter folds the job's argument values into
// the lock resource string so each (type, method, args) tuple gets its own lock.
public sealed class DisableConcurrentExecutionByArgsAttribute(int timeoutSeconds)
    : JobFilterAttribute, IServerFilter
{
    private const string LockKey = "DistributedLock";

    public void OnPerforming(PerformingContext filterContext)
    {
        var resource = BuildResource(filterContext.BackgroundJob.Job);
        var distributedLock = filterContext.Connection.AcquireDistributedLock(
            resource, TimeSpan.FromSeconds(timeoutSeconds));
        filterContext.Items[LockKey] = distributedLock;
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        if (filterContext.Items.TryGetValue(LockKey, out var stored) && stored is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static string BuildResource(Job job)
    {
        var args = job.Args.Select(a => a?.ToString() ?? "null");
        return $"{job.Type.FullName}.{job.Method.Name}:{string.Join(":", args)}";
    }
}
