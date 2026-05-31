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

The mapper (#12) already throws `InvalidOperationException` when it sees a non-`IssueEdited` event with a null `SourceEventId`. The persister does not catch this exception. The current issue's transaction rolls back and the run halts; the orchestrator/host's existing Sentry wiring surfaces the uncaught exception — the persister does not call into Sentry itself.

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

**Field definitions:**

- `IssuesCommitted` — count of issues whose transaction reached `COMMIT`. An issue counts here even if all of its events were deduped or unknown-kind, because the transaction (which always advances the cursor) still committed.
- `EventsAttempted` — count of mapped `CanonicalEvent` rows that were sent into an `INSERT` batch. Does **not** include unknown-kind source events (those never become canonical events).
- `EventsInserted` — count of those `EventsAttempted` rows that the database actually wrote (the rest were absorbed by `ON CONFLICT DO NOTHING`).
- `EventsSkippedUnknownKind` — count of source events the mapper returned `null` for.
- `FinalCursor` — the cursor's `LastEventTime` after the last successful commit, or `null` if no issue committed during this call (including the case where the stream was empty).

The orchestrator (#14/#15) calls this with a config ID and the fetcher's stream. The persister owns per-issue grouping, mapping (via injected `ICanonicalEventMapper`), actor/mapping persistence (the resolver's existing side-effects), raw-SQL event insert, and cursor update.

Dependencies (constructor): `AppDbContext`, `ICanonicalEventMapper`, `ILogger<IssueEventPersister>`. No new third-party packages. (No `TimeProvider` — `CanonicalEvent.IngestedAt` is stamped inside the mapper, and the cursor advances to the source-provided `IssueUpdatedAt`. The persister does not stamp time of its own.) Registered as `services.AddScoped<IIssueEventPersister, IssueEventPersister>()` in `IngestionServiceCollectionExtensions.AddIngestion`.

### Stream contract

The persister groups by walking the stream and emitting a commit each time `SourceEntityId` changes. That correctness rests on two contracts the fetcher (#11) provides today:

1. **Events for one issue are contiguous in the stream.** The fetcher buffers an issue's events from initial page + follow-up connections, sorts them by `(EventTime, SourceEventId)`, and yields them as a contiguous block before moving on.
2. **Issues are yielded in non-decreasing `IssueUpdatedAt` order** (GraphQL `orderBy: UPDATED_AT ASC`).

If either contract breaks, the cursor can advance past an issue that later reappears with a lower watermark, and the next run would skip it via the `since` filter. The persister does not defend against this — it trusts the fetcher's ordering. The implementation may add a `Debug.Assert` for contract (2) (the monotonic check is cheap) but does not error in release builds.

### Per-issue commit cycle

For each contiguous group of events sharing the same `SourceEntityId`:

1. **Begin transaction.** `await using var tx = await db.Database.BeginTransactionAsync(ct);`. The `AppDbContext`'s connection is now open and enlisted in this transaction.
2. **Map each source event** by calling `ICanonicalEventMapper.MapAsync` in order. The mapper accumulates new `CanonicalActor` and `IdentityMapping` rows in EF's `ChangeTracker`. Unknown-kind events (mapper returns `null`) are dropped and counted in `EventsSkippedUnknownKind`. Malformed events (mapper throws) propagate, aborting the transaction.
3. **Flush actor and mapping rows.** `await db.SaveChangesAsync(ct);` Must precede the event insert because `CanonicalEvent.ActorId` is a foreign key.
4. **Batched raw-SQL insert** of the issue's canonical events:
   ```sql
   INSERT INTO "CanonicalEvents" (
       "Id", "SyncConfigurationId", "Source", "SourceEntityType",
       "SourceEntityId", "SourceEventId", "EventKind", "EventTime",
       "ActorId", "PayloadJson", "IngestedAt")
   VALUES (@p0_0, @p0_1, ...), (@p1_0, @p1_1, ...), ...
   ON CONFLICT ON CONSTRAINT "<canonical-events-unique-index-name>" DO NOTHING
   ```
   Notes:
   - The command must run on the `AppDbContext`'s enlisted connection and transaction so it rolls back atomically with the EF changes from step 3. Obtain the connection via `db.Database.GetDbConnection()` (already open from step 1) and the transaction via `db.Database.CurrentTransaction.GetDbTransaction()`; assign both on the `NpgsqlCommand`. **Do not open a new connection** — that would run outside the enlisted transaction and leave committed event rows even if the outer rollback fires.
   - The `ON CONFLICT` target names the composite unique index from `CanonicalEventConfiguration` explicitly. The exact constraint name is taken from the migration; the implementation plan resolves the literal string before code lands.
   - `Source`, `SourceEntityType`, and `EventKind` are stored as integers (EF's default for enums); parameter types must match.
   - Returns the number of inserted rows, recorded as `EventsInserted`. `EventsAttempted - EventsInserted` is the silent dedup count (no per-row log).
5. **Upsert `SyncCursor`** in a single round-trip:
   ```sql
   INSERT INTO "SyncCursors" ("Id", "SyncConfigurationId", "LastEventTime")
   VALUES (@id, @configId, @issueUpdatedAt)
   ON CONFLICT ("SyncConfigurationId") DO UPDATE SET
     "LastEventTime" = GREATEST(
       EXCLUDED."LastEventTime",
       COALESCE("SyncCursors"."LastEventTime", EXCLUDED."LastEventTime"))
   ```
   The `COALESCE` is defensive against the case where the orchestrator pre-created a cursor row with `LastEventTime = NULL`. PostgreSQL's `GREATEST` actually ignores NULL arguments (it deviates from the SQL standard), so the bare `GREATEST(EXCLUDED., "SyncCursors".)` would also work — but the explicit COALESCE makes the intent readable without requiring the reader to know that quirk. Run-level fields are left alone (the upsert touches `LastEventTime` only).
6. **Commit.** `await tx.CommitAsync(ct);`

`CancellationToken` requested mid-transaction triggers `await using` disposal and an implicit rollback. The in-flight issue's events do not commit; the cursor stays at the last completed issue.

**Cursor advance for "empty" issues:** when an issue yields zero `EventsAttempted` (all source events were unknown-kind, mapper returned null for every one), step 5 still runs and the cursor still advances to that issue's `IssueUpdatedAt`. Not advancing would mean re-fetching the same skip-and-log noise on every subsequent run forever. The cost — the cursor "leaks" past an issue we stored nothing for — is acceptable because the issue produced no canonical events worth replaying.

One `LogInformation` per committed issue with `{ConfigId, SourceEntityId, EventsAttempted, EventsInserted, CursorAdvancedTo}`. Aggregate counts roll up via `PersistResult` for the orchestrator to surface. (Per-issue logging volume is acceptable for v1; if a busy repo makes this too noisy, the orchestrator can downgrade to aggregate-only later — that's an observability tuning question, not a persister bug.)

### Error categories

| Class | Source | Response |
|---|---|---|
| Expected per-row dedup | Normal overlap of fetch windows; replay after crash | Absorbed by `ON CONFLICT DO NOTHING` at the database — no exception ever reaches application code; counted in `EventsAttempted - EventsInserted` |
| Unknown `GitHubEventKind` | Future enum value, malformed source | Mapper returns null and logs; persister increments `EventsSkippedUnknownKind` and continues |
| Mapper throws (`InvalidOperationException`: null `SourceEventId` on non-edit) | Bug-shaped event | Persister does not catch; transaction rolls back; run halts |
| Actor resolver throws (`InvalidOperationException`: configured `TargetUserId` not in `TargetUsers`) | Misconfiguration | Persister does not catch; existing #12 behaviour; halts the run before any data lands |
| `DbUpdateException` / `PostgresException` other than `23505` | Connectivity, schema mismatch, FK violation, etc. | Persister does not catch; CLAUDE.md's systemic-failure carve-out applies |
| `23505` on any index (`IdentityMappings`, `CanonicalActors`) | Race between concurrent persisters on the same config — out-of-scope per Concurrency Assumption below | Persister does not catch; halts the run |

#### Concurrency assumption

The persister assumes a single instance runs per `SyncConfiguration` at any time. The orchestrator (#14/#15) must serialise per-config runs, e.g. via Hangfire's `DisableConcurrentExecution`. Multiple concurrent persisters on the same config are undefined behaviour for v1.

Consequences worth pinning:

- **`CanonicalActor` idempotency in v1 is EF read-path only**, not DB-level upsert. `ActorResolver` does `FirstOrDefaultAsync → Add`, which is correct under single-writer concurrency. Idempotency.md's "DB enforces uniqueness" principle is still satisfied (the unique index exists and would catch a violation), but the application code does not use `INSERT ... ON CONFLICT DO UPDATE` for actors. A future v2 multi-writer story will need a real DB-level upsert here.
- **`IdentityMapping` is also read-path only** for the same reason. The existing "insert-once; existing wins" semantics live in `ActorResolver.EnsureIdentityMappingAsync` and depend on the same single-writer assumption.
- A concurrent second persister hitting the actor or identity-mapping unique indexes would surface as `23505` and halt the run (last row in the table above). Acceptable for v1; documented so v2 has the right starting point.

## Test plan

### New project: `tests/GithubSync.Data.Tests`

The fetcher design (#11) added all its tests to the existing `GithubSync.Tests` project. This issue introduces a *separate* project because the test surface here is qualitatively different:

- Real PostgreSQL is required (the unique-index, raw-SQL, and `NULLS NOT DISTINCT` behaviours can't be exercised under InMemory).
- Postgres-specific test dependencies (admin connection for DB create/drop, raw-SQL assertions) don't belong in the API test project.
- Idempotency.md's tests 1–6 also belong in this project once implemented (see "Idempotency.md test-plan ownership" below). Having a dedicated home for them keeps the project structure aligned with the docs.

References: `GithubSync.Data`, `GithubSync.Api` (for the persister and its DI extensions), `GithubSync.Sources.GitHub` (for `GitHubIssueEvent`). xUnit, FluentAssertions (already used elsewhere in the repo), Npgsql.

### Fixture: `PostgresTestFixture` (`IAsyncLifetime`)

- Resolves the connection string from env var `GITHUBSYNC_TEST_POSTGRES`, falling back to User Secrets (`ConnectionStrings:TestPostgres` on the `GithubSync.Data.Tests` project).
- If neither is set, integration tests are skipped at fixture initialisation time. The skip message names the env var and the `dotnet user-secrets set "ConnectionStrings:TestPostgres" "..."` command form so the reader can copy-paste a fix. CLAUDE.md grows a short "Tests against Postgres" subsection that documents the same two paths — the skip message points at it. The exact skip mechanism (xUnit's `Skip`, `Xunit.SkippableFact`, or constructor-level `throw new SkipException`) is an implementation-plan detail.
- On `InitializeAsync`: connect to the configured instance, create database `githubsync_test_{guid:N}`, build an `AppDbContext` against the new database, apply migrations.
- Exposes a factory for fresh `AppDbContext` instances and the connection string.
- On `DisposeAsync`: drop the test database.

Used as `IAsyncLifetime` per test class. May be promoted to a collection fixture if startup cost dominates once the suite grows; #13's ten tests do not justify that yet.

### Tests

Acceptance-criterion mappings noted per case. All tests use a static, hand-built `IAsyncEnumerable<GitHubIssueEvent>` (no fetcher in the test path) so the persister is tested in isolation.

1. **Repeat-window: zero duplicates.** Persist N events across 3 issues. Call `PersistAsync` a second time with an equivalent stream. Assert `CanonicalEvents` row count is N. Maps to "re-ingesting the same window twice produces zero duplicate rows" *and* covers idempotency.md test 1.

2. **Cursor advances only after each issue commits.** Three `PersistAsync` calls with growing streams: first call yields issue 1, second yields issues 1 and 2 (with a fresh `PersistResult` showing issue 1 deduped, issue 2 committed), third yields all three. After each call, assert `SyncCursor.LastEventTime == max(IssueUpdatedAt of all committed issues so far)`. Three calls — not one — because a single `PersistAsync` exposes no per-issue hook to assert against without adding an internal test seam, and three calls also exercise the dedup-on-re-seen-issue path. Maps to "cursor advances only after events for that window are durably committed".

3. **Crash-safety: cancellation mid-issue-2, then resume.** The stream is a wrapper `IAsyncEnumerable` that triggers a `CancellationTokenSource.Cancel()` after yielding the first event of issue 2. Assert: persister throws `OperationCanceledException`, issue 1's events committed, none of issue 2's events present, cursor at issue 1's `IssueUpdatedAt`. Then call `PersistAsync` again with a fresh, complete stream (issues 1 through 3) and assert the end-state matches a clean run — issue 1's events deduped, issues 2 and 3 inserted, cursor at issue 3's `IssueUpdatedAt`. **Important framing:** the persister does not read the cursor — only the fetcher/orchestrator does. "Resume from cursor" here means "feed the same stream again and rely on `ON CONFLICT DO NOTHING` for issue 1". This test does **not** prove that the fetcher's `since`-filter resume works; that's an end-to-end test for #14/#15. Maps to "kill the process mid-batch, restart, confirm no missing or duplicate events".

4. **Malformed event halts the run.** Inject a `GitHubEventKind.Closed` event (a mapped, non-edit kind) with `SourceEventId = null`. Assert the persister throws `InvalidOperationException`, no events from that issue land (transaction rolled back), and the cursor stays at the previous issue's value. Closed is used specifically because it routes through `TryMapKind` to a non-`IssueEdited` canonical kind — using an unmapped enum value would instead hit the unknown-kind path (test 6) and not exercise the mapper's invariant check. Maps to idempotency.md's "rejected at ingest rather than silently persisted" invariant.

5. **First-run cursor creation.** No `SyncCursor` row for the config. Persist one issue. Assert a `SyncCursor` row now exists with `LastEventTime = IssueUpdatedAt` and all run-level fields null.

6. **Unknown-kind events skipped, not failed.** Inject a synthetic `GitHubIssueEvent` with `Kind = (GitHubEventKind)999` *alongside* a normal event for the same issue. Assert it does not reach the database, `EventsSkippedUnknownKind` is incremented in `PersistResult`, the run continues, the issue's normal event commits, and the cursor advances to that issue's `IssueUpdatedAt`.

7. **Pre-created cursor row with null `LastEventTime` is correctly advanced.** Insert a `SyncCursor` row with `LastEventTime = null` before calling `PersistAsync`. Persist one issue. Assert `LastEventTime` is now that issue's `IssueUpdatedAt` (not null). Guards the `COALESCE` in the upsert SQL.

8. **Overlapping-window tail re-ingest.** Persist events at times `[T1..T10]` across multiple issues. Persist a second stream covering `[T5..T15]`. Assert events at `[T5..T10]` exist exactly once, total row count is N5..15. Covers idempotency.md test 2.

9. **Null `SourceEventId` dedup (NULLS NOT DISTINCT).** Persist two `IssueEdited` events with identical `(SourceEntityId, EventKind, EventTime)` and both `SourceEventId = null`. Assert exactly one row. Covers idempotency.md test 3 and proves the `NULLS NOT DISTINCT` index decision behaves as documented.

10. **Concurrent insert of the same event.** Run two `PersistAsync` calls in parallel, each on its own `AppDbContext`, both inserting the same canonical event. Assert exactly one row commits and neither caller sees an exception (the `ON CONFLICT DO NOTHING` absorbs the conflict cleanly on the losing side). Covers idempotency.md test 6.

Cancellation-token plumbing is asserted at compile-time (every async signature accepts one) and exercised functionally by test 3.

### Unit tests in `tests/GithubSync.Tests/Sync/Ingestion`

A small companion `IssueEventPersisterTests` file for branching logic that doesn't require unique-constraint enforcement: cursor watermark `max` rule, `PersistResult` field arithmetic, first-run cursor creation, malformed-event guard. EF Core InMemory is acceptable per the project memory note ("InMemory OK for unit branching tests").

**InMemory's reach is limited and the integration tests are load-bearing for:**

- The `NULLS NOT DISTINCT` index decision (integration test 9).
- The raw-SQL `ON CONFLICT DO NOTHING` dedup behaviour (integration tests 1, 8, 10) — InMemory does not enforce unique indexes and would silently let duplicates through.
- The EF-transaction-plus-raw-SQL enlistment correctness (no provider-level equivalent to assert against).

Treat InMemory unit tests as cheap pre-flight checks on branching logic; the Postgres suite is the contract.

The two suites overlap on test 5 (first-run cursor creation) deliberately — once at the EF-tracker level (cheap, fast), once at the real-Postgres level (proves the cursor row materialises through migrations and the upsert SQL runs against the real schema).

## Idempotency.md test-plan ownership

Idempotency.md enumerates six tests for `tests/GithubSync.Data.Tests/`. Mapping them against this spec's plan:

| idempotency.md test | Lives in | Note |
|---|---|---|
| 1. Repeat-window: zero duplicates | #13 — test 1 | Direct match. |
| 2. Overlapping window tail re-ingest | #13 — test 8 | Folded into #13's plan because it tests the same `ON CONFLICT DO NOTHING` path on the same code. |
| 3. Null `SourceEventId` deduplication | #13 — test 9 | Folded in because it directly validates the unique-index decision the persister depends on. |
| 4. `CanonicalActor` upsert preserves identity | **Out of scope for #13** | Covered by existing `ActorResolverTests` in `tests/GithubSync.Tests/Sync/Ingestion/`. The behaviour belongs to #12 (the resolver), not #13 (the persister). If the existing tests don't cover the FirstSeenAt-preserved / LastSeenAt-advanced / id-unchanged trio against real Postgres, that's a follow-up bug on #12, not new work for #13. |
| 5. `WorkItemMapping` insert-once hard fail | **Deferred to #14/#15** | `WorkItemMapping` is written by the exporter. The persister never touches it. |
| 6. Concurrent insert of same canonical event | #13 — test 10 | Folded in because it exercises the persister's batched `ON CONFLICT DO NOTHING` under contention. |

Tests 2, 3, 6 from idempotency.md are now covered by #13 explicitly (tests 8, 9, 10 in this spec). Test 4 stays with #12. Test 5 belongs to the exporter.

## Issue body updates required

When #13 is closed, the issue body needs three small adjustments so PR reviewers don't bounce on apparent contradictions:

1. **Add #11 to "Depends on".** The persister consumes the fetcher's stream and trusts its ordering contract.
2. **Note the raw-SQL carve-out from "EF Core over raw SQL".** The implementation uses raw SQL for the batched `ON CONFLICT DO NOTHING` insert per the decision locked in idempotency.md; CLAUDE.md's expressiveness exception applies.
3. **Note the "halt-on-malformed" carve-out from AC #5.** A non-`IssueEdited` event with a null `SourceEventId` is treated as a systemic failure (structural-invariant violation, bug-shaped) and halts the run, not skip-and-log. This honours the mapper's existing throw (#12) and idempotency.md's "fail loud" instruction. CLAUDE.md's "systemic failures throw" rule is the explicit authority.

These edits land on the issue body as part of the implementation-PR description so the reviewer sees the same context the spec carries.

## Scope explicitly deferred to #14/#15

- Run-level `SyncCursor` fields (`LastRunStartedAt`, `LastRunCompletedAt`, `LastRunStatus`, `LastRunMessage`) — orchestrator wraps the persister call and records run-level state around it.
- Serialising concurrent runs per config — orchestrator's responsibility (e.g. Hangfire `DisableConcurrentExecution`).
- Per-run metrics aggregation, Sentry breadcrumbs, dashboard counters — orchestrator decides what to do with the returned `PersistResult`.
- Backoff / retry of transient DB errors — out of scope; persister halts on systemic failures and the orchestrator handles retry scheduling at the run level.
- Reading run direction (incremental vs. backfill) — irrelevant to the persister; the orchestrator's `since` argument to the fetcher determines the stream.
- End-to-end test combining fetcher + persister with cursor-as-`since`-filter — belongs to the orchestrator's test scope.

## What this unblocks

- **#14 / #15** — the orchestrator wires fetcher → persister, owns per-run metrics, serialises per-config runs, surfaces errors. With this issue complete, the v1 ingestion pipeline is end-to-end functional from "GitHub API" to "row in `CanonicalEvents` + cursor advanced".
- Closing epic **#3** (GitHub ingestion MVP) once #14/#15 land.
