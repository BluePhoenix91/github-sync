# github-sync

## Architecture

High-level architecture and project structure are documented in `ARCHITECTURE.md`.

## Project goal

`github-sync` exists to provide realistic, live issue-tracker data for development and demos without exposing private or client information.

In many environments, confidential tracker data cannot be used for building, testing, or showcasing importer and wrapper logic.  
This project solves that by syncing issue activity from public/open-source GitHub repositories into another tracker (for example Azure DevOps or Jira).

## Problem this solves

- Enables safe development and demonstration using only public data.
- Recreates real team workflows instead of artificial test fixtures.
- Provides ongoing data streams to validate importer/wrapper behavior.

## What gets synced

The connector is intended to copy issue interactions such as:

- issue creation
- issue updates (title, description, labels, assignees, comments, etc.)
- status/process transitions (for example moving through workflow states)
- other timeline events that represent work progression

## How it should run

The sync should run periodically (scheduled/recurring execution) and transfer new interactions from GitHub to the target tracker.

At a high level:

1. Read recent issue interactions from configured public GitHub projects.
2. Transform them into the destination tracker's model.
3. Create/update/move corresponding work items in Azure DevOps, Jira, or other supported systems.
4. Repeat on a fixed interval.

## Architecture decisions

- implementation stack: C# / .NET
- cloud/platform context: AWS-based deployment
- sync direction (v1): GitHub -> external tracker
- source scope (v1): GitHub Issues only
- primary target (v1): Azure DevOps
- synchronization mode (v1): incremental sync of new events only
- execution model: periodic scheduled worker
- schedule (v1): every 5 minutes
- persistence: PostgreSQL with EF Core
- configuration model: per-sync configuration (source + target pairing)
- orchestration/scheduler: Hangfire
- workflow handling: exact workflow/status mapping
- identity handling: least-loaded assignment from configured Azure DevOps users for unknown actors, with persistent reuse of saved mappings
- failure behavior: skip-and-log for non-blocking record failures
- retry behavior: exponential backoff with jitter for transient failures
- dead-letter behavior: persist failed records for replay
- duplicate handling: enforced at the database level via unique indexes; ingestion is idempotent at the canonical-events layer (see `docs/idempotency.md`)

## Pipeline model

The synchronization is split into two stages:

1. Importer: ingest GitHub issue interactions into the internal store.
2. Exporter: replay stored events to Azure DevOps based on active mappings and configuration.

## Intended outcome

A reusable connector service that keeps target trackers populated with realistic, continuously updated public issue activity, so downstream systems and importers can be developed, tested, and demonstrated safely.

## Local development setup

End-to-end bootstrap for a fresh clone, from no database to a running API. Run all commands from the repo root — the `dotnet ef` and `dotnet run` commands below use paths relative to it.

### Prerequisites

- .NET 10 SDK
- PostgreSQL **15 or newer**

PostgreSQL 15+ is required because the `CanonicalEvents` unique index uses `NULLS NOT DISTINCT`, a PG 15 feature. PG 14 fails to apply the Initial migration with `42601: syntax error at or near "NULLS"`. See [`docs/idempotency.md`](docs/idempotency.md) for the rationale behind that index.

### 1. Run PostgreSQL

If you already have PostgreSQL 15+ running locally, skip to step 2.

Otherwise, the lowest-friction path is Docker:

```bash
docker run -d --name pg-githubsync -e POSTGRES_PASSWORD=<your-password> -p 5432:5432 postgres:18
```

Pick any password you like — it will only live in your local User Secrets.

### 2. Create the database

```bash
createdb -h localhost -p 5432 -U postgres githubsync
```

Or via `psql`:

```sql
CREATE DATABASE githubsync;
```

### 3. Set the connection string via User Secrets

The repo deliberately ships no developer-specific connection string in `appsettings*.json` (see CLAUDE.md). Local config lives in User Secrets:

```bash
dotnet user-secrets set "ConnectionStrings:AppDb" "Host=localhost;Port=5432;Database=githubsync;Username=postgres;Password=<your-password>" --project src/GithubSync.Api
```

### 4. Apply migrations

```bash
dotnet ef database update --project src/GithubSync.Data --startup-project src/GithubSync.Api
```

`--startup-project` is required because the `AppDbContext` registration and connection string live in the API project.

### 5. Sanity check

```bash
dotnet run --project src/GithubSync.Api
```

The API should start without errors. To confirm the schema landed, connect with `psql` and run `\dt` — you should see the 8 app tables defined by the Initial migration.
