# Logging

How structured logging is wired in `GithubSync.Api`. Companion to the Observability epic ([#30](https://github.com/BluePhoenix91/github-sync/issues/30)).

## Stack

Serilog (`Serilog.AspNetCore`) configured in [`LoggingWiring.Configure`](../src/GithubSync.Api/Startup/LoggingWiring.cs), called from [`Program.cs`](../src/GithubSync.Api/Program.cs) immediately after `SentryWiring.Configure`. Sentry receives log events via the official `Sentry.Serilog` sink with `InitializeSdk = false` — the SDK itself is initialized by `Sentry.AspNetCore`.

## Standard enrichers

Every log record carries these four properties:

| Property | Source | Notes |
|---|---|---|
| `ApplicationName` | constant `"github-sync"` | Distinguishes this app's logs once a second service ships on the same host. |
| `Environment` | `IHostEnvironment.EnvironmentName` | `"Production"` on the Lightsail box, `"Development"` locally. |
| `Release` | `Assembly.GetEntryAssembly().AssemblyInformationalVersion` | Same source Sentry uses for its `release` tag, so the two align. CD stamps this via MSBuild `SourceRevisionId` — see [deploy.md → Sentry](deploy.md#sentry). |
| `MachineName` | `Serilog.Enrichers.Environment` | Host name. Useful once we run more than one box. |

## Field convention — named placeholders

Use named template placeholders, **never** anonymous-object arguments. Named placeholders land as discrete top-level JSON properties; anonymous objects only do so when prefixed with `@` and add fragility.

**Do:**

```csharp
logger.LogWarning(
    "Skipped {Source} item {ExternalId}: {Reason}",
    source, externalId, reason);
```

**Don't:**

```csharp
logger.LogWarning("Skipped {@state}", new { Source = source, ExternalId = externalId, Reason = reason });
```

The skip-and-log convention from [CLAUDE.md](../CLAUDE.md) mandates the field names `Source`, `ExternalId`, `Reason`. Keep them spelled exactly that way so production grep queries stay stable.

## Output destinations

| Environment | Sink | Format |
|---|---|---|
| Development | `Console` | Human-readable, default Serilog template. |
| Production | `File` — `C:\Azureflow-QA\GithubSync.API\logs\app-yyyyMMdd.log` | `CompactJsonFormatter` (CLEF) — one JSON line per event. |
| All environments | `Sentry` (via `Sentry.Serilog`) | Warnings/errors land as Sentry breadcrumbs; errors with exceptions land as Sentry events. |

ASP.NET Core's IIS `stdoutLog` is intentionally disabled — `Serilog.Sinks.File` owns the rolling/retention story.

## Rolling and retention

`Serilog.Sinks.File` is configured with:

- `rollingInterval: Day` — a new file at midnight, named `app-yyyyMMdd.log`.
- `fileSizeLimitBytes: 1 GB` + `rollOnFileSizeLimit: true` — within a single day, a runaway logger creates suffix files (`app-yyyyMMdd_001.log`, `_002.log`, ...) rather than dropping events.
- `retainedFileCountLimit: 14` — older files are automatically deleted. Worst-case disk footprint ≈ 14 GB if every day hit the size cap (in practice, daily volume is in the MB).
- `shared: true` — coordinates the file handle across briefly-coexisting worker processes, e.g., during an IIS overlapped recycle. (We also disable overlapped recycle at the IIS level — see [deploy.md → App pool settings](deploy.md#app-pool-settings) — but `shared: true` is the right default regardless.)

## Grep recipe

On the Lightsail host, all production log lines are queryable with PowerShell's `Select-String`. The JSON shape means structured fields can be matched directly:

```powershell
# All warnings from the GitHub source in the current day's file.
Select-String -Path 'C:\Azureflow-QA\GithubSync.API\logs\app-*.log' -Pattern '"Source":"github"' `
  | Select-String '"@l":"Warning"'

# Specific external id across the whole retention window.
Select-String -Path 'C:\Azureflow-QA\GithubSync.API\logs\app-*.log' -Pattern '"ExternalId":"issue-456"'

# Rate-limit hits in the last seven days.
Get-ChildItem 'C:\Azureflow-QA\GithubSync.API\logs\app-*.log' `
  | Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-7) } `
  | Select-String '"Reason":"rate-limited"'
```

Once `Select-String` becomes the bottleneck, follow-up issue [#53](https://github.com/BluePhoenix91/github-sync/issues/53) introduces a Seq instance on the box with a real query UI.

## PII rule

**Raw GitHub or Azure DevOps payloads stay at `LogLevel.Debug` only.** `Information` and `Warning` logs name external identifiers and outcomes, never bodies, titles, or comments.

This is currently a code-review convention, not a runtime filter. The planned v2 enforcement is `Destructure.ByTransforming<T>(...)` registered on the `LoggerConfiguration` for each sensitive payload type — landed alongside the first concrete payload-bearing logger call ([#11](https://github.com/BluePhoenix91/github-sync/issues/11) / [#14](https://github.com/BluePhoenix91/github-sync/issues/14) / [#15](https://github.com/BluePhoenix91/github-sync/issues/15)).

`SentryOptions.SendDefaultPii` stays at the SDK default (`false`).

## Write-failure behaviour

If the `C:\Azureflow-QA\GithubSync.API\logs\` directory becomes unwritable (full disk, ACL change, missing directory after a manual delete), `Serilog.Sinks.File` retries briefly and then drops further events for that sink. Sentry still receives warnings/errors via the Sentry sink, so observability degrades to "Sentry only" rather than going completely dark.

Check Sentry first if you notice missing file-side log lines; if Sentry is also empty, check the host's disk and ACLs on `logs/`.

## Follow-ups

- [#53](https://github.com/BluePhoenix91/github-sync/issues/53) — evaluate Seq self-hosted as a real log aggregator.
- [#54](https://github.com/BluePhoenix91/github-sync/issues/54) — wrap the file sink in `Serilog.Sinks.Async` if write latency ever shows up on a profile.
- [#55](https://github.com/BluePhoenix91/github-sync/issues/55) — disable IIS overlapped recycle on the `github-sync-api` app pool.
