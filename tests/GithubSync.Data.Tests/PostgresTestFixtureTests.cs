using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace GithubSync.Data.Tests;

public class PostgresTestFixtureTests : IAsyncLifetime, IClassFixture<PostgresTestFixture>
{
    private readonly PostgresTestFixture fixture;

    public PostgresTestFixtureTests(PostgresTestFixture fixture) => this.fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Fixture_provisions_a_database_with_migrations_applied()
    {
        await using var db = fixture.CreateContext();

        // Migrations applied means the SyncConfigurations table exists and can be queried.
        // We don't assert emptiness — the round-trip test in this class may have already inserted.
        Assert.True(await db.Database.CanConnectAsync());
        _ = await db.SyncConfigurations.CountAsync();
    }

    [SkippableFact]
    public async Task Fixture_persists_a_round_trip()
    {
        await using var db = fixture.CreateContext();

        db.SyncConfigurations.Add(new SyncConfiguration
        {
            Id = Guid.NewGuid(),
            Name = "smoke",
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

        await using var db2 = fixture.CreateContext();
        var name = await db2.SyncConfigurations.Select(x => x.Name).SingleAsync();
        Assert.Equal("smoke", name);
    }
}
