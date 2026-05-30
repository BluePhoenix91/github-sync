# Issue Event Persister Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `IssueEventPersister` — the missing last step of the v1 ingestion pipeline — so `GitHubIssueEvent` streams from the fetcher are mapped, written to PostgreSQL with `ON CONFLICT DO NOTHING` semantics, and `SyncCursor.LastEventTime` is advanced atomically per-issue. Closes [issue #13](https://github.com/BluePhoenix91/github-sync/issues/13).

**Architecture:** Per-issue transaction. Walk the fetcher's `IAsyncEnumerable<GitHubIssueEvent>`, group by contiguous `SourceEntityId`, and for each group: begin a Postgres transaction, map via `ICanonicalEventMapper` (accumulating actor/identity-mapping side-effects on `AppDbContext`'s `ChangeTracker`), `SaveChangesAsync` to flush actors, batch-insert canonical events with parameterised raw SQL `INSERT ... ON CONFLICT (...) DO NOTHING` enlisted on the open EF connection/transaction, upsert `SyncCursor.LastEventTime` to `max(current, IssueUpdatedAt)`, commit. Cursor advances only after `COMMIT`; cancellation mid-transaction rolls back via `await using`.

**Tech Stack:** .NET 10, EF Core 10, Npgsql.EntityFrameworkCore.PostgreSQL, xUnit, Xunit.SkippableFact, FluentAssertions, real PostgreSQL via dev/runner connection string (no Docker).

**Companion design document:** [docs/superpowers/specs/2026-05-30-issue-event-persister-design.md](../specs/2026-05-30-issue-event-persister-design.md). Read it before starting — it captures the decisions this plan implements.

---

## File map

### New production files

| Path | Responsibility |
|---|---|
| `src/GithubSync.Api/Sync/Ingestion/IIssueEventPersister.cs` | Public interface + `PersistResult` record |
| `src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs` | Implementation — per-issue commit cycle, raw-SQL batched insert, cursor upsert |

### Modified production files

| Path | Change |
|---|---|
| `src/GithubSync.Api/Sync/Ingestion/IngestionServiceCollectionExtensions.cs` | Add `services.AddScoped<IIssueEventPersister, IssueEventPersister>()` |

### New test project: `tests/GithubSync.Data.Tests`

| Path | Responsibility |
|---|---|
| `tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj` | Project file referencing API, Data, Sources.GitHub |
| `tests/GithubSync.Data.Tests/PostgresTestFixture.cs` | `IAsyncLifetime` fixture: resolve connection string, create/drop unique test DB, build `AppDbContext` factory |
| `tests/GithubSync.Data.Tests/GitHubIssueEventBuilder.cs` | Compact, readable construction of `GitHubIssueEvent` test data |
| `tests/GithubSync.Data.Tests/PostgresTestFixtureTests.cs` | Smoke tests: fixture creates/drops DB and runs migrations |
| `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs` | Integration tests 1–4 and 6–10 from the spec |
| `tests/GithubSync.Data.Tests/Sync/Ingestion/FirstRunCursorTests.cs` | Integration test 5 (first-run cursor creation) — kept separate for fixture-state isolation |

### Modified test files

| Path | Change |
|---|---|
| `tests/GithubSync.Tests/Sync/Ingestion/IssueEventPersisterUnitTests.cs` | New file: InMemory-backed unit tests for branching logic (cursor watermark `max`, malformed-event guard, `PersistResult` arithmetic) |
| `GithubSync.sln` | Add `tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj` |
| `CLAUDE.md` | Add "Tests against Postgres" subsection under **Commands** |

---

## Task 1: Scaffold the new test project

**Files:**
- Create: `tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj`
- Modify: `GithubSync.sln` (add project to solution)

- [ ] **Step 1: Create the project file.**

Create `tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" Version="10.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="Npgsql" Version="10.0.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
    <PackageReference Include="Xunit.SkippableFact" Version="1.5.23" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\GithubSync.Api\GithubSync.Api.csproj" />
    <ProjectReference Include="..\..\src\GithubSync.Data\GithubSync.Data.csproj" />
    <ProjectReference Include="..\..\src\GithubSync.Sources.GitHub\GithubSync.Sources.GitHub.csproj" />
  </ItemGroup>

</Project>
```

Note: package versions mirror `tests/GithubSync.Tests/GithubSync.Tests.csproj`. `FluentAssertions` is added because the spec calls it out as the repo's idiom (and it's available transitively elsewhere); verify it loads. If `FluentAssertions` is not actually used elsewhere in the repo (grep `using FluentAssertions`), drop the reference and use plain `Assert.*` throughout the test code in later tasks.

- [ ] **Step 2: Add the project to the solution.**

Run from repo root:

```powershell
dotnet sln add tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj
```

Expected: `Project ... added to the solution.`

- [ ] **Step 3: Enable User Secrets on the project.**

Run from repo root:

```powershell
dotnet user-secrets init --project tests/GithubSync.Data.Tests
```

Expected: writes a `UserSecretsId` into the new `.csproj`. This is required so `dotnet user-secrets set "ConnectionStrings:TestPostgres" "..."` works against this project later.

- [ ] **Step 4: Verify build.**

Run:

```powershell
dotnet build tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj
```

Expected: `Build succeeded.` with 0 warnings, 0 errors. Project has no source files yet so this should compile to an empty test assembly.

- [ ] **Step 5: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj GithubSync.sln
git commit -m "test: scaffold GithubSync.Data.Tests project"
```

---

## Task 2: PostgresTestFixture

**Files:**
- Create: `tests/GithubSync.Data.Tests/PostgresTestFixture.cs`
- Create: `tests/GithubSync.Data.Tests/PostgresTestFixtureTests.cs`

The fixture resolves a connection string from `GITHUBSYNC_TEST_POSTGRES` env var or `ConnectionStrings:TestPostgres` User Secret, creates a unique test database, runs EF migrations, exposes an `AppDbContext` factory, and drops the database on dispose. If no connection string is configured, `IAsyncLifetime.InitializeAsync` throws `SkipException` so the entire test class is skipped.

- [ ] **Step 1: Write the fixture smoke test (failing).**

Create `tests/GithubSync.Data.Tests/PostgresTestFixtureTests.cs`:

```csharp
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

        // Migrations applied means the SyncConfigurations table exists and is empty.
        var any = await db.SyncConfigurations.AnyAsync();
        Assert.False(any);
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
```

- [ ] **Step 2: Run the test to verify it fails to compile.**

Run:

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj
```

Expected: compile error — `PostgresTestFixture` does not exist.

- [ ] **Step 3: Implement the fixture.**

Create `tests/GithubSync.Data.Tests/PostgresTestFixture.cs`:

```csharp
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
        var originalDatabase = builder.Database;
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
```

- [ ] **Step 4: Run the smoke tests against a configured Postgres.**

Before running, set the connection string (one of):

```powershell
# Local dev:
dotnet user-secrets set "ConnectionStrings:TestPostgres" "Host=localhost;Port=5432;Username=postgres;Password=<your-password>;Database=postgres" --project tests/GithubSync.Data.Tests

# Or:
$env:GITHUBSYNC_TEST_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=<your-password>;Database=postgres"
```

The `Database` segment of the connection string is replaced internally by the fixture, but Npgsql requires it to be present for parsing. Pointing at `postgres` (the default admin DB) is conventional.

Then run:

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj
```

Expected: both fixture tests PASS. If Postgres is not running locally, both tests SKIP with the configured message — that's a valid outcome but the implementer should still ensure a passing run before moving on.

- [ ] **Step 5: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/PostgresTestFixture.cs tests/GithubSync.Data.Tests/PostgresTestFixtureTests.cs
git commit -m "test: Postgres integration test fixture"
```

---

## Task 3: GitHubIssueEventBuilder

**Files:**
- Create: `tests/GithubSync.Data.Tests/GitHubIssueEventBuilder.cs`

A compact factory for building `GitHubIssueEvent` instances with sensible defaults so test cases stay readable.

- [ ] **Step 1: Implement the builder.**

Create `tests/GithubSync.Data.Tests/GitHubIssueEventBuilder.cs`:

```csharp
using System.Text.Json;
using GithubSync.Sources.GitHub;

namespace GithubSync.Data.Tests;

// Compact builder for GitHubIssueEvent test data. All fields default to plausible values;
// override only what the test cares about. Null Actor is intentional — most #13 tests do not
// exercise actor resolution, which keeps the seed surface small.
public static class GitHubIssueEventBuilder
{
    public static GitHubIssueEvent Build(
        string sourceEntityId = "1",
        string? sourceEventId = "evt-1",
        GitHubEventKind kind = GitHubEventKind.IssueOpened,
        DateTimeOffset? eventTime = null,
        DateTimeOffset? issueUpdatedAt = null,
        GitHubActor? actor = null,
        string? payloadJson = null)
    {
        var et = eventTime ?? new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        return new GitHubIssueEvent(
            SourceEntityId: sourceEntityId,
            SourceEventId: sourceEventId,
            Kind: kind,
            EventTime: et,
            IssueUpdatedAt: issueUpdatedAt ?? et,
            Actor: actor,
            PayloadJson: payloadJson ?? JsonSerializer.Serialize(new { stub = true }));
    }

    // Materialises a sequence of events as an IAsyncEnumerable so it can flow into PersistAsync
    // without needing a real fetcher.
    public static async IAsyncEnumerable<GitHubIssueEvent> AsStream(params GitHubIssueEvent[] events)
    {
        foreach (var e in events)
        {
            yield return e;
            await Task.Yield();
        }
    }
}
```

- [ ] **Step 2: Verify it compiles.**

```powershell
dotnet build tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj
```

Expected: build succeeds.

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/GitHubIssueEventBuilder.cs
git commit -m "test: add GitHubIssueEventBuilder helper"
```

---

## Task 4: Persister interface and DI registration

**Files:**
- Create: `src/GithubSync.Api/Sync/Ingestion/IIssueEventPersister.cs`
- Create: `src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs` (stub only — implementation grows in later tasks)
- Modify: `src/GithubSync.Api/Sync/Ingestion/IngestionServiceCollectionExtensions.cs`

- [ ] **Step 1: Define the interface and result record.**

Create `src/GithubSync.Api/Sync/Ingestion/IIssueEventPersister.cs`:

```csharp
using GithubSync.Sources.GitHub;

namespace GithubSync.Api.Sync.Ingestion;

public interface IIssueEventPersister
{
    Task<PersistResult> PersistAsync(
        Guid syncConfigurationId,
        IAsyncEnumerable<GitHubIssueEvent> source,
        CancellationToken ct);
}

// IssuesCommitted   — count of issues whose transaction reached COMMIT (includes empty/all-deduped issues).
// EventsAttempted   — count of mapped CanonicalEvent rows sent into an INSERT batch (excludes unknown-kind).
// EventsInserted    — of EventsAttempted, the count that the DB actually wrote (rest absorbed by ON CONFLICT).
// EventsSkippedUnknownKind — count of source events the mapper returned null for.
// FinalCursor       — SyncCursor.LastEventTime after the last successful commit, or null if no issue committed.
public sealed record PersistResult(
    int IssuesCommitted,
    int EventsAttempted,
    int EventsInserted,
    int EventsSkippedUnknownKind,
    DateTimeOffset? FinalCursor);
```

- [ ] **Step 2: Stub the implementation.**

Create `src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs`:

```csharp
using GithubSync.Data;
using GithubSync.Sources.GitHub;
using Microsoft.Extensions.Logging;

namespace GithubSync.Api.Sync.Ingestion;

public class IssueEventPersister(
    AppDbContext db,
    ICanonicalEventMapper mapper,
    ILogger<IssueEventPersister> logger) : IIssueEventPersister
{
    public Task<PersistResult> PersistAsync(
        Guid syncConfigurationId,
        IAsyncEnumerable<GitHubIssueEvent> source,
        CancellationToken ct) =>
        throw new NotImplementedException();
}
```

- [ ] **Step 3: Wire DI registration.**

Edit `src/GithubSync.Api/Sync/Ingestion/IngestionServiceCollectionExtensions.cs`, add to `AddIngestion` after the existing `AddScoped<ICanonicalEventMapper, ...>` registration:

```csharp
services.AddScoped<IIssueEventPersister, IssueEventPersister>();
```

The full method should now read:

```csharp
public static IServiceCollection AddIngestion(
    this IServiceCollection services, IConfiguration configuration)
{
    services.Configure<IdentityMappingOptions>(
        configuration.GetSection(IdentityMappingOptions.SectionName));

    services.AddSingleton(TimeProvider.System);

    // Scoped: both services capture the request/job-scoped AppDbContext and the resolver
    // holds a per-run cache. A new scope per sync run gives us a clean cache by construction.
    services.AddScoped<IActorResolver, ActorResolver>();
    services.AddScoped<ICanonicalEventMapper, CanonicalEventMapper>();
    services.AddScoped<IIssueEventPersister, IssueEventPersister>();

    return services;
}
```

- [ ] **Step 4: Verify build.**

```powershell
dotnet build
```

Expected: solution builds with no new warnings.

- [ ] **Step 5: Commit.**

```powershell
git add src/GithubSync.Api/Sync/Ingestion/IIssueEventPersister.cs src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs src/GithubSync.Api/Sync/Ingestion/IngestionServiceCollectionExtensions.cs
git commit -m "feat: IIssueEventPersister interface and DI registration"
```

---

## Task 5: First-run cursor creation (test 5)

The simplest happy path — drives the minimum per-issue commit cycle (transaction + cursor upsert) without yet exercising the event insert. After this task the persister can commit an issue's transaction and create a cursor row, but stores no canonical events.

**Files:**
- Create: `tests/GithubSync.Data.Tests/Sync/Ingestion/FirstRunCursorTests.cs`
- Modify: `src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs`

- [ ] **Step 1: Write the failing test.**

Create `tests/GithubSync.Data.Tests/Sync/Ingestion/FirstRunCursorTests.cs`:

```csharp
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

    public Task DisposeAsync() => Task.CompletedTask;

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
```

- [ ] **Step 2: Run the test to verify it fails.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter FullyQualifiedName~FirstRunCursorTests
```

Expected: FAIL — `NotImplementedException` from `PersistAsync`.

- [ ] **Step 3: Implement minimal per-issue commit with cursor upsert only.**

Replace `src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs` with:

```csharp
using GithubSync.Data;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace GithubSync.Api.Sync.Ingestion;

public class IssueEventPersister(
    AppDbContext db,
    ICanonicalEventMapper mapper,
    ILogger<IssueEventPersister> logger) : IIssueEventPersister
{
    public async Task<PersistResult> PersistAsync(
        Guid syncConfigurationId,
        IAsyncEnumerable<GitHubIssueEvent> source,
        CancellationToken ct)
    {
        var stats = new RunStats();

        // Group by contiguous SourceEntityId. The fetcher contract (#11) guarantees an issue's
        // events are emitted as one contiguous block, in non-decreasing IssueUpdatedAt order
        // across issues. We trust that ordering; see docs/superpowers/specs/2026-05-30-issue-event-persister-design.md#stream-contract.
        string? currentIssueId = null;
        var buffer = new List<GitHubIssueEvent>(16);

        await foreach (var ev in source.WithCancellation(ct))
        {
            if (currentIssueId is not null && ev.SourceEntityId != currentIssueId)
            {
                await CommitIssueAsync(syncConfigurationId, currentIssueId, buffer, stats, ct);
                buffer.Clear();
            }
            currentIssueId = ev.SourceEntityId;
            buffer.Add(ev);
        }

        if (currentIssueId is not null)
        {
            await CommitIssueAsync(syncConfigurationId, currentIssueId, buffer, stats, ct);
        }

        return new PersistResult(
            IssuesCommitted: stats.IssuesCommitted,
            EventsAttempted: stats.EventsAttempted,
            EventsInserted: stats.EventsInserted,
            EventsSkippedUnknownKind: stats.EventsSkippedUnknownKind,
            FinalCursor: stats.FinalCursor);
    }

    private async Task CommitIssueAsync(
        Guid syncConfigurationId,
        string sourceEntityId,
        IReadOnlyList<GitHubIssueEvent> buffered,
        RunStats stats,
        CancellationToken ct)
    {
        var issueUpdatedAt = buffered[0].IssueUpdatedAt;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // TODO Task 6: map source events and INSERT them via raw SQL ON CONFLICT DO NOTHING.
        // For now we only advance the cursor so test 5 passes.

        await UpsertCursorAsync(syncConfigurationId, issueUpdatedAt, ct);

        await tx.CommitAsync(ct);

        stats.IssuesCommitted++;
        stats.FinalCursor = stats.FinalCursor is null
            ? issueUpdatedAt
            : (issueUpdatedAt > stats.FinalCursor ? issueUpdatedAt : stats.FinalCursor);

        logger.LogInformation(
            "Issue commit {ConfigId} {SourceEntityId} {EventsAttempted} {EventsInserted} {CursorAdvancedTo}",
            syncConfigurationId, sourceEntityId, 0, 0, issueUpdatedAt);
    }

    private async Task UpsertCursorAsync(
        Guid syncConfigurationId, DateTimeOffset issueUpdatedAt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO "SyncCursors" ("Id", "SyncConfigurationId", "LastEventTime")
            VALUES (@id, @configId, @issueUpdatedAt)
            ON CONFLICT ("SyncConfigurationId") DO UPDATE SET
              "LastEventTime" = GREATEST(
                EXCLUDED."LastEventTime",
                COALESCE("SyncCursors"."LastEventTime", EXCLUDED."LastEventTime"))
            """;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var efTx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("UpsertCursorAsync must run inside an EF transaction.");
        var tx = (NpgsqlTransaction)efTx.GetDbTransaction();

        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter("@configId", NpgsqlDbType.Uuid) { Value = syncConfigurationId });
        cmd.Parameters.Add(new NpgsqlParameter("@issueUpdatedAt", NpgsqlDbType.TimestampTz) { Value = issueUpdatedAt });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class RunStats
    {
        public int IssuesCommitted;
        public int EventsAttempted;
        public int EventsInserted;
        public int EventsSkippedUnknownKind;
        public DateTimeOffset? FinalCursor;
    }
}
```

You may need a `using Microsoft.EntityFrameworkCore.Storage;` for the `GetDbTransaction()` extension.

- [ ] **Step 4: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter FullyQualifiedName~FirstRunCursorTests
```

Expected: PASS.

- [ ] **Step 5: Commit.**

```powershell
git add src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs tests/GithubSync.Data.Tests/Sync/Ingestion/FirstRunCursorTests.cs
git commit -m "feat(#13): first-run cursor creation via per-issue transaction"
```

---

## Task 6: Repeat-window dedup (test 1) + raw SQL event insert

Drives the batched-insert path. After this task the persister stores canonical events with `ON CONFLICT DO NOTHING` semantics.

**Files:**
- Create: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`
- Modify: `src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs`

- [ ] **Step 1: Add a shared base class for the multi-test integration suite.**

Create `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs` with the class shell and one test:

```csharp
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
    private Guid configId;

    public IssueEventPersisterTests(PostgresTestFixture fixture) => this.fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "CanonicalEvents", "SyncCursors", "IdentityMappings", "CanonicalActors", "SyncConfigurations", "TargetUsers", "DeadLetters", "WorkItemMappings" RESTART IDENTITY CASCADE""");

        configId = await SeedSyncConfigurationAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
```

- [ ] **Step 2: Run the test to verify it fails.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_1
```

Expected: FAIL — assertion failure, row count is 0 because the persister doesn't insert events yet.

- [ ] **Step 3: Implement the raw-SQL batched insert.**

In `src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs`, replace the `CommitIssueAsync` method and add helpers:

```csharp
private async Task CommitIssueAsync(
    Guid syncConfigurationId,
    string sourceEntityId,
    IReadOnlyList<GitHubIssueEvent> buffered,
    RunStats stats,
    CancellationToken ct)
{
    var issueUpdatedAt = buffered[0].IssueUpdatedAt;

    await using var tx = await db.Database.BeginTransactionAsync(ct);

    var canonical = new List<Data.Entities.CanonicalEvent>(buffered.Count);
    foreach (var ev in buffered)
    {
        var mapped = await mapper.MapAsync(ev, syncConfigurationId, ct);
        if (mapped is null)
        {
            stats.EventsSkippedUnknownKind++;
            continue;
        }
        canonical.Add(mapped);
    }

    await db.SaveChangesAsync(ct);

    if (canonical.Count > 0)
    {
        var inserted = await BulkInsertEventsAsync(canonical, ct);
        stats.EventsAttempted += canonical.Count;
        stats.EventsInserted += inserted;
    }

    await UpsertCursorAsync(syncConfigurationId, issueUpdatedAt, ct);
    await tx.CommitAsync(ct);

    stats.IssuesCommitted++;
    stats.FinalCursor = stats.FinalCursor is null
        ? issueUpdatedAt
        : (issueUpdatedAt > stats.FinalCursor ? issueUpdatedAt : stats.FinalCursor);

    logger.LogInformation(
        "Issue commit {ConfigId} {SourceEntityId} {EventsAttempted} {EventsInserted} {CursorAdvancedTo}",
        syncConfigurationId, sourceEntityId, canonical.Count, stats.EventsInserted, issueUpdatedAt);
}

private async Task<int> BulkInsertEventsAsync(
    IReadOnlyList<Data.Entities.CanonicalEvent> events,
    CancellationToken ct)
{
    // Parameterised multi-row INSERT with ON CONFLICT (column-list) DO NOTHING.
    // ON CONFLICT ON CONSTRAINT is not used because the unique constraint was created by
    // CREATE UNIQUE INDEX (not ALTER TABLE ADD CONSTRAINT) — Postgres requires column-list
    // inference for that case. With NULLS NOT DISTINCT on the index, the column-list form
    // still matches.
    var sb = new System.Text.StringBuilder();
    sb.Append("""
        INSERT INTO "CanonicalEvents" (
          "Id", "SyncConfigurationId", "Source", "SourceEntityType",
          "SourceEntityId", "SourceEventId", "EventKind", "EventTime",
          "ActorId", "PayloadJson", "IngestedAt")
        VALUES
        """);

    var parameters = new List<NpgsqlParameter>(events.Count * 11);
    for (int i = 0; i < events.Count; i++)
    {
        if (i > 0) sb.Append(',');
        sb.Append($" (@id{i}, @cfg{i}, @src{i}, @set{i}, @sei{i}, @sev{i}, @ek{i}, @et{i}, @aid{i}, @pj{i}, @ia{i})");

        var e = events[i];
        parameters.Add(new NpgsqlParameter($"@id{i}", NpgsqlDbType.Uuid) { Value = e.Id });
        parameters.Add(new NpgsqlParameter($"@cfg{i}", NpgsqlDbType.Uuid) { Value = e.SyncConfigurationId });
        parameters.Add(new NpgsqlParameter($"@src{i}", NpgsqlDbType.Integer) { Value = (int)e.Source });
        parameters.Add(new NpgsqlParameter($"@set{i}", NpgsqlDbType.Integer) { Value = (int)e.SourceEntityType });
        parameters.Add(new NpgsqlParameter($"@sei{i}", NpgsqlDbType.Text) { Value = e.SourceEntityId });
        parameters.Add(new NpgsqlParameter($"@sev{i}", NpgsqlDbType.Text) { Value = (object?)e.SourceEventId ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter($"@ek{i}", NpgsqlDbType.Integer) { Value = (int)e.EventKind });
        parameters.Add(new NpgsqlParameter($"@et{i}", NpgsqlDbType.TimestampTz) { Value = e.EventTime });
        parameters.Add(new NpgsqlParameter($"@aid{i}", NpgsqlDbType.Uuid) { Value = (object?)e.ActorId ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter($"@pj{i}", NpgsqlDbType.Jsonb) { Value = e.PayloadJson });
        parameters.Add(new NpgsqlParameter($"@ia{i}", NpgsqlDbType.TimestampTz) { Value = e.IngestedAt });
    }

    sb.AppendLine();
    sb.Append("""
        ON CONFLICT ("Source", "SourceEntityType", "SourceEntityId", "EventKind", "EventTime", "SourceEventId") DO NOTHING
        """);

    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    var efTx = db.Database.CurrentTransaction
        ?? throw new InvalidOperationException("BulkInsertEventsAsync must run inside an EF transaction.");
    var tx = (NpgsqlTransaction)efTx.GetDbTransaction();

    await using var cmd = new NpgsqlCommand(sb.ToString(), connection, tx);
    foreach (var p in parameters) cmd.Parameters.Add(p);
    return await cmd.ExecuteNonQueryAsync(ct);
}
```

- [ ] **Step 4: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_1
```

Expected: PASS.

- [ ] **Step 5: Re-run all tests so far to confirm no regression.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj
```

Expected: all PASS or SKIP — `FirstRunCursorTests`, `IssueEventPersisterTests.Test_1_*`, `PostgresTestFixtureTests`.

- [ ] **Step 6: Commit.**

```powershell
git add src/GithubSync.Api/Sync/Ingestion/IssueEventPersister.cs tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "feat(#13): batched ON CONFLICT DO NOTHING event insert"
```

---

## Task 7: Cursor advances across calls (test 2)

Three sequential `PersistAsync` calls with growing streams. Confirms the cursor advances to the max `IssueUpdatedAt` seen so far on each call.

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append to the `IssueEventPersisterTests` class:

```csharp
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
```

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_2
```

Expected: PASS first time (the `GREATEST + COALESCE` upsert from Task 5 already handles this case).

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): cursor advances across multiple PersistAsync calls"
```

---

## Task 8: Null-watermark guard (test 7)

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append:

```csharp
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
```

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_7
```

Expected: PASS first time (the `COALESCE` in the upsert SQL already guards this).

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): pre-created null cursor watermark is advanced"
```

---

## Task 9: Unknown-kind events skipped (test 6)

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append:

```csharp
[SkippableFact]
public async Task Test_6_unknown_kind_events_are_skipped_and_counted()
{
    var ts = At(2026, 5, 10);
    var unknown = GitHubIssueEventBuilder.Build(
        sourceEntityId: "1", sourceEventId: "u1",
        kind: (GitHubEventKind)999,
        eventTime: ts, issueUpdatedAt: ts);
    var normal = GitHubIssueEventBuilder.Build(
        sourceEntityId: "1", sourceEventId: "n1",
        kind: GitHubEventKind.IssueOpened,
        eventTime: ts, issueUpdatedAt: ts);

    var persister = BuildPersister();
    var result = await persister.PersistAsync(
        configId, GitHubIssueEventBuilder.AsStream(unknown, normal), CancellationToken.None);

    Assert.Equal(1, result.EventsSkippedUnknownKind);
    Assert.Equal(1, result.EventsAttempted);
    Assert.Equal(1, result.EventsInserted);
    Assert.Equal(1, result.IssuesCommitted);
    Assert.Equal(ts, result.FinalCursor);

    await using var db = fixture.CreateContext();
    Assert.Equal(1, await db.CanonicalEvents.CountAsync());
}
```

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_6
```

Expected: PASS first time (the persister already increments `EventsSkippedUnknownKind` when the mapper returns `null` — verify this in `CommitIssueAsync` from Task 6).

If it fails because the count is wrong, double-check that the `stats.EventsSkippedUnknownKind++` line runs *before* `continue;` in the mapping loop.

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): unknown-kind events skipped and counted"
```

---

## Task 10: Malformed event halts the run (test 4)

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append:

```csharp
[SkippableFact]
public async Task Test_4_non_edit_with_null_SourceEventId_halts_the_run()
{
    var goodTs = At(2026, 5, 1);
    var badTs = At(2026, 5, 2);

    var good = GitHubIssueEventBuilder.Build(
        sourceEntityId: "1", sourceEventId: "g1",
        kind: GitHubEventKind.IssueOpened,
        eventTime: goodTs, issueUpdatedAt: goodTs);

    // Closed is a mapped, non-edit kind. SourceEventId=null violates the invariant from
    // docs/idempotency.md and the mapper throws InvalidOperationException.
    var bad = GitHubIssueEventBuilder.Build(
        sourceEntityId: "2", sourceEventId: null,
        kind: GitHubEventKind.Closed,
        eventTime: badTs, issueUpdatedAt: badTs);

    var persister = BuildPersister();
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        persister.PersistAsync(
            configId,
            GitHubIssueEventBuilder.AsStream(good, bad),
            CancellationToken.None));

    await using var db = fixture.CreateContext();
    // Issue 1 (good) was committed before issue 2 (bad) failed. Issue 2 rolled back.
    Assert.Equal(1, await db.CanonicalEvents.CountAsync());
    Assert.Equal("1", (await db.CanonicalEvents.SingleAsync()).SourceEntityId);

    var cursor = await db.SyncCursors.SingleAsync();
    Assert.Equal(goodTs, cursor.LastEventTime);
}
```

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_4
```

Expected: PASS first time (the persister does not catch the mapper's `InvalidOperationException`; the `await using var tx = ...` disposal rolls back the bad-issue transaction).

If it fails because issue 1's events aren't there, the issue 1 transaction wasn't committed — re-read the per-issue commit cycle for ordering bugs.

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): malformed events halt the run with prior commits preserved"
```

---

## Task 11: Crash-safety (test 3)

Tests the cancellation-mid-issue resume pattern. The stream wrapper cancels after issue 2's first event yields.

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append:

```csharp
[SkippableFact]
public async Task Test_3_cancellation_mid_issue_2_then_resume_via_dedup_produces_clean_state()
{
    var i1u = At(2026, 5, 1);
    var i2u = At(2026, 5, 2);
    var i3u = At(2026, 5, 3);

    var i1e1 = GitHubIssueEventBuilder.Build(sourceEntityId: "1", sourceEventId: "i1e1", eventTime: i1u, issueUpdatedAt: i1u);
    var i2e1 = GitHubIssueEventBuilder.Build(sourceEntityId: "2", sourceEventId: "i2e1", eventTime: i2u, issueUpdatedAt: i2u);
    var i2e2 = GitHubIssueEventBuilder.Build(sourceEntityId: "2", sourceEventId: "i2e2", kind: GitHubEventKind.Commented, eventTime: i2u.AddSeconds(1), issueUpdatedAt: i2u);
    var i3e1 = GitHubIssueEventBuilder.Build(sourceEntityId: "3", sourceEventId: "i3e1", eventTime: i3u, issueUpdatedAt: i3u);

    var cts = new CancellationTokenSource();

    // Wrapper stream that cancels the token after yielding the first event of issue 2.
    static async IAsyncEnumerable<GitHubIssueEvent> CancellingStream(
        CancellationTokenSource cts,
        GitHubIssueEvent[] events,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool seenIssue2 = false;
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
            await Task.Yield();
            if (!seenIssue2 && e.SourceEntityId == "2")
            {
                seenIssue2 = true;
                cts.Cancel();
            }
        }
    }

    var persister1 = BuildPersister();
    await Assert.ThrowsAsync<OperationCanceledException>(() =>
        persister1.PersistAsync(
            configId,
            CancellingStream(cts, new[] { i1e1, i2e1, i2e2, i3e1 }, cts.Token),
            cts.Token));

    // After cancellation: issue 1 committed, issue 2 rolled back, cursor at i1u.
    await using (var db = fixture.CreateContext())
    {
        Assert.Equal(1, await db.CanonicalEvents.CountAsync());
        Assert.Equal("1", (await db.CanonicalEvents.SingleAsync()).SourceEntityId);
        Assert.Equal(i1u, (await db.SyncCursors.SingleAsync()).LastEventTime);
    }

    // Resume: feed the same full stream again (the persister does not read the cursor — see
    // docs/superpowers/specs/2026-05-30-issue-event-persister-design.md, test 3 framing).
    var persister2 = BuildPersister();
    var result = await persister2.PersistAsync(
        configId,
        GitHubIssueEventBuilder.AsStream(i1e1, i2e1, i2e2, i3e1),
        CancellationToken.None);

    Assert.Equal(3, result.IssuesCommitted);
    Assert.Equal(i3u, result.FinalCursor);

    await using (var db = fixture.CreateContext())
    {
        Assert.Equal(4, await db.CanonicalEvents.CountAsync());
        Assert.Equal(i3u, (await db.SyncCursors.SingleAsync()).LastEventTime);
    }
}
```

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_3
```

Expected: PASS first time. The `await using` on the transaction rolls back when the `OperationCanceledException` unwinds.

If the test fails because issue 2's events leaked into the DB, check that `BulkInsertEventsAsync` and `UpsertCursorAsync` both honour the cancellation token and that no `SaveChangesAsync` runs *after* the `CommitAsync` for issue 1 has happened.

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): crash-safety via cancellation + resume dedup"
```

---

## Task 12: Overlapping-window tail re-ingest (test 8)

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append:

```csharp
[SkippableFact]
public async Task Test_8_overlapping_window_tail_re_ingest_is_idempotent()
{
    // First stream covers T1..T10 across 10 issues.
    var first = Enumerable.Range(1, 10).Select(i =>
        GitHubIssueEventBuilder.Build(
            sourceEntityId: i.ToString(),
            sourceEventId: $"e{i}",
            eventTime: At(2026, 5, i),
            issueUpdatedAt: At(2026, 5, i))).ToArray();

    // Second stream covers T5..T15 — overlaps the tail of the first window.
    var second = Enumerable.Range(5, 11).Select(i =>
        GitHubIssueEventBuilder.Build(
            sourceEntityId: i.ToString(),
            sourceEventId: $"e{i}",
            eventTime: At(2026, 5, i),
            issueUpdatedAt: At(2026, 5, i))).ToArray();

    var p1 = BuildPersister();
    await p1.PersistAsync(configId, GitHubIssueEventBuilder.AsStream(first), CancellationToken.None);

    var p2 = BuildPersister();
    var r2 = await p2.PersistAsync(configId, GitHubIssueEventBuilder.AsStream(second), CancellationToken.None);

    await using var db = fixture.CreateContext();
    // 1..15 distinct issues, one event each.
    Assert.Equal(15, await db.CanonicalEvents.CountAsync());
    // T5..T10 were re-attempted but deduped.
    Assert.Equal(11, r2.EventsAttempted);
    Assert.Equal(5, r2.EventsInserted);
    Assert.Equal(At(2026, 5, 15), (await db.SyncCursors.SingleAsync()).LastEventTime);
}
```

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_8
```

Expected: PASS.

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): overlapping-window tail re-ingest is idempotent"
```

---

## Task 13: NULLS NOT DISTINCT dedup (test 9)

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append:

```csharp
[SkippableFact]
public async Task Test_9_null_SourceEventId_dedup_via_NULLS_NOT_DISTINCT()
{
    var ts = At(2026, 5, 10);
    var edit1 = GitHubIssueEventBuilder.Build(
        sourceEntityId: "1", sourceEventId: null,
        kind: GitHubEventKind.BodyEdited,
        eventTime: ts, issueUpdatedAt: ts);

    var edit2 = GitHubIssueEventBuilder.Build(
        sourceEntityId: "1", sourceEventId: null,
        kind: GitHubEventKind.BodyEdited,
        eventTime: ts, issueUpdatedAt: ts);

    var persister = BuildPersister();
    var result = await persister.PersistAsync(
        configId,
        GitHubIssueEventBuilder.AsStream(edit1, edit2),
        CancellationToken.None);

    Assert.Equal(2, result.EventsAttempted);
    Assert.Equal(1, result.EventsInserted);

    await using var db = fixture.CreateContext();
    Assert.Equal(1, await db.CanonicalEvents.CountAsync());
}
```

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_9
```

Expected: PASS — the `NULLS NOT DISTINCT` annotation on the unique index from migration `20260523071520_Initial` makes two rows with identical key-and-null-`SourceEventId` collide, and the `ON CONFLICT (...) DO NOTHING` clause absorbs the second insert.

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): null SourceEventId dedup via NULLS NOT DISTINCT"
```

---

## Task 14: Concurrent insert of the same canonical event (test 10)

**Files:**
- Modify: `tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs`

- [ ] **Step 1: Add the failing test.**

Append:

```csharp
[SkippableFact]
public async Task Test_10_concurrent_insert_of_same_event_absorbed_cleanly_on_loser_side()
{
    var ts = At(2026, 5, 12);
    var ev = GitHubIssueEventBuilder.Build(
        sourceEntityId: "42", sourceEventId: "shared",
        eventTime: ts, issueUpdatedAt: ts);

    var p1 = BuildPersister();
    var p2 = BuildPersister();

    var task1 = p1.PersistAsync(configId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);
    var task2 = p2.PersistAsync(configId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);

    var results = await Task.WhenAll(task1, task2);

    // Both calls complete without exceptions.
    Assert.Equal(2, results.Length);
    // Together they attempted 2 inserts; one was the winner, one was absorbed by ON CONFLICT.
    Assert.Equal(2, results.Sum(r => r.EventsAttempted));
    Assert.Equal(1, results.Sum(r => r.EventsInserted));

    await using var db = fixture.CreateContext();
    Assert.Equal(1, await db.CanonicalEvents.CountAsync());
}
```

Note: this test relies on the persisters each getting their own `AppDbContext` and `IActorResolver` via separate DI scopes — `BuildPersister` already does that. The concurrent cursor upsert is also absorbed by `ON CONFLICT (SyncConfigurationId) DO UPDATE`, so neither side throws.

- [ ] **Step 2: Run the test.**

```powershell
dotnet test tests/GithubSync.Data.Tests/GithubSync.Data.Tests.csproj --filter Test_10
```

Expected: PASS. If the test fails with a unique-constraint violation, double-check the bulk insert SQL targets the correct conflict column list (the six-column composite, in the same order as the unique index).

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Data.Tests/Sync/Ingestion/IssueEventPersisterTests.cs
git commit -m "test(#13): concurrent insert of same event absorbed cleanly"
```

---

## Task 15: Unit tests in GithubSync.Tests (InMemory)

InMemory-backed unit tests for cheap branching checks. The integration suite is the real contract — these are pre-flight pings.

**Files:**
- Create: `tests/GithubSync.Tests/Sync/Ingestion/IssueEventPersisterUnitTests.cs`

- [ ] **Step 1: Write the unit tests.**

Create `tests/GithubSync.Tests/Sync/Ingestion/IssueEventPersisterUnitTests.cs`:

```csharp
using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GithubSync.Tests.Sync.Ingestion;

// Unit tests covering branching behaviour that does not require unique-constraint enforcement.
// Integration tests in GithubSync.Data.Tests cover NULLS NOT DISTINCT, ON CONFLICT DO NOTHING,
// and EF-tx + raw-SQL enlistment — InMemory does not enforce any of those (see spec for
// full reasoning). Treat these as cheap pre-flight checks.
public class IssueEventPersisterUnitTests
{
    [Fact]
    public async Task Empty_stream_returns_zeroed_PersistResult_with_null_FinalCursor()
    {
        await using var db = NewDb();
        var persister = NewPersister(db);

        var result = await persister.PersistAsync(
            Guid.NewGuid(),
            EmptyStream(),
            CancellationToken.None);

        Assert.Equal(0, result.IssuesCommitted);
        Assert.Equal(0, result.EventsAttempted);
        Assert.Equal(0, result.EventsInserted);
        Assert.Equal(0, result.EventsSkippedUnknownKind);
        Assert.Null(result.FinalCursor);
    }

    [Fact]
    public async Task Malformed_non_edit_with_null_SourceEventId_propagates_InvalidOperationException()
    {
        await using var db = NewDb();
        var persister = NewPersister(db);
        var bad = new GitHubIssueEvent(
            SourceEntityId: "1",
            SourceEventId: null,
            Kind: GitHubEventKind.Closed,
            EventTime: DateTimeOffset.UtcNow,
            IssueUpdatedAt: DateTimeOffset.UtcNow,
            Actor: null,
            PayloadJson: "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persister.PersistAsync(Guid.NewGuid(), AsStream(bad), CancellationToken.None));
    }

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IIssueEventPersister NewPersister(AppDbContext db)
    {
        var resolver = new ActorResolver(
            db,
            Options.Create(new IdentityMappingOptions()),
            NullLogger<ActorResolver>.Instance,
            TimeProvider.System);
        var mapper = new CanonicalEventMapper(
            resolver,
            NullLogger<CanonicalEventMapper>.Instance,
            TimeProvider.System);
        return new IssueEventPersister(db, mapper, NullLogger<IssueEventPersister>.Instance);
    }

    private static async IAsyncEnumerable<GitHubIssueEvent> EmptyStream()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<GitHubIssueEvent> AsStream(params GitHubIssueEvent[] events)
    {
        foreach (var e in events) { yield return e; await Task.Yield(); }
    }
}
```

Note: the InMemory provider does **not** support `BeginTransactionAsync` with the same semantics as a relational provider. The persister's transaction will be a no-op under InMemory — that's fine for these unit tests because they only assert branching behaviour, not transaction rollback. The malformed-event test exercises the mapper's throw and verifies it propagates; the rollback semantics are tested for real in integration test 4.

Important: the integration test for the empty-stream case is implicitly covered by the per-issue accounting in tests 1, 2, 6 (which all assert `EventsAttempted`/`EventsInserted` correctly).

- [ ] **Step 2: Run the tests.**

```powershell
dotnet test tests/GithubSync.Tests/GithubSync.Tests.csproj --filter IssueEventPersisterUnitTests
```

Expected: both PASS.

If the malformed test fails because the persister throws before reaching the mapper, recheck that the mapper.MapAsync call inside `CommitIssueAsync` is not wrapped in a try/catch.

- [ ] **Step 3: Commit.**

```powershell
git add tests/GithubSync.Tests/Sync/Ingestion/IssueEventPersisterUnitTests.cs
git commit -m "test(#13): InMemory unit tests for IssueEventPersister branching"
```

---

## Task 16: Document the Postgres test setup in CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add a Tests-against-Postgres subsection.**

Find the **Commands** section in [CLAUDE.md](../../CLAUDE.md). After the existing `Hangfire dashboard (local): http://localhost:5000/hangfire` line, append:

```markdown

### Tests against Postgres

Integration tests in `tests/GithubSync.Data.Tests/` need a real PostgreSQL. The fixture resolves the connection string in this order:

1. Env var `GITHUBSYNC_TEST_POSTGRES`
2. User Secrets on the test project: `ConnectionStrings:TestPostgres`

Set one of:

```powershell
# User Secrets (local dev, recommended):
dotnet user-secrets set "ConnectionStrings:TestPostgres" "Host=localhost;Port=5432;Username=postgres;Password=<your-password>;Database=postgres" --project tests/GithubSync.Data.Tests

# Or an env var (CI / Lightsail runner):
$env:GITHUBSYNC_TEST_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=<your-password>;Database=postgres"
```

The `Database` segment must be present for Npgsql to parse the string; the fixture replaces it with a unique `githubsync_test_<guid>` database that it creates and drops per fixture lifecycle.

If neither source is set, the integration tests skip — `dotnet test` still passes overall — and the skip message points back here.
```

(The triple-backtick markdown above is the file content. When editing CLAUDE.md, paste it literally — the engineer is editing prose, not running it.)

- [ ] **Step 2: Verify the markdown renders sensibly.**

Open `CLAUDE.md` in your editor or render it (e.g. `gh markdown CLAUDE.md` or open in VS Code preview). Check the new subsection sits under **Commands** and the code fences are balanced.

- [ ] **Step 3: Commit.**

```powershell
git add CLAUDE.md
git commit -m "docs: document Tests against Postgres setup in CLAUDE.md"
```

---

## Task 17: Final validation and PR prep

**Files:**
- None (verification only).

- [ ] **Step 1: Run the full build.**

```powershell
dotnet build
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Run the full test suite.**

```powershell
dotnet test
```

Expected: all PASS (or SKIP for integration tests if no Postgres is configured; locally you should have it configured). Specifically verify:

- `GithubSync.Tests` — all green including the new `IssueEventPersisterUnitTests`.
- `GithubSync.Data.Tests` — all 11 tests green (`FirstRunCursorTests` + 10 in `IssueEventPersisterTests` + 2 in `PostgresTestFixtureTests`).

- [ ] **Step 3: Run /simplify against the branch diff.**

Per CLAUDE.md repo etiquette: `Run /simplify against the branch diff before pushing any PR that touches .cs files.` In Claude Code, invoke:

```
/simplify
```

Review the findings. Address actionable ones (apply suggested simplifications). If any finding is deliberately skipped, record a one-line reason in the PR description per CLAUDE.md.

- [ ] **Step 4: Push the branch.**

```powershell
git push -u origin feat/13-issue-event-persister
```

- [ ] **Step 5: Open the PR.**

Use `gh pr create` if `gh` is on PATH (it isn't on this machine — fall back to the GitHub web UI or `mcp__github__create_pull_request`). PR title:

```
feat: persist canonical events idempotently and advance cursor safely (#13)
```

PR body (paste this — it also contains the three issue-body adjustments from the spec's "Issue body updates required" section so the reviewer has full context):

```markdown
## Summary

Implements `IssueEventPersister` — the last step of the v1 ingestion pipeline. Consumes the fetcher's `IAsyncEnumerable<GitHubIssueEvent>`, runs it through `CanonicalEventMapper`, persists `CanonicalEvent` rows with `ON CONFLICT DO NOTHING`, and advances `SyncCursor.LastEventTime` atomically per-issue. Closes #13.

Companion design: `docs/superpowers/specs/2026-05-30-issue-event-persister-design.md`.

## Issue body carve-outs (apply to issue #13 on merge)

1. **Add #11 to "Depends on".** The persister consumes the fetcher's stream and trusts its ordering contract.
2. **Raw-SQL carve-out from "EF Core over raw SQL".** Batched `INSERT ... ON CONFLICT DO NOTHING` per the decision locked in `docs/idempotency.md` (no fluent EF equivalent). CLAUDE.md's expressiveness exception applies.
3. **Halt-on-malformed carve-out from AC #5.** A non-`IssueEdited` event with a null `SourceEventId` is treated as a systemic failure (structural-invariant violation) and halts the run, not skip-and-log. Honours the mapper's existing throw and `idempotency.md`'s "fail loud" instruction; CLAUDE.md's "systemic failures throw" rule is the explicit authority.

## Test plan

- [ ] `dotnet build` clean
- [ ] `dotnet test` — all green locally with `ConnectionStrings:TestPostgres` configured
- [ ] `/simplify` findings addressed (or one-line reason per skipped finding below)
- [ ] Spot-check `CLAUDE.md` renders correctly

## /simplify findings skipped

<!-- list each as: - <finding>: <one-line reason> -->

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

- [ ] **Step 6: Move issue #13 to "In review" on the project board.**

Use the GraphQL mutation form documented in [memory: GitHub project board IDs](C:\Users\Bart\.claude\projects\d--Repos-github-sync\memory\reference_project_board.md) with `singleSelectOptionId: "df73e18b"` (In review). Item ID for #13 was `PVTI_lAHOAmEg2c4BYXa3zgtZnJ8` at spec-writing time — re-query if it has changed.

- [ ] **Step 7: Notify the user the PR is open.**

Post the PR URL and the bullet list of issue-body adjustments (from Step 5's PR body) so the user can apply them to the issue at merge time.

---

## Self-review checklist (for the implementer)

After all tasks pass:

- [ ] **Spec coverage:** Every numbered test in the spec's "Tests" section has a corresponding implemented test. Cross-check spec tests 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 against the implemented `Test_*` methods.
- [ ] **Idempotency.md mapping:** Tests 1, 8, 9, 10 cover idempotency.md tests 1, 2, 3, 6 respectively. The spec's "Idempotency.md test-plan ownership" table is now accurate against the code.
- [ ] **`PersistResult` field arithmetic:** A single end-to-end check — run an integration test mentally where you have 3 issues, 2 events each, one unknown-kind, and verify the expected `PersistResult`. `IssuesCommitted=3, EventsAttempted=5, EventsInserted=5, EventsSkippedUnknownKind=1, FinalCursor=last issue's IssueUpdatedAt`.
- [ ] **Cursor advance for empty issues:** if an issue had only unknown-kind events, the cursor still advances — covered by test 6.
- [ ] **No leftover `NotImplementedException`, `TODO`, or unfinished stubs in `IssueEventPersister.cs`.**
- [ ] **DI registration matches the interface.** Resolving `IIssueEventPersister` from a request scope returns `IssueEventPersister` and the scope shares `AppDbContext` with the mapper and resolver.
