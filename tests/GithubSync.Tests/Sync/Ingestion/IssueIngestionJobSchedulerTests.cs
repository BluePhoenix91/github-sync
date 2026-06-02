using System.Linq.Expressions;
using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GithubSync.Tests.Sync.Ingestion;

public class IssueIngestionJobSchedulerTests
{
    [Fact]
    public async Task Scheduler_enqueues_one_per_config_job_per_enabled_GitHub_config()
    {
        await using var db = NewDb();
        var enabledA = NewConfig(enabled: true, source: Source.GitHub);
        var enabledB = NewConfig(enabled: true, source: Source.GitHub);
        var disabled = NewConfig(enabled: false, source: Source.GitHub);
        db.SyncConfigurations.AddRange(enabledA, enabledB, disabled);
        await db.SaveChangesAsync();

        var bgClient = new RecordingBackgroundJobClient();
        var job = new IssueIngestionJob(
            db,
            fetcher: null!, // unused on the scheduler path
            persister: null!, // unused
            emitter: null!, // unused
            timeProvider: TimeProvider.System,
            logger: NullLogger<IssueIngestionJob>.Instance,
            backgroundJobClient: bgClient);

        await job.RunSchedulerAsync(CancellationToken.None);

        Assert.Equal(2, bgClient.EnqueuedConfigIds.Count);
        Assert.Contains(enabledA.Id, bgClient.EnqueuedConfigIds);
        Assert.Contains(enabledB.Id, bgClient.EnqueuedConfigIds);
        Assert.DoesNotContain(disabled.Id, bgClient.EnqueuedConfigIds);
    }

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SyncConfiguration NewConfig(bool enabled, Source source) => new()
    {
        Id = Guid.NewGuid(),
        Name = "cfg",
        Source = source,
        SourceLocator = """{"owner":"o","repo":"r"}""",
        TargetSystem = TargetSystem.AzureDevOps,
        TargetLocator = """{"organization":"x","project":"y"}""",
        TargetTypeMapping = "{}",
        Enabled = enabled,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public List<Guid> EnqueuedConfigIds { get; } = new();

        public string Create(Job job, IState state)
        {
            // The scheduler enqueues IssueIngestionJob.RunForConfigurationAsync(configId, ct).
            // First arg is the Guid; second is a CancellationToken placeholder Hangfire substitutes.
            var configId = (Guid)job.Args[0]!;
            EnqueuedConfigIds.Add(configId);
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) =>
            throw new NotSupportedException();
    }
}
