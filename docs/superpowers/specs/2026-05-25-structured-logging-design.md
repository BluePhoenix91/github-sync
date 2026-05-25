# Structured logging design — issue #37

Part of the Observability epic ([#30](https://github.com/BluePhoenix91/github-sync/issues/30)).

## Goal

Install structured JSON logging so the skip-and-log warnings mandated by [CLAUDE.md](../../../CLAUDE.md) (fields `{ Source, ExternalId, Reason }`) stay queryable end-to-end, with standard enrichers (app name, environment, release, machine) attached to every record, and human-readable output in Development.

## Library choice — Serilog

`Serilog.AspNetCore` is preferred over Microsoft's built-in `Console` JSON formatter for two concrete reasons that map directly to the acceptance criteria:

1. **Top-level JSON properties for free.** With `Serilog.Formatting.Compact.CompactJsonFormatter` (CLEF), named template parameters land as siblings of `@t`/`@m`/`@l`. The built-in `Microsoft.Extensions.Logging.Console` JSON formatter nests them under `State` together with `{OriginalFormat}` noise — to get the discrete top-level shape the acceptance criterion requires, we'd have to write a custom `ConsoleFormatter`.
2. **Global enrichers are first-class.** `Enrich.WithProperty("ApplicationName", "...")` and `Enrich.WithMachineName()` attach a property to every event with one line. MEL has no equivalent hook — global stamping would require a custom `ILoggerProvider` wrapper or pushing scopes everywhere.

The cost is one additional package family to track for advisories; Serilog is stable and widely used, and the wiring is ~15 lines.

## Packages and versions

Pinned to the latest stable as of 2026-05-25:

| Package | Version | Notes |
|---|---|---|
| `Serilog.AspNetCore` | `10.0.0` | Host integration, console sink, JSON formatter bundle. |
| `Serilog.Sinks.File` | `7.0.0` | Daily-rolling file sink with retention cap. |
| `Serilog.Enrichers.Environment` | `3.0.1` | For `Enrich.WithMachineName()`. |
| `Serilog.Formatting.Compact` | `3.0.0` | `CompactJsonFormatter` (CLEF). |
| `Sentry.Serilog` | `6.5.0` | Pinned to the same version as the existing `Sentry.AspNetCore` 6.5.0. |

## Sentry integration

Sentry maintainers acknowledge the Serilog + AspNetCore combo is "extremely confusing" ([sentry-dotnet#2884](https://github.com/getsentry/sentry-dotnet/issues/2884)). The de-facto recipe, reconstructed from Sentry's [Serilog docs](https://docs.sentry.io/platforms/dotnet/guides/serilog/) and the `InitializeSdk` flag, is:

- Keep `UseSentry()` as the SDK initializer — it owns ASP.NET-specific enrichment, tracing, the request middleware.
- Add a Serilog sink with `WriteTo.Sentry(o => o.InitializeSdk = false)` so log events flow to Sentry as breadcrumbs/events without re-initializing the SDK.
- **Ordering matters**: `SentryWiring.Configure(builder)` must run before `LoggingWiring.Configure(builder)`, otherwise SDK init is deferred to after Serilog replaces MEL.

We deliberately **do not** use `writeToProviders: true` on `UseSerilog()`. That option has two open bugs — a memory leak via `EventSourceLoggerProvider` ([serilog-aspnetcore#249](https://github.com/serilog/serilog-aspnetcore/issues/249)) and ignored per-provider `LogLevel` filters ([serilog#1628](https://github.com/serilog/serilog/issues/1628)) — and the official `Sentry.Serilog` sink supersedes it.

## Wiring shape

Mirrors the existing [`SentryWiring`](../../../src/GithubSync.Api/Startup/SentryWiring.cs) pattern. A new static class `LoggingWiring` under `src/GithubSync.Api/Startup/`, exposing `Configure(WebApplicationBuilder builder)`.

[`Program.cs`](../../../src/GithubSync.Api/Program.cs) call order:

```csharp
var builder = WebApplication.CreateBuilder(args);

SentryWiring.Configure(builder);     // first — initializes Sentry SDK
LoggingWiring.Configure(builder);    // second — Serilog takes over MEL pipeline

// ... existing DbContext, health check registrations
```

Inside `LoggingWiring.Configure`:

```csharp
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)   // reads the Serilog:* section
        .Enrich.WithProperty("ApplicationName", "github-sync")
        .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
        .Enrich.WithProperty("Release", ReleaseStamp.Current)
        .Enrich.WithMachineName();

    if (context.HostingEnvironment.IsDevelopment())
    {
        configuration.WriteTo.Console();   // human-readable default template
    }
    else
    {
        configuration.WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: "logs/app-.log",
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: 1L * 1024 * 1024 * 1024,   // 1 GB per file
            retainedFileCountLimit: 14,
            shared: true);                                 // safe under IIS recycle overlap
    }

    configuration.WriteTo.Sentry(o => o.InitializeSdk = false);
});
```

`ReleaseStamp.Current` is a small `internal static` helper colocated with `LoggingWiring` in `src/GithubSync.Api/Startup/`. It reads `Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion` once at first access and caches the value. This is the same source the Sentry SDK uses as its `Release` default ([deploy.md → Sentry](../../deploy.md#sentry)), so the Serilog `Release` enricher and Sentry's `release` tag stay aligned automatically. Falls back to `"unknown"` if the attribute is missing (only happens in test rigs).

## Output destinations

| Environment | Sink | Format |
|---|---|---|
| Development | `Console` | Default human-readable template |
| Production | `File` — `C:\Azureflow-QA\GithubSync.API\logs\app-yyyyMMdd.log` | `CompactJsonFormatter` — one JSON line per event |
| All environments | `Sentry` (via `Sentry.Serilog`) | Warnings/errors land as breadcrumbs on subsequent events; errors with exceptions land as events |

**Rolling and retention** are owned by `Serilog.Sinks.File`: daily rolling, 14 files retained, then automatic deletion. No host-side scheduled task needed.

**ANCM `stdoutLog` stays disabled** (the IIS default). It would only capture catastrophic startup failures *before* Serilog initializes — but `RequiredSecrets.Validate` already throws clearly for misconfigured-secret startup failures, and any later startup exception is caught by `Sentry.AspNetCore`'s host integration. Keeping ANCM stdout off avoids a second, uncapped log stream on disk.

**Directory.** `ApplicationPoolIdentity` (the deploy.md default app pool identity) already owns `C:\Azureflow-QA\GithubSync.API\` and its children inherit that ownership, so no ACL change is needed when the `logs/` subdirectory is created at first write.

Off-box log shipping (Seq, Grafana Loki, Better Stack) is intentionally out of scope — captured as a follow-up.

## Operational safeguards

- **Disk cap.** `retainedFileCountLimit: 14` plus daily rolling means worst-case ~14 days of logs on disk. Sentry takes the alert load, so the file sink is for forensics, not real-time alerting; 14 days is comfortable.
- **Per-file size limit + size rolling.** `fileSizeLimitBytes: 1 GB` paired with `rollOnFileSizeLimit: true` means a runaway log loop within a single day hits the size cap and Serilog creates suffix files (`app-yyyyMMdd_001.log`, `_002.log`, …) instead of silently dropping events. The suffix files still count against `retainedFileCountLimit`, so the disk stays bounded. **Note:** without `rollOnFileSizeLimit: true`, Serilog stops accepting events once the size cap is reached — that flag is load-bearing, not cosmetic.
- **IIS recycle overlap.** `shared: true` uses a coordinated `FileStream` so two briefly-coexisting worker processes (during an overlapped app pool recycle) can both write to the current file without one failing to open it. Our deploy CD does a hard `Stop-WebAppPool` rather than an overlapped recycle, but `shared: true` is also the right default for any future operator-initiated recycle from IIS Manager.
- **Write failure handling.** If the `logs/` directory becomes unwritable, `Serilog.Sinks.File` drops events after a short retry. Sentry still receives warnings/errors via the Serilog Sentry sink, so observability degrades to "Sentry only" rather than going dark. Document this in `docs/logging.md`.
- **Sync writes, no async wrapper.** v1 writes synchronously. `Serilog.Sinks.Async` is a known follow-up if production logging volume ever shows up on a profile. Local SSD writes at our expected event rate are not a concern today.
- **No duplicate sinks.** Each environment configures exactly one persistent sink (Console in Dev, File in non-Dev) plus the Sentry sink. Adding a second persistent sink to either branch is a non-goal — duplication doubles disk usage and creates a divergent grep target.

## Standard enrichers

Every log record carries:

| Property | Source | Value example |
|---|---|---|
| `ApplicationName` | constant | `"github-sync"` |
| `Environment` | `IHostEnvironment.EnvironmentName` | `"Production"` |
| `Release` | `Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion` | `"1.0.0+abc1234"` |
| `MachineName` | `Serilog.Enrichers.Environment` | `"WIN-LIGHTSAIL-01"` |

## Field convention — named placeholders

Mandate named template parameters over anonymous-object arguments. The issue's literal example `new { Source, ExternalId, Reason }` only destructures into top-level properties when the template uses `{@properties}` — error-prone.

Required form:

```csharp
logger.LogWarning(
    "Skipped {Source} item {ExternalId}: {Reason}",
    source, externalId, reason);
```

Produces a single JSON line with `Source`, `ExternalId`, `Reason` as discrete top-level properties.

## PII guard rails

Raw GitHub or Azure DevOps payloads stay at `LogLevel.Debug` only. Information-level logs name external identifiers and outcomes, never bodies/titles/comments. This is a **code-review convention** in v1 — not a runtime filter — documented in `docs/logging.md`.

**Known enforcement gap, planned v2 mechanism.** The right runtime safeguard is `Destructure.ByTransforming<T>(payload => new { payload.Id, payload.Status, … })` registered on the `LoggerConfiguration` for each sensitive type. We defer this until issues [#11](https://github.com/BluePhoenix91/github-sync/issues/11) (GitHub fetch) and [#14](https://github.com/BluePhoenix91/github-sync/issues/14)/[#15](https://github.com/BluePhoenix91/github-sync/issues/15) (ADO exporter) introduce the concrete payload types — registering transformers now would be speculative. A follow-up issue lands the first transformer the first time a payload type is touched in a logger call.

`SentryOptions.SendDefaultPii` stays at its default (`false`), matching the current `SentryWiring` setup.

## Configuration files

`ReadFrom.Configuration(context.Configuration)` reads the `Serilog:*` section, **not** the standard MEL `Logging:LogLevel:*` keys. Once Serilog owns the pipeline (no `writeToProviders`), the existing `Logging:LogLevel` keys in [`appsettings.json`](../../../src/GithubSync.Api/appsettings.json) and [`appsettings.Development.json`](../../../src/GithubSync.Api/appsettings.Development.json) become dead config. They are replaced by a Serilog section.

**`appsettings.json` gains:**

```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "Microsoft.EntityFrameworkCore": "Warning",
      "System": "Warning"
    }
  }
}
```

The `Logging:LogLevel` section is **removed** in the same edit, so there is exactly one place to set log levels. `Microsoft.Hosting.Lifetime` stays at `Information` so startup banner lines (which environment, which URLs) still appear — matches typical ASP.NET Core defaults.

**`appsettings.Development.json`** keeps an empty/inherited Serilog section (no per-Dev overrides currently needed; can grow later).

Sinks and enrichers stay in code for symmetry with `SentryWiring`/`HealthEndpoints`/`RequiredSecrets`. Only level minimums live in configuration, because ops want to change those without a redeploy.

## Tests

xUnit, in `tests/GithubSync.Tests/`. Style matches [`SentryWiringTests`](../../../tests/GithubSync.Tests/SentryWiringTests.cs).

**`LoggingWiringTests`** — unit-level, no `WebApplicationFactory`:

- Build a `LoggerConfiguration` via a testable seam exposed on `LoggingWiring` (e.g., `internal static LoggerConfiguration BuildForTests(IHostEnvironment env)`).
- Attach an in-memory `ILogEventSink` collecting `LogEvent`s.
- Log a representative `LogWarning` using the mandated named-placeholder shape.
- Assert: the captured event carries `Source`, `ExternalId`, `Reason`, `ApplicationName`, `Environment`, `Release`, `MachineName` as discrete `Properties` entries.
- For host/env-derived enrichers (`MachineName`, `Release`), assert **non-empty** rather than an exact value — those are machine- and build-specific and would make the test brittle across CI agents.
- For constant/test-input enrichers (`ApplicationName`, `Environment`, `Source`, `ExternalId`, `Reason`), assert exact values.
- Render the same event through `CompactJsonFormatter` and assert all seven property names appear as top-level JSON keys (guards against the destructure regression — values follow the same exact/non-empty split above).

**`LoggingWiringIntegrationTests`** — minimal smoke:

- Spin a `WebApplicationFactory<Program>` and resolve an `ILogger<T>` from the test host. Assert it is backed by Serilog's MEL adapter (`Serilog.Extensions.Logging.SerilogLoggerProvider` appears in the resolved provider chain).
- Assert host startup completes without throwing — exercises the full Sentry-then-Serilog ordering in `Program.cs` end-to-end.

No assertion on stdout content — coupling tests to captured console output is brittle.

## Documentation

**New file: `docs/logging.md`** covers:

- The four standard enrichers and their values.
- The named-placeholder convention with a worked example.
- A `Select-String` recipe for grepping a production log on the Lightsail box: e.g., `Select-String -Path 'C:\Azureflow-QA\GithubSync.API\logs\app-*.log' -Pattern '"Source":"github"' | Select-String '"Reason":"rate-limited"'`.
- The PII rule (raw payloads = `Debug` only) and the v2 `Destructure.ByTransforming` plan.
- The file sink's rolling/retention policy and what happens if the logs directory becomes unwritable (Sentry still gets warnings/errors).
- A pointer to `deploy.md` for where logs physically live on the host.
- Forward note: structured log aggregation (Seq / Loki / Better Stack) is a follow-up; today logs live in the rolling file on the host and Sentry breadcrumbs.

**Update: `docs/deploy.md`** gains:

- A small "Logging" subsection (after "Sentry") pointing at `logging.md`, noting the log path `C:\Azureflow-QA\GithubSync.API\logs\app-yyyyMMdd.log`, the 14-day retention, and that ANCM `stdoutLog` is intentionally disabled.
- A note that the `logs/` subdirectory does not need ACL setup because `ApplicationPoolIdentity` already owns the parent directory tree per the existing deploy convention.
- A line under "App pool settings" calling out that "Disable Overlapped Recycle" should be set to `True` on the `github-sync-api` app pool. This is defence in depth alongside `shared: true` on the file sink — even though our CD does a hard stop/start (not overlapped) and periodic/idle recycles are disabled, an operator-initiated recycle from IIS Manager would otherwise overlap workers. Tracked as a separate host-setup issue under epic #29.

## Out of scope (captured for follow-up)

- A real log aggregator (Seq self-hosted is the leading candidate — file a follow-up issue once `Select-String` on the rolling log file becomes painful).
- Per-request log enrichment (correlation IDs, traceparent, etc.) — depends on distributed tracing which is explicitly out of scope per epic #30.
- Hangfire log integration — lives with the future Hangfire epic.

## Acceptance criteria coverage

| Issue criterion | Covered by |
|---|---|
| `LogWarning` with `{ Source, ExternalId, Reason }` → JSON with three discrete top-level properties | Named-placeholder convention + `CompactJsonFormatter` + `LoggingWiringTests` |
| Standard enrichers on every record | Four `Enrich.*` calls in `LoggingWiring` + test asserts |
| Development → human-readable; Production → JSON | Environment switch in `LoggingWiring.Configure` |
| IIS stdout captures JSON cleanly (verified once IIS site setup lands) | **Reinterpreted**: replaced with `Serilog.Sinks.File` rolling daily JSON file under the deployed app's `logs/` directory. ANCM `stdoutLog` intentionally stays off. End-to-end verification on the Lightsail box deferred to the IIS site issue (#31) per the criterion's own caveat. Acceptance is "a JSON file with one event per line lands on disk in the expected location with the right shape", whether that file is owned by ANCM or by Serilog. |
| `docs/logging.md` documents conventions + grep recipe | New file as scoped above |
