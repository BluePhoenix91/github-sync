# github-sync architecture

This document captures the intended v1 architecture and project structure for `github-sync`.

## v1 scope and direction

- Sync direction: `GitHub Issues -> Azure DevOps`
- Synchronization mode: incremental (new events only)
- Pipeline model:
  1. Importer ingests source events into a canonical internal store
  2. Exporter replays canonical events to destination systems

## Structural decision

Use a **pipeline-first core** with **platform adapters**:

- Core code is organized by pipeline stage (`Ingestion`, `Egress`) and shared domain concerns.
- Platform-specific implementations live under connector folders (for example `GitHub`, `AzureDevOps`, `Jira`).
- A platform can implement ingest, egress, or both, without changing the core pipeline model.

Why this is the default:

- Matches the existing v1 two-stage architecture decision.
- Keeps canonical event rules and mapping logic separate from API-specific connector code.
- Makes testing simpler (core orchestration tests + connector contract tests).
- Supports future platforms that may be source-only, destination-only, or dual-role.

## Proposed project layout

```text
src/
  GithubSync.Api/                       # Host, DI composition, Hangfire registration, endpoints
  GithubSync.Application/
    Ingestion/                          # Import use-cases (source -> canonical)
    Egress/                             # Export use-cases (canonical -> destination)
    Common/                             # Shared contracts, policies, DTOs
  GithubSync.Domain/
    SyncEvents/                         # Canonical event model and invariants
    Mappings/                           # Workflow/status and identity mapping rules
    Configurations/                     # Sync configuration model
  GithubSync.Infrastructure/
    Persistence/                        # EF Core DbContext, repositories, migrations
    Scheduling/                         # Hangfire jobs and recurring registrations
    Connectors/
      GitHub/
        Ingest/                         # GitHub -> canonical events
        Egress/                         # Optional (if needed later)
      AzureDevOps/
        Ingest/                         # Optional (if needed later)
        Egress/                         # Canonical events -> ADO work items
      Jira/
        Ingest/
        Egress/
    Observability/                      # Logging, Sentry, metrics/tracing
  GithubSync.Contracts/                 # Optional: shared interfaces across projects

tests/
  Unit/
  Integration/
  Contract/
```

## Adapter contract shape

Keep stage contracts in core/application and implement them per platform in infrastructure:

- `IIngestionAdapter`: source platform -> canonical events
- `IEgressAdapter`: canonical events -> destination platform operations

Examples:

- `GitHubIngestionAdapter`
- `AzureDevOpsEgressAdapter`
- `JiraIngestionAdapter`
- `JiraEgressAdapter`

## Operational notes

- Persistence: PostgreSQL + EF Core as application store
- Scheduler/background processing: Hangfire
- Mapping behavior: configuration-driven per sync configuration
- Failure handling: skip-and-log for non-blocking failures
- Dead-letter: persist failed records for replay

## Evolution guidance

- Keep this document as the high-level architecture source of truth.
- Add ADR files later for major changes (for example new pipeline stage, cross-platform dedupe strategy, or connector contract changes).
- Prefer incremental refinements over large structural rewrites.
