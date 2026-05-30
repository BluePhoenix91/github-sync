# Persist imported events idempotently and advance cursor safely — issue #13

Part of the GitHub ingestion MVP epic ([#3](https://github.com/BluePhoenix91/github-sync/issues/3)).

## Goal

Introduce the missing last step of the v1 ingestion pipeline: a component that consumes the fetcher's stream of `GitHubIssueEvent`s, runs them through the existing mapper, persists the resulting `CanonicalEvent` rows to PostgreSQL idempotently, and advances `SyncCursor.LastEventTime` only after each batch is durably committed. A crash mid-run must never lose events or leave the cursor past unwritten data.

Builds directly on the contracts laid down in [#8](https://github.com/BluePhoenix91/github-sync/issues/8) ([idempotency.md](../../idempotency.md)), [#9](https://github.com/BluePhoenix91/github-sync/issues/9), [#11](https://github.com/BluePhoenix91/github-sync/issues/11), and [#12](https://github.com/BluePhoenix91/github-sync/issues/12).

## Decision — commit boundary is the issue

The fetcher already yields events grouped by issue, in non-decreasing `EventTime` order within each issue, and stamps every event with the source issue's `updatedAt` on `GitHubIssueEvent.IssueUpdatedAt`. That hint was placed there in #11 for exactly this consumer. The persister honours it: one PostgreSQL transaction per issue, cursor advance per committed issue.

Alternatives considered:

- **Per-event transaction.** Maximally fine-grained but defeats the batched-insert benefit decided in [idempotency.md option 1](../../idempotency.md#implementation-note-where-the-on-conflict-do-nothing-lives), and forces the cursor to track per-event time — which breaks the `since` semantics of GitHub's filter (see "Cursor semantics" below).
- **Multi-issue batched transaction.** Larger commits at the cost of cursor-watermark logic that has to track "which issues are fully done within this batch". More state, more bugs, and bigger rollback windows on crash.

Per-issue won on: matches the existing watermark hint, keeps the cursor rule one line (`max(current, issue.IssueUpdatedAt)`), gives crash-safety an integration-testable shape, and keeps transactions small.

## Decision — cursor stores `issue.updatedAt`, not event time

GitHub's `since` filter operates on `issue.updatedAt`, not per-event timestamps. If the cursor tracked the max event time, a subsequent edit on the same issue at a later `updatedAt` but with an event time before the cursor would be filtered out by GitHub at the next run — silently lost.

Storing `issue.updatedAt` and re-fetching the latest committed issue on resume guarantees no gap. The unique index absorbs the re-fetched events at the row level. Steady-state incremental syncs replay 0–1 issues.

This is forced by an external constraint, not a design choice. Documented here for the implementation to reference.

## Decision — malformed events halt the run

The mapper (#12) already throws `InvalidOperationException` when it sees a non-`IssueEdited` event with a null `SourceEventId`. The persister does not catch this exception. The current issue's transaction rolls back, the run halts, the operator is alerted via Sentry.

Idempotency.md called this "fail loud". Two readings were possible — skip-and-log per CLAUDE.md's "non-blocking record failures" rule, or halt per CLAUDE.md's "systemic failures" carve-out. The structural-invariant violation belongs in the systemic family: it indicates either a GitHub API change or a mapper bug, both of which warrant operator attention, not silent skipping. Test 4 below pins this contract.

The cost is explicit: one weirdly-shaped event from a future GitHub API change would halt the nightly sync until a code fix ships. Preferred over quiet data drift.

## Decision — persister creates the cursor row on first commit

A `SyncConfiguration` with no `SyncCursor` row is "a config that has never run" per [data-model.md](../../data-model.md). The persister handles that case itself: on its first successful commit for the config it inserts a new `SyncCursor` row with `LastEventTime = issue.IssueUpdatedAt` and all run-level fields null. The orchestrator may pre-create cursors if it wants, but the persister does not require it.

Keeps the persister's contract simple ("hand me a config, I'll persist its events") and matches the implicit lifecycle in the data model.

## Decision — test infrastructure: env-var PostgreSQL, no Docker

Integration tests run against a real PostgreSQL, configured by connection string. The test fixture creates a uniquely named database per fixture, runs migrations, drops it on dispose.

Connection string source order:

1. Env var `GITHUBSYNC_TEST_POSTGRES`
2. .NET User Secrets (key `ConnectionStrings:TestPostgres`) for local dev
3. If neither is set, integration tests skip with a clear message

Docker / Testcontainers were considered and rejected for this project specifically:

- The Lightsail Windows Server runner doesn't expose nested virtualisation (`systeminfo` confirmed "A hypervisor has been detected. Features required for Hyper-V will not be displayed.") so no Linux container runtime can run on it.
- The runner already has a colocated PostgreSQL for the application; the integration tests reuse the same instance with a dedicated test database name pattern.
- Local dev uses a developer's local PostgreSQL the same way. Same code path everywhere — no environment-conditional fixture.

Rejected dual-path (Testcontainers locally, env-var on CI) because environment symmetry was preferred over local convenience.

## Architecture

### New class: `IssueEventPersister`

Lives in `src/GithubSync.Api/Sync/Ingestion/` alongside the existing `CanonicalEventMapper` and `ActorResolver`. Scoped lifetime (one persister per sync run, sharing the resolver's per-run actor cache via the same `AppDbContext` scope).

#### Interface

```csharp
namespace GithubSync.Api.Sync.Ingestion;

public interface IIssueEventPersister
{
    Task<PersistResult> PersistAsync(
        Guid syncConfigurationId,
        IAsyncEnumerable<GitHubIssueEvent> source,
        CancellationToken ct);
}

public sealed record PersistResult(
    int IssuesCommitted,
    int EventsAttempted,
    int EventsInserted,
    int EventsSkippedUnknownKind,
    DateTimeOffset? FinalCursor);
```

The orchestrator (#14/#15) calls this with a config ID and the fetcher's stream. The persister owns per-issue grouping, mapping (via injected `ICanonicalEventMapper`), actor/mapping persistence (the resolver's existing side-effects), raw-SQL event insert, and cursor update.

Dependencies (constructor): `AppDbContext`, `ICanonicalEventMapper`, `ILogger<IssueEventPersister>`, `TimeProvider`. No new third-party packages. Registered as `services.AddScoped<IIssueEventPersister, IssueEventPersister>()` in `IngestionServiceCollectionExtensions.AddIngestion`.

### Per-issue commit cycle

For each contiguous group of events sharing the same `SourceEntityId`:

1. **Begin transaction.** `await using var tx = await db.Database.BeginTransactionAsync(ct);`
2. **Map each source event** by calling `ICanonicalEventMapper.MapAsync` in order. The mapper accumulates new `CanonicalActor` and `IdentityMapping` rows in EF's `ChangeTracker`. Unknown-kind events (mapper returns `null`) are dropped and counted in `EventsSkippedUnknownKind`. Malformed events (mapper throws) propagate, aborting the transaction.
3. **Flush actor and mapping rows.** `await db.SaveChangesAsync(ct);` Must precede the event insert because `CanonicalEvent.ActorId` is a foreign key.
4. **Batched raw-SQL insert** of the issue's canonical events:
   ```sql
   INSERT INTO "CanonicalEvents" ("Id", "SyncConfigurationId", ...)
   VALUES (@p0_0, @p0_1, ...), (@p1_0, @p1_1, ...), ...
   ON CONFLICT DO NOTHING
   ```
   Built with parameterised `NpgsqlCommand` parameters, executed via `db.Database.GetDbConnection()`. Returns the number of inserted rows, recorded as `EventsInserted`. `EventsAttempted - EventsInserted` is the silent dedup count (no per-row log).
5. **Upsert `SyncCursor`** in a single round-trip via `INSERT ... ON CONFLICT ("SyncConfigurationId") DO UPDATE SET "LastEventTime" = GREATEST(EXCLUDED."LastEventTime", "SyncCursors"."LastEventTime")`. Run-level fields are left alone (the upsert touches `LastEventTime` only).
6. **Commit.** `await tx.CommitAsync(ct);`

`CancellationToken` requested mid-transaction triggers `await using` disposal and an implicit rollback. The in-flight issue's events do not commit; the cursor stays at the last completed issue.

One `LogInformation` per committed issue with `{ConfigId, SourceEntityId, EventsAttempted, EventsInserted, CursorAdvancedTo}`. Aggregate counts roll up via `PersistResult` for the orchestrator to surface.

### Error categories

| Class | Source | Response |
|---|---|---|
| Expected per-row dedup (`23505` on `CanonicalEvents` unique index) | Normal overlap of fetch windows; replay after crash | Silent at the database level via `ON CONFLICT DO NOTHING`; counted in `EventsAttempted - EventsInserted` |
| Unknown `GitHubEventKind` | Future enum value, malformed source | Mapper returns null and logs; persister increments `EventsSkippedUnknownKind` and continues |
| Mapper throws (`InvalidOperationException`: null `SourceEventId` on non-edit) | Bug-shaped event | Persister does not catch; transaction rolls back; run halts |
| Actor resolver throws (`InvalidOperationException`: configured `TargetUserId` not in `TargetUsers`) | Misconfiguration | Persister does not catch; existing #12 behaviour; halts the run before any data lands |
| `DbUpdateException` / `PostgresException` other than `23505` | Connectivity, schema mismatch, FK violation, etc. | Persister does not catch; CLAUDE.md's systemic-failure carve-out applies |
| `23505` on indexes other than `CanonicalEvents` (e.g. `IdentityMappings`) | Race between concurrent persisters on the same config — out-of-scope per Concurrency Assumption below | Persister does not catch; halts the run |

#### Concurrency assumption

The persister assumes a single instance runs per `SyncConfiguration` at any time. The orchestrator (#14/#15) must serialise per-config runs, e.g. via Hangfire's `DisableConcurrentExecution`. Multiple concurrent persisters on the same config are undefined behaviour for v1.

## Test plan

### New project: `tests/GithubSync.Data.Tests`

Reason: integration tests pull Postgres-specific test dependencies (npgsql connection management, raw-SQL assertions) that don't belong in the existing `GithubSync.Tests` API test project, and idempotency.md test cases 4–6 also belong here once implemented.

References: `GithubSync.Data`, `GithubSync.Api` (for the persister and its DI extensions), `GithubSync.Sources.GitHub` (for `GitHubIssueEvent`). xUnit, FluentAssertions (already used elsewhere in the repo), Npgsql.

### Fixture: `PostgresTestFixture` (`IAsyncLifetime`)

- Resolves the connection string from env var `GITHUBSYNC_TEST_POSTGRES`, falling back to User Secrets (`ConnectionStrings:TestPostgres`).
- If neither is set, integration tests are skipped at fixture initialisation time with a message pointing at the User Secrets setup command in CLAUDE.md. The exact skip mechanism (xUnit's `Skip`, `Xunit.SkippableFact`, or constructor-level `throw new SkipException`) is an implementation-plan detail.
- On `InitializeAsync`: connect to the configured instance, create database `githubsync_test_{guid:N}`, build an `AppDbContext` against the new database, apply migrations.
- Exposes a factory for fresh `AppDbContext` instances and the connection string.
- On `DisposeAsync`: drop the test database.

Used as `IAsyncLifetime` per test class. May be promoted to a collection fixture if startup cost dominates once we have more tests; #13's six tests do not justify that yet.

### Tests

Acceptance-criterion mappings noted per case.

1. **Repeat-window: zero duplicates.** Persist N events across 3 issues. Re-run with the same input stream. Assert `CanonicalEvents` row count is N. Maps to "re-ingesting the same window twice produces zero duplicate rows".
2. **Cursor advances only after commit.** Persist 3 issues' events in sequence. After each issue, assert `SyncCursor.LastEventTime == max(IssueUpdatedAt seen so far)`. Confirms per-issue cursor advance.
3. **Crash-safety: cancellation mid-second-issue.** Persist 3 issues, cancel the token mid-issue 2. Assert: issue 1 committed; issue 2's events absent; cursor at issue 1's `IssueUpdatedAt`. Re-run the full stream from that cursor → assert end-state matches a clean run (issues 1+2+3 all present exactly once, cursor at issue 3's `IssueUpdatedAt`). Maps to "kill the process mid-batch, restart, confirm no missing or duplicate events".
4. **Malformed event halts the run.** Inject a non-edit event with `SourceEventId = null`. Assert the persister throws, no events from that issue land, cursor unchanged. Maps to idempotency.md's "rejected at ingest rather than silently persisted" invariant.
5. **First-run cursor creation.** No `SyncCursor` row for the config. Persist one issue. Assert a `SyncCursor` row now exists with `LastEventTime = IssueUpdatedAt` and all run-level fields null.
6. **Unknown-kind events skipped, not failed.** Inject a synthetic `GitHubIssueEvent` with `Kind = (GitHubEventKind)999`. Assert it does not reach the database, `EventsSkippedUnknownKind` is incremented in `PersistResult`, the run continues, the issue's other events commit.

Cancellation-token plumbing is asserted at compile-time (every async signature accepts one) and exercised functionally by test 3.

### Unit tests in `tests/GithubSync.Tests/Sync/Ingestion`

A small companion `IssueEventPersisterTests` file for branching logic that doesn't require unique-constraint enforcement: cursor watermark `max` rule, `PersistResult` field arithmetic, first-run cursor creation. EF Core InMemory is acceptable per the project memory note ("InMemory OK for unit branching tests").

The two suites overlap on test 5 deliberately — once at the EF-tracker level (cheap, fast), once at the real-Postgres level (proves the unique index does its job and the cursor row materialises through migrations).

## Scope explicitly deferred to #14/#15

- Run-level `SyncCursor` fields (`LastRunStartedAt`, `LastRunCompletedAt`, `LastRunStatus`, `LastRunMessage`) — orchestrator wraps the persister call and records run-level state around it.
- Serialising concurrent runs per config — orchestrator's responsibility (e.g. Hangfire `DisableConcurrentExecution`).
- Per-run metrics aggregation, Sentry breadcrumbs, dashboard counters — orchestrator decides what to do with the returned `PersistResult`.
- Backoff / retry of transient DB errors — out of scope; persister halts on systemic failures and the orchestrator handles retry scheduling at the run level.
- Reading run direction (incremental vs. backfill) — irrelevant to the persister; the orchestrator's `since` argument to the fetcher determines the stream.

## What this unblocks

- **#14 / #15** — the orchestrator wires fetcher → persister, owns per-run metrics, serialises per-config runs, surfaces errors. With this issue complete, the v1 ingestion pipeline is end-to-end functional from "GitHub API" to "row in `CanonicalEvents` + cursor advanced".
- Closing epic **#3** (GitHub ingestion MVP) once #14/#15 land.
