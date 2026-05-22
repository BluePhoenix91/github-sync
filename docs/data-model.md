# Canonical data model (v1)

This document defines the canonical persistence model that sits between GitHub (source) and Azure DevOps (target). v1 scope is GitHub Issues to ADO work items, incremental sync only.

The model is **event-sourced at issue-activity granularity**. We persist each meaningful GitHub issue interaction as a `CanonicalEvent` row. The exporter replays those events as ADO patches.

**Decision: the target tracker is the source of truth for current state.** We do not maintain a denormalised issue snapshot. Each canonical event is self-contained enough for the exporter to act on, and any question of "what's the current title/state of issue #N?" is answered by reading the ADO work item, not by querying our store. This keeps writes cheap, avoids replay-vs-snapshot drift, and matches the v1 pipeline direction (we only push to ADO; we never read back from our store to serve users).

## Conventions

### Timestamps: `DateTimeOffset`, stored UTC

All datetime columns use C# `DateTimeOffset`, persisted as PostgreSQL `timestamp with time zone`. Rationale:

- Npgsql 6+ defaults `DateTime` columns to `timestamp with time zone` and requires UTC `DateTime.Kind`. Mismatches throw at runtime (CLAUDE.md gotcha).
- `DateTimeOffset` is unambiguous at the C# type level — readers cannot mistake a value for local time. Postgres stores in UTC, so the offset reads back as `+00:00`; the type wins us safety in app code without changing storage semantics.
- The Hangfire UTC requirement (CLAUDE.md gotcha) makes the "everything is UTC" rule project-wide.

Consequence: any field below typed `timestamp` is a `DateTimeOffset` in UTC.

### Identifiers

- Primary keys: `Guid` (`uuid` in Postgres), generated app-side. Avoids round-trips and lets us pre-populate FKs before insert.
- External IDs: `string` (e.g. GitHub issue number, ADO work item ID). GitHub issue numbers are scoped per repo, so any uniqueness check involving them must also include source repo identity.

### Source/target neutrality

`Source` and `TargetSystem` enum-style fields are present on entities that could in principle come from a different platform later, even though v1 only writes `GitHub` and `AzureDevOps`. Cost is one column; benefit is the v2 Jira/etc. work in the architecture doc doesn't require a schema rewrite.

---

## Entities

### `SyncConfiguration`

A configured source-repo to target-project pairing. The unit of "what we sync".

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `Name` | string | yes | Human-readable label for logs and the Hangfire dashboard |
| `Source` | enum (`GitHub`) | yes | Source platform |
| `SourceLocator` | jsonb | yes | Platform-specific address of the source. v1: `{ "owner": "...", "repo": "..." }` for `Source = GitHub`. See "Locator shapes" below. |
| `TargetSystem` | enum (`AzureDevOps`) | yes | Target platform |
| `TargetLocator` | jsonb | yes | Platform-specific address of the target. v1: `{ "organization": "...", "project": "..." }` for `TargetSystem = AzureDevOps`. See "Locator shapes" below. |
| `TargetTypeMapping` | jsonb | yes | Rules for resolving an incoming issue to a target work-item / issue type. Carries a default and optional overrides keyed by hierarchy/native issue type/label (see below). Concrete JSON shape is intentionally not pinned here — finalised when the exporter lands (#14). |
| `Enabled` | bool | yes | If false, scheduler skips this config |
| `CreatedAt` | timestamp | yes | |
| `UpdatedAt` | timestamp | yes | |

Uniqueness: `(Source, SourceLocator, TargetSystem, TargetLocator)` — same pair cannot be configured twice. Postgres jsonb equality canonicalises key order and whitespace; key casing is fixed by `LocatorJsonOptions.Default` (camelCase) so serialised values are deterministic.

**Locator shapes (v1):**

| Platform | Used as | JSON shape | C# record |
|---|---|---|---|
| GitHub | source | `{ "owner": "<org-or-user>", "repo": "<repo>" }` | `Locators.GitHubSourceLocator(Owner, Repo)` |
| Azure DevOps | target | `{ "organization": "<org>", "project": "<project>" }` | `Locators.AzureDevOpsTargetLocator(Organization, Project)` |

**Why jsonb rather than typed columns:** the foreseeable roadmap adds ADO/Jira/Linear as both source and destination. Each has its own two-segment vocabulary (Jira: site + project key; Linear: workspace + team; ADO: organization + project). Per-platform typed columns would mean either four migrations (one per platform onboarded) or a platform-neutral but illegible naming like `Locator1`/`Locator2`. jsonb keeps the columns honest about being shape-deferred while the platform enums remain the discriminators. Application code reads the JSON via the platform-specific record in `src/GithubSync.Data/Locators/` selected by `Source` / `TargetSystem`.

**Type-mapping resolution order (informs `TargetTypeMapping` shape):**

1. **Hierarchy wins.** If the source issue has a parent link (GitHub's native sub-issue relationship), it is treated as a child; if it is itself a parent of other issues or carries a configured epic indicator, it maps to ADO `Epic`. Hierarchy is preserved end-to-end — children stay children, epics stay epics.
2. **Native GitHub issue type** (org-level feature: `Task`, `Bug`, `Feature`, plus org customs, max 25). If present, look it up in the mapping.
3. **Label-based fallback** for repos that drive type by label convention (this repo's own `type:*` namespace is an example). Look up labels in the mapping.
4. **Default** if nothing matched.

`TargetTypeMapping` must be expressive enough to cover all four. It is jsonb to keep that flexibility without re-migrating each time we refine the rules. The exact JSON schema is owned by #14.

**Resolution runs once per work item, at create time.** The result is persisted on `WorkItemMapping.TargetWorkItemType` and not re-derived on updates — see the immutability note on `WorkItemMapping` below for rationale and the v2 path.

### `SyncCursor`

Per-configuration incremental sync state. One row per `SyncConfiguration`.

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `SyncConfigurationId` | Guid | yes | FK, unique (1:1) |
| `LastEventTime` | timestamp | no | Watermark — fetch events after this time |
| `LastETag` | string | no | For conditional `If-None-Match` requests (#11) |
| `LastRunStartedAt` | timestamp | no | |
| `LastRunCompletedAt` | timestamp | no | Null while a run is in flight |
| `LastRunStatus` | enum (`Success`, `Partial`, `Failed`) | no | |
| `LastRunMessage` | string | no | Short human note for failed/partial runs |

Cursor advances only after the events for the window are durably committed (#13).

### `CanonicalEvent`

The atomic unit of "something happened to an issue". One row per source interaction.

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `SyncConfigurationId` | Guid | yes | FK |
| `Source` | enum (`GitHub`) | yes | |
| `SourceEntityType` | enum (`Issue`) | yes | v1: `Issue` only |
| `SourceEntityId` | string | yes | GitHub issue number (as string, scoped per repo) |
| `SourceEventId` | string | no | Stable per-event ID from the source. v1 invariant: null is allowed *only* for `IssueEdited` (GitHub does not surface a per-event ID for title/body edits on all ingestion paths). See [`idempotency.md`](./idempotency.md) for the per-EventKind ID source and the accepted same-second-edit gap. |
| `EventKind` | enum | yes | See list below |
| `EventTime` | timestamp | yes | Source-provided event time, normalised to UTC |
| `ActorId` | Guid | no | FK to `CanonicalActor`. Null only for events with no actor (rare; system events). |
| `PayloadJson` | jsonb | yes | Raw source payload for the event — exporter reads from here |
| `IngestedAt` | timestamp | yes | When we wrote the row |

**`EventKind` values (v1):**

- `IssueCreated`
- `IssueEdited` — title and/or body changes (see note below)
- `IssueLabeled`
- `IssueUnlabeled`
- `IssueAssigned`
- `IssueUnassigned`
- `IssueTyped` — native GitHub issue type set or changed
- `IssueUntyped` — native GitHub issue type removed
- `IssueParentAdded` — sub-issue parent linkage created (or swapped target — old parent removal is its own event)
- `IssueParentRemoved` — sub-issue parent linkage removed
- `IssueCommented`
- `IssueClosed`
- `IssueReopened`

Unknown action types from GitHub are skipped-and-logged per CLAUDE.md (#12), not added as new enum values implicitly.

**Note on `IssueEdited` (GitHub semantics):** the GitHub webhook fires a single `issues.edited` action for a single edit user-action, regardless of whether the title, the body, or both were changed. The webhook payload's `changes` object holds the previous values: `changes.title.from` and/or `changes.body.from` are present only for the fields that changed. We mirror this 1:1 — one user edit becomes one `IssueEdited` canonical event whose `PayloadJson` carries both deltas (when present). The mapper (#12) does not split this into two events.

**Ingestion-path caveat (informs #11):** GitHub's REST issue *events* API (`/issues/events`) emits a `renamed` event for title changes but **no event at all for body changes**. If #11 picks that endpoint as the ingestion source, body edits will be invisible without snapshot diffing. The webhook path and the GraphQL `IssueTimelineItems` connection both surface body edits via the issue's updated state. #11 should pick an approach that doesn't lose body deltas.

**Notes on type and parent events:**

- `IssueTyped` / `IssueUntyped` mirror the GitHub webhook `typed` / `untyped` actions and carry the native issue type (if any). Org-level feature — not all source repos use it; many still drive type from labels. Both signals are handled at export time via `TargetTypeMapping`.
- `IssueParentAdded` / `IssueParentRemoved` correspond to GitHub's sub-issue webhook actions. The payload carries the source-side parent issue identity; the exporter resolves it through `WorkItemMapping` to the target ADO ID and stamps the corresponding link (e.g. `System.LinkTypes.Hierarchy-Reverse` for ADO).
- A swap (parent A → parent B) appears as two events in order: remove-A then add-B, matching the underlying webhook.

**Idempotency:** the natural composite key is `(Source, SourceEntityType, SourceEntityId, EventKind, EventTime, SourceEventId)`. Null handling for `SourceEventId`, conflict strategy, and the EF Core unique-index sketch live in [`idempotency.md`](./idempotency.md).

### `CanonicalActor`

A unified per-source actor. Created on first sight; never deleted.

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `Source` | enum (`GitHub`) | yes | |
| `SourceActorId` | string | yes | GitHub user ID (numeric, string-encoded). Stable across login renames. |
| `SourceActorLogin` | string | yes | GitHub login at last sight |
| `DisplayName` | string | no | GitHub display name at last sight |
| `FirstSeenAt` | timestamp | yes | |
| `LastSeenAt` | timestamp | yes | |

Uniqueness: `(Source, SourceActorId)`. Login can change; `SourceActorId` is the stable join key.

### `IdentityMapping`

Maps a `CanonicalActor` to a specific ADO user. Persistent so least-loaded fallback assignments stay stable across runs.

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `CanonicalActorId` | Guid | yes | FK, unique (1:1 with actor) |
| `TargetSystem` | enum (`AzureDevOps`) | yes | |
| `TargetUserId` | string | yes | ADO user identifier (UPN or descriptor) |
| `TargetUserDisplayName` | string | yes | |
| `MappingSource` | enum (`Configured`, `LeastLoadedFallback`) | yes | How this row came to exist |
| `CreatedAt` | timestamp | yes | |

Uniqueness: `(CanonicalActorId, TargetSystem)` — one mapping per actor per target.

### `TargetUserPool`

Configured set of ADO users available for assignment, including for the least-loaded fallback.

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `TargetSystem` | enum (`AzureDevOps`) | yes | |
| `TargetUserId` | string | yes | |
| `TargetUserDisplayName` | string | yes | |
| `Enabled` | bool | yes | Disabled users are skipped by least-loaded selection |
| `CreatedAt` | timestamp | yes | |

Uniqueness: `(TargetSystem, TargetUserId)`.

Per CLAUDE.md, least-loaded selection queries current assignment counts at decision time (against `WorkItemMapping` joined to `IdentityMapping`) rather than caching counts. Counts self-correct as assignments drift.

### `WorkItemMapping`

Persistent source-entity to target-entity ID mapping. Lets the exporter route updates to the right ADO work item without re-creating it (#14, #15).

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `SyncConfigurationId` | Guid | yes | FK — scopes the mapping to a config |
| `Source` | enum (`GitHub`) | yes | |
| `SourceEntityType` | enum (`Issue`) | yes | |
| `SourceEntityId` | string | yes | GitHub issue number |
| `TargetSystem` | enum (`AzureDevOps`) | yes | |
| `TargetEntityId` | string | yes | ADO work item ID |
| `TargetWorkItemType` | string | yes | ADO work item type resolved at create time (e.g. `User Story`, `Bug`, `Epic`). Locked once written — see immutability note below. The exporter reads this to build type-appropriate JSON Patches on update without re-querying ADO. |
| `CreatedAt` | timestamp | yes | |

Uniqueness: `(SyncConfigurationId, Source, SourceEntityType, SourceEntityId)` and `(SyncConfigurationId, TargetSystem, TargetEntityId)`. A source entity maps to exactly one target entity and vice versa within a configuration.

**v1 decision: `TargetWorkItemType` is immutable after create.** `TargetTypeMapping` resolution runs once, at the first export (`IssueCreated` event). Subsequent re-labelling, native type changes (`IssueTyped` / `IssueUntyped`), or rule-mapping changes do *not* trigger an ADO work-item-type change. Rationale: ADO's change-type operation is not a regular JSON Patch — it has process-template constraints and drops fields that don't exist on the destination type, so cheap-looking "just flip the type" exports can quietly lose data. We accept the small fidelity gap for v1, observe how often type-after-create actually changes in production, and revisit. The source-side events (`IssueTyped`, `IssueUntyped`, `IssueLabeled`, `IssueUnlabeled`) are still persisted normally — only the *target-type* derivation is locked. When we revisit, no schema change is required; the exporter gains a new branch that compares re-resolved type against `WorkItemMapping.TargetWorkItemType` and issues the ADO change-type call when they diverge.

### `DeadLetter`

Failed exports that exhausted retries. Queryable for inspection and manual replay. Never blocks the pipeline (#15).

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | Guid | yes | PK |
| `CanonicalEventId` | Guid | yes | FK |
| `TargetSystem` | enum (`AzureDevOps`) | yes | |
| `AttemptedAt` | timestamp | yes | Time of the final failed attempt |
| `AttemptCount` | int | yes | Total attempts including the final one |
| `Reason` | string | yes | Short categorisation (e.g. `ValidationFailed`, `RetryExhausted`) |
| `RawResponse` | jsonb | no | Captured response body for triage |
| `Resolved` | bool | yes | Set when an operator replays or dismisses |
| `ResolvedAt` | timestamp | no | |

---

## Relationships

```
SyncConfiguration 1 ──── 1 SyncCursor
SyncConfiguration 1 ──── N CanonicalEvent
SyncConfiguration 1 ──── N WorkItemMapping

CanonicalEvent    N ──── 1 CanonicalActor   (nullable on the event side)
CanonicalEvent    1 ──── N DeadLetter       (typically 0 or 1 per event in practice)

CanonicalActor    1 ──── 0..1 IdentityMapping (per TargetSystem)
TargetUserPool         (standalone reference; least-loaded selection queries it)
WorkItemMapping        (no FKs into CanonicalEvent — keyed by source entity identity)
```

Cardinality notes:

- `SyncConfiguration` to `SyncCursor` is strictly 1:1; a config without a cursor is a config that has never run.
- `CanonicalEvent.ActorId` is nullable to leave room for system-generated events (e.g. automated label changes by bots without a user actor). v1 GitHub events all have actors in practice.
- `IdentityMapping` is 0..1 per `(CanonicalActor, TargetSystem)` — actors only need mapping rows for targets we sync to.
- `DeadLetter` is N:1 to `CanonicalEvent` (an event can be replayed and fail again, generating multiple DLQ rows over time, though `Resolved=false` filtering usually leaves at most one active).

---

## Out of scope (v2+)

Per epic #1 and issue #7 scope:

- Pull request events (PR open/close/review)
- Project board events (column moves, project field changes)
- Code review activity (review comments, approvals)
- Cross-platform deduplication (same logical work item in two sources)
- Sync direction reversal (ADO to GitHub)

When any of these land, expect:

- New `EventKind` values, possibly new `SourceEntityType` values
- A new entity for project-field state if board events need replay semantics
- No structural change to `CanonicalActor`, `IdentityMapping`, `TargetUserPool`, `WorkItemMapping`, `DeadLetter` — these are platform-neutral by design.

## What this unblocks

- **#8** — idempotency key composition and unique-index design, building on the candidate composite key noted on `CanonicalEvent`. Resolved in [`idempotency.md`](./idempotency.md).
- **#9** — `AppDbContext` + one `IEntityTypeConfiguration` per entity above.
- **#10** — initial EF Core migration generated from #9.
- **#11–#13** — ingestion pipeline writes into `CanonicalEvent` and advances `SyncCursor`.
- **#14, #15** — exporter reads `CanonicalEvent`, writes via `WorkItemMapping`, falls back to `DeadLetter` on terminal failure.
