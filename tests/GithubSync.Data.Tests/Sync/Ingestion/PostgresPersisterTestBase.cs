using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GithubSync.Data.Tests.Sync.Ingestion;

// Shared scaffolding for integration tests that exercise IssueEventPersister against real Postgres.
// Per-test isolation: InitializeAsync truncates all app tables, then seeds a fresh SyncConfiguration.
// DI lifecycle: BuildPersister tracks the created ServiceProvider so DisposeAsync can dispose every
// scope/DbContext created during the test. Without that disposal the Npgsql pool would slowly leak.
public abstract class PostgresPersisterTestBase : IAsyncLifetime
{
    private const string TruncateAllSql =
        """TRUNCATE TABLE "CanonicalEvents", "SyncCursors", "IdentityMappings", "CanonicalActors", "SyncConfigurations", "TargetUsers", "DeadLetters", "WorkItemMappings" RESTART IDENTITY CASCADE""";

    protected PostgresTestFixture Fixture { get; }
    protected Guid ConfigId { get; private set; }

    private readonly List<ServiceProvider> providers = new();

    protected PostgresPersisterTestBase(PostgresTestFixture fixture) => Fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = Fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(TruncateAllSql);
        ConfigId = await SeedSyncConfigurationAsync(db);
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in providers)
        {
            await provider.DisposeAsync();
        }
        providers.Clear();
    }

    protected IIssueEventPersister BuildPersister()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(Fixture.TestConnectionString));
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

    protected async Task AssertCursorAsync(DateTimeOffset expected)
    {
        await using var db = Fixture.CreateContext();
        var cursor = await db.SyncCursors.SingleAsync();
        Assert.Equal(expected, cursor.LastEventTime);
    }

    protected static DateTimeOffset At(int year, int month, int day, int hour = 0) =>
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
