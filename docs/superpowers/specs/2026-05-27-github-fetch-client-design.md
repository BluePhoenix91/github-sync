# GitHub issues incremental fetch client — issue #11

Part of the GitHub ingestion MVP epic ([#3](https://github.com/BluePhoenix91/github-sync/issues/3)).

## Goal

Implement the first step of the ingestion pipeline: an HTTP client that fetches GitHub issue activity since a cursor and yields raw payloads to the mapper ([#12](https://github.com/BluePhoenix91/github-sync/issues/12)). The client must cover every v1 `EventKind`, honour incremental semantics, respect GitHub's rate limits, and flow `CancellationToken` end-to-end per [CLAUDE.md](../../../CLAUDE.md).

## Decision — GraphQL, not REST

The fetcher hits GitHub's GraphQL API (`https://api.github.com/graphql`) with a single query per page of issues. REST was considered and rejected.

| Concern | REST (`/issues` + `/issues/events` + `/issues/comments`) | GraphQL |
|---|---|---|
| Body-edit deltas | Not emitted by `/issues/events`; would require a `LastSeenBody*` cache + diff on every fetch. | Native via `userContentEdits` connection on `Issue`. Each edit is one node with `editedAt`, `editor`, prior text. (Comment body edits are not in v1 scope — see below.) |
| Round-trip count | 3 endpoints per cycle, plus `/issues/events` page-walking with no `since` parameter. | 1 query per page; one optional follow-up per overflowing connection. |
| Rate-limit accounting | Request count (5000/hr). | Point-based (5000 points/hr, query complexity counts). |
| Library | `Octokit.net`, mature. | Hand-rolled HTTP + JSON. `Octokit.GraphQL.NET` is dormant. |
| Coverage of v1 `EventKind` set | Misses `IssueEdited` body deltas natively. | All 13 covered in one query shape. |

The body-edit deltas requirement is load-bearing — [data-model.md:128](../../data-model.md#L128) explicitly says #11 must pick an approach that doesn't lose them. GraphQL also keeps `EventKind` mapping (#12) free of multi-stream merging logic, since each yielded payload corresponds to one source event with one timestamp.

`SyncCursor.LastETag` stays in the schema but is **unused for `Source=GitHub` in v1**. GitHub's GraphQL endpoint does not honour `If-None-Match`, and the cheapness of an empty `issues(filterBy:{since:...})` query makes the ETag short-circuit non-load-bearing. No migration. Revisit only if a REST supplementation path is added.

## Bootstrap behaviour

When `SyncCursor.LastEventTime` is null, the fetcher does **not** walk historical data. The cursor's *initial value* is the orchestrator's concern, not the fetcher's. The default initial value is "now" (cold start ingests from the moment of activation). An optional `StartDate` field on `SyncConfiguration` to let an operator backfill from an earlier point is a future issue, not part of #11.

The fetcher just respects whatever cursor it receives.

## Architecture

### New project: `GithubSync.Sources.GitHub`

A new class library under `src/`, referenced by `GithubSync.Api`. Rationale:

- The v2 roadmap (ADO/Jira/Linear as sources) makes a `Sources.*` seam load-bearing within 12 months. A class library is a 5-line `dotnet new`; carrying it now costs nothing.
- Keeps HTTP-calling source code out of the web host project so the fetcher is reusable from a future one-shot bootstrap CLI without dragging in `WebApplicationBuilder`.

The new project does **not** reference `GithubSync.Data`. The interface takes primitive `(string owner, string repo)` parameters rather than the existing `Locators.GitHubSourceLocator` type — that locator is a persistence concern (it carries the JSON canonicalisation rules used by the jsonb unique-index invariant), which has no business inside an HTTP client. The orchestrator unwraps the locator before calling the fetcher.

### Interface

```csharp
namespace GithubSync.Sources.GitHub;

public interface IGitHubIssueFetcher
{
    IAsyncEnumerable<GitHubIssueEvent> FetchAsync(
        string owner,
        string repo,
        DateTimeOffset? since,
        CancellationToken ct);
}

public sealed record GitHubIssueEvent(
    string SourceEntityId,           // GitHub issue number, as string (scoped per repo)
    string? SourceEventId,           // GraphQL node id; null only for body edits — matches CanonicalEvent rule from idempotency.md
    GitHubEventKind Kind,            // source-side discriminator; mapper translates to Data.Enums.EventKind in #12
    DateTimeOffset EventTime,        // UTC
    DateTimeOffset IssueUpdatedAt,   // watermark hint — see ordering contract below
    GitHubActor? Actor,              // null for deleted-user / system / "ghost" actors — see below
    string PayloadJson);             // raw GitHub payload slice the mapper + persister need

public sealed record GitHubActor(
    string Login,                    // GitHub login at observation time — can change; do not use as a join key
    string DatabaseId,               // GitHub numeric ID, string-encoded; the stable join key (matches CanonicalActor.SourceActorId)
    GitHubActorKind Kind);           // User / Bot / Mannequin — derived from GraphQL __typename

public enum GitHubActorKind { User, Bot, Mannequin, Other }
```

`GitHubEventKind` is a source-side enum (`IssueOpened`, `Labeled`, `Renamed`, `BodyEdited`, `Commented`, `Closed`, `Reopened`, `Assigned`, `Unassigned`, `Typed`, `Untyped`, `ParentAdded`, `ParentRemoved`). The mapper layer (#12) owns the canonical translation and the unknown-kind skip-and-log behaviour mandated by CLAUDE.md.

**Actor nullability.** GitHub timeline events and comments can have null actors — deleted-user accounts, "ghost" placeholder responses, and certain system-initiated actions surface as `actor: null`. These are not malformed records and must not be skipped. The fetcher passes through `Actor: null` faithfully; the mapper (#12) decides how to surface this canonically. `CanonicalEvent.ActorId` is already nullable in the schema for exactly this case, so the natural mapping is `null` actor → `null` ActorId. A sentinel "ghost" `CanonicalActor` row is *not* recommended because it would mean the actor table contains entries that don't correspond to real GitHub users, with downstream join-and-filter complexity. Final call belongs to #12.

**Actor kind.** `__typename` on a GraphQL actor field returns `User`, `Bot`, `Mannequin`, or `EnterpriseUserAccount` (rare). We surface this as `GitHubActorKind` so the mapper can filter bots out (if v1 chooses to) without re-parsing `PayloadJson`. Any unrecognised typename maps to `Other`, never throws.

**`DatabaseId` vs node `id` — why the asymmetry with `SourceEventId`.** Actor identity uses GraphQL `databaseId` because the canonical layer's `CanonicalActor.SourceActorId` was deliberately defined as the numeric GitHub user ID — it's stable across login renames in a way the global node ID isn't a substitute for. Timeline-event identity (`GitHubIssueEvent.SourceEventId`) uses GraphQL `id` (the global node ID), because not every timeline event type exposes a `databaseId` in GraphQL (notably `UserContentEdit` nodes for body edits), and the canonical layer's `CanonicalEvent.SourceEventId` only requires "stable per-event ID from the source" — node ID satisfies that uniformly. So the query asks for *both kinds of identifier* in different spots, by design.

### Why source-side models, not canonical models

The fetcher project speaks GitHub vocabulary only. The mapper (`#12`) translates `GitHubIssueEvent` to `CanonicalEvent`. Two payoffs:

- **Testability.** Fetcher tests stub WireMock and assert against `GitHubIssueEvent` literals — no DB, no actor table. Mapper tests assert against `CanonicalEvent` literals — no HTTP, no rate-limit mocks. Each side is testable without the other's machinery.
- **v2 readiness with no v2 work.** When `GithubSync.Sources.Jira` lands, it returns its own `JiraIssueEvent` shape; the mapper grows a second translation. If the fetcher returned `CanonicalEvent` directly, every new source would either fake the canonical shape from inside its own project or duplicate the mapping work. The seam stays clean.

This is the textbook anti-corruption layer / port-and-adapter pattern.

## GraphQL query and pagination

### Query shape — one page of issues with nested connections

```graphql
query IssuesPage($owner: String!, $repo: String!, $since: DateTime, $cursor: String) {
  repository(owner: $owner, name: $repo) {
    issues(
      first: 100, after: $cursor,
      filterBy: { since: $since },
      orderBy: { field: UPDATED_AT, direction: ASC }
    ) {
      pageInfo { endCursor hasNextPage }
      nodes {
        id
        number
        databaseId
        createdAt
        updatedAt
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
          LABELED_EVENT, UNLABELED_EVENT,
          ASSIGNED_EVENT, UNASSIGNED_EVENT,
          CLOSED_EVENT, REOPENED_EVENT,
          TYPED_EVENT, UNTYPED_EVENT,
          PARENT_ISSUE_ADDED_EVENT, PARENT_ISSUE_REMOVED_EVENT
        ]) {
          pageInfo { endCursor hasNextPage }
          nodes {
            __typename
            ... on LabeledEvent  { id createdAt actor { login databaseId __typename } label { name } }
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
```

The 13th `EventKind` (`IssueCreated`) is not a timeline item type in GraphQL. The fetcher synthesises a `GitHubEventKind.IssueOpened` event from each issue node's `createdAt` field whenever that timestamp falls inside the fetch window.

### Two-level pagination

- **Outer.** Walk `issues.pageInfo` until `hasNextPage` is false. Each call passes the previous response's `endCursor` as `after`.
- **Inner.** If any of the three per-issue connections returns `hasNextPage: true`, fire a targeted follow-up query for *just that issue*, draining the one overflowing connection by `endCursor`. The follow-up does not re-walk the outer issue list.

The follow-up code path is **shipped in v1**, not deferred. The data-model doc treats body-delta completeness as load-bearing, and "silently lose the last N events on a long-running issue" is exactly the unrealistic fidelity the demo (astragali.be) is trying to avoid.

### Ordering contract

The fetcher yields events **grouped by issue, in non-decreasing `issue.updatedAt` order, within a single `FetchAsync` invocation**. Within an issue, events are in `event time` order. The contract is a per-call guarantee; the caller is responsible for chaining successive calls with appropriate cursor values. This is what makes #13's watermark advance crash-safe:

- After committing all events for an issue with `updatedAt = T`, the persister sets the cursor to `T`. Every issue updated before `T` is already committed.
- If the process crashes mid-issue, a re-fetch from `T_of_last_completed_issue` replays only the in-flight issue's events. The idempotency unique key from #8 dedupes them.
- The boundary issue (the one whose `updatedAt` equals the previous cursor write) is always re-fetched on the next run because `filterBy: { since: T }` returns issues with `updatedAt >= T`. Same dedup absorbs that.

`GitHubIssueEvent.IssueUpdatedAt` carries the watermark hint so #13 doesn't have to re-derive it.

### `SyncCursor.LastEventTime` semantics for `Source = GitHub`

The column's docstring says "Watermark — fetch events after this time". For GitHub-via-GraphQL the more precise reading is "fetch issues with `updatedAt >= this`". Same column, slightly different semantics. The spec flags this; no rename. If a second source lands with materially different semantics, the column gets re-documented (or renamed) at that time.

## Rate limits, errors, cancellation

### Three failure categories, handled differently

1. **Transient HTTP** (5xx, network drops, gateway timeouts) — Polly retry policy, 3 retries on top of the initial attempt (up to 4 HTTP calls per logical request), exponential backoff between retries (1s → 2s → 4s). After the final attempt the exception surfaces.
2. **Rate limit, three signals**:
   - *Pre-flight budget check* — handled before each query. After each response, the in-memory `GitHubRateLimitBudget` is updated from `rateLimit { remaining cost resetAt }`. Before the next query: if `remaining < lastObservedCost * 2`, sleep until `resetAt` (interruptible via `Task.Delay(timespan, ct)`). The previous response's `cost` is a fair estimate for the next call because query shape is identical between pages.
   - *HTTP 403 with `Retry-After` header* — secondary rate limit. Sleep that long, retry once. Prefer this over header-based reset when both are present, since `Retry-After` is the explicit hint.
   - *HTTP 403 with `X-RateLimit-Remaining: 0` and `X-RateLimit-Reset: <epoch>`* — primary rate limit hit at the HTTP layer (the in-memory budget was stale or absent). Sleep until `X-RateLimit-Reset`, retry once.
   - If a 403 has *neither* signal, it's a hard auth failure (no retry).
   - After one retry-and-still-403, throw `GitHubRateLimitException`.
3. **Hard "no" responses** (401, 404, 403 without rate-limit signals, GraphQL `errors` body) — throw a typed exception immediately, no retry.

### Typed exceptions

All in `GithubSync.Sources.GitHub`:

- `GitHubAuthException` — 401, or 403 without any rate-limit header signal. Token invalid, missing scopes, repo not accessible.
- `GitHubRateLimitException` — any rate-limit retry path (secondary `Retry-After` or primary `X-RateLimit-*`) exhausted: a 403 received again after the one retry.
- `GitHubGraphQLException` — non-recoverable error in a `200 OK` body's `errors` array. Schema drift, malformed query, semantic error.

Exhausted transient retries surface as `HttpRequestException`. The orchestrator's mapping rule: any of the three typed exceptions → `SyncRunStatus.Failed` for the run, log structured, do not retry the run within the same scheduled job execution. `HttpRequestException` after retry exhaustion: same treatment.

### Cancellation propagation

`CancellationToken` flows from `FetchAsync` into:

- Each HTTP `SendAsync` call (via the typed `HttpClient`).
- Each `Task.Delay` used for backoff or rate-limit waiting — never `Thread.Sleep`.
- The `yield return` loop in the `IAsyncEnumerable` implementation, with `ct.ThrowIfCancellationRequested()` at await points.

No internal `CancellationTokenSource` that the caller can't reach. The orchestrator (Hangfire job) owns the token; the fetcher passes it through.

### Authentication

PAT-based for v1, read at HttpClient registration time from the `GITHUB_TOKEN` env var (already convention per the repo). Attached as `Authorization: Bearer <token>` on every request. Never hardcoded; user secrets locally, env vars in deployed environments — same secret strategy as the rest of the project.

GitHub App auth and OAuth flows are out of scope for v1.

### No circuit breaker

Polly supports `CircuitBreaker` — explicitly not used. The fetcher runs in scheduled batches with reasonable spacing; if GitHub is fully down, the next scheduled run already absorbs the wait. A circuit breaker would add state-machine complexity for negligible v1 benefit.

### Logging shape

Adds to the Serilog + Sentry breadcrumb pipeline established by PRs #56 / #57:

| Event | Level | Structured fields |
|---|---|---|
| Fetch started | Information | `Source: "github"`, `Owner`, `Repo`, `Since` |
| Fetch completed | Information | above plus `IssuesYielded`, `EventsYielded`, `DurationMs`, `RateLimitRemaining` |
| Transient retry | Warning | `Attempt`, `ReasonShort`, `Owner`, `Repo` |
| Hard failure | Error | typed exception, structured fields, full exception via Sentry |

Per CLAUDE.md, the skip-and-log convention applies to *unexpected record-level failures* — for the fetcher, that means a malformed GraphQL node missing required fields. Such records are logged at Warning with `{ Source: "github", ExternalId: <node id>, Reason }` and skipped. Systemic failures throw.

## Testing strategy

### Test project — extend `GithubSync.Tests`

Tests for the GitHub fetcher live at `tests/GithubSync.Tests/Sources/GitHub/`, in the existing single test project. One test project is sufficient until a second source project lands with conflicting test-host needs; splitting later is a 10-minute job.

New dependency: `WireMock.Net` — the .NET port for stubbing HTTP endpoints. Matches POST to `/graphql` based on query content patterns.

### Fourteen unit tests

| # | Scenario | Asserts |
|---|---|---|
| 1 | Empty page | `repository.issues.nodes: []`, `hasNextPage: false` → zero yielded events, clean completion. |
| 2 | Single page, varied content | 5 issues with mixed timeline events → expected events in expected order. |
| 3 | Outer pagination | 200 issues across 2 outer pages → fetcher passes `endCursor` as `after` on second call. |
| 4 | Inner pagination follow-up | Issue with >100 timeline items → follow-up query targets that issue; outer issue list not re-walked. |
| 5 | Secondary rate limit retry | First POST 403 + `Retry-After: 1`; second POST 200 → fetcher sleeps, retries once, succeeds. |
| 6 | Primary limit via headers | First POST 403 with `X-RateLimit-Remaining: 0` and `X-RateLimit-Reset: now+2s`, no `Retry-After`; second POST 200 → fetcher sleeps until reset, retries once. |
| 7 | Transient 5xx retry | Two 503s then 200 → Polly applies, fetcher succeeds. |
| 8 | GraphQL error in body | 200 with `errors: [...]` → `GitHubGraphQLException`, no retry. |
| 9 | Auth failure (403 without rate-limit signals) | 403 with no `Retry-After` and no `X-RateLimit-*` → `GitHubAuthException`, no retry. |
| 10 | Auth failure (401) | 401 → `GitHubAuthException`, no retry. |
| 11 | Null actor passthrough | Timeline event with `actor: null` (e.g. deleted user) → yielded event has `Actor: null`, not skipped. |
| 12 | Cancellation during rate-limit sleep | Forced 30s sleep, token cancelled at 100ms → `OperationCanceledException` quickly. |
| 13 | Ordering contract | Mixed `updatedAt` issues → yielded events maintain non-decreasing `IssueUpdatedAt` group order. |
| 14 | Pre-flight budget wait | First response `remaining: 1, cost: 100, resetAt: now+2s` → fetcher waits before next query. |

### One optional integration test

A single test against real GitHub, hitting `octocat/Hello-World` (GitHub's stable demo repo). Gated on both `GITHUB_TOKEN` env var and `RUN_INTEGRATION_TESTS=true`. Off in CI by default; runnable on-demand before opening a PR. Not part of the standard test suite — it's a smoke-test sanity check, not continuous validation.

## Issue #11 acceptance criteria — proposed update

The current acceptance criteria include "Conditional requests reuse ETag and skip unchanged pages on 304". That is REST-specific and not applicable under the GraphQL choice. Proposed edits for the issue body:

- **Replace** *"Conditional requests reuse ETag and skip unchanged pages on 304."*
- **With** *"GraphQL `rateLimit { remaining cost resetAt }` is consulted before each query; the fetcher waits until reset when the remaining budget is below the next call's projected cost."*

- **Replace** the *"304"* row in the unit-tests bullet.
- **With** *"Inner pagination follow-up for issues with overflowing connections."*

The other acceptance criteria stay as written.

## What this unblocks

- **#12** can be implemented against the `GitHubIssueEvent` shape defined here. The mapper takes one of these and produces one `CanonicalEvent`.
- **#13** has a fetcher to wire its end-of-window cursor advance against. The ordering contract makes its crash-safety story straightforward.

## Out of scope (deferred)

- **Comment body edits.** `EventKind` has `IssueEdited` (issue title/body) but no `CommentEdited`. The query intentionally omits `userContentEdits` on each `IssueComment` node. A future issue can add the enum value, extend the query, and add the corresponding mapping — at which point the fetcher's contract grows backward-compatibly.
- **Webhook-based real-time triggers.** A separate ingestion topology; would warrant its own epic.
- **REST supplementation** of any specific edge case. Revisited if a real gap appears.
- **GitHub App auth.** PAT only for v1.
- **`StartDate` on `SyncConfiguration`** for operator-driven backfill. Future issue; the fetcher already supports it via the existing cursor input.
- **Bot-actor filtering.** The fetcher surfaces `GitHubActorKind` faithfully (User / Bot / Mannequin / Other) but does not filter. If v1 ends up wanting bot exclusion, the call belongs in the mapper (#12) or in `SyncConfiguration` settings — not the fetcher.
- **Cross-source watermark coordination.** v2 concern when multiple sources feed one configuration.
