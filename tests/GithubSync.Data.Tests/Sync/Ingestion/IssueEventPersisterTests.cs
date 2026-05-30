using System.Runtime.CompilerServices;
using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GithubSync.Data.Tests.Sync.Ingestion;

public class IssueEventPersisterTests : IAsyncLifetime, IClassFixture<PostgresTestFixture>
{
    private readonly PostgresTestFixture fixture;
    private readonly List<ServiceProvider> providers = new();
    private Guid configId;

    public IssueEventPersisterTests(PostgresTestFixture fixture) => this.fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "CanonicalEvents", "SyncCursors", "IdentityMappings", "CanonicalActors", "SyncConfigurations", "TargetUsers", "DeadLetters", "WorkItemMappings" RESTART IDENTITY CASCADE""");

        configId = await SeedSyncConfigurationAsync(db);
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in providers)
        {
            await provider.DisposeAsync();
        }
        providers.Clear();
    }

    [SkippableFact]
    public async Task Test_1_repeat_window_produces_zero_duplicates()
    {
        var ev1 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: "e1",
            eventTime: At(2026, 5, 1));
        var ev2 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: "e2", kind: GitHubEventKind.Commented,
            eventTime: At(2026, 5, 1, 1));
        var ev3 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "2", sourceEventId: "e3",
            eventTime: At(2026, 5, 2));

        var persister1 = BuildPersister();
        await persister1.PersistAsync(
            configId,
            GitHubIssueEventBuilder.AsStream(ev1, ev2, ev3),
            CancellationToken.None);

        var persister2 = BuildPersister();
        await persister2.PersistAsync(
            configId,
            GitHubIssueEventBuilder.AsStream(ev1, ev2, ev3),
            CancellationToken.None);

        await using var db = fixture.CreateContext();
        var rowCount = await db.CanonicalEvents.CountAsync();
        Assert.Equal(3, rowCount);
    }

    [SkippableFact]
    public async Task Test_2_cursor_advances_after_each_call_to_max_IssueUpdatedAt()
    {
        var i1u = At(2026, 5, 1);
        var i2u = At(2026, 5, 2);
        var i3u = At(2026, 5, 3);

        var ev1 = GitHubIssueEventBuilder.Build(sourceEntityId: "1", sourceEventId: "e1", eventTime: i1u, issueUpdatedAt: i1u);
        var ev2 = GitHubIssueEventBuilder.Build(sourceEntityId: "2", sourceEventId: "e2", eventTime: i2u, issueUpdatedAt: i2u);
        var ev3 = GitHubIssueEventBuilder.Build(sourceEntityId: "3", sourceEventId: "e3", eventTime: i3u, issueUpdatedAt: i3u);

        var p1 = BuildPersister();
        var r1 = await p1.PersistAsync(configId, GitHubIssueEventBuilder.AsStream(ev1), CancellationToken.None);
        Assert.Equal(i1u, r1.FinalCursor);
        await AssertCursorAsync(i1u);

        var p2 = BuildPersister();
        var r2 = await p2.PersistAsync(configId, GitHubIssueEventBuilder.AsStream(ev1, ev2), CancellationToken.None);
        Assert.Equal(i2u, r2.FinalCursor);
        // Issue 1 was deduped, issue 2 inserted: 1 + 1 attempted, 0 + 1 inserted.
        Assert.Equal(2, r2.EventsAttempted);
        Assert.Equal(1, r2.EventsInserted);
        await AssertCursorAsync(i2u);

        var p3 = BuildPersister();
        var r3 = await p3.PersistAsync(configId, GitHubIssueEventBuilder.AsStream(ev1, ev2, ev3), CancellationToken.None);
        Assert.Equal(i3u, r3.FinalCursor);
        await AssertCursorAsync(i3u);

        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.CanonicalEvents.CountAsync());
    }

    private async Task AssertCursorAsync(DateTimeOffset expected)
    {
        await using var db = fixture.CreateContext();
        var cursor = await db.SyncCursors.SingleAsync();
        Assert.Equal(expected, cursor.LastEventTime);
    }

    [SkippableFact]
    public async Task Test_7_pre_created_cursor_with_null_LastEventTime_is_advanced_on_first_commit()
    {
        // Orchestrator pre-created a cursor row before any sync ran.
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.SyncCursors.Add(new SyncCursor
            {
                Id = Guid.NewGuid(),
                SyncConfigurationId = configId,
                LastEventTime = null,
            });
            await seedDb.SaveChangesAsync();
        }

        var issueUpdatedAt = At(2026, 5, 15);
        var ev = GitHubIssueEventBuilder.Build(
            sourceEntityId: "9", sourceEventId: "n9",
            eventTime: issueUpdatedAt, issueUpdatedAt: issueUpdatedAt);

        var persister = BuildPersister();
        var result = await persister.PersistAsync(
            configId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);

        Assert.Equal(issueUpdatedAt, result.FinalCursor);
        await AssertCursorAsync(issueUpdatedAt);
    }

    private IIssueEventPersister BuildPersister()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.TestConnectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IActorResolver, ActorResolver>();
        services.AddScoped<ICanonicalEventMapper, CanonicalEventMapper>();
        services.AddScoped<IIssueEventPersister, IssueEventPersister>();
        services.Configure<IdentityMappingOptions>(_ => { });

        var provider = services.BuildServiceProvider();
        providers.Add(provider);
        var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IIssueEventPersister>();
    }

    private static DateTimeOffset At(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static async Task<Guid> SeedSyncConfigurationAsync(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.SyncConfigurations.Add(new SyncConfiguration
        {
            Id = id,
            Name = "test-cfg",
            Source = Source.GitHub,
            SourceLocator = """{"owner":"o","repo":"r"}""",
            TargetSystem = TargetSystem.AzureDevOps,
            TargetLocator = """{"organization":"x","project":"y"}""",
            TargetTypeMapping = "{}",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
