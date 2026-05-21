# Issue Workflow

This file defines how work must be created and moved through GitHub Issues in this repository.

## Core Principles

- Every implementation change starts from an issue.
- Every execution issue must have exactly one parent epic.
- Keep work small. Prefer narrowly scoped child issues over broad tasks.
- Issue acceptance criteria define what "done" means.

## Issue Types

- `type:epic`: parent coordination issue that groups child issues.
- `type:feature`: implementation task.
- `type:spike`: short research/de-risk task with explicit output.

## Required Labels

Each child issue must have:

- one `area:*` label (for example `area:data-model`, `area:ingestion`, `area:egress`)
- one `priority:*` label (`priority:p0` or `priority:p1`)
- one `type:*` label (`type:feature` or `type:spike`)

## Parent/Child Relationship Rules

- Child issues must use GitHub's native parent relationship (sub-issue model).
- Parent epics own planning and roll-up tracking.
- Child issues own executable scope and acceptance criteria.
- Do not encode parent links only in free text when native relationship exists.

## Lifecycle

`Backlog -> Ready -> In Progress -> Review -> Done`

Status can be represented via GitHub Project columns/fields or labels when no project is used.

## Definition of Ready

Before moving an issue to `In Progress`:

- Parent epic relation exists.
- Required labels are set.
- Scope is small enough to complete in one focused PR.
- Acceptance criteria are clear and testable.

## Definition of Done

Before closing an issue:

- Acceptance criteria are satisfied.
- Relevant tests/build checks pass.
- PR references the issue (for example `Closes #123`).
- If part of an epic, parent progress is updated.

## Post-merge cleanup

Once the PR closing an issue is merged:

- Verify the issue is closed and its project Status is `Done`. Automation usually handles this; verify, don't assume.
- Delete the branch on the remote (`git push origin --delete <branch>`).
- Switch the local working copy back to `main` and fast-forward (`git checkout main && git pull --ff-only`).
- Delete the local branch. After a squash-merge, `git branch -d` will refuse with "not fully merged" because the squash creates a new commit — confirm `main` contains the merged content first, then use `git branch -D <branch>`.

Keeps the branch list scoped to in-flight work and prevents stale local branches from drifting against `main`.

## Splitting Rule

If an issue grows to include multiple independent concerns, split it into smaller child issues and keep the original as a coordination issue or close it as superseded.
