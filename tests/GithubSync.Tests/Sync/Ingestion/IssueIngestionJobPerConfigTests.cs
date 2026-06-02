using GithubSync.Api.Sync;
using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GithubSync.Tests.Sync.Ingestion;

public class IssueIngestionJobPerConfigTests
{
    [Fact]
    public async Task Happy_path_writes_SyncRun_with_Success_status_and_PersistResult_counts()
    {
        var configId = Guid.NewGuid();
        await using var db = await NewDbWithConfigAsync(configId);

        var fakeFetcher = new FakeFetcher(); // yields one event for issue "1"
        var fakePersister = new FakePersister(new PersistResult(
            IssuesCommitted: 1,
            EventsAttempted: 1,
            EventsInserted: 1,
            EventsSkippedUnknownKind: 0,
            FinalCursor: new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero)));

        var emitter = new SyncRunMetricsEmitter(NullLogger<SyncRunMetricsEmitter>.Instance);

        var job = new IssueIngestionJob(
            db, fakeFetcher,
            persister: fakePersister,
            emitter, TimeProvider.System,
            NullLogger<IssueIngestionJob>.Instance,
            backgroundJobClient: new NullBackgroundJobClient());

        await job.RunForConfigurationAsync(configId, CancellationToken.None);

        var run = await db.SyncRuns.SingleAsync();
        Assert.Equal(SyncRunStatus.Success, run.Status);
        Assert.Equal(1, run.IssuesCommitted);
        Assert.Equal(1, run.EventsAttempted);
        Assert.Equal(1, run.EventsInserted);
        Assert.Equal(0, run.EventsSkippedUnknownKind);
        Assert.Equal(configId, run.SyncConfigurationId);
        Assert.Equal(Source.GitHub, run.Source);
        Assert.Null(run.Message);
        Assert.True(run.CompletedAt >= run.StartedAt);
    }

    [Fact]
    public async Task Exception_thrown_by_persister_is_caught_SyncRun_written_with_Failed()
    {
        var configId = Guid.NewGuid();
        await using var db = await NewDbWithConfigAsync(configId);

        var fakeFetcher = new FakeFetcher();
        var bombPersister = new ThrowingPersister(new InvalidOperationException("boom"));
        var emitter = new SyncRunMetricsEmitter(NullLogger<SyncRunMetricsEmitter>.Instance);

        var job = new IssueIngestionJob(
            db, fakeFetcher,
            persister: bombPersister,
            emitter, TimeProvider.System,
            NullLogger<IssueIngestionJob>.Instance,
            backgroundJobClient: new NullBackgroundJobClient());

        // Orchestrator must NOT rethrow — Hangfire should not retry-storm a config error.
        await job.RunForConfigurationAsync(configId, CancellationToken.None);

        var run = await db.SyncRuns.SingleAsync();
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Equal("boom", run.Message);
        Assert.Equal(0, run.IssuesCommitted);
    }

    private static async Task<AppDbContext> NewDbWithConfigAsync(Guid configId)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.SyncConfigurations.Add(new SyncConfiguration
        {
            Id = configId,
            Name = "cfg",
            Source = Source.GitHub,
            SourceLocator = """{"owner":"octocat","repo":"hello-world"}""",
            TargetSystem = TargetSystem.AzureDevOps,
            TargetLocator = """{"organization":"x","project":"y"}""",
            TargetTypeMapping = "{}",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private sealed class FakeFetcher : IGitHubIssueFetcher
    {
        public async IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
            string owner, string repo, DateTimeOffset? since,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new GitHubIssueEvent(
                SourceEntityId: "1",
                SourceEventId: "e1",
                Kind: GitHubEventKind.IssueOpened,
                EventTime: new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
                IssueUpdatedAt: new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero),
                Actor: null,
                PayloadJson: "{}");
        }
    }

    private sealed class FakePersister(PersistResult result) : IIssueEventPersister
    {
        public async Task<PersistResult> PersistAsync(
            Guid syncConfigurationId,
            IAsyncEnumerable<GitHubIssueEvent> source,
            CancellationToken ct)
        {
            // Drain the stream so cancellation semantics match a real persister.
            await foreach (var _ in source.WithCancellation(ct)) { }
            return result;
        }
    }

    private sealed class ThrowingPersister(Exception ex) : IIssueEventPersister
    {
        public Task<PersistResult> PersistAsync(
            Guid syncConfigurationId,
            IAsyncEnumerable<GitHubIssueEvent> source,
            CancellationToken ct) => throw ex;
    }

    private sealed class NullBackgroundJobClient : IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
            => Guid.NewGuid().ToString();
        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState)
            => true;
    }
}
