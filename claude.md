# github-sync

Service that syncs issue activity from public GitHub repos into other trackers (Azure DevOps in v1). Provides realistic, non-confidential data for development and demos of importer/wrapper logic.

See `README.md` for project background and rationale.

## Stack

- C# / .NET, PostgreSQL + EF Core
- ASP.NET Core Web API host on AWS Lightsail
- Hangfire for scheduling (PostgreSQL storage, separate `hangfire` schema)
- v1 scope: GitHub Issues to Azure DevOps work items, incremental sync only

## Code style

- EF Core over raw SQL. Drop to raw SQL only when there is a clear perf or expressiveness reason, and document why inline.
- C# primary constructor syntax for classes that primarily hold dependencies. Example: `public class IssueSyncer(IGitHubClient github, AppDbContext db, ILogger<IssueSyncer> logger)`.
- Nullable reference types enabled. No `!` suppression without an inline comment explaining why, except `= null!` on EF Core required navigation properties — that's universal EF idiom (EF populates the reference on materialisation) and doesn't need a per-site comment.
- Async end-to-end. No `.Result` or `.Wait()`. Pass `CancellationToken` through any I/O path.
- xUnit for tests.

## Logging and error reporting

- Sentry, DSN supplied via env var `SENTRY_DSN`, never hardcoded.
- Skip-and-log for *unexpected* non-blocking record failures (malformed payload, missing required field, unparseable value). Use `LogWarning` with structured fields `{ Source, ExternalId, Reason }`. Throw only when the failure is systemic (auth, connectivity, persistent schema mismatch).
- *Expected* per-row occurrences — most notably dedup hits during normal incremental sync (overlapping fetch windows, retries replaying the previous tail, webhook + poll double-coverage) — are silent at the row level. Surface them via aggregate per-run metrics instead. Warning logs only retain signal value if they don't fire on by-design behaviour.

## Identity mapping

GitHub actors map to a fixed set of Azure DevOps users via configuration. Unknown actors are assigned via least-loaded selection: pick the configured user with the fewest currently assigned items. Counts are queried at decision time so the algorithm self-corrects if assignments drift uneven.

## Scheduling

- Hangfire with PostgreSQL storage in a separate `hangfire` schema (do not mix with app tables).
- Recurring jobs registered by stable string ID. Updating cron requires re-registering with the same ID.
- Dashboard mounted at `/hangfire`. Protected by an authorization filter in non-Development environments.

## Commands

[Fill in as we scaffold:]

- Build: `dotnet build`
- Test: `dotnet test`
- Add migration: `dotnet ef migrations add <Name> --project src/GithubSync.Data`
- Apply migrations: `dotnet ef database update --project src/GithubSync.Data`
- Set local AppDb connection (User Secrets, one-time per machine): `dotnet user-secrets set "ConnectionStrings:AppDb" "Host=localhost;Port=5432;Database=githubsync;Username=postgres;Password=<your-password>" --project src/GithubSync.Api`
- Run API locally: `dotnet run --project src/GithubSync.Api`
- Hangfire dashboard (local): http://localhost:5000/hangfire

## Repo etiquette

- `main` is the only long-lived branch.
- Feature branches: `feat/<short-kebab-description>`, `fix/<...>`, `chore/<...>`.
- Commits inside a branch can be messy. Squash-merge into `main`.
- Squash-merge title follows Conventional Commits: `feat: add Azure DevOps work-item adapter`, `fix: handle GitHub secondary rate-limit response`, `chore: bump EF Core to 9.0.2`.
- One PR = one logical change. If the description contains "and also", split it.
- Run `/simplify` against the branch diff before pushing any PR that touches `.cs` files. Address actionable findings; surface any skipped findings in the PR description with a one-line reason each. Doc/config-only PRs skip this step.

## Issue workflow

- Follow `docs/issue-workflow.md` as the source of truth for backlog and execution flow.
- Start implementation only from an active issue, not from ad-hoc chat intent alone.
- Child issues must use GitHub's native parent relationship to exactly one epic.
- Keep issues small and split scope when a task starts spanning multiple concerns.
- Before closing an issue, verify acceptance criteria and link PR closure (`Closes #...`).

## v1 scope philosophy

When a v1 feature decision is ambiguous and the schema/architecture can support the harder behavior later without migration, prefer the simpler v1 option, document the call with an explicit revisit trigger (for example "revisit when production data shows X"), and move on. Don't gold-plate v1 with flexibility for hypothetical future requirements.

## CI/CD

GitHub Actions. Repo is public since the project only handles public GitHub data, which makes Actions minutes unlimited at no cost.

## Known gotchas

- GitHub REST API has both primary (5000/hr authenticated) and secondary rate limits. Use conditional requests (ETag / If-None-Match) and respect `X-RateLimit-Reset`. Back off on 403 secondary rate-limit responses; do not retry tightly.
- Azure DevOps work-item updates use JSON Patch. Field reference names (`System.Title`, etc.) are case-sensitive on some endpoints.
- EF Core + PostgreSQL: since Npgsql 6, `DateTime` columns default to `timestamp with time zone` (UTC). Use `DateTimeOffset` or `.ToUniversalTime()` consistently.
- Hangfire dashboard is unauthenticated by default. Always configure an authorization filter before deploying anywhere reachable.
- Hangfire.PostgreSql package: pin a version explicitly. Two community forks with similar names and incompatible APIs exist. The maintained one is `Hangfire.PostgreSql` by frankhommers.
- Hangfire requires UTC for scheduled times. Mixing local and UTC produces silent drift in recurring triggers.
