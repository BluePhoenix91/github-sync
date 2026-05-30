using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GithubSync.Data.Tests.Sync.Ingestion;

public class FirstRunCursorTests : IAsyncLifetime, IClassFixture<PostgresTestFixture>
{
    private readonly PostgresTestFixture fixture;
    private readonly List<ServiceProvider> providers = new();
    private Guid configId;

    public FirstRunCursorTests(PostgresTestFixture fixture) => this.fixture = fixture;

    public async Task InitializeAsync()
    {
        // Each test class instance runs against a freshly-truncated set of tables so tests don't share state.
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
    public async Task First_call_creates_SyncCursor_row_with_IssueUpdatedAt()
    {
        var issueUpdatedAt = new DateTimeOffset(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);
        var ev = GitHubIssueEventBuilder.Build(
            sourceEntityId: "42",
            eventTime: issueUpdatedAt,
            issueUpdatedAt: issueUpdatedAt);

        var persister = BuildPersister();
        var result = await persister.PersistAsync(
            configId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);

        Assert.Equal(1, result.IssuesCommitted);
        Assert.Equal(issueUpdatedAt, result.FinalCursor);

        await using var db = fixture.CreateContext();
        var cursor = await db.SyncCursors.SingleAsync();
        Assert.Equal(configId, cursor.SyncConfigurationId);
        Assert.Equal(issueUpdatedAt, cursor.LastEventTime);
        Assert.Null(cursor.LastRunStartedAt);
        Assert.Null(cursor.LastRunCompletedAt);
        Assert.Null(cursor.LastRunStatus);
        Assert.Null(cursor.LastRunMessage);
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
