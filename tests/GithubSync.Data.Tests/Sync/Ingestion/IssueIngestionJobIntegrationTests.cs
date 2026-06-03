using GithubSync.Api.Sync;
using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using GithubSync.Sources.GitHub.GraphQL;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GithubSync.Data.Tests.Sync.Ingestion;

public class IssueIngestionJobIntegrationTests : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
    private readonly PostgresTestFixture _fixture;
    private Guid _configId;

    public IssueIngestionJobIntegrationTests(PostgresTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        // Mirrors PostgresPersisterTestBase's truncate list with the new SyncRuns table prepended.
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "SyncRuns", "CanonicalEvents", "SyncCursors", "IdentityMappings", "CanonicalActors", "SyncConfigurations", "TargetUsers", "DeadLetters", "WorkItemMappings" RESTART IDENTITY CASCADE""");

        _configId = Guid.NewGuid();
        db.SyncConfigurations.Add(new SyncConfiguration
        {
            Id = _configId,
            Name = "octocat/hello-world",
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task One_tick_produces_canonical_events_and_a_SyncRun_row_and_advances_the_cursor()
    {
        using var github = new WireMockGitHubServer();
        github.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(OneIssueZeroTimelineGraphQLBody));

        // Configuration the canonical AddGitHubSource expects (token key).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = "test-token",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_fixture.TestConnectionString));

        // Canonical fetcher wiring — same registration Production uses.
        services.AddGitHubSource(config);

        // Override the GitHubGraphQLClient's HttpClient base URL to point at WireMock.
        // AddHttpClient on the same typed client is last-call-wins for client configuration,
        // and the Polly handler from AddGitHubSource above is preserved.
        services.AddHttpClient<GitHubGraphQLClient>(c =>
        {
            c.BaseAddress = new Uri(github.BaseUrl);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/test");
        });

        services.AddSingleton(TimeProvider.System);
        services.Configure<IdentityMappingOptions>(_ => { });
        services.Configure<IngestionOptions>(_ => { });
        services.AddScoped<IActorResolver, ActorResolver>();
        services.AddScoped<ICanonicalEventMapper, CanonicalEventMapper>();
        services.AddScoped<IIssueEventPersister, IssueEventPersister>();
        services.AddScoped<SyncRunMetricsEmitter>();
        services.AddScoped<IssueIngestionJob>();

        services.AddSingleton<IBackgroundJobClient, NullBackgroundJobClient>();

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();

        var job = scope.ServiceProvider.GetRequiredService<IssueIngestionJob>();
        await job.RunForConfigurationAsync(_configId, CancellationToken.None);

        await using var assertDb = _fixture.CreateContext();

        // Canonical events landed (the synthesised IssueOpened from the issue node).
        Assert.Equal(1, await assertDb.CanonicalEvents.CountAsync());

        // Cursor advanced (the persister upserts it on first commit).
        var cursor = await assertDb.SyncCursors.SingleAsync();
        Assert.NotNull(cursor.LastEventTime);

        // SyncRun row written for the configuration.
        var run = await assertDb.SyncRuns.SingleAsync();
        Assert.Equal(SyncRunStatus.Success, run.Status);
        Assert.Equal(_configId, run.SyncConfigurationId);
        Assert.Equal(Source.GitHub, run.Source);
        Assert.Equal(1, run.IssuesCommitted);
        Assert.True(run.EventsInserted >= 1);
        Assert.Null(run.Message);
    }

    private sealed class NullBackgroundJobClient : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => Guid.NewGuid().ToString();
        public bool ChangeState(string jobId, IState state, string expectedState) =>
            throw new NotSupportedException();
    }

    // Skeleton lifted from tests/GithubSync.Tests/Sources/GitHub/IssuesPageResponseDeserializationTests.cs.
    // Required fields per IssueNode: id, number, databaseId, createdAt, updatedAt, author,
    // userContentEdits, comments, timelineItems. Zero-timeline payload still yields the
    // synthesised IssueOpened the fetcher derives from the issue node itself.
    private const string OneIssueZeroTimelineGraphQLBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw1",
                    "number": 42,
                    "databaseId": 1042,
                    "createdAt": "2026-05-31T00:00:00Z",
                    "updatedAt": "2026-05-31T00:00:00Z",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": []
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-06-01T00:00:00Z", "limit": 5000 }
          }
        }
        """;
}
