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
