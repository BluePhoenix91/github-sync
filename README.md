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

## Pipeline model

The synchronization is split into two stages:

1. Importer: ingest GitHub issue interactions into the internal store.
2. Exporter: replay stored events to Azure DevOps based on active mappings and configuration.

## Intended outcome

A reusable connector service that keeps target trackers populated with realistic, continuously updated public issue activity, so downstream systems and importers can be developed, tested, and demonstrated safely.
