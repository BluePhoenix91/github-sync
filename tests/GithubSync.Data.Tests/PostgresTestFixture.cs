using GithubSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace GithubSync.Data.Tests;

// Fixture that resolves a Postgres connection string, provisions a per-fixture test database,
// runs EF migrations against it, and drops the database on dispose.
//
// Connection string source order:
//   1. Env var GITHUBSYNC_TEST_POSTGRES (CI / Lightsail runner)
//   2. User Secrets key ConnectionStrings:TestPostgres on this test project (local dev)
//
// If neither is set, InitializeAsync throws SkipException so every test using the fixture skips.
public sealed class PostgresTestFixture : IAsyncLifetime
{
    private const string EnvVar = "GITHUBSYNC_TEST_POSTGRES";
    private const string UserSecretsKey = "ConnectionStrings:TestPostgres";

    private string? adminConnectionString;
    private string? testConnectionString;
    private string? testDatabaseName;

    public async Task InitializeAsync()
    {
        var rawConnectionString =
            Environment.GetEnvironmentVariable(EnvVar)
            ?? new ConfigurationBuilder()
                .AddUserSecrets<PostgresTestFixture>(optional: true)
                .Build()[UserSecretsKey];

        Skip.If(string.IsNullOrWhiteSpace(rawConnectionString),
            $"Postgres integration tests require {EnvVar} env var or " +
            $"`dotnet user-secrets set \"{UserSecretsKey}\" \"<connection-string>\" --project tests/GithubSync.Data.Tests`. " +
            "See CLAUDE.md > Commands > Tests against Postgres.");

        // Build the admin connection (no specific database) by reusing the configured connection's
        // host/port/credentials and switching the Database property to 'postgres'. This lets us issue
        // CREATE DATABASE / DROP DATABASE statements.
        var builder = new NpgsqlConnectionStringBuilder(rawConnectionString);
        builder.Database = "postgres";
        adminConnectionString = builder.ConnectionString;

        testDatabaseName = $"githubsync_test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{testDatabaseName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        builder.Database = testDatabaseName;
        testConnectionString = builder.ConnectionString;

        // Apply migrations to the fresh database.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (testDatabaseName is null || adminConnectionString is null) return;

        // Close any pooled connections to the test DB before dropping it.
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        // FORCE allows DROP DATABASE to terminate active backends. Postgres 13+.
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{testDatabaseName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    public string TestConnectionString =>
        testConnectionString ?? throw new InvalidOperationException("Fixture not initialised.");

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestConnectionString)
            .Options;
        return new AppDbContext(options);
    }
}
