# Idempotency keys and DB constraints (v1)

Re-runs of ingestion must not create duplicate rows. This document defines, per canonical entity, the natural idempotency key, the unique-index strategy in EF Core, and the conflict-handling decision. Constraints are enforced at the **database** level, not by application-side pre-checks — application code may add fast-path checks for ergonomics, but the database is the safety net.

See [`data-model.md`](./data-model.md) for entity definitions. Keys here build directly on the uniqueness notes recorded there.

## Principles

1. **DB enforces uniqueness.** Every idempotency claim is backed by a PostgreSQL unique index. Application-side checks are not load-bearing.
2. **Append-only events.** `CanonicalEvent` rows are immutable once written. Re-seeing the same source event is a no-op, not a merge.
3. **Mutable lookup tables upsert.** `CanonicalActor` refreshes per-sight (login/display can change). `SyncCursor` is per-config state that overwrites.
4. **One-shot mappings stay put.** `IdentityMapping` and `WorkItemMapping` are written once and never replaced — re-mapping would break the stable-assignment and stable-target-ID guarantees the rest of the pipeline relies on.
5. **Configuration conflicts throw.** `SyncConfiguration` and `TargetUser` are admin-managed. Duplicate inserts indicate misconfiguration and should fail loudly.

## Per-entity key and conflict strategy

| Entity | Natural key | Conflict strategy |
|---|---|---|
| `SyncConfiguration` | `(Source, SourceLocator, TargetSystem, TargetLocator)` (jsonb equality is canonical for whitespace + key order; key casing fixed by `LocatorJsonOptions`) | Throw on conflict (admin-managed) |
| `SyncCursor` | `SyncConfigurationId` | Upsert (per-config state) |
| `CanonicalEvent` | `(Source, SourceEntityType, SourceEntityId, EventKind, EventTime, SourceEventId)` | Insert-or-ignore (`ON CONFLICT DO NOTHING`) |
| `CanonicalActor` | `(Source, SourceActorId)` | Upsert (`LastSeenAt`, `SourceActorLogin`, `DisplayName`) |
| `IdentityMapping` | `(CanonicalActorId, TargetSystem)` | Insert-once; treat existing as authoritative |
| `TargetUser` | `(TargetSystem, TargetUserId)` | Throw on conflict (admin-managed) |
| `WorkItemMapping` | `(SyncConfigurationId, Source, SourceEntityType, SourceEntityId)` **and** `(SyncConfigurationId, TargetSystem, TargetEntityId)` | Insert-once; treat existing as authoritative |
| `DeadLetter` | none | Pure append; multiple failures per event allowed |

### Why two unique indexes on `WorkItemMapping`

A source entity maps to exactly one target entity and vice versa within a configuration (per `data-model.md`). One index covers `source → target`; the other guards against the same target ID being claimed by two different source entities. Both are needed; one alone leaves the other direction unconstrained.

## `CanonicalEvent` — handling nullable `SourceEventId`

`SourceEventId` is nullable on `CanonicalEvent` (some GitHub events — e.g. title-only edits — have no stable per-event ID). PostgreSQL unique indexes default to **NULLS DISTINCT**, meaning two rows whose composite key differs only by `SourceEventId IS NULL` would not be considered duplicates and would both insert.

**Decision: use `NULLS NOT DISTINCT` on the `CanonicalEvent` unique index** (PostgreSQL 15+ feature). Two events with the same `(Source, SourceEntityType, SourceEntityId, EventKind, EventTime)` and both null `SourceEventId` are treated as the same event.

Alternatives considered:

- **Coalesce to sentinel at write time** (e.g. empty string). Works on any PostgreSQL version but adds an app-side concern that exists only to compensate for the index default. Rejected — we already require recent PostgreSQL for other reasons (Npgsql 6+ timestamp handling).
- **Two partial indexes** (one for `SourceEventId IS NOT NULL` with six columns, one for `IS NULL` with five). Works without `NULLS NOT DISTINCT`. Rejected for being two indexes where one suffices.

**Caveat on the EF Core fluent API:** the exact extension method for emitting `NULLS NOT DISTINCT` from Npgsql.EntityFrameworkCore.PostgreSQL is verified when #9 lands. If the provider version we pin lacks the extension, fall back to a raw SQL migration step for that one index — the design decision (semantic uniqueness across null `SourceEventId`) does not change.

### Invariant: `IssueEdited` is the only EventKind allowed to have a null `SourceEventId`

GitHub's second-precision timestamps mean two events at the same `EventTime` collide on the unique key unless `SourceEventId` distinguishes them. State-changing events all have stable per-event IDs in GitHub's APIs; only `IssueEdited` does not (and even then only on some ingestion paths). The v1 rule:

| EventKind | Source of `SourceEventId` |
|---|---|
| `IssueCreated` | Issue's GitHub numeric `id` (globally unique, not the per-repo `number`) |
| `IssueClosed`, `IssueReopened`, `IssueAssigned`, `IssueUnassigned`, `IssueLabeled`, `IssueUnlabeled`, `IssueTyped`, `IssueUntyped`, `IssueParentAdded`, `IssueParentRemoved` | `id` of the corresponding event from `/issues/events` (or equivalent GraphQL timeline node ID) |
| `IssueCommented` | Comment's `id` |
| `IssueEdited` | `null` — accepted gap, see below |

**Ingestion code that persists a non-`IssueEdited` event with a null `SourceEventId` is a bug.** #11/#12 must fail loud on this (throw before insert), and #13's test suite must include a case asserting that a state-transition event missing its source ID is rejected at ingest rather than silently persisted.

**Hybrid ingestion (webhook + poll) must read the same ID source for the same logical event.** GitHub returns identical numeric event IDs across the REST events endpoint, the GraphQL timeline, and webhook delivery payloads, so this is preserved as long as ingestion reads the `id` field from the event object itself — not, for example, the webhook's `X-GitHub-Delivery` header, which is delivery-scoped rather than event-scoped.

**Accepted gap for `IssueEdited`:** two distinct title/body edits at the same second collapse into one canonical event. The exporter will then apply only the first edit's payload to ADO, and the ADO work item silently disagrees with GitHub's current title/body. v1 accepts this because (a) title/body content fidelity is not a business-critical property of the demo data — the activity *shape* is what matters — and (b) `WorkItemMapping` keeps the source-to-target ID pairing intact, so any consumer that cares about current content can resolve back through the GitHub source. Revisit if production traffic shows back-to-back same-second edits at non-negligible rates.

## Why insert-or-ignore for `CanonicalEvent`, not skip-and-log

Duplicate events during normal incremental sync are **expected**, not anomalous: fetch windows overlap, retries after partial failure replay the tail of the previous window, and webhook + poll hybrid sources will double-cover. Treating every duplicate as a CLAUDE.md "non-blocking record failure" worth a `LogWarning` would flood logs with normal-operation noise.

**Decision:** at the database level, `INSERT ... ON CONFLICT DO NOTHING`. Application code does not log per duplicate. Aggregate counts (rows attempted vs. rows inserted per ingestion run) can be surfaced in run metrics later if observability needs it — not in v1.

CLAUDE.md's skip-and-log rule still applies to **unexpected** per-row failures during ingestion (malformed payload, schema mismatch, missing required field). Those throw `DbUpdateException` for reasons other than unique-violation `23505` and bubble up to the per-record handler, which logs `{Source, ExternalId, Reason}` and continues.

## Conflict strategy details for the other "interesting" entities

### `CanonicalActor` — upsert

Refresh `LastSeenAt`, `SourceActorLogin`, `DisplayName` on every sight. GitHub logins can change (`SourceActorId` is the stable join key per `data-model.md`); display names change more often. We want the latest cached values without losing the row's identity. `FirstSeenAt` is never overwritten.

**v1 limitation:** `DisplayName` is not refreshed today because the GitHub fetcher's `GitHubActor` DTO does not surface it. The column stays nullable and only gets populated once the source side starts emitting display names. Tracked as a follow-up.

### `IdentityMapping` — insert-once

The mapping row records *how this actor was resolved* (configured vs. least-loaded fallback) and *which target user owns the assignment*. Replacing an existing row on re-resolution would defeat the "persistent so least-loaded fallback assignments stay stable across runs" guarantee from `data-model.md`. On attempted re-insert: treat the existing row as authoritative and skip silently. Operator-driven re-mapping is an explicit out-of-band action, not an automatic resolution side-effect.

### `WorkItemMapping` — insert-once

A duplicate insert here would mean the exporter tried to create the same source entity in ADO twice, which would also create a second ADO work item (because the DB constraint only fires *after* the ADO call has already succeeded if the exporter doesn't pre-check). The right pattern in the exporter (#14) is **read-before-create**: query for an existing mapping by `(SyncConfigurationId, Source, SourceEntityType, SourceEntityId)`; if found, route the update to the recorded `TargetEntityId` instead of creating. The DB constraint exists as a backstop against race conditions and bugs — if it fires, surface as a hard error (not skip-and-log) because at that point an orphan ADO work item likely exists and needs operator attention.

## EF Core unique-index sketches

Stubs for `#9` (`IEntityTypeConfiguration` per entity). Final code lives in `src/GithubSync.Data/Configurations/`.

```csharp
// SyncConfiguration — SourceLocator/TargetLocator are jsonb; equality canonicalises
// whitespace + key order, and LocatorJsonOptions pins key casing on write.
builder.HasIndex(x => new
{
    x.Source, x.SourceLocator,
    x.TargetSystem, x.TargetLocator
}).IsUnique();

// SyncCursor — 1:1 with config
builder.HasIndex(x => x.SyncConfigurationId).IsUnique();

// CanonicalEvent — see "handling nullable SourceEventId" above
builder.HasIndex(x => new
{
    x.Source, x.SourceEntityType, x.SourceEntityId,
    x.EventKind, x.EventTime, x.SourceEventId
})
.IsUnique()
.AreNullsDistinct(false); // exact API verified in #9; fall back to raw SQL if unavailable

// CanonicalActor
builder.HasIndex(x => new { x.Source, x.SourceActorId }).IsUnique();

// IdentityMapping
builder.HasIndex(x => new { x.CanonicalActorId, x.TargetSystem }).IsUnique();

// TargetUser
builder.HasIndex(x => new { x.TargetSystem, x.TargetUserId }).IsUnique();

// WorkItemMapping — two unique indexes (see rationale above)
builder.HasIndex(x => new
{
    x.SyncConfigurationId, x.Source, x.SourceEntityType, x.SourceEntityId
}).IsUnique();
builder.HasIndex(x => new
{
    x.SyncConfigurationId, x.TargetSystem, x.TargetEntityId
}).IsUnique();

// DeadLetter — no unique indexes; non-unique on (CanonicalEventId, Resolved) for triage queries
builder.HasIndex(x => new { x.CanonicalEventId, x.Resolved });
```

## Implementation note: where the `ON CONFLICT DO NOTHING` lives

EF Core has no native upsert/ignore primitive. Two viable shapes for `CanonicalEvent` batched inserts (decided in #13):

1. **Raw SQL `INSERT ... ON CONFLICT DO NOTHING`** for a whole batch. One round-trip per batch, clean semantics. Per CLAUDE.md "EF Core over raw SQL" with an expressiveness exception: there is no fluent equivalent, and per-row exception handling has worse semantics and performance.
2. **Catch `DbUpdateException` with `PostgresException.SqlState == "23505"`** per row inside a loop. Simpler dependency-wise but slower and noisier.

v1 leans toward option 1. The decision is locked in #13; nothing in #8 forces either choice.

## Test plan

Integration tests against a real PostgreSQL instance (per CLAUDE.md test posture — no mocked database). All in `tests/GithubSync.Data.Tests/`:

1. **Repeat-window: zero duplicates.** Ingest a fixed set of N canonical events. Re-ingest the same set in a second call. Assert `CanonicalEvent` row count is N (not 2N). This is the primary acceptance test from issue #8.
2. **Overlapping window: tail re-ingest is a no-op.** Ingest events at times `[t1..t10]`. Ingest events at times `[t5..t15]`. Assert events at `[t5..t10]` exist exactly once; total row count is 15.
3. **Null `SourceEventId` deduplication.** Insert two events with identical `(Source, SourceEntityType, SourceEntityId, EventKind, EventTime)` and both `SourceEventId == null`. Assert exactly one row. Guards the `NULLS NOT DISTINCT` decision.
4. **`CanonicalActor` upsert preserves identity.** Insert actor. Re-insert same `(Source, SourceActorId)` with a changed `SourceActorLogin` and later `LastSeenAt`. Assert single row, `Id` unchanged, `SourceActorLogin` updated, `LastSeenAt` advanced, `FirstSeenAt` unchanged.
5. **`WorkItemMapping` insert-once.** Insert mapping for `(config, source-entity)`. Attempt to insert a second mapping with the same source key but a different `TargetEntityId`. Assert the second insert raises a constraint violation (hard fail — see rationale above).
6. **Concurrent insert of same event.** Two parallel transactions insert the same canonical event. Assert exactly one row commits and the other transaction sees the conflict cleanly (no caller-visible exception when using `ON CONFLICT DO NOTHING`).

Tests 1–3 directly satisfy the issue's acceptance criterion ("re-ingesting the same window twice produces zero duplicate rows"). Tests 4–6 cover the per-entity decisions that the criterion does not name explicitly but that this document commits to.

## What this unblocks

- **#9** — `IEntityTypeConfiguration` per entity, with the unique-index fluent calls above.
- **#10** — first migration includes these indexes; the `NULLS NOT DISTINCT` clause must survive migration generation (or be patched in raw SQL).
- **#13** — ingestion persists events with `ON CONFLICT DO NOTHING` semantics and advances cursor only after the batch commits.
- **#14, #15** — exporter relies on `WorkItemMapping`'s two unique indexes for read-before-create routing and target-ID stability.
