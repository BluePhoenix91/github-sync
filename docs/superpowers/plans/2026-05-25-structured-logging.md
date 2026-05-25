# Structured Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire structured JSON logging into `GithubSync.Api` using Serilog, with standard enrichers on every record, a daily-rolling JSON file in production, human-readable console in Development, and Sentry breadcrumb/event forwarding via the official `Sentry.Serilog` sink.

**Architecture:** A new `LoggingWiring` static class under `src/GithubSync.Api/Startup/` mirrors the existing `SentryWiring` pattern. It calls `builder.Host.UseSerilog(...)` with enrichers (`ApplicationName`, `Environment`, `Release`, `MachineName`) applied globally, a `Console` sink in Development and a `Serilog.Sinks.File` rolling sink (1 GB cap, 14-day retention, `shared: true`) in non-Development, plus `WriteTo.Sentry(o => o.InitializeSdk = false)`. `Program.cs` calls `SentryWiring.Configure` **before** `LoggingWiring.Configure` so the Sentry SDK is initialized by the time the Serilog Sentry sink starts forwarding events.

**Tech Stack:** .NET 10, ASP.NET Core, Serilog.AspNetCore 10.0.0, Serilog.Sinks.File 7.0.0, Serilog.Enrichers.Environment 3.0.1, Serilog.Formatting.Compact 3.0.0, Sentry.Serilog 6.5.0, xUnit, `WebApplicationFactory<Program>` for integration tests.

**Spec:** [docs/superpowers/specs/2026-05-25-structured-logging-design.md](../specs/2026-05-25-structured-logging-design.md)

**Issue:** [#37](https://github.com/BluePhoenix91/github-sync/issues/37)

---

## File structure

**Create:**
- `src/GithubSync.Api/Startup/ReleaseStamp.cs` — `internal static` helper that resolves `AssemblyInformationalVersion` once and caches.
- `src/GithubSync.Api/Startup/LoggingWiring.cs` — public `Configure(WebApplicationBuilder)` plus `internal static` seams `ApplyEnrichers` and `ApplyDestinations` for tests.
- `tests/GithubSync.Tests/ReleaseStampTests.cs` — unit test for the helper.
- `tests/GithubSync.Tests/LoggingWiringTests.cs` — Serilog enricher/template/format assertions via an in-memory sink.
- `tests/GithubSync.Tests/LoggingWiringIntegrationTests.cs` — `WebApplicationFactory<Program>` smoke that asserts Serilog is the active logger provider and host startup completes.
- `docs/logging.md` — field conventions, named-placeholder rule, grep recipe, PII rule, retention behaviour, follow-ups.

**Modify:**
- `src/GithubSync.Api/GithubSync.Api.csproj` — add five NuGet packages.
- `src/GithubSync.Api/Program.cs` — call `LoggingWiring.Configure(builder)` immediately after `SentryWiring.Configure(builder)`.
- `src/GithubSync.Api/appsettings.json` — remove `Logging:LogLevel`, add `Serilog:MinimumLevel` section.
- `src/GithubSync.Api/appsettings.Development.json` — remove `Logging:LogLevel` (no Dev-specific Serilog overrides needed yet).
- `docs/deploy.md` — new "Logging" subsection after "Sentry"; add a "Disable Overlapped Recycle" line under "App pool settings".

**Do not modify:**
- EF Core migrations under `src/GithubSync.Data/Migrations/`.
- Existing `SentryWiring`, `RequiredSecrets`, `HealthEndpoints` files (none of their logic changes).

---

## Task 1: Add Serilog and Sentry.Serilog NuGet packages

**Files:**
- Modify: `src/GithubSync.Api/GithubSync.Api.csproj`

- [ ] **Step 1: Add the five package references**

In `src/GithubSync.Api/GithubSync.Api.csproj`, replace the existing `<ItemGroup>` block that contains `<PackageReference>` entries with:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.8">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="10.0.8" />
    <PackageReference Include="Sentry.AspNetCore" Version="6.5.0" />
    <PackageReference Include="Sentry.Serilog" Version="6.5.0" />
    <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
    <PackageReference Include="Serilog.Enrichers.Environment" Version="3.0.1" />
    <PackageReference Include="Serilog.Formatting.Compact" Version="3.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
  </ItemGroup>
```

- [ ] **Step 2: Restore and build**

Run: `dotnet build src/GithubSync.Api/GithubSync.Api.csproj -c Debug`
Expected: build succeeds, output shows the new packages resolved.

- [ ] **Step 3: Commit**

```bash
git add src/GithubSync.Api/GithubSync.Api.csproj
git commit -m "chore: add Serilog + Sentry.Serilog package references for #37"
```

---

## Task 2: Create ReleaseStamp helper (TDD)

**Files:**
- Create: `tests/GithubSync.Tests/ReleaseStampTests.cs`
- Create: `src/GithubSync.Api/Startup/ReleaseStamp.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/GithubSync.Tests/ReleaseStampTests.cs`:

```csharp
using GithubSync.Api.Startup;

namespace GithubSync.Tests;

public class ReleaseStampTests
{
    [Fact]
    public void Current_returns_non_empty_value()
    {
        var stamp = ReleaseStamp.Current;

        Assert.False(string.IsNullOrWhiteSpace(stamp));
    }

    [Fact]
    public void Current_returns_same_value_on_repeated_access()
    {
        var first = ReleaseStamp.Current;
        var second = ReleaseStamp.Current;

        Assert.Same(first, second);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~ReleaseStampTests"`
Expected: FAIL with compilation error "The type or namespace name 'ReleaseStamp' does not exist".

- [ ] **Step 3: Write the helper**

Create `src/GithubSync.Api/Startup/ReleaseStamp.cs`:

```csharp
using System.Reflection;

namespace GithubSync.Api.Startup;

internal static class ReleaseStamp
{
    private static readonly Lazy<string> Cached = new(Resolve);

    public static string Current => Cached.Value;

    private static string Resolve()
    {
        var attribute = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return string.IsNullOrWhiteSpace(attribute?.InformationalVersion)
            ? "unknown"
            : attribute.InformationalVersion;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~ReleaseStampTests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/GithubSync.Api/Startup/ReleaseStamp.cs tests/GithubSync.Tests/ReleaseStampTests.cs
git commit -m "feat: add ReleaseStamp helper for Serilog Release enricher (#37)"
```

---

## Task 3: Create LoggingWiring with the first enricher + test (TDD)

**Files:**
- Create: `tests/GithubSync.Tests/LoggingWiringTests.cs`
- Create: `src/GithubSync.Api/Startup/LoggingWiring.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/GithubSync.Tests/LoggingWiringTests.cs`:

```csharp
using GithubSync.Api.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace GithubSync.Tests;

public class LoggingWiringTests
{
    [Fact]
    public void ApplyEnrichers_attaches_ApplicationName_property()
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogWarning("any message");

        var captured = Assert.Single(sink.Events);
        Assert.Equal("\"github-sync\"", captured.Properties["ApplicationName"].ToString());
    }

    internal static (ILogger<LoggingWiringTests> logger, CapturingSink sink) BuildTestLogger(string envName)
    {
        var env = new TestHostEnvironment(envName);
        var sink = new CapturingSink();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose();
        LoggingWiring.ApplyEnrichers(serilog, env);
        serilog.WriteTo.Sink(sink);

        var factory = new SerilogLoggerFactory(serilog.CreateLogger(), dispose: true);
        return (factory.CreateLogger<LoggingWiringTests>(), sink);
    }

    internal sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    internal sealed class TestHostEnvironment(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~LoggingWiringTests"`
Expected: FAIL with compilation error "The type or namespace name 'LoggingWiring' does not exist".

- [ ] **Step 3: Write the minimal LoggingWiring with ApplyEnrichers**

Create `src/GithubSync.Api/Startup/LoggingWiring.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Serilog;

namespace GithubSync.Api.Startup;

public static class LoggingWiring
{
    internal const string ApplicationNameProperty = "github-sync";

    internal static void ApplyEnrichers(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        configuration.Enrich.WithProperty("ApplicationName", ApplicationNameProperty);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~LoggingWiringTests"`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/GithubSync.Api/Startup/LoggingWiring.cs tests/GithubSync.Tests/LoggingWiringTests.cs
git commit -m "feat: add LoggingWiring with ApplicationName enricher (#37)"
```

---

## Task 4: Add Environment, Release, MachineName enrichers (TDD)

**Files:**
- Modify: `tests/GithubSync.Tests/LoggingWiringTests.cs`
- Modify: `src/GithubSync.Api/Startup/LoggingWiring.cs`

- [ ] **Step 1: Extend the test for the remaining three enrichers**

In `tests/GithubSync.Tests/LoggingWiringTests.cs`, add three new tests inside the class:

```csharp
[Fact]
public void ApplyEnrichers_attaches_Environment_property_from_host()
{
    var (logger, sink) = BuildTestLogger(Environments.Production);

    logger.LogInformation("any");

    var captured = Assert.Single(sink.Events);
    Assert.Equal("\"Production\"", captured.Properties["Environment"].ToString());
}

[Fact]
public void ApplyEnrichers_attaches_Release_property_non_empty()
{
    var (logger, sink) = BuildTestLogger(Environments.Production);

    logger.LogInformation("any");

    var captured = Assert.Single(sink.Events);
    var release = captured.Properties["Release"].ToString();
    Assert.False(string.IsNullOrWhiteSpace(release));
    Assert.NotEqual("\"\"", release);
}

[Fact]
public void ApplyEnrichers_attaches_MachineName_property_non_empty()
{
    var (logger, sink) = BuildTestLogger(Environments.Production);

    logger.LogInformation("any");

    var captured = Assert.Single(sink.Events);
    var machineName = captured.Properties["MachineName"].ToString();
    Assert.False(string.IsNullOrWhiteSpace(machineName));
    Assert.NotEqual("\"\"", machineName);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~LoggingWiringTests"`
Expected: 3 new tests FAIL with `KeyNotFoundException` on `captured.Properties["..."]`.

- [ ] **Step 3: Implement the remaining enrichers**

In `src/GithubSync.Api/Startup/LoggingWiring.cs`, replace the body of `ApplyEnrichers` with:

```csharp
    internal static void ApplyEnrichers(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        configuration
            .Enrich.WithProperty("ApplicationName", ApplicationNameProperty)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            .Enrich.WithProperty("Release", ReleaseStamp.Current)
            .Enrich.WithMachineName();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~LoggingWiringTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/GithubSync.Api/Startup/LoggingWiring.cs tests/GithubSync.Tests/LoggingWiringTests.cs
git commit -m "feat: add Environment/Release/MachineName Serilog enrichers (#37)"
```

---

## Task 5: Test that named placeholders produce discrete properties

**Files:**
- Modify: `tests/GithubSync.Tests/LoggingWiringTests.cs`

This task adds an assertion-only test — no production-code change. It guards the named-placeholder convention from the spec: `logger.LogWarning("Skipped {Source} ...", source, ...)` must produce `Source`, `ExternalId`, `Reason` as discrete Serilog properties.

- [ ] **Step 1: Add the named-placeholder test**

In `tests/GithubSync.Tests/LoggingWiringTests.cs`, add:

```csharp
[Fact]
public void Named_placeholder_template_produces_discrete_top_level_properties()
{
    using var (logger, sink) = BuildTestLogger(Environments.Production);

    logger.LogWarning(
        "Skipped {Source} item {ExternalId}: {Reason}",
        "github", "issue-123", "rate-limited");

    var captured = Assert.Single(sink.Events);
    Assert.Equal("\"github\"", captured.Properties["Source"].ToString());
    Assert.Equal("\"issue-123\"", captured.Properties["ExternalId"].ToString());
    Assert.Equal("\"rate-limited\"", captured.Properties["Reason"].ToString());
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~LoggingWiringTests"`
Expected: 5 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/GithubSync.Tests/LoggingWiringTests.cs
git commit -m "test: assert named-placeholder template emits discrete properties (#37)"
```

---

## Task 6: Test CompactJsonFormatter emits all seven property names as top-level JSON keys

**Files:**
- Modify: `tests/GithubSync.Tests/LoggingWiringTests.cs`

This task adds the JSON-shape assertion that mirrors the acceptance criterion. Renders a single `LogEvent` through `CompactJsonFormatter` and parses the resulting JSON line.

- [ ] **Step 1: Add the JSON-shape test**

At the top of `tests/GithubSync.Tests/LoggingWiringTests.cs`, add these `using` lines if not already present:

```csharp
using System.Text.Json;
using Serilog.Formatting.Compact;
```

Then add this test method inside the class:

```csharp
[Fact]
public void CompactJsonFormatter_renders_all_seven_property_names_as_top_level_keys()
{
    using var (logger, sink) = BuildTestLogger(Environments.Production);

    logger.LogWarning(
        "Skipped {Source} item {ExternalId}: {Reason}",
        "github", "issue-123", "rate-limited");

    var captured = Assert.Single(sink.Events);

    var formatter = new CompactJsonFormatter();
    var writer = new StringWriter();
    formatter.Format(captured, writer);

    using var doc = JsonDocument.Parse(writer.ToString());
    var root = doc.RootElement;

    Assert.True(root.TryGetProperty("ApplicationName", out _), "ApplicationName missing");
    Assert.True(root.TryGetProperty("Environment", out _), "Environment missing");
    Assert.True(root.TryGetProperty("Release", out _), "Release missing");
    Assert.True(root.TryGetProperty("MachineName", out _), "MachineName missing");
    Assert.True(root.TryGetProperty("Source", out var source) && source.GetString() == "github", "Source missing or wrong");
    Assert.True(root.TryGetProperty("ExternalId", out var extId) && extId.GetString() == "issue-123", "ExternalId missing or wrong");
    Assert.True(root.TryGetProperty("Reason", out var reason) && reason.GetString() == "rate-limited", "Reason missing or wrong");
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~LoggingWiringTests"`
Expected: 6 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/GithubSync.Tests/LoggingWiringTests.cs
git commit -m "test: assert CompactJsonFormatter emits properties as top-level keys (#37)"
```

---

## Task 7: Add ApplyDestinations and the public Configure method

**Files:**
- Modify: `src/GithubSync.Api/Startup/LoggingWiring.cs`

This task adds the production-only sinks (Console/File/Sentry) behind the `ApplyDestinations` seam, and a public `Configure(WebApplicationBuilder)` method that ties enrichers + destinations into `UseSerilog`. Tests don't call `ApplyDestinations` directly — Task 10 covers the integration smoke.

- [ ] **Step 1: Extend LoggingWiring with ApplyDestinations and Configure**

Replace the entire contents of `src/GithubSync.Api/Startup/LoggingWiring.cs` with:

```csharp
using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;
using Serilog.Formatting.Compact;

namespace GithubSync.Api.Startup;

public static class LoggingWiring
{
    internal const string ApplicationNameProperty = "github-sync";

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, _, configuration) =>
        {
            configuration.ReadFrom.Configuration(context.Configuration);
            ApplyEnrichers(configuration, context.HostingEnvironment);
            ApplyDestinations(configuration, context.HostingEnvironment);
        });
    }

    internal static void ApplyEnrichers(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        configuration
            .Enrich.WithProperty("ApplicationName", ApplicationNameProperty)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            .Enrich.WithProperty("Release", ReleaseStamp.Current)
            .Enrich.WithMachineName();
    }

    internal static void ApplyDestinations(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            configuration.WriteTo.Console();
        }
        else
        {
            configuration.WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: "logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 1L * 1024 * 1024 * 1024,
                retainedFileCountLimit: 14,
                shared: true);
        }

        configuration.WriteTo.Sentry(o => o.InitializeSdk = false);
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/GithubSync.Api/GithubSync.Api.csproj -c Debug`
Expected: build succeeds with zero warnings.

- [ ] **Step 3: Run all existing tests to confirm no regression**

Run: `dotnet test`
Expected: every test still passes (LoggingWiring isn't wired into `Program.cs` yet, so existing tests are unaffected).

- [ ] **Step 4: Commit**

```bash
git add src/GithubSync.Api/Startup/LoggingWiring.cs
git commit -m "feat: add LoggingWiring.Configure with File/Sentry destinations (#37)"
```

---

## Task 8: Wire LoggingWiring into Program.cs (after SentryWiring)

**Files:**
- Modify: `src/GithubSync.Api/Program.cs`

Ordering matters: `SentryWiring.Configure` initializes the Sentry SDK, which the Serilog Sentry sink relies on at log-emit time. The Serilog `UseSerilog` call also silences MEL providers by default, so it must run after `UseSentry` has set up its hooks.

- [ ] **Step 1: Modify Program.cs**

Replace lines 5–7 of `src/GithubSync.Api/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

SentryWiring.Configure(builder);
```

with:

```csharp
var builder = WebApplication.CreateBuilder(args);

SentryWiring.Configure(builder);
LoggingWiring.Configure(builder);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/GithubSync.Api/GithubSync.Api.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 3: Run all tests to verify no regression**

Run: `dotnet test`
Expected: every existing test still passes. `HealthFactory` already sets `SENTRY_DSN=""`, which both `SentryWiring` and the Serilog Sentry sink tolerate.

- [ ] **Step 4: Commit**

```bash
git add src/GithubSync.Api/Program.cs
git commit -m "feat: wire LoggingWiring into Program.cs after SentryWiring (#37)"
```

---

## Task 9: Migrate appsettings.json from Logging:LogLevel to Serilog:MinimumLevel

**Files:**
- Modify: `src/GithubSync.Api/appsettings.json`
- Modify: `src/GithubSync.Api/appsettings.Development.json`

Once Serilog owns the MEL pipeline, the `Logging:LogLevel` keys are dead — Serilog reads `Serilog:*` via `ReadFrom.Configuration`.

- [ ] **Step 1: Rewrite appsettings.json**

Replace the entire contents of `src/GithubSync.Api/appsettings.json` with:

```json
{
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
  },
  "AllowedHosts": "*"
}
```

- [ ] **Step 2: Rewrite appsettings.Development.json**

Replace the entire contents of `src/GithubSync.Api/appsettings.Development.json` with:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information"
    }
  }
}
```

The Development override keeps the door open for per-Dev minimum-level tweaks later without leaving a dead empty file.

- [ ] **Step 3: Run all tests to verify no regression**

Run: `dotnet test`
Expected: every test still passes.

- [ ] **Step 4: Verify the API starts locally**

Run: `dotnet run --project src/GithubSync.Api`
Expected: the startup banner appears (Serilog text format in Development), the app listens, and no exception is thrown. Hit Ctrl-C to stop.

If `ConnectionStrings:AppDb` is not set via user-secrets, the warning logged by `RequiredSecrets.Validate` should now appear as a human-readable Serilog console line (not the previous default MEL line).

- [ ] **Step 5: Commit**

```bash
git add src/GithubSync.Api/appsettings.json src/GithubSync.Api/appsettings.Development.json
git commit -m "chore: migrate log-level config to Serilog:MinimumLevel (#37)"
```

---

## Task 10: Add LoggingWiringIntegrationTests smoke

**Files:**
- Create: `tests/GithubSync.Tests/LoggingWiringIntegrationTests.cs`

Verifies the end-to-end wiring through `WebApplicationFactory<Program>`: that the host builds, that the resolved `ILogger<T>` is backed by Serilog, and that the Sentry-then-Serilog ordering doesn't trip startup.

- [ ] **Step 1: Write the integration smoke test**

Create `tests/GithubSync.Tests/LoggingWiringIntegrationTests.cs`:

```csharp
using GithubSync.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace GithubSync.Tests;

public class LoggingWiringIntegrationTests
{
    [Fact]
    public void Host_resolves_ILogger_backed_by_Serilog()
    {
        using var factory = new TestFactory();

        var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<LoggingWiringIntegrationTests>();

        // Smoke: logging through the resolved factory must not throw,
        // and Serilog must be the registered provider once UseSerilog has run.
        logger.LogInformation("Integration smoke message");

        var providers = factory.Services.GetServices<ILoggerProvider>().ToList();
        Assert.Contains(providers, p => p is SerilogLoggerProvider);
    }

    [Fact]
    public void Host_starts_without_throwing()
    {
        using var factory = new TestFactory();

        // Forcing CreateClient builds the host end-to-end; if Sentry/Serilog
        // ordering broke, this would surface here.
        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    private sealed class TestFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureHostConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AppDb"] = "Host=placeholder;Database=placeholder;Username=placeholder;Password=placeholder",
                    [SentryWiring.DsnConfigKey] = "",
                });
            });
            return base.CreateHost(builder);
        }
    }
}
```

- [ ] **Step 2: Run integration tests to verify they pass**

Run: `dotnet test tests/GithubSync.Tests --filter "FullyQualifiedName~LoggingWiringIntegrationTests"`
Expected: 2 passed.

- [ ] **Step 3: Run the full test suite to confirm no regression**

Run: `dotnet test`
Expected: every test in the solution passes (existing + new).

- [ ] **Step 4: Commit**

```bash
git add tests/GithubSync.Tests/LoggingWiringIntegrationTests.cs
git commit -m "test: add LoggingWiring integration smoke covering Serilog wiring (#37)"
```

---

## Task 11: Write docs/logging.md

**Files:**
- Create: `docs/logging.md`

- [ ] **Step 1: Write the logging conventions doc**

Create `docs/logging.md`:

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add docs/logging.md
git commit -m "docs: add logging conventions and grep recipe (#37)"
```

---

## Task 12: Update docs/deploy.md (Logging subsection + overlapped recycle note)

**Files:**
- Modify: `docs/deploy.md`

- [ ] **Step 1: Add "Disable Overlapped Recycle" line under App pool settings**

In `docs/deploy.md`, find the `### App pool settings` subsection (around line 63). Inside the bulleted list of settings, after the "**Specific Times** (under "Recycling"): leave empty. No fixed-time recycle in v1." line, add:

```markdown
- **Disable Overlapped Recycle** (under "Recycling"): `True`. Default IIS behaviour keeps two worker processes briefly alive across a recycle, which complicates reasoning about file locks (relevant once the Serilog file sink lands — see [docs/logging.md](logging.md)). Our deploy CD does a hard `Stop-WebAppPool` / `Start-WebAppPool` rather than an overlapped recycle, but an operator-initiated recycle from IIS Manager would default to overlapping. Tracking host-side flip under [#55](https://github.com/BluePhoenix91/github-sync/issues/55).
```

- [ ] **Step 2: Add a Logging subsection after Sentry**

In `docs/deploy.md`, find the `## Sentry` section (around line 304) and the `## HTTPS / certificate` heading that follows it. Insert this new `## Logging` section between the two:

```markdown
## Logging

Structured logging conventions, field shape, and the grep recipe live in [docs/logging.md](logging.md). This section captures the host-side specifics.

- **Where logs land on disk:** `C:\Azureflow-QA\GithubSync.API\logs\app-yyyyMMdd.log`, one JSON line per event. Suffix files (`app-yyyyMMdd_001.log`, ...) appear if a single day hits the 1 GB per-file cap.
- **Retention:** 14 files kept, older files deleted automatically by `Serilog.Sinks.File`.
- **No setup needed on the host.** The `logs/` subdirectory is created on first write. `ApplicationPoolIdentity` already owns the parent `C:\Azureflow-QA\GithubSync.API\` directory under the existing deploy convention, so child directories inherit write access without an explicit ACL grant.
- **ANCM `stdoutLog` is intentionally disabled** (the IIS default). The app's Serilog file sink owns the rolling/retention story. `RequiredSecrets.Validate` covers misconfigured-secret startup failures with a clear thrown message, and any later startup exception goes through `Sentry.AspNetCore`. Leaving ANCM stdout off avoids a second uncapped log stream.

If logs stop appearing on disk: check Sentry for events first — the Serilog Sentry sink is independent of the file sink, so Sentry being silent too narrows the cause to the app itself rather than the file system.
```

- [ ] **Step 3: Commit**

```bash
git add docs/deploy.md
git commit -m "docs: deploy.md notes for Serilog file sink and overlapped recycle (#37)"
```

---

## Task 13: Run /simplify against the branch diff

The repo etiquette in [CLAUDE.md](../../../CLAUDE.md) requires `/simplify` against the branch diff before pushing any PR that touches `.cs` files.

- [ ] **Step 1: Invoke /simplify**

Run the `/simplify` slash command on the branch diff. Address each actionable finding (apply the suggested edit + commit, one finding per commit). If you decide to skip a finding, capture it in a notes file or directly in the PR description with a one-line reason.

- [ ] **Step 2: Re-run the test suite after applying simplification fixes**

Run: `dotnet test`
Expected: every test still passes.

- [ ] **Step 3: Commit any remaining simplification fixes**

If individual `/simplify` findings were not committed inline, batch the remainder:

```bash
git add -A
git commit -m "refactor: address /simplify findings (#37)"
```

---

## Task 14: Final verification and PR

- [ ] **Step 1: Run the full test suite one final time**

Run: `dotnet test`
Expected: all tests in the solution pass.

- [ ] **Step 2: Run a clean build to confirm zero warnings**

Run: `dotnet build -c Release`
Expected: build succeeds with zero warnings. New code must be warning-clean.

- [ ] **Step 3: Manual smoke locally**

Run: `dotnet run --project src/GithubSync.Api`
Verify:
- The console shows the human-readable Serilog format (not the default MEL format).
- The startup logs include the `RequiredSecrets.Validate` warning (if secrets are not set via user-secrets), formatted by Serilog.
- Hitting `/health/live` returns 200 and produces a Serilog-formatted request line in the console.
- Ctrl-C cleanly shuts down with a host-stopped log line.

- [ ] **Step 4: Push branch and open PR**

Push the branch:

```bash
git push -u origin <branch-name>
```

Then open the PR. Title (Conventional Commits, used as the squash-merge title):

```
feat: structured logging with Serilog + Sentry breadcrumbs
```

Body:

```
Closes #37.

Wires Serilog into GithubSync.Api per the design at docs/superpowers/specs/2026-05-25-structured-logging-design.md.

## Highlights

- `LoggingWiring.Configure(builder)` in src/GithubSync.Api/Startup/, mirrors the SentryWiring pattern.
- Enrichers: `ApplicationName`, `Environment`, `Release`, `MachineName`.
- Output: Console in Development; `Serilog.Sinks.File` (daily roll, 1 GB / file, 14 retained, `shared: true`) in non-Development.
- Sentry log forwarding via the official `Sentry.Serilog` sink with `InitializeSdk = false`.
- `appsettings.{json,Development.json}` migrated from dead `Logging:LogLevel` keys to `Serilog:MinimumLevel`.
- New tests: LoggingWiringTests (enricher + named-placeholder + CLEF JSON shape), LoggingWiringIntegrationTests (Serilog provider resolution + host startup smoke), ReleaseStampTests.
- New docs: docs/logging.md (conventions, grep recipe, PII rule, retention/failure behaviour) + deploy.md updates (Logging subsection + Disable Overlapped Recycle note).

## Follow-ups filed under the Observability epic (#30) and Host epic (#29)

- #53 — Seq self-hosted as a structured log aggregator.
- #54 — `Serilog.Sinks.Async` wrapper if write latency ever shows up on a profile.
- #55 — host-side flip of "Disable Overlapped Recycle" on the github-sync-api app pool.

## /simplify notes

[List any /simplify findings that were intentionally skipped, each with a one-line reason. Remove this section if every finding was addressed.]
```

- [ ] **Step 5: Verify the issue lifecycle automation moves #37 to In Review**

After the PR opens, the project board should move issue #37 to the "In Review" column automatically. If it doesn't, set it manually per the user's [issue lifecycle automation](https://github.com/BluePhoenix91) workflow.

---

## Notes for the implementer

- **Do not run `dotnet ef migrations add`.** No entity changes in this plan — see the user's standing rule about migrations requiring explicit approval.
- **Use `dotnet` from the existing toolchain.** The csproj targets `net10.0`; the SDK is already pinned per `.github/workflows/ci.yml`.
- **Test ordering matters in `LoggingWiringTests`.** All tests build their own `LoggerConfiguration` via `BuildTestLogger`, which calls `CreateLogger()` per test — there is no shared `Log.Logger` static state to leak between tests, but if you change that, beware xUnit's parallel test runner.
- **`SerilogLoggerProvider` namespace** is `Serilog.Extensions.Logging` (transitive dep of `Serilog.AspNetCore`, no separate package reference needed).
- **`Sentry` namespace import** is required in `LoggingWiring.cs` for the `o.InitializeSdk` option type; the `Sentry.Serilog` package brings it.
