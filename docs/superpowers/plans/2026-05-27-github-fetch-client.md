# GitHub Issues Incremental Fetch Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first stage of the ingestion pipeline — a typed HTTP client in a new `GithubSync.Sources.GitHub` class library that streams GitHub issue activity since a cursor via GraphQL, covers every v1 `EventKind`, honours all three GitHub rate-limit signals, and flows `CancellationToken` end-to-end. Output is a stream of source-side `GitHubIssueEvent` records ready for the canonical mapper (#12).

**Architecture:** A new `GithubSync.Sources.GitHub` class library hosts an `IGitHubIssueFetcher` whose implementation runs an `IAsyncEnumerable` loop against GitHub's GraphQL endpoint. The endpoint is reached via a typed `HttpClient` configured with PAT auth, Polly 3-retry transient policy, and JSON deserialization. A `GitHubRateLimitBudget` singleton tracks remaining budget from `rateLimit { remaining cost resetAt }` and gates pre-flight; HTTP-level 403 with `Retry-After` or `X-RateLimit-*` headers triggers a one-shot sleep-and-retry. The fetcher yields events grouped by issue in non-decreasing `issue.updatedAt` order — the contract that lets #13 advance the cursor crash-safely. The new project has no reference to `GithubSync.Data`; source-side types translate to the canonical model in #12.

**Tech Stack:** .NET 10, GitHub GraphQL API (`https://api.github.com/graphql`), `System.Text.Json`, `Microsoft.Extensions.Http`, Polly 8.x (`Polly.Core`), xUnit, WireMock.Net for HTTP stubbing.

**Spec:** [docs/superpowers/specs/2026-05-27-github-fetch-client-design.md](../specs/2026-05-27-github-fetch-client-design.md)

**Issue:** [#11](https://github.com/BluePhoenix91/github-sync/issues/11)

---

## File structure

**Create (new project `src/GithubSync.Sources.GitHub`):**
- `src/GithubSync.Sources.GitHub/GithubSync.Sources.GitHub.csproj` — class library, net10.0.
- `src/GithubSync.Sources.GitHub/IGitHubIssueFetcher.cs` — public interface.
- `src/GithubSync.Sources.GitHub/GitHubIssueEvent.cs` — source-side event record.
- `src/GithubSync.Sources.GitHub/GitHubActor.cs` — actor record.
- `src/GithubSync.Sources.GitHub/GitHubActorKind.cs` — actor type enum.
- `src/GithubSync.Sources.GitHub/GitHubEventKind.cs` — source-side event kind enum (13 values).
- `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs` — main implementation.
- `src/GithubSync.Sources.GitHub/GitHubSourceServiceCollectionExtensions.cs` — DI registration extension.
- `src/GithubSync.Sources.GitHub/Exceptions/GitHubAuthException.cs`
- `src/GithubSync.Sources.GitHub/Exceptions/GitHubRateLimitException.cs`
- `src/GithubSync.Sources.GitHub/Exceptions/GitHubGraphQLException.cs`
- `src/GithubSync.Sources.GitHub/RateLimiting/GitHubRateLimitBudget.cs` — in-memory budget tracker + waiter.
- `src/GithubSync.Sources.GitHub/GraphQL/IssuesPageQuery.cs` — GraphQL query string constants.
- `src/GithubSync.Sources.GitHub/GraphQL/GitHubGraphQLClient.cs` — typed `HttpClient` wrapper.
- `src/GithubSync.Sources.GitHub/GraphQL/Dto/*.cs` — response shape DTOs.

**Create (test additions):**
- `tests/GithubSync.Tests/Sources/GitHub/WireMockGitHubServer.cs` — test helper for stubbing GitHub.
- `tests/GithubSync.Tests/Sources/GitHub/GitHubRateLimitBudgetTests.cs`
- `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`
- `tests/GithubSync.Tests/Sources/GitHub/GitHubIntegrationTests.cs` — env-gated.

**Modify:**
- `GithubSync.sln` — add the new project.
- `src/GithubSync.Api/GithubSync.Api.csproj` — add ProjectReference to new project.
- `tests/GithubSync.Tests/GithubSync.Tests.csproj` — add ProjectReference to new project + WireMock.Net package.
- `src/GithubSync.Api/Program.cs` — call `AddGitHubSource(...)` extension.

**Do not modify:**
- EF Core migrations under `src/GithubSync.Data/Migrations/`.
- The `SyncCursor` schema — the spec keeps `LastETag` as-is (unused for GitHub, no migration).

---

## Task 1: Scaffold the `GithubSync.Sources.GitHub` class library

**Files:**
- Create: `src/GithubSync.Sources.GitHub/GithubSync.Sources.GitHub.csproj`
- Modify: `GithubSync.sln`
- Modify: `src/GithubSync.Api/GithubSync.Api.csproj`
- Modify: `tests/GithubSync.Tests/GithubSync.Tests.csproj`

- [ ] **Step 1: Create the class library**

Run from repo root:
```powershell
dotnet new classlib -n GithubSync.Sources.GitHub -o src/GithubSync.Sources.GitHub --framework net10.0
```

This creates `src/GithubSync.Sources.GitHub/GithubSync.Sources.GitHub.csproj` and a default `Class1.cs`. Delete the placeholder:
```powershell
Remove-Item src/GithubSync.Sources.GitHub/Class1.cs
```

- [ ] **Step 2: Configure the csproj**

Replace the contents of `src/GithubSync.Sources.GitHub/GithubSync.Sources.GitHub.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" />
    <PackageReference Include="Polly" Version="8.5.0" />
    <PackageReference Include="Microsoft.Extensions.Http.Polly" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="GithubSync.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add the project to the solution**

Run from repo root:
```powershell
dotnet sln GithubSync.sln add src/GithubSync.Sources.GitHub/GithubSync.Sources.GitHub.csproj --solution-folder src
```

- [ ] **Step 4: Reference the new project from `GithubSync.Api`**

In `src/GithubSync.Api/GithubSync.Api.csproj`, find the `<ItemGroup>` containing the existing `<ProjectReference>` line and add a sibling:

```xml
  <ItemGroup>
    <ProjectReference Include="..\GithubSync.Data\GithubSync.Data.csproj" />
    <ProjectReference Include="..\GithubSync.Sources.GitHub\GithubSync.Sources.GitHub.csproj" />
  </ItemGroup>
```

- [ ] **Step 5: Reference the new project from `GithubSync.Tests`**

In `tests/GithubSync.Tests/GithubSync.Tests.csproj`, add to the `<ItemGroup>` containing the existing `<ProjectReference>` lines:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\GithubSync.Api\GithubSync.Api.csproj" />
    <ProjectReference Include="..\..\src\GithubSync.Data\GithubSync.Data.csproj" />
    <ProjectReference Include="..\..\src\GithubSync.Sources.GitHub\GithubSync.Sources.GitHub.csproj" />
  </ItemGroup>
```

- [ ] **Step 6: Verify the build**

Run from repo root:
```powershell
dotnet build GithubSync.sln -c Debug
```

Expected: Build succeeds; the new `GithubSync.Sources.GitHub` assembly is listed in the output.

- [ ] **Step 7: Commit**

```powershell
git add GithubSync.sln src/GithubSync.Sources.GitHub/ src/GithubSync.Api/GithubSync.Api.csproj tests/GithubSync.Tests/GithubSync.Tests.csproj
git commit -m "chore: scaffold GithubSync.Sources.GitHub class library (#11)"
```

---

## Task 2: Source-side data types and interface

**Files:**
- Create: `src/GithubSync.Sources.GitHub/GitHubEventKind.cs`
- Create: `src/GithubSync.Sources.GitHub/GitHubActorKind.cs`
- Create: `src/GithubSync.Sources.GitHub/GitHubActor.cs`
- Create: `src/GithubSync.Sources.GitHub/GitHubIssueEvent.cs`
- Create: `src/GithubSync.Sources.GitHub/IGitHubIssueFetcher.cs`

- [ ] **Step 1: Write `GitHubEventKind.cs`**

```csharp
namespace GithubSync.Sources.GitHub;

// Source-side event discriminator. Translated to GithubSync.Data.Enums.EventKind by the mapper (#12).
public enum GitHubEventKind
{
    IssueOpened = 1,
    Renamed = 2,
    BodyEdited = 3,
    Labeled = 4,
    Unlabeled = 5,
    Assigned = 6,
    Unassigned = 7,
    Typed = 8,
    Untyped = 9,
    ParentAdded = 10,
    ParentRemoved = 11,
    Commented = 12,
    Closed = 13,
    Reopened = 14,
}
```

- [ ] **Step 2: Write `GitHubActorKind.cs`**

```csharp
namespace GithubSync.Sources.GitHub;

// Derived from GraphQL __typename on actor selections. Any unrecognised value maps to Other.
public enum GitHubActorKind
{
    User = 1,
    Bot = 2,
    Mannequin = 3,
    Other = 4,
}
```

- [ ] **Step 3: Write `GitHubActor.cs`**

```csharp
namespace GithubSync.Sources.GitHub;

public sealed record GitHubActor(
    string Login,         // GitHub login at observation time — can change; do not use as a join key.
    string DatabaseId,    // GitHub numeric ID, string-encoded; the stable join key (matches CanonicalActor.SourceActorId).
    GitHubActorKind Kind);
```

- [ ] **Step 4: Write `GitHubIssueEvent.cs`**

```csharp
namespace GithubSync.Sources.GitHub;

public sealed record GitHubIssueEvent(
    string SourceEntityId,         // GitHub issue number, as string (scoped per repo).
    string? SourceEventId,         // GraphQL node id; null only for body edits — matches CanonicalEvent rule.
    GitHubEventKind Kind,
    DateTimeOffset EventTime,      // UTC.
    DateTimeOffset IssueUpdatedAt, // Watermark hint — used by #13 to advance the cursor.
    GitHubActor? Actor,            // Null for deleted-user / system / "ghost" actors. Not skipped.
    string PayloadJson);           // Raw GitHub payload slice for downstream mapping + persistence.
```

- [ ] **Step 5: Write `IGitHubIssueFetcher.cs`**

```csharp
namespace GithubSync.Sources.GitHub;

public interface IGitHubIssueFetcher
{
    // Yields events grouped by issue, in non-decreasing issue.updatedAt order, within this invocation.
    // 'since' = null means "from now"; the caller decides cursor semantics.
    IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner,
        string repo,
        DateTimeOffset? since,
        CancellationToken ct);
}
```

- [ ] **Step 6: Verify the build**

```powershell
dotnet build src/GithubSync.Sources.GitHub/GithubSync.Sources.GitHub.csproj -c Debug
```

Expected: Build succeeds.

- [ ] **Step 7: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/
git commit -m "feat: source-side types + IGitHubIssueFetcher interface (#11)"
```

---

## Task 3: Typed exceptions

**Files:**
- Create: `src/GithubSync.Sources.GitHub/Exceptions/GitHubAuthException.cs`
- Create: `src/GithubSync.Sources.GitHub/Exceptions/GitHubRateLimitException.cs`
- Create: `src/GithubSync.Sources.GitHub/Exceptions/GitHubGraphQLException.cs`

- [ ] **Step 1: Write `GitHubAuthException.cs`**

```csharp
namespace GithubSync.Sources.GitHub.Exceptions;

// Thrown on 401, or 403 without any rate-limit header signal.
// Token invalid, missing scopes, repo not accessible.
public sealed class GitHubAuthException(string message) : Exception(message);
```

- [ ] **Step 2: Write `GitHubRateLimitException.cs`**

```csharp
namespace GithubSync.Sources.GitHub.Exceptions;

// Thrown when the one-shot rate-limit retry (secondary Retry-After or primary X-RateLimit-*) still returns 403.
public sealed class GitHubRateLimitException(string message) : Exception(message);
```

- [ ] **Step 3: Write `GitHubGraphQLException.cs`**

```csharp
namespace GithubSync.Sources.GitHub.Exceptions;

// Thrown on 200 OK with non-empty `errors` array in the body — schema drift, malformed query, semantic error.
public sealed class GitHubGraphQLException : Exception
{
    public IReadOnlyList<string> ErrorMessages { get; }

    public GitHubGraphQLException(IReadOnlyList<string> errorMessages)
        : base("GitHub GraphQL response contained errors: " + string.Join("; ", errorMessages))
    {
        ErrorMessages = errorMessages;
    }
}
```

- [ ] **Step 4: Verify the build**

```powershell
dotnet build src/GithubSync.Sources.GitHub/GithubSync.Sources.GitHub.csproj -c Debug
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/Exceptions/
git commit -m "feat: typed exceptions for GitHub fetcher (#11)"
```

---

## Task 4: Add WireMock.Net dependency and stub helper

**Files:**
- Modify: `tests/GithubSync.Tests/GithubSync.Tests.csproj`
- Create: `tests/GithubSync.Tests/Sources/GitHub/WireMockGitHubServer.cs`

- [ ] **Step 1: Add WireMock.Net to the test project**

In `tests/GithubSync.Tests/GithubSync.Tests.csproj`, add to the existing `<PackageReference>` `<ItemGroup>`:

```xml
    <PackageReference Include="WireMock.Net" Version="1.6.7" />
```

- [ ] **Step 2: Create the stub helper**

Create `tests/GithubSync.Tests/Sources/GitHub/WireMockGitHubServer.cs`:

```csharp
using WireMock.Server;

namespace GithubSync.Tests.Sources.GitHub;

// Thin wrapper around WireMockServer.Start() so tests don't repeat the lifecycle dance.
// Exposes the base URL the typed HttpClient is pointed at.
internal sealed class WireMockGitHubServer : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public string BaseUrl => _server.Url!;

    public WireMockServer Server => _server;

    public void Dispose() => _server.Stop();
}
```

- [ ] **Step 3: Add a smoke test that proves the helper boots**

Append to a new file `tests/GithubSync.Tests/Sources/GitHub/WireMockGitHubServerTests.cs`:

```csharp
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GithubSync.Tests.Sources.GitHub;

public class WireMockGitHubServerTests
{
    [Fact]
    public async Task Stubbed_endpoint_responds_to_post()
    {
        using var server = new WireMockGitHubServer();
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"ok":true}"""));

        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var resp = await http.PostAsync("/graphql", new StringContent(""));

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("ok", await resp.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 4: Build and run the smoke test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~WireMockGitHubServerTests"
```

Expected: 1 test passes.

- [ ] **Step 5: Commit**

```powershell
git add tests/GithubSync.Tests/
git commit -m "test: WireMock.Net dependency + GitHub stub helper (#11)"
```

---

## Task 5: `GitHubRateLimitBudget` (TDD)

**Files:**
- Create: `tests/GithubSync.Tests/Sources/GitHub/GitHubRateLimitBudgetTests.cs`
- Create: `src/GithubSync.Sources.GitHub/RateLimiting/GitHubRateLimitBudget.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/GithubSync.Tests/Sources/GitHub/GitHubRateLimitBudgetTests.cs`:

```csharp
using GithubSync.Sources.GitHub.RateLimiting;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubRateLimitBudgetTests
{
    [Fact]
    public async Task With_plenty_of_budget_WaitIfLowAsync_returns_immediately()
    {
        var budget = new GitHubRateLimitBudget();
        budget.Update(remaining: 5000, cost: 5, resetAt: DateTimeOffset.UtcNow.AddMinutes(30));

        var elapsed = await MeasureAsync(() => budget.WaitIfLowAsync(CancellationToken.None));

        Assert.True(elapsed < TimeSpan.FromMilliseconds(100),
            $"Expected immediate return, took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task With_remaining_below_safety_multiplier_sleeps_until_reset()
    {
        var budget = new GitHubRateLimitBudget();
        var resetAt = DateTimeOffset.UtcNow.AddSeconds(1);
        // remaining (5) < cost (5) * 2 -> must wait
        budget.Update(remaining: 5, cost: 5, resetAt: resetAt);

        var elapsed = await MeasureAsync(() => budget.WaitIfLowAsync(CancellationToken.None));

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(900),
            $"Expected ~1s wait, took {elapsed.TotalMilliseconds}ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"Wait took too long: {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task Cancellation_during_wait_throws_OperationCanceledException()
    {
        var budget = new GitHubRateLimitBudget();
        budget.Update(remaining: 1, cost: 100, resetAt: DateTimeOffset.UtcNow.AddSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TaskCanceledException>(
            async () => await budget.WaitIfLowAsync(cts.Token));
    }

    [Fact]
    public async Task Before_any_update_WaitIfLowAsync_returns_immediately()
    {
        // Fresh budget with no observations yet should not block — the first real query is allowed.
        var budget = new GitHubRateLimitBudget();

        var elapsed = await MeasureAsync(() => budget.WaitIfLowAsync(CancellationToken.None));

        Assert.True(elapsed < TimeSpan.FromMilliseconds(100));
    }

    private static async Task<TimeSpan> MeasureAsync(Func<Task> work)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await work();
        sw.Stop();
        return sw.Elapsed;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubRateLimitBudgetTests"
```

Expected: Compilation error — `GitHubRateLimitBudget` does not exist.

- [ ] **Step 3: Implement the class**

Create `src/GithubSync.Sources.GitHub/RateLimiting/GitHubRateLimitBudget.cs`:

```csharp
namespace GithubSync.Sources.GitHub.RateLimiting;

// Tracks GitHub GraphQL rate-limit budget across queries. Thread-safe for a single fetcher
// instance — concurrent calls from multiple fetchers are not the v1 topology.
public sealed class GitHubRateLimitBudget
{
    private int _remaining = int.MaxValue;       // No observation yet -> assume plenty.
    private int _lastObservedCost = 1;
    private DateTimeOffset _resetAt = DateTimeOffset.UtcNow;
    private bool _hasObservation;
    private readonly object _lock = new();

    public void Update(int remaining, int cost, DateTimeOffset resetAt)
    {
        lock (_lock)
        {
            _remaining = remaining;
            _lastObservedCost = Math.Max(1, cost);  // Defensive against zero/negative.
            _resetAt = resetAt;
            _hasObservation = true;
        }
    }

    public async Task WaitIfLowAsync(CancellationToken ct)
    {
        TimeSpan? wait = null;
        lock (_lock)
        {
            if (_hasObservation && _remaining < _lastObservedCost * 2)
            {
                var delta = _resetAt - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    wait = delta;
                }
            }
        }

        if (wait is { } w)
        {
            await Task.Delay(w, ct);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubRateLimitBudgetTests"
```

Expected: All 4 tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/RateLimiting/ tests/GithubSync.Tests/Sources/GitHub/GitHubRateLimitBudgetTests.cs
git commit -m "feat: GitHubRateLimitBudget with pre-flight waiter (#11)"
```

---

## Task 6: GraphQL query strings and response DTOs

**Files:**
- Create: `src/GithubSync.Sources.GitHub/GraphQL/IssuesPageQuery.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/IssuesPageResponse.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/RepositoryDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/IssuesConnection.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/IssueNode.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/PageInfoDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/ActorDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/UserContentEditNode.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/CommentNode.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/TimelineItemNode.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/LabelDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/IssueTypeRefDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/IssueParentRefDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/UserAssigneeDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/RateLimitDto.cs`
- Create: `src/GithubSync.Sources.GitHub/GraphQL/Dto/GraphQLErrorDto.cs`
- Create: `tests/GithubSync.Tests/Sources/GitHub/IssuesPageResponseDeserializationTests.cs`

- [ ] **Step 1: Write the query string constants**

Create `src/GithubSync.Sources.GitHub/GraphQL/IssuesPageQuery.cs`:

```csharp
namespace GithubSync.Sources.GitHub.GraphQL;

internal static class IssuesPageQuery
{
    // Outer query — one page of issues with first 100 of each nested connection.
    // Variables: $owner (String!), $repo (String!), $since (DateTime), $cursor (String).
    public const string Outer = """
        query IssuesPage($owner: String!, $repo: String!, $since: DateTime, $cursor: String) {
          repository(owner: $owner, name: $repo) {
            issues(first: 100, after: $cursor, filterBy: { since: $since },
                   orderBy: { field: UPDATED_AT, direction: ASC }) {
              pageInfo { endCursor hasNextPage }
              nodes {
                id number databaseId createdAt updatedAt
                author { login databaseId __typename }
                userContentEdits(first: 100) {
                  pageInfo { endCursor hasNextPage }
                  nodes { id editedAt diff editor { login databaseId __typename } }
                }
                comments(first: 100) {
                  pageInfo { endCursor hasNextPage }
                  nodes { id databaseId createdAt body author { login databaseId __typename } }
                }
                timelineItems(first: 100, itemTypes: [
                  LABELED_EVENT, UNLABELED_EVENT, ASSIGNED_EVENT, UNASSIGNED_EVENT,
                  CLOSED_EVENT, REOPENED_EVENT, TYPED_EVENT, UNTYPED_EVENT,
                  PARENT_ISSUE_ADDED_EVENT, PARENT_ISSUE_REMOVED_EVENT
                ]) {
                  pageInfo { endCursor hasNextPage }
                  nodes {
                    __typename
                    ... on LabeledEvent   { id createdAt actor { login databaseId __typename } label { name } }
                    ... on UnlabeledEvent { id createdAt actor { login databaseId __typename } label { name } }
                    ... on AssignedEvent   { id createdAt actor { login databaseId __typename } assignee { ... on User { login databaseId } } }
                    ... on UnassignedEvent { id createdAt actor { login databaseId __typename } assignee { ... on User { login databaseId } } }
                    ... on ClosedEvent     { id createdAt actor { login databaseId __typename } }
                    ... on ReopenedEvent   { id createdAt actor { login databaseId __typename } }
                    ... on TypedEvent      { id createdAt actor { login databaseId __typename } issueType { name } }
                    ... on UntypedEvent    { id createdAt actor { login databaseId __typename } prevIssueType { name } }
                    ... on ParentIssueAddedEvent   { id createdAt actor { login databaseId __typename } parent { number } }
                    ... on ParentIssueRemovedEvent { id createdAt actor { login databaseId __typename } parent { number } }
                  }
                }
              }
            }
          }
          rateLimit { remaining cost resetAt limit }
        }
        """;
}
```

- [ ] **Step 2: Write the response DTOs (one file each)**

Create the following files. Each is a single-record file under `src/GithubSync.Sources.GitHub/GraphQL/Dto/`:

`IssuesPageResponse.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssuesPageResponse(
    [property: JsonPropertyName("data")] IssuesPageData? Data,
    [property: JsonPropertyName("errors")] IReadOnlyList<GraphQLErrorDto>? Errors);

internal sealed record IssuesPageData(
    [property: JsonPropertyName("repository")] RepositoryDto? Repository,
    [property: JsonPropertyName("rateLimit")] RateLimitDto? RateLimit);
```

`RepositoryDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record RepositoryDto(
    [property: JsonPropertyName("issues")] IssuesConnection? Issues);
```

`IssuesConnection.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssuesConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<IssueNode> Nodes);
```

`IssueNode.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("author")] ActorDto? Author,
    [property: JsonPropertyName("userContentEdits")] EditsConnection? UserContentEdits,
    [property: JsonPropertyName("comments")] CommentsConnection? Comments,
    [property: JsonPropertyName("timelineItems")] TimelineItemsConnection? TimelineItems);

internal sealed record EditsConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<UserContentEditNode> Nodes);

internal sealed record CommentsConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<CommentNode> Nodes);

internal sealed record TimelineItemsConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<TimelineItemNode> Nodes);
```

`PageInfoDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record PageInfoDto(
    [property: JsonPropertyName("endCursor")] string? EndCursor,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage);
```

`ActorDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record ActorDto(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("__typename")] string? TypeName);
```

`UserContentEditNode.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record UserContentEditNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("editedAt")] DateTimeOffset EditedAt,
    [property: JsonPropertyName("diff")] string? Diff,
    [property: JsonPropertyName("editor")] ActorDto? Editor);
```

`CommentNode.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record CommentNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("author")] ActorDto? Author);
```

`TimelineItemNode.cs` (the polymorphic node; we use a flat record with nullable per-type fields rather than a JsonConverter for simplicity):
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record TimelineItemNode(
    [property: JsonPropertyName("__typename")] string TypeName,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("actor")] ActorDto? Actor,
    [property: JsonPropertyName("label")] LabelDto? Label,
    [property: JsonPropertyName("assignee")] UserAssigneeDto? Assignee,
    [property: JsonPropertyName("issueType")] IssueTypeRefDto? IssueType,
    [property: JsonPropertyName("prevIssueType")] IssueTypeRefDto? PrevIssueType,
    [property: JsonPropertyName("parent")] IssueParentRefDto? Parent);
```

`LabelDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record LabelDto([property: JsonPropertyName("name")] string Name);
```

`IssueTypeRefDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueTypeRefDto([property: JsonPropertyName("name")] string Name);
```

`IssueParentRefDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueParentRefDto([property: JsonPropertyName("number")] int Number);
```

`UserAssigneeDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record UserAssigneeDto(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("databaseId")] long DatabaseId);
```

`RateLimitDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record RateLimitDto(
    [property: JsonPropertyName("remaining")] int Remaining,
    [property: JsonPropertyName("cost")] int Cost,
    [property: JsonPropertyName("resetAt")] DateTimeOffset ResetAt,
    [property: JsonPropertyName("limit")] int Limit);
```

`GraphQLErrorDto.cs`:
```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record GraphQLErrorDto(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string? Type);
```

- [ ] **Step 3: Write a deserialization smoke test**

Create `tests/GithubSync.Tests/Sources/GitHub/IssuesPageResponseDeserializationTests.cs`:

```csharp
using System.Text.Json;
using GithubSync.Sources.GitHub.GraphQL.Dto;

namespace GithubSync.Tests.Sources.GitHub;

public class IssuesPageResponseDeserializationTests
{
    [Fact]
    public void Deserializes_minimal_response_with_one_issue_and_one_label_event()
    {
        const string json = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw",
                    "number": 1,
                    "databaseId": 1001,
                    "createdAt": "2026-01-01T00:00:00Z",
                    "updatedAt": "2026-01-02T00:00:00Z",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_1", "createdAt": "2026-01-02T00:00:00Z",
                          "actor": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                          "label": { "name": "bug" } }
                      ]
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

        var response = JsonSerializer.Deserialize<IssuesPageResponse>(json);

        Assert.NotNull(response);
        Assert.Null(response!.Errors);
        Assert.NotNull(response.Data?.Repository?.Issues);
        var issue = Assert.Single(response.Data.Repository.Issues.Nodes);
        Assert.Equal(1, issue.Number);
        Assert.Equal("octocat", issue.Author?.Login);
        var ev = Assert.Single(issue.TimelineItems!.Nodes);
        Assert.Equal("LabeledEvent", ev.TypeName);
        Assert.Equal("bug", ev.Label?.Name);
        Assert.Equal(4999, response.Data.RateLimit?.Remaining);
    }
}
```

Because the DTOs are `internal`, the test relies on `InternalsVisibleTo("GithubSync.Tests")` declared in Task 1 step 2.

- [ ] **Step 4: Run the test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~IssuesPageResponseDeserializationTests"
```

Expected: 1 test passes.

- [ ] **Step 5: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/GraphQL/ tests/GithubSync.Tests/Sources/GitHub/IssuesPageResponseDeserializationTests.cs
git commit -m "feat: GraphQL query string + response DTOs (#11)"
```

---

## Task 7: Typed `GitHubGraphQLClient` with auth and Polly transient retry

**Files:**
- Create: `src/GithubSync.Sources.GitHub/GraphQL/GitHubGraphQLClient.cs`
- Create: `src/GithubSync.Sources.GitHub/GitHubSourceServiceCollectionExtensions.cs`
- Create: `tests/GithubSync.Tests/Sources/GitHub/GitHubGraphQLClientTests.cs`

- [ ] **Step 1: Write the client**

Create `src/GithubSync.Sources.GitHub/GraphQL/GitHubGraphQLClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GithubSync.Sources.GitHub.Exceptions;
using GithubSync.Sources.GitHub.GraphQL.Dto;

namespace GithubSync.Sources.GitHub.GraphQL;

internal sealed class GitHubGraphQLClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IssuesPageResponse> QueryIssuesPageAsync(
        string owner, string repo, DateTimeOffset? since, string? cursor, CancellationToken ct)
    {
        var body = new
        {
            query = IssuesPageQuery.Outer,
            variables = new
            {
                owner,
                repo,
                since = since?.ToUniversalTime(),
                cursor,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(body),
        };

        using var response = await SendWithRateLimitRetryAsync(request, ct);

        var payload = await response.Content.ReadFromJsonAsync<IssuesPageResponse>(JsonOptions, ct)
            ?? throw new GitHubGraphQLException(["empty response body"]);

        if (payload.Errors is { Count: > 0 } errs)
        {
            throw new GitHubGraphQLException(errs.Select(e => e.Message).ToList());
        }

        return payload;
    }

    // Sends the request through the HttpClient (Polly handles 5xx transient retries).
    // Adds a one-shot retry for 403 rate-limit signals (Retry-After OR X-RateLimit-Remaining=0 + Reset).
    // Throws GitHubAuthException for 401 and for 403 with no rate-limit header signal.
    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await httpClient.SendAsync(CloneRequest(request), ct);
        if (response.StatusCode != HttpStatusCode.Forbidden && response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new GitHubAuthException("GitHub returned 401 Unauthorized.");
        }

        // 403 — decide between rate limit and auth.
        if (TryGetRateLimitWait(response, out var wait))
        {
            response.Dispose();
            await Task.Delay(wait, ct);

            var retried = await httpClient.SendAsync(CloneRequest(request), ct);
            if (retried.StatusCode == HttpStatusCode.Forbidden)
            {
                retried.Dispose();
                throw new GitHubRateLimitException("Rate-limit retry still returned 403.");
            }
            return retried;
        }

        response.Dispose();
        throw new GitHubAuthException("GitHub returned 403 with no rate-limit header signal.");
    }

    private static bool TryGetRateLimitWait(HttpResponseMessage response, out TimeSpan wait)
    {
        // Prefer Retry-After (secondary limit) over header-based reset (primary limit).
        if (response.Headers.RetryAfter is { Delta: { } delta })
        {
            wait = delta;
            return true;
        }
        if (response.Headers.RetryAfter is { Date: { } date })
        {
            wait = date - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return true;
        }

        // Primary limit via headers.
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingVals)
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals)
            && int.TryParse(remainingVals.FirstOrDefault(), out var remaining)
            && long.TryParse(resetVals.FirstOrDefault(), out var resetEpoch)
            && remaining == 0)
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            wait = resetAt - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return true;
        }

        wait = default;
        return false;
    }

    // HttpRequestMessage instances cannot be sent twice; clone for retry.
    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        if (source.Content is not null)
        {
            var ms = new MemoryStream();
            source.Content.CopyToAsync(ms).GetAwaiter().GetResult();
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            foreach (var h in source.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        foreach (var h in source.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }
}
```

- [ ] **Step 2: Write the DI extension**

Create `src/GithubSync.Sources.GitHub/GitHubSourceServiceCollectionExtensions.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using GithubSync.Sources.GitHub.GraphQL;
using GithubSync.Sources.GitHub.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;

namespace GithubSync.Sources.GitHub;

public static class GitHubSourceServiceCollectionExtensions
{
    public const string TokenConfigKey = "GITHUB_TOKEN";

    public static IServiceCollection AddGitHubSource(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<GitHubRateLimitBudget>();

        services.AddHttpClient<GitHubGraphQLClient>(client =>
            {
                client.BaseAddress = new Uri("https://api.github.com");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/1.0");

                var token = configuration[TokenConfigKey];
                if (!string.IsNullOrWhiteSpace(token))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                }
            })
            .AddPolicyHandler(BuildTransientRetryPolicy());

        services.AddTransient<IGitHubIssueFetcher, GitHubIssueFetcher>();

        return services;
    }

    // 3 retries on top of the initial attempt; exponential backoff 1s -> 2s -> 4s.
    // Handles HttpRequestException and 5xx responses. Does not handle 403 — that's the client's job.
    private static IAsyncPolicy<HttpResponseMessage> BuildTransientRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
}
```

Note: `GitHubIssueFetcher` is declared in this DI block but the class itself is added in Task 8. The build will currently fail at that registration line; Task 8 fixes it. To keep this task individually buildable, stub the class as an empty placeholder for now and replace its body in Task 8.

- [ ] **Step 3: Write the stub fetcher to allow the build to succeed**

Create `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace GithubSync.Sources.GitHub;

internal sealed class GitHubIssueFetcher : IGitHubIssueFetcher
{
    // Stub — implemented in Task 8.
    public async IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner, string repo, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
```

- [ ] **Step 4: Test that auth header is attached + transient retry triggers**

Create `tests/GithubSync.Tests/Sources/GitHub/GitHubGraphQLClientTests.cs`:

```csharp
using System.Net;
using GithubSync.Sources.GitHub;
using GithubSync.Sources.GitHub.GraphQL;
using GithubSync.Sources.GitHub.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubGraphQLClientTests
{
    [Fact]
    public async Task Attaches_bearer_token_from_configuration()
    {
        using var server = new WireMockGitHubServer();
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithHeader("Authorization", "Bearer test-token"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"data":{"repository":{"issues":{"pageInfo":{"endCursor":null,"hasNextPage":false},"nodes":[]}},"rateLimit":{"remaining":4999,"cost":1,"resetAt":"2026-01-01T01:00:00Z","limit":5000}}}"""));

        var client = BuildClient(server.BaseUrl, token: "test-token");

        var resp = await client.QueryIssuesPageAsync("o", "r", since: null, cursor: null, ct: default);

        Assert.NotNull(resp);
        // WireMock would have returned 404 if the Authorization header didn't match the stub above.
    }

    [Fact]
    public async Task Polly_retries_on_503_then_succeeds()
    {
        using var server = new WireMockGitHubServer();
        var scenario = "transient";
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs(null)
            .WillSetStateTo("got-one")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("got-one")
            .WillSetStateTo("got-two")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("got-two")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"data":{"repository":{"issues":{"pageInfo":{"endCursor":null,"hasNextPage":false},"nodes":[]}},"rateLimit":{"remaining":4999,"cost":1,"resetAt":"2026-01-01T01:00:00Z","limit":5000}}}"""));

        var client = BuildClient(server.BaseUrl, token: "test-token");

        var resp = await client.QueryIssuesPageAsync("o", "r", null, null, default);

        Assert.NotNull(resp);
        Assert.Equal(3, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

    private static GitHubGraphQLClient BuildClient(string baseUrl, string token)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = token,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddGitHubSource(config);
        // Re-point base address at the WireMock URL for this test.
        services.AddHttpClient<GitHubGraphQLClient>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/1.0");
        });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<GitHubGraphQLClient>();
    }
}
```

- [ ] **Step 5: Run tests**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubGraphQLClientTests"
```

Expected: Both tests pass. (The transient retry test verifies that the 3 attempts occurred — initial + 2 retries before success on attempt 3.)

- [ ] **Step 6: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/ tests/GithubSync.Tests/Sources/GitHub/GitHubGraphQLClientTests.cs
git commit -m "feat: GitHubGraphQLClient with auth + Polly transient retry (#11)"
```

---

## Task 8: Fetcher core + empty page (test 1)

**Files:**
- Modify: `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs`
- Create: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`
- Create: `tests/GithubSync.Tests/Sources/GitHub/FetcherTestHarness.cs`

- [ ] **Step 1: Write the fetcher test harness**

Create `tests/GithubSync.Tests/Sources/GitHub/FetcherTestHarness.cs`:

```csharp
using GithubSync.Sources.GitHub;
using GithubSync.Sources.GitHub.GraphQL;
using GithubSync.Sources.GitHub.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GithubSync.Tests.Sources.GitHub;

internal static class FetcherTestHarness
{
    public static IGitHubIssueFetcher Build(string baseUrl, string token = "test-token")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = token,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitHubSource(config);
        // Override base address to point at the WireMock URL.
        services.AddHttpClient<GitHubGraphQLClient>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/1.0");
        });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IGitHubIssueFetcher>();
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubIssueFetcherTests
{
    [Fact]
    public async Task Empty_page_yields_zero_events()
    {
        using var server = new WireMockGitHubServer();
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EmptyPageBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        var events = new List<global::GithubSync.Sources.GitHub.GitHubIssueEvent>();
        await foreach (var e in fetcher.FetchAsync("octocat", "Hello-World", since: null, ct: default))
        {
            events.Add(e);
        }

        Assert.Empty(events);
    }

    private const string EmptyPageBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": []
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;
}
```

- [ ] **Step 3: Run the test to verify it fails**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubIssueFetcherTests.Empty_page"
```

Expected: Test passes (the stub fetcher yields nothing — so this test actually passes on the placeholder!). Continue to step 4 to implement the real fetcher; subsequent tests will exercise actual behaviour.

- [ ] **Step 4: Replace the stub with the real fetcher**

Replace the contents of `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using GithubSync.Sources.GitHub.GraphQL;
using GithubSync.Sources.GitHub.GraphQL.Dto;
using GithubSync.Sources.GitHub.RateLimiting;
using Microsoft.Extensions.Logging;

namespace GithubSync.Sources.GitHub;

internal sealed class GitHubIssueFetcher(
    GitHubGraphQLClient client,
    GitHubRateLimitBudget budget,
    ILogger<GitHubIssueFetcher> logger) : IGitHubIssueFetcher
{
    public async IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner, string repo, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation(
            "GitHub fetch started {Source} {Owner} {Repo} {Since}",
            "github", owner, repo, since);

        var issuesYielded = 0;
        var eventsYielded = 0;
        var startedAt = DateTimeOffset.UtcNow;
        int lastRemaining = -1;

        string? cursor = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await budget.WaitIfLowAsync(ct);

            var response = await client.QueryIssuesPageAsync(owner, repo, since, cursor, ct);
            if (response.Data?.RateLimit is { } rl)
            {
                budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);
                lastRemaining = rl.Remaining;
            }

            var issues = response.Data?.Repository?.Issues;
            if (issues is null) yield break;

            foreach (var issue in issues.Nodes)
            {
                issuesYielded++;
                foreach (var ev in ExtractEvents(issue, since))
                {
                    eventsYielded++;
                    yield return ev;
                }
            }

            if (!issues.PageInfo.HasNextPage) break;
            cursor = issues.PageInfo.EndCursor;
        }

        logger.LogInformation(
            "GitHub fetch completed {Source} {Owner} {Repo} {IssuesYielded} {EventsYielded} {DurationMs} {RateLimitRemaining}",
            "github", owner, repo, issuesYielded, eventsYielded,
            (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, lastRemaining);
    }

    private static IEnumerable<GitHubIssueEvent> ExtractEvents(IssueNode issue, DateTimeOffset? since)
    {
        // Implemented in Task 9.
        yield break;
    }
}
```

- [ ] **Step 5: Verify the empty-page test still passes**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubIssueFetcherTests.Empty_page"
```

Expected: Test passes.

- [ ] **Step 6: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs tests/GithubSync.Tests/Sources/GitHub/FetcherTestHarness.cs tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "feat: GitHubIssueFetcher core loop + empty page test (#11)"
```

---

## Task 9: Event extraction — issue-opened synthesis, timeline mapping, comments, body edits, null actors (tests 2, 11)

**Files:**
- Modify: `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs`
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Single_page_with_varied_content_yields_expected_events_in_order()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SinglePageVariedBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        var events = await CollectAsync(fetcher);

        // One issue with: create (synth), label, comment, close — in event-time order.
        Assert.Equal(4, events.Count);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.IssueOpened, events[0].Kind);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.Labeled, events[1].Kind);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.Commented, events[2].Kind);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.Closed, events[3].Kind);
        Assert.All(events, e => Assert.Equal("42", e.SourceEntityId));
    }

    [Fact]
    public async Task Null_actor_is_passed_through_not_skipped()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(NullActorBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        var events = await CollectAsync(fetcher);

        // Two events: create (synth, with author), labeled (with null actor)
        Assert.Equal(2, events.Count);
        var labeled = events.Single(e => e.Kind == global::GithubSync.Sources.GitHub.GitHubEventKind.Labeled);
        Assert.Null(labeled.Actor);
    }

    private const string SinglePageVariedBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_42",
                    "number": 42,
                    "databaseId": 4242,
                    "createdAt": "2026-01-01T10:00:00Z",
                    "updatedAt": "2026-01-01T12:00:00Z",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "id": "C_1", "databaseId": 5001, "createdAt": "2026-01-01T10:30:00Z", "body": "hi",
                          "author": { "login": "octocat", "databaseId": 1, "__typename": "User" } }
                      ]
                    },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_1", "createdAt": "2026-01-01T10:15:00Z",
                          "actor": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                          "label": { "name": "bug" } },
                        { "__typename": "ClosedEvent", "id": "CE_1", "createdAt": "2026-01-01T12:00:00Z",
                          "actor": { "login": "octocat", "databaseId": 1, "__typename": "User" } }
                      ]
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    private const string NullActorBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_99",
                    "number": 99,
                    "databaseId": 9999,
                    "createdAt": "2026-01-01T10:00:00Z",
                    "updatedAt": "2026-01-01T11:00:00Z",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_99", "createdAt": "2026-01-01T11:00:00Z",
                          "actor": null,
                          "label": { "name": "stale" } }
                      ]
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    private static async Task<List<global::GithubSync.Sources.GitHub.GitHubIssueEvent>> CollectAsync(
        global::GithubSync.Sources.GitHub.IGitHubIssueFetcher fetcher)
    {
        var list = new List<global::GithubSync.Sources.GitHub.GitHubIssueEvent>();
        await foreach (var e in fetcher.FetchAsync("octocat", "Hello-World", since: null, ct: default))
            list.Add(e);
        return list;
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubIssueFetcherTests.Single_page_with_varied_content"
```

Expected: Test fails — 0 events instead of 4 (`ExtractEvents` is still the stub).

- [ ] **Step 3: Implement the event extraction**

Replace the `ExtractEvents` method in `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs` with:

```csharp
    private static IEnumerable<GitHubIssueEvent> ExtractEvents(IssueNode issue, DateTimeOffset? since)
    {
        var sourceEntityId = issue.Number.ToString();
        var issueUpdatedAt = issue.UpdatedAt;

        // Build an event list, then sort by event time so within-issue ordering is stable.
        var events = new List<GitHubIssueEvent>(16);

        // 1. Synthesise IssueOpened from createdAt (if in window).
        if (since is null || issue.CreatedAt >= since)
        {
            events.Add(new GitHubIssueEvent(
                SourceEntityId: sourceEntityId,
                SourceEventId: issue.Id,
                Kind: GitHubEventKind.IssueOpened,
                EventTime: issue.CreatedAt,
                IssueUpdatedAt: issueUpdatedAt,
                Actor: ToActor(issue.Author),
                PayloadJson: SerializeIssueOpenedPayload(issue)));
        }

        // 2. Body edits.
        if (issue.UserContentEdits is { } edits)
        {
            foreach (var edit in edits.Nodes)
            {
                if (since is not null && edit.EditedAt < since) continue;
                events.Add(new GitHubIssueEvent(
                    SourceEntityId: sourceEntityId,
                    SourceEventId: null, // body edits do not carry a stable per-event ID we treat as canonical
                    Kind: GitHubEventKind.BodyEdited,
                    EventTime: edit.EditedAt,
                    IssueUpdatedAt: issueUpdatedAt,
                    Actor: ToActor(edit.Editor),
                    PayloadJson: JsonSerializer.Serialize(edit)));
            }
        }

        // 3. Comments.
        if (issue.Comments is { } comments)
        {
            foreach (var c in comments.Nodes)
            {
                if (since is not null && c.CreatedAt < since) continue;
                events.Add(new GitHubIssueEvent(
                    SourceEntityId: sourceEntityId,
                    SourceEventId: c.Id,
                    Kind: GitHubEventKind.Commented,
                    EventTime: c.CreatedAt,
                    IssueUpdatedAt: issueUpdatedAt,
                    Actor: ToActor(c.Author),
                    PayloadJson: JsonSerializer.Serialize(c)));
            }
        }

        // 4. Timeline items.
        if (issue.TimelineItems is { } timeline)
        {
            foreach (var t in timeline.Nodes)
            {
                if (since is not null && t.CreatedAt < since) continue;
                var kind = MapTimelineKind(t.TypeName);
                if (kind is null) continue; // skip unknown __typename — mapper handles unknown-canonical-kind logging later
                events.Add(new GitHubIssueEvent(
                    SourceEntityId: sourceEntityId,
                    SourceEventId: t.Id,
                    Kind: kind.Value,
                    EventTime: t.CreatedAt,
                    IssueUpdatedAt: issueUpdatedAt,
                    Actor: ToActor(t.Actor),
                    PayloadJson: JsonSerializer.Serialize(t)));
            }
        }

        // Within-issue: order by event time, then by node id for ties.
        events.Sort((a, b) =>
        {
            var c = a.EventTime.CompareTo(b.EventTime);
            return c != 0 ? c : string.CompareOrdinal(a.SourceEventId ?? "", b.SourceEventId ?? "");
        });

        return events;
    }

    private static string SerializeIssueOpenedPayload(IssueNode issue) =>
        JsonSerializer.Serialize(new
        {
            issue.Id, issue.Number, issue.DatabaseId, issue.CreatedAt, issue.Author,
        });

    private static GitHubActor? ToActor(ActorDto? dto)
    {
        if (dto is null) return null;
        var kind = dto.TypeName switch
        {
            "User" => GitHubActorKind.User,
            "Bot" => GitHubActorKind.Bot,
            "Mannequin" => GitHubActorKind.Mannequin,
            _ => GitHubActorKind.Other,
        };
        return new GitHubActor(dto.Login, dto.DatabaseId.ToString(), kind);
    }

    private static GitHubEventKind? MapTimelineKind(string typeName) => typeName switch
    {
        "LabeledEvent" => GitHubEventKind.Labeled,
        "UnlabeledEvent" => GitHubEventKind.Unlabeled,
        "AssignedEvent" => GitHubEventKind.Assigned,
        "UnassignedEvent" => GitHubEventKind.Unassigned,
        "ClosedEvent" => GitHubEventKind.Closed,
        "ReopenedEvent" => GitHubEventKind.Reopened,
        "TypedEvent" => GitHubEventKind.Typed,
        "UntypedEvent" => GitHubEventKind.Untyped,
        "ParentIssueAddedEvent" => GitHubEventKind.ParentAdded,
        "ParentIssueRemovedEvent" => GitHubEventKind.ParentRemoved,
        _ => null,
    };
```

- [ ] **Step 4: Run tests to verify they pass**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubIssueFetcherTests"
```

Expected: All 3 fetcher tests pass (empty page + varied content + null actor).

- [ ] **Step 5: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "feat: event extraction with timeline + edits + comments + null actor passthrough (#11)"
```

---

## Task 10: Outer pagination (test 3)

**Files:**
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

The fetcher's outer pagination loop is already implemented in Task 8 (the `while` loop with `cursor` chaining). This task verifies it with a multi-page stub.

- [ ] **Step 1: Write the failing test**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Outer_pagination_walks_two_pages_passing_endCursor_as_after()
    {
        using var server = new WireMockGitHubServer();

        // Page 1: hasNextPage true, returns 1 issue
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && !b.Contains("\"cursor\":\"page2cursor\"")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(PaginationPage1));

        // Page 2: cursor present, hasNextPage false
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && b.Contains("\"cursor\":\"page2cursor\"")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(PaginationPage2));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var events = await CollectAsync(fetcher);

        // 2 issues across pages, each yielding only IssueOpened (no timeline content)
        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "1", "2" }, events.Select(e => e.SourceEntityId).ToArray());
    }

    private const string PaginationPage1 = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": "page2cursor", "hasNextPage": true },
                "nodes": [
                  {
                    "id": "I_kw_1", "number": 1, "databaseId": 1, "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                    "author": { "login": "a", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    private const string PaginationPage2 = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_2", "number": 2, "databaseId": 2, "createdAt": "2026-01-02T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                    "author": { "login": "b", "databaseId": 2, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4998, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;
```

- [ ] **Step 2: Run the test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~Outer_pagination"
```

Expected: Test passes (the pagination loop was already implemented in Task 8).

- [ ] **Step 3: Commit**

```powershell
git add tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "test: outer pagination walks two pages (#11)"
```

---

## Task 11: Inner pagination follow-up for overflowing connections (test 4)

**Files:**
- Modify: `src/GithubSync.Sources.GitHub/GraphQL/IssuesPageQuery.cs`
- Modify: `src/GithubSync.Sources.GitHub/GraphQL/GitHubGraphQLClient.cs`
- Modify: `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs`
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

Per the spec: when any per-issue connection returns `hasNextPage: true`, fire a targeted follow-up. We pick the simplest viable shape — three separate follow-up queries, one per connection type, each draining one connection by `endCursor`.

- [ ] **Step 1: Add the follow-up query strings**

Append to `src/GithubSync.Sources.GitHub/GraphQL/IssuesPageQuery.cs`:

```csharp
    // Per-issue follow-up: drains the timelineItems connection for one issue from the given cursor.
    public const string IssueTimelineFollowUp = """
        query IssueTimelineFollowUp($owner: String!, $repo: String!, $number: Int!, $cursor: String!) {
          repository(owner: $owner, name: $repo) {
            issue(number: $number) {
              updatedAt
              timelineItems(first: 100, after: $cursor, itemTypes: [
                LABELED_EVENT, UNLABELED_EVENT, ASSIGNED_EVENT, UNASSIGNED_EVENT,
                CLOSED_EVENT, REOPENED_EVENT, TYPED_EVENT, UNTYPED_EVENT,
                PARENT_ISSUE_ADDED_EVENT, PARENT_ISSUE_REMOVED_EVENT
              ]) {
                pageInfo { endCursor hasNextPage }
                nodes {
                  __typename
                  ... on LabeledEvent   { id createdAt actor { login databaseId __typename } label { name } }
                  ... on UnlabeledEvent { id createdAt actor { login databaseId __typename } label { name } }
                  ... on AssignedEvent   { id createdAt actor { login databaseId __typename } assignee { ... on User { login databaseId } } }
                  ... on UnassignedEvent { id createdAt actor { login databaseId __typename } assignee { ... on User { login databaseId } } }
                  ... on ClosedEvent     { id createdAt actor { login databaseId __typename } }
                  ... on ReopenedEvent   { id createdAt actor { login databaseId __typename } }
                  ... on TypedEvent      { id createdAt actor { login databaseId __typename } issueType { name } }
                  ... on UntypedEvent    { id createdAt actor { login databaseId __typename } prevIssueType { name } }
                  ... on ParentIssueAddedEvent   { id createdAt actor { login databaseId __typename } parent { number } }
                  ... on ParentIssueRemovedEvent { id createdAt actor { login databaseId __typename } parent { number } }
                }
              }
            }
          }
          rateLimit { remaining cost resetAt limit }
        }
        """;

    public const string IssueCommentsFollowUp = """
        query IssueCommentsFollowUp($owner: String!, $repo: String!, $number: Int!, $cursor: String!) {
          repository(owner: $owner, name: $repo) {
            issue(number: $number) {
              updatedAt
              comments(first: 100, after: $cursor) {
                pageInfo { endCursor hasNextPage }
                nodes { id databaseId createdAt body author { login databaseId __typename } }
              }
            }
          }
          rateLimit { remaining cost resetAt limit }
        }
        """;

    public const string IssueEditsFollowUp = """
        query IssueEditsFollowUp($owner: String!, $repo: String!, $number: Int!, $cursor: String!) {
          repository(owner: $owner, name: $repo) {
            issue(number: $number) {
              updatedAt
              userContentEdits(first: 100, after: $cursor) {
                pageInfo { endCursor hasNextPage }
                nodes { id editedAt diff editor { login databaseId __typename } }
              }
            }
          }
          rateLimit { remaining cost resetAt limit }
        }
        """;
```

- [ ] **Step 2: Add follow-up response DTOs**

Create `src/GithubSync.Sources.GitHub/GraphQL/Dto/IssueFollowUpResponse.cs`:

```csharp
using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueFollowUpResponse(
    [property: JsonPropertyName("data")] IssueFollowUpData? Data,
    [property: JsonPropertyName("errors")] IReadOnlyList<GraphQLErrorDto>? Errors);

internal sealed record IssueFollowUpData(
    [property: JsonPropertyName("repository")] IssueFollowUpRepository? Repository,
    [property: JsonPropertyName("rateLimit")] RateLimitDto? RateLimit);

internal sealed record IssueFollowUpRepository(
    [property: JsonPropertyName("issue")] IssueFollowUpIssue? Issue);

internal sealed record IssueFollowUpIssue(
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("timelineItems")] TimelineItemsConnection? TimelineItems,
    [property: JsonPropertyName("comments")] CommentsConnection? Comments,
    [property: JsonPropertyName("userContentEdits")] EditsConnection? UserContentEdits);
```

- [ ] **Step 3: Add follow-up methods to the GraphQL client**

Append to `src/GithubSync.Sources.GitHub/GraphQL/GitHubGraphQLClient.cs`, inside the class:

```csharp
    public Task<IssueFollowUpResponse> FollowUpTimelineAsync(
        string owner, string repo, int number, string cursor, CancellationToken ct) =>
        FollowUpAsync(IssuesPageQuery.IssueTimelineFollowUp, owner, repo, number, cursor, ct);

    public Task<IssueFollowUpResponse> FollowUpCommentsAsync(
        string owner, string repo, int number, string cursor, CancellationToken ct) =>
        FollowUpAsync(IssuesPageQuery.IssueCommentsFollowUp, owner, repo, number, cursor, ct);

    public Task<IssueFollowUpResponse> FollowUpEditsAsync(
        string owner, string repo, int number, string cursor, CancellationToken ct) =>
        FollowUpAsync(IssuesPageQuery.IssueEditsFollowUp, owner, repo, number, cursor, ct);

    private async Task<IssueFollowUpResponse> FollowUpAsync(
        string query, string owner, string repo, int number, string cursor, CancellationToken ct)
    {
        var body = new
        {
            query,
            variables = new { owner, repo, number, cursor },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(body),
        };
        using var response = await SendWithRateLimitRetryAsync(request, ct);

        var payload = await response.Content.ReadFromJsonAsync<IssueFollowUpResponse>(JsonOptions, ct)
            ?? throw new GitHubGraphQLException(["empty follow-up response body"]);
        if (payload.Errors is { Count: > 0 } errs)
            throw new GitHubGraphQLException(errs.Select(e => e.Message).ToList());
        return payload;
    }
```

- [ ] **Step 4: Plumb follow-up into the fetcher**

Modify `src/GithubSync.Sources.GitHub/GitHubIssueFetcher.cs`. Inside the `foreach (var issue in issues.Nodes)` loop, after the existing `foreach (var ev in ExtractEvents(...))` block, add follow-up draining:

```csharp
                foreach (var ev in ExtractEvents(issue, since))
                {
                    eventsYielded++;
                    yield return ev;
                }

                await foreach (var ev in DrainOverflowingConnectionsAsync(owner, repo, issue, since, ct))
                {
                    eventsYielded++;
                    yield return ev;
                }
```

Add the helper method to the class:

```csharp
    private async IAsyncEnumerable<GitHubIssueEvent> DrainOverflowingConnectionsAsync(
        string owner, string repo, IssueNode issue, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Timeline overflow
        if (issue.TimelineItems is { PageInfo.HasNextPage: true, PageInfo.EndCursor: { } tCursor })
        {
            string? cursor = tCursor;
            while (cursor is not null)
            {
                ct.ThrowIfCancellationRequested();
                await budget.WaitIfLowAsync(ct);
                var resp = await client.FollowUpTimelineAsync(owner, repo, issue.Number, cursor, ct);
                if (resp.Data?.RateLimit is { } rl) budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);

                var conn = resp.Data?.Repository?.Issue?.TimelineItems;
                if (conn is null) break;
                foreach (var ev in ExtractTimelineEvents(issue, conn.Nodes, since))
                    yield return ev;
                cursor = conn.PageInfo.HasNextPage ? conn.PageInfo.EndCursor : null;
            }
        }

        // Comments overflow
        if (issue.Comments is { PageInfo.HasNextPage: true, PageInfo.EndCursor: { } cCursor })
        {
            string? cursor = cCursor;
            while (cursor is not null)
            {
                ct.ThrowIfCancellationRequested();
                await budget.WaitIfLowAsync(ct);
                var resp = await client.FollowUpCommentsAsync(owner, repo, issue.Number, cursor, ct);
                if (resp.Data?.RateLimit is { } rl) budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);

                var conn = resp.Data?.Repository?.Issue?.Comments;
                if (conn is null) break;
                foreach (var ev in ExtractCommentEvents(issue, conn.Nodes, since))
                    yield return ev;
                cursor = conn.PageInfo.HasNextPage ? conn.PageInfo.EndCursor : null;
            }
        }

        // Body edits overflow
        if (issue.UserContentEdits is { PageInfo.HasNextPage: true, PageInfo.EndCursor: { } eCursor })
        {
            string? cursor = eCursor;
            while (cursor is not null)
            {
                ct.ThrowIfCancellationRequested();
                await budget.WaitIfLowAsync(ct);
                var resp = await client.FollowUpEditsAsync(owner, repo, issue.Number, cursor, ct);
                if (resp.Data?.RateLimit is { } rl) budget.Update(rl.Remaining, rl.Cost, rl.ResetAt);

                var conn = resp.Data?.Repository?.Issue?.UserContentEdits;
                if (conn is null) break;
                foreach (var ev in ExtractEditEvents(issue, conn.Nodes, since))
                    yield return ev;
                cursor = conn.PageInfo.HasNextPage ? conn.PageInfo.EndCursor : null;
            }
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractTimelineEvents(
        IssueNode issue, IReadOnlyList<TimelineItemNode> nodes, DateTimeOffset? since)
    {
        foreach (var t in nodes)
        {
            if (since is not null && t.CreatedAt < since) continue;
            var kind = MapTimelineKind(t.TypeName);
            if (kind is null) continue;
            yield return new GitHubIssueEvent(
                issue.Number.ToString(), t.Id, kind.Value, t.CreatedAt, issue.UpdatedAt,
                ToActor(t.Actor), JsonSerializer.Serialize(t));
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractCommentEvents(
        IssueNode issue, IReadOnlyList<CommentNode> nodes, DateTimeOffset? since)
    {
        foreach (var c in nodes)
        {
            if (since is not null && c.CreatedAt < since) continue;
            yield return new GitHubIssueEvent(
                issue.Number.ToString(), c.Id, GitHubEventKind.Commented, c.CreatedAt, issue.UpdatedAt,
                ToActor(c.Author), JsonSerializer.Serialize(c));
        }
    }

    private static IEnumerable<GitHubIssueEvent> ExtractEditEvents(
        IssueNode issue, IReadOnlyList<UserContentEditNode> nodes, DateTimeOffset? since)
    {
        foreach (var e in nodes)
        {
            if (since is not null && e.EditedAt < since) continue;
            yield return new GitHubIssueEvent(
                issue.Number.ToString(), null, GitHubEventKind.BodyEdited, e.EditedAt, issue.UpdatedAt,
                ToActor(e.Editor), JsonSerializer.Serialize(e));
        }
    }
```

- [ ] **Step 5: Write the test**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Inner_pagination_follow_up_drains_overflowing_timeline()
    {
        using var server = new WireMockGitHubServer();

        // Outer query: 1 issue with timeline.hasNextPage = true, endCursor = "t-cursor"
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && b.Contains("IssuesPage")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(OuterWithOverflow));

        // Follow-up timeline: hasNextPage = false, returns one more event
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && b.Contains("IssueTimelineFollowUp")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(FollowUpTimeline));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var events = await CollectAsync(fetcher);

        // Expected: IssueOpened + initial LabeledEvent + follow-up ClosedEvent = 3 events
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.Kind == global::GithubSync.Sources.GitHub.GitHubEventKind.Closed);

        // Verify the follow-up query was called exactly once
        var followUps = server.Server.LogEntries
            .Count(le => le.RequestMessage.Body?.Contains("IssueTimelineFollowUp") == true);
        Assert.Equal(1, followUps);
    }

    private const string OuterWithOverflow = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_77", "number": 77, "databaseId": 77,
                    "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T01:00:00Z",
                    "author": { "login": "x", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": "t-cursor", "hasNextPage": true },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_X", "createdAt": "2026-01-01T00:30:00Z",
                          "actor": { "login": "x", "databaseId": 1, "__typename": "User" },
                          "label": { "name": "bug" } }
                      ]
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    private const string FollowUpTimeline = """
        {
          "data": {
            "repository": {
              "issue": {
                "updatedAt": "2026-01-01T01:00:00Z",
                "timelineItems": {
                  "pageInfo": { "endCursor": null, "hasNextPage": false },
                  "nodes": [
                    { "__typename": "ClosedEvent", "id": "CE_X", "createdAt": "2026-01-01T01:00:00Z",
                      "actor": { "login": "x", "databaseId": 1, "__typename": "User" } }
                  ]
                }
              }
            },
            "rateLimit": { "remaining": 4998, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;
```

- [ ] **Step 6: Run tests**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~Inner_pagination"
```

Expected: Test passes.

- [ ] **Step 7: Commit**

```powershell
git add src/GithubSync.Sources.GitHub/ tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "feat: inner pagination follow-up for overflowing connections (#11)"
```

---

## Task 12: Rate-limit retry — secondary + primary via headers (tests 5, 6)

**Files:**
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

The retry logic is already implemented in Task 7's `SendWithRateLimitRetryAsync`. This task verifies both branches with end-to-end tests.

- [ ] **Step 1: Write the failing tests**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Secondary_rate_limit_retry_after_header_sleeps_then_succeeds()
    {
        using var server = new WireMockGitHubServer();
        var scenario = "ratelimit-secondary";

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs(null)
            .WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Retry-After", "1"));

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EmptyPageBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var events = await CollectAsync(fetcher);
        sw.Stop();

        Assert.Empty(events);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"Expected ~1s wait, took {sw.Elapsed.TotalMilliseconds}ms");
        Assert.Equal(2, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

    [Fact]
    public async Task Primary_rate_limit_via_X_RateLimit_headers_sleeps_then_succeeds()
    {
        using var server = new WireMockGitHubServer();
        var scenario = "ratelimit-primary";
        var resetAt = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs(null)
            .WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(403)
                .WithHeader("X-RateLimit-Remaining", "0")
                .WithHeader("X-RateLimit-Reset", resetAt.ToString()));

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EmptyPageBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var events = await CollectAsync(fetcher);
        sw.Stop();

        Assert.Empty(events);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(700),
            $"Expected ~1s wait until reset, took {sw.Elapsed.TotalMilliseconds}ms");
    }
```

- [ ] **Step 2: Run tests**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~rate_limit"
```

Expected: Both tests pass.

- [ ] **Step 3: Commit**

```powershell
git add tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "test: rate-limit retries — secondary Retry-After + primary X-RateLimit headers (#11)"
```

---

## Task 13: Hard-fail error handling (tests 7, 8, 9, 10)

**Files:**
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

Transient 5xx retry, GraphQL errors body, 401 auth, and 403-without-rate-limit-signals all map to behaviour already implemented (Polly retry in Task 7; client logic in Task 7). This task verifies each path.

- [ ] **Step 1: Add the four tests**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Transient_503_retries_then_succeeds()
    {
        using var server = new WireMockGitHubServer();
        var scenario = "transient";

        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs(null).WillSetStateTo("one")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("one").WillSetStateTo("two")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("two")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EmptyPageBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var events = await CollectAsync(fetcher);

        Assert.Empty(events);
        Assert.Equal(3, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

    [Fact]
    public async Task GraphQL_errors_body_throws_GitHubGraphQLException()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(GraphQLErrorBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        await Assert.ThrowsAsync<global::GithubSync.Sources.GitHub.Exceptions.GitHubGraphQLException>(
            async () => await CollectAsync(fetcher));

        Assert.Equal(1, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

    [Fact]
    public async Task Status_401_throws_GitHubAuthException_no_retry()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(401));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        await Assert.ThrowsAsync<global::GithubSync.Sources.GitHub.Exceptions.GitHubAuthException>(
            async () => await CollectAsync(fetcher));
        Assert.Equal(1, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

    [Fact]
    public async Task Status_403_without_rate_limit_signals_throws_GitHubAuthException()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(403));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        await Assert.ThrowsAsync<global::GithubSync.Sources.GitHub.Exceptions.GitHubAuthException>(
            async () => await CollectAsync(fetcher));
    }

    private const string GraphQLErrorBody = """
        {
          "data": null,
          "errors": [
            { "message": "Field 'foo' doesn't exist on type 'Repository'", "type": "FIELD_NOT_FOUND" }
          ]
        }
        """;
```

- [ ] **Step 2: Run tests**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubIssueFetcherTests"
```

Expected: All tests pass.

- [ ] **Step 3: Commit**

```powershell
git add tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "test: hard-fail error handling (5xx exhaustion + GraphQL errors + auth) (#11)"
```

---

## Task 14: Cancellation during rate-limit sleep (test 12)

**Files:**
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

Cancellation is already wired (Task 7's `Task.Delay(wait, ct)` + Task 5's `Task.Delay(w, ct)` + the `ct.ThrowIfCancellationRequested()` in the fetcher loop). This task verifies it end-to-end.

- [ ] **Step 1: Write the test**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Cancellation_during_rate_limit_sleep_aborts_quickly()
    {
        using var server = new WireMockGitHubServer();
        // Force a 30s Retry-After so the fetcher is mid-sleep when cancellation fires.
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Retry-After", "30"));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in fetcher.FetchAsync("o", "r", null, cts.Token)) { }
        });

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Cancellation should be prompt; took {sw.Elapsed.TotalMilliseconds}ms");
    }
```

- [ ] **Step 2: Run test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~Cancellation_during"
```

Expected: Test passes.

- [ ] **Step 3: Commit**

```powershell
git add tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "test: cancellation during rate-limit sleep aborts quickly (#11)"
```

---

## Task 15: Ordering contract — non-decreasing IssueUpdatedAt (test 13)

**Files:**
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

GraphQL's `orderBy: { field: UPDATED_AT, direction: ASC }` already gives us issue-level ordering. This test pins the guarantee.

- [ ] **Step 1: Write the test**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Yielded_events_are_in_non_decreasing_IssueUpdatedAt_order()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(MultiIssueOrderedBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var events = await CollectAsync(fetcher);

        Assert.NotEmpty(events);
        for (var i = 1; i < events.Count; i++)
        {
            Assert.True(events[i].IssueUpdatedAt >= events[i - 1].IssueUpdatedAt,
                $"Order violation at index {i}: {events[i].IssueUpdatedAt:o} < {events[i - 1].IssueUpdatedAt:o}");
        }
    }

    private const string MultiIssueOrderedBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_a", "number": 1, "databaseId": 1,
                    "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                    "author": { "login": "a", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] }
                  },
                  {
                    "id": "I_b", "number": 2, "databaseId": 2,
                    "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-03T00:00:00Z",
                    "author": { "login": "b", "databaseId": 2, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;
```

- [ ] **Step 2: Run test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~non_decreasing"
```

Expected: Test passes.

- [ ] **Step 3: Commit**

```powershell
git add tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "test: yielded events maintain non-decreasing IssueUpdatedAt order (#11)"
```

---

## Task 16: Pre-flight budget wait (test 14)

**Files:**
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

The pre-flight budget check is already wired in Task 8 (`await budget.WaitIfLowAsync(ct)` before each query, `budget.Update(...)` after each response). The first response in this test reports a low budget; the second query must wait.

- [ ] **Step 1: Write the test**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Pre_flight_budget_wait_pauses_before_next_query()
    {
        using var server = new WireMockGitHubServer();
        var resetAt = DateTimeOffset.UtcNow.AddSeconds(1).ToString("o");

        // Page 1: hasNextPage=true, budget remaining=1 cost=100 -> forces pre-flight wait before page 2
        var body1 = $$"""
            {
              "data": {
                "repository": {
                  "issues": {
                    "pageInfo": { "endCursor": "next", "hasNextPage": true },
                    "nodes": []
                  }
                },
                "rateLimit": { "remaining": 1, "cost": 100, "resetAt": "{{resetAt}}", "limit": 5000 }
              }
            }
            """;

        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && !b.Contains("\"cursor\":\"next\"")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body1));

        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && b.Contains("\"cursor\":\"next\"")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EmptyPageBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var events = await CollectAsync(fetcher);
        sw.Stop();

        Assert.Empty(events);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(700),
            $"Expected pre-flight wait of ~1s, took {sw.Elapsed.TotalMilliseconds}ms");
    }
```

- [ ] **Step 2: Run test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~Pre_flight_budget"
```

Expected: Test passes.

- [ ] **Step 3: Commit**

```powershell
git add tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs
git commit -m "test: pre-flight budget wait pauses before next query (#11)"
```

---

## Task 17: Logging integration verification

**Files:**
- Modify: `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`

The fetcher already emits `LogInformation` at start and end (Task 8). This task pins the field shape with a capturing sink test.

- [ ] **Step 1: Write the test**

Append to `tests/GithubSync.Tests/Sources/GitHub/GitHubIssueFetcherTests.cs`:

```csharp
    [Fact]
    public async Task Logs_structured_start_and_end_events()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EmptyPageBody));

        var sink = new CapturingSink();
        var fetcher = FetcherTestHarness.BuildWithSink(server.BaseUrl, sink);

        await CollectAsync(fetcher);

        var started = Assert.Single(sink.Records, r => r.RenderedMessage.Contains("fetch started"));
        var completed = Assert.Single(sink.Records, r => r.RenderedMessage.Contains("fetch completed"));
        Assert.Contains("github", started.Properties["Source"]?.ToString() ?? "");
        Assert.Contains("octocat", started.Properties["Owner"]?.ToString() ?? "");
        Assert.True(completed.Properties.ContainsKey("DurationMs"));
        Assert.True(completed.Properties.ContainsKey("RateLimitRemaining"));
    }
```

Note: the `CapturingSink` class already exists in the test project from PR #56. Verify with `dotnet build` that the symbol resolves; if not, see `tests/GithubSync.Tests/CapturingSink.cs`.

- [ ] **Step 2: Extend the test harness with a Serilog sink hook**

Add to `tests/GithubSync.Tests/Sources/GitHub/FetcherTestHarness.cs`:

```csharp
    public static IGitHubIssueFetcher BuildWithSink(string baseUrl, CapturingSink sink, string token = "test-token")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = token,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger(), dispose: true));
        services.AddGitHubSource(config);
        services.AddHttpClient<GitHubGraphQLClient>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/1.0");
        });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IGitHubIssueFetcher>();
    }
```

- [ ] **Step 3: Run test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~Logs_structured"
```

Expected: Test passes.

- [ ] **Step 4: Commit**

```powershell
git add tests/GithubSync.Tests/Sources/GitHub/
git commit -m "test: verify structured start/end log records (#11)"
```

---

## Task 18: Wire `AddGitHubSource` into `Program.cs`

**Files:**
- Modify: `src/GithubSync.Api/Program.cs`

- [ ] **Step 1: Modify `Program.cs`**

Open `src/GithubSync.Api/Program.cs` and add the new wiring call. Replace the existing file contents with:

```csharp
using GithubSync.Api.Startup;
using GithubSync.Data;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

SentryWiring.Configure(builder);
LoggingWiring.Configure(builder);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AppDb")));

builder.Services.AddAppHealthChecks();
builder.Services.AddGitHubSource(builder.Configuration);

var app = builder.Build();

RequiredSecrets.Validate(
    app.Configuration,
    app.Environment,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Secrets"));

app.MapAppHealthEndpoints();

app.Run();

public partial class Program;
```

- [ ] **Step 2: Build the whole solution**

```powershell
dotnet build GithubSync.sln -c Debug
```

Expected: Build succeeds with no warnings about ambiguous references.

- [ ] **Step 3: Add an integration smoke that asserts the fetcher resolves from DI**

Create `tests/GithubSync.Tests/Sources/GitHub/GitHubSourceDIRegistrationTests.cs`:

```csharp
using GithubSync.Sources.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubSourceDIRegistrationTests
{
    [Fact]
    public void Fetcher_resolves_from_app_factory()
    {
        using var factory = new ConfiguredAppFactory();
        using var scope = factory.Services.CreateScope();

        var fetcher = scope.ServiceProvider.GetRequiredService<IGitHubIssueFetcher>();

        Assert.NotNull(fetcher);
    }
}
```

- [ ] **Step 4: Run the test**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubSourceDIRegistration"
```

Expected: Test passes.

- [ ] **Step 5: Commit**

```powershell
git add src/GithubSync.Api/Program.cs tests/GithubSync.Tests/Sources/GitHub/GitHubSourceDIRegistrationTests.cs
git commit -m "feat: register GitHub source services in Program.cs (#11)"
```

---

## Task 19: Optional integration test against real GitHub (env-gated)

**Files:**
- Create: `tests/GithubSync.Tests/Sources/GitHub/GitHubIntegrationTests.cs`

- [ ] **Step 1: Write the gated integration test**

Create `tests/GithubSync.Tests/Sources/GitHub/GitHubIntegrationTests.cs`:

```csharp
using GithubSync.Sources.GitHub;
using GithubSync.Sources.GitHub.GraphQL;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubIntegrationTests
{
    [SkippableFact]
    public async Task Hits_octocat_Hello_World_and_yields_events()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var runFlag = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS");

        Skip.If(string.IsNullOrWhiteSpace(token) || runFlag != "true",
            "Integration tests require GITHUB_TOKEN and RUN_INTEGRATION_TESTS=true.");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = token,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitHubSource(config);
        using var provider = services.BuildServiceProvider();

        var fetcher = provider.GetRequiredService<IGitHubIssueFetcher>();

        var events = new List<GitHubIssueEvent>();
        // Tight time window: just want to prove the query runs against real GitHub.
        var since = DateTimeOffset.UtcNow.AddYears(-1);
        await foreach (var e in fetcher.FetchAsync("octocat", "Hello-World", since, default))
        {
            events.Add(e);
            if (events.Count >= 5) break; // bail early; we just want to prove ingestion works
        }

        Assert.NotEmpty(events);
    }
}
```

- [ ] **Step 2: Add the Xunit.SkippableFact dependency**

In `tests/GithubSync.Tests/GithubSync.Tests.csproj`, add to the `<PackageReference>` `<ItemGroup>`:

```xml
    <PackageReference Include="Xunit.SkippableFact" Version="1.5.23" />
```

- [ ] **Step 3: Verify the test is skipped without env vars**

```powershell
dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~GitHubIntegrationTests"
```

Expected: 1 test skipped (not failed) with the message "Integration tests require GITHUB_TOKEN…".

- [ ] **Step 4: Commit**

```powershell
git add tests/GithubSync.Tests/
git commit -m "test: env-gated integration test against octocat/Hello-World (#11)"
```

---

## Task 20: Update issue #11 acceptance criteria on GitHub

**Files:** none (GitHub API call)

Per the spec, issue #11's acceptance criteria still mention ETag/304 — REST-specific and N/A under GraphQL. Update the issue body.

- [ ] **Step 1: Fetch the current issue body**

Run:
```powershell
$query = '{"query":"query { repository(owner:\"BluePhoenix91\", name:\"github-sync\") { issue(number:11) { id body } } }"}'
Invoke-RestMethod -Uri "https://api.github.com/graphql" -Method Post `
  -Headers @{ Authorization = "Bearer $env:GITHUB_TOKEN"; "Content-Type" = "application/json" } `
  -Body $query | ConvertTo-Json -Depth 10
```

Capture the existing body and the issue's node `id`.

- [ ] **Step 2: Build the updated body**

In the captured body, replace:
- *"Conditional requests reuse ETag and skip unchanged pages on 304."*
- With: *"GraphQL `rateLimit { remaining cost resetAt }` is consulted before each query; the fetcher waits until reset when the remaining budget is below the next call's projected cost."*

And replace:
- *"304"* row in the unit-tests bullet
- With: *"Inner pagination follow-up for issues with overflowing connections."*

- [ ] **Step 3: Update the issue body via the REST API**

```powershell
$body = @{ body = "<UPDATED_BODY_HERE>" } | ConvertTo-Json
Invoke-RestMethod -Uri "https://api.github.com/repos/BluePhoenix91/github-sync/issues/11" `
  -Method Patch `
  -Headers @{ Authorization = "Bearer $env:GITHUB_TOKEN"; "Accept" = "application/vnd.github+json" } `
  -Body $body
```

- [ ] **Step 4: Visually confirm on GitHub**

Open `https://github.com/BluePhoenix91/github-sync/issues/11` in a browser and confirm the new wording is in place.

No code commit — this is a GitHub-side metadata change.

---

## Task 21: Final verification, push, open PR

**Files:** none (verification + git push + PR creation)

- [ ] **Step 1: Run the full test suite**

```powershell
dotnet test GithubSync.sln -c Debug
```

Expected: All tests pass, no failures, no skips other than the env-gated integration test.

- [ ] **Step 2: Run a clean build with warnings-as-errors check**

```powershell
dotnet build GithubSync.sln -c Release
```

Expected: Build succeeds. Review the output for warnings; address any that are introduced by this work.

- [ ] **Step 3: Run `/simplify` against the branch diff**

Per CLAUDE.md repo etiquette: before pushing a PR that touches `.cs` files, run `/simplify`. Address actionable findings; surface any skipped findings in the PR description with a one-line reason each.

- [ ] **Step 4: Pause for user confirmation before pushing**

Show the user the list of commits on this branch:
```powershell
git log --oneline main..HEAD
```

Ask: "Ready to push `feat/issue-11-github-fetch-client` to origin and open a PR? (yes / no)"

Wait for explicit confirmation. Do not push automatically.

- [ ] **Step 5: Push the branch**

```powershell
git push -u origin feat/issue-11-github-fetch-client
```

- [ ] **Step 6: Move issue #11 to In review**

Use the project-board GraphQL mutation from `reference_project_board.md` (memory), targeting option `df73e18b` (In review).

```powershell
$mutation = '{"query":"mutation { updateProjectV2ItemFieldValue(input:{ projectId:\"PVT_kwHOAmEg2c4BYXa3\", itemId:\"PVTI_lAHOAmEg2c4BYXa3zgtZnJI\", fieldId:\"PVTSSF_lAHOAmEg2c4BYXa3zhTdyVM\", value:{ singleSelectOptionId:\"df73e18b\" } }) { projectV2Item { id } } }"}'
Invoke-RestMethod -Uri "https://api.github.com/graphql" -Method Post `
  -Headers @{ Authorization = "Bearer $env:GITHUB_TOKEN"; "Content-Type" = "application/json" } `
  -Body $mutation
```

- [ ] **Step 7: Open the PR**

Build the PR description from the commits and spec. Use the GitHub MCP `create_pull_request` tool (or `Invoke-RestMethod` against the REST API). Title: `feat: GitHub issues incremental fetch client (#11)`. Body should reference the spec, summarise the architecture in 3-5 bullets, and include a test plan checklist per CLAUDE.md PR conventions.

- [ ] **Step 8: Verify CI green**

Watch the Actions tab for the PR's CI run. Address any failures.

---

## Self-Review

**Spec coverage:**

- ✅ GraphQL over REST decision — `IssuesPageQuery.Outer` + DTOs (Task 6) + fetcher (Tasks 8-9).
- ✅ Bootstrap "from now" default — fetcher passes `since` through; no historical walk (Tasks 8, 9).
- ✅ New `GithubSync.Sources.GitHub` project — Task 1.
- ✅ Interface takes `(string owner, string repo)` — `IGitHubIssueFetcher` (Task 2).
- ✅ `GitHubIssueEvent` + `GitHubActor` + `GitHubActorKind` + `GitHubEventKind` — Task 2.
- ✅ Source-side vocabulary, no canonical leakage — no `GithubSync.Data` reference in new project (Task 1).
- ✅ Outer + inner pagination — Tasks 8, 10 (outer); Task 11 (inner follow-up).
- ✅ Issue-grouped non-decreasing `updatedAt` ordering — Task 15.
- ✅ Three rate-limit signals (pre-flight + Retry-After + X-RateLimit-*) — Task 5 (budget), Task 7 (HTTP-level), Tasks 12, 16 (tests).
- ✅ Three typed exceptions — Task 3.
- ✅ Cancellation through HttpClient + `Task.Delay(...,ct)` + IAsyncEnumerable — Task 8 (impl), Task 14 (test).
- ✅ Authentication via `GITHUB_TOKEN` config key — Task 7's `AddGitHubSource`.
- ✅ Polly transient retry — Task 7.
- ✅ Logging shape — Task 8 (impl), Task 17 (test).
- ✅ 14 unit tests + 1 env-gated integration test — Tasks 8-17, 19.
- ✅ Issue #11 acceptance criteria update — Task 20.

**Placeholder scan:** No "TBD", "TODO", "implement later", or vague "handle edge cases" instructions remain. The fetcher stub in Task 7 step 3 is explicitly noted as a stub-then-replace pattern with the replacement code provided in Task 8.

**Type consistency:**
- `GitHubEventKind` values match between the enum (Task 2) and the `MapTimelineKind` switch (Task 9).
- `GitHubActor.DatabaseId` matches the field name in `ActorDto.DatabaseId` mapping (Task 9 `ToActor`).
- Token config key — `GitHubSourceServiceCollectionExtensions.TokenConfigKey = "GITHUB_TOKEN"` matches the existing `RequiredSecrets.cs` convention (read in Task 7).
- `IssueNode.UserContentEdits` / `Comments` / `TimelineItems` connection types match between DTOs (Task 6) and consumer code (Tasks 9, 11).
- WireMock scenarios use `WhenStateIs(null)` initially, which matches WireMock.Net 1.6.x semantics for "no state set yet".

No issues found.
