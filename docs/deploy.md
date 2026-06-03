# Deploy

How the github-sync API is hosted. Companion to the Host + CI/CD epic ([#29](https://github.com/BluePhoenix91/github-sync/issues/29)).

## Scope of this document

What's covered:

- IIS site, app pool, bindings, and environment configuration on the Lightsail host ([#31](https://github.com/BluePhoenix91/github-sync/issues/31)).
- Database and role provisioning on the colocated Postgres instance ([#33](https://github.com/BluePhoenix91/github-sync/issues/33)).
- Secrets wiring — Sentry DSN, GitHub PAT, ADO PAT, DB connection string ([#35](https://github.com/BluePhoenix91/github-sync/issues/35)).
- HTTPS topology and cert source.

What is **not** covered yet — separate child issues under [#29](https://github.com/BluePhoenix91/github-sync/issues/29) will fill these in:

- Hangfire keep-alive strategy. The dashboard authorization filter and recurring job registration both landed in #70 — see [Ingestion](#ingestion) below.

CI lives in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) (build + test on PR and on push to `main`). CD is described below.

## Topology

```
Internet
   │
   ▼
DNS: bart.consulting (Cloudflare)
   │   github-sync.bart.consulting
   ▼
AWS Lightsail (Windows Server)
   │   IIS
   │     ├── app pool: github-sync-api  (No Managed Code, ApplicationPoolIdentity)
   │     └── site:     github-sync-api  →  C:\Azureflow-QA\GithubSync.API
   │   PostgreSQL (colocated; consumed by this app, not exposed externally)
   ▼
ASP.NET Core app (Kestrel, out-of-process via ANCM)
```

**About the DNS configuration:** the records were originally set up to match a sibling site's pattern rather than being designed specifically for github-sync. github-sync is a backend API and doesn't benefit from any of Cloudflare's path/host-rewriting features, so a future cleanup pass can normalise the records to a simpler configuration. Treat the current DNS as the documented baseline, not as a target — check the Cloudflare panel for the live values.

## Prerequisites on the host

- **Windows Server** (already in place, hosting other IIS sites).
- **IIS** with the Static Content, ASP.NET Core Module v2 (ANCM), and Default Document features (all included by the Hosting Bundle below).
- **ASP.NET Core 10 Hosting Bundle** installed. Verify with `dotnet --info` — output should list a `Microsoft.AspNetCore.App 10.x` runtime.
- **PostgreSQL 15+** running on the box. The `Database` section below carves out a dedicated DB and role on this existing server.
- **win-acme** installed for HTTPS cert issuance and renewal.

If `dotnet --info` does not show the .NET 10 runtime, download the Hosting Bundle from <https://dotnet.microsoft.com/download/dotnet/10.0> and install. The bundle includes the runtime, the ASP.NET Core runtime, and the IIS module.

## IIS site

| | Value |
|---|---|
| Site name | `github-sync-api` |
| App pool name | `github-sync-api` |
| Physical path | `C:\Azureflow-QA\GithubSync.API` |
| Bindings | `http :80 github-sync.bart.consulting`, `https :443 github-sync.bart.consulting` |

The `C:\Azureflow-QA\<ProjectName>` path follows the existing convention on this host.

### App pool settings

Open the app pool in IIS Manager → Advanced Settings:

- **.NET CLR version**: `No Managed Code`. The app runs out-of-process via ANCM; the pool itself does not load any CLR.
- **Identity**: `ApplicationPoolIdentity`.
- **Idle Time-out (minutes)**: `0`. The default of 20 minutes silently suspends the worker process when no requests arrive — which would kill background jobs once Hangfire lands. Setting it to `0` keeps the worker alive indefinitely.
- **Regular Time Interval (minutes)** (under "Recycling"): `0`. The default of 1740 minutes (~29 hours) causes the worker to recycle at drifting times of day. Disabling it removes that variability; the process runs until deploy or reboot.
- **Specific Times** (under "Recycling"): leave empty. No fixed-time recycle in v1.
- **Disable Overlapped Recycle** (under "Recycling"): `True`. Default IIS behaviour keeps two worker processes briefly alive across a recycle, which complicates reasoning about file locks (relevant once the Serilog file sink lands — see [docs/logging.md](logging.md)). Our deploy CD does a hard `Stop-WebAppPool` / `Start-WebAppPool` rather than an overlapped recycle, but an operator-initiated recycle from IIS Manager would default to overlapping. Tracking host-side flip under [#55](https://github.com/BluePhoenix91/github-sync/issues/55).

If memory pressure ever becomes a real problem on this box, "Private Memory Limit" is the lever to reach for — not the periodic recycle.

### Environment variables

- **`ASPNETCORE_ENVIRONMENT=Production`** — set as a **system-wide** environment variable on the host. The host serves this single environment, so per-pool scoping isn't needed and the existing setting is left in place.
- **`ASPNETCORE_URLS`** — **not set anywhere**. Under the IIS in-process ANCM hosting model, IIS hands the port to Kestrel via internal environment variables (`ASPNETCORE_PORT`, `ASPNETCORE_TOKEN`). Setting `ASPNETCORE_URLS` would compete with that and is a known source of misconfiguration. Resist the urge to add it "just in case".
- **App-pool-scoped environment variables**: wired by the CD workflow on every deploy from GitHub Actions secrets — see [Secrets](#secrets) below.

## Database

The app uses a dedicated database and role on the **existing** PostgreSQL server already running on this host. We do not stand up a separate Postgres instance and we do not use Lightsail managed PostgreSQL.

| | Value |
|---|---|
| Server | `localhost:5432` (colocated with IIS on the Lightsail box) |
| Database | `github_sync` |
| Role | `github_sync` (owns the database) |
| Connection-string shape | `Host=localhost;Port=5432;Database=github_sync;Username=github_sync;Password=<from env>` |

The password is not stored in this repo. It is set on the host as the `ConnectionStrings__AppDb` environment variable on the `github-sync-api` app pool — wired in by the secrets child issue under [#29](https://github.com/BluePhoenix91/github-sync/issues/29).

`sslmode` is intentionally omitted: traffic stays on `localhost` so a TLS handshake adds cost without crossing any trust boundary. Npgsql's default (`Prefer`) negotiates TLS if the server offers it and falls back to plaintext otherwise — appropriate for loopback. If Postgres is ever moved off-box, revisit and pin `sslmode=Require` (or stronger).

### Provisioning

Run once on the Lightsail host, as a Postgres superuser (typically `postgres`):

```sql
-- 1. Role with login. Pick a strong password; it never lands in this repo.
CREATE ROLE github_sync WITH LOGIN PASSWORD '<choose-a-strong-password>';

-- 2. Database owned by the app role.
CREATE DATABASE github_sync OWNER github_sync;

-- 3. Hand ownership of the default `public` schema to the app role.
--    Required since PostgreSQL 15, where `public` is no longer writable by
--    non-owners. Lets the role CREATE TABLE in `public` and CREATE SCHEMA
--    for `hangfire` (and any future schemas) without further grants.
\c github_sync
ALTER SCHEMA public OWNER TO github_sync;
```

The role owns both the database and the `public` schema. EF Core migrations and Hangfire's `hangfire` schema bootstrap both run as this role and will not need extra grants.

### Verify

From the Lightsail host (or any machine that can reach the Postgres port; in v1 only the host itself can):

```powershell
# Connectivity + identity check.
psql "host=localhost port=5432 dbname=github_sync user=github_sync password=<the-password>" `
  -c "select current_user, current_database();"

# Table-create + schema-create probes; confirms the role has the rights
# EF Core migrations and Hangfire bootstrap will need.
psql "host=localhost port=5432 dbname=github_sync user=github_sync password=<the-password>" `
  -c "create table _probe (id int); drop table _probe; create schema _probe; drop schema _probe;"
```

Both commands should succeed without permission errors. The Initial migration is already in `main`, so a full schema smoke test is:

```powershell
$env:ConnectionStrings__AppDb = "Host=localhost;Port=5432;Database=github_sync;Username=github_sync;Password=<the-password>"
dotnet ef database update --project src/GithubSync.Data --startup-project src/GithubSync.Api
```

Expected: `Done.` and the 8 app tables present under `psql ... -c "\dt"`.

### Network exposure

Postgres listens on `localhost` only — the Lightsail instance's network firewall does **not** open `5432` to the public internet, and there is no reason to. The API reaches Postgres over loopback inside the box; everything else stays out.

## Placeholder content

Before the CD pipeline lands, the physical path contains a single `index.html` so the site returns a valid response over both bindings without 5xx-ing.

```html
<!-- C:\Azureflow-QA\GithubSync.API\index.html -->
hello world
```

Because the app pool is `No Managed Code`, IIS falls through to the Static Content and Default Document modules — ANCM is not involved, and the runtime is not exercised by this placeholder. The first real `dotnet publish` artifact overwrites `index.html` and adds the `web.config` that wires up ANCM.

### Verify

From any machine with network access:

```powershell
Invoke-WebRequest -Uri 'https://github-sync.bart.consulting/' -UseBasicParsing
```

Expected: `Status: 200 OK`, body `hello world`.

## CD

[`.github/workflows/cd.yml`](../.github/workflows/cd.yml) deploys to this host on every merge to `main`, plus on manual `workflow_dispatch`.

### Mechanism

A **self-hosted Actions runner** registered on the Lightsail box runs the workflow. The alternatives — MSDeploy (needs Web Deploy + a credentialed publish endpoint on the host) and OpenSSH/SCP (needs sshd on Windows + key management as a repo secret) — both require an inbound auth surface to harden. The runner already has direct file-system and IIS access from inside the box, so the deploy itself needs no inbound credentials. Trade-off: a long-running Actions runner service to maintain on the host.

The runner is expected to carry the labels `self-hosted`, `Windows`, `lightsail`. Registering the runner on the host is a one-off step — see **Self-hosted runner setup** below.

### Self-hosted runner setup

One-off provisioning on the Lightsail host. The CD workflow above does nothing useful until this is in place.

**1. Register.** In the GitHub repo: **Settings → Actions → Runners → New self-hosted runner**. Select **Windows** / **x64**. GitHub generates a one-time registration token used in `config.cmd` below; copy the download URL it shows for the matching runner version (the version changes; don't hard-code one here).

On the Lightsail host, in an elevated PowerShell:

```powershell
$runnerRoot = 'C:\actions-runner-github-sync'
New-Item -ItemType Directory -Path $runnerRoot -Force | Out-Null
Set-Location $runnerRoot

# Download URL + token both come from the "New self-hosted runner" page.
Invoke-WebRequest -Uri '<runner-download-url-from-github>' -OutFile runner.zip
Expand-Archive -Path runner.zip -DestinationPath . -Force

.\config.cmd `
  --url https://github.com/BluePhoenix91/github-sync `
  --token <REGISTRATION_TOKEN> `
  --labels Windows,lightsail `
  --unattended
```

The `self-hosted` label is added automatically; `Windows` and `lightsail` are the additional labels the workflow keys off. The directory name `C:\actions-runner-github-sync` is convention, not a requirement — pick anything outside `C:\Azureflow-QA\` so the runner's own files never live next to deploy artefacts.

**2. Install as a service** so the runner survives reboots and runs unattended:

```powershell
.\svc.cmd install <DOMAIN\Account>
.\svc.cmd start
```

The runner identity must be able to:

- start and stop the `github-sync-api` IIS app pool (requires local admin, or an account explicitly granted IIS administration rights), and
- write to `C:\Azureflow-QA\GithubSync.API` and its sibling backup folder.

The simplest workable identity is **a dedicated local administrator account on the box** (separate from your console login, with its own password). Finer-grained ACLs are possible but not justified for a single-tenant host. Default `NETWORK SERVICE` will **not** work — it cannot `Stop-WebAppPool`.

**3. Verify.** Trigger the workflow manually from **Actions → CD → Run workflow** on `main`. The first run should publish the API, replace the `hello world` placeholder, and leave the site serving the real app.

**To replace, remove, or rotate the runner later:**

```powershell
Set-Location 'C:\actions-runner-github-sync'
.\svc.cmd stop
.\svc.cmd uninstall
.\config.cmd remove --token <REMOVE_TOKEN>
```

A fresh remove-token is generated from the same Settings → Runners page.

### Steps the workflow runs

1. `actions/checkout@v4` + `actions/setup-dotnet@v4` pinned to `10.0.x`.
2. `dotnet publish src/GithubSync.Api/GithubSync.Api.csproj -c Release -r win-x64 --self-contained false -o publish`. Framework-dependent because the host's Hosting Bundle provides the runtime.
3. Stop the `github-sync-api` app pool and wait up to 30 seconds for the worker to exit. The stop is what releases file locks on the running binaries; a recycle alone would not.
4. Rotate the previous publish folder to a timestamped sibling backup: `C:\Azureflow-QA\GithubSync.API` → `C:\Azureflow-QA\GithubSync.API.backup-<yyyyMMdd-HHmmss>`. Any older `GithubSync.API.backup-*` is removed first, so exactly **one** backup is kept on disk at a time.
5. Copy the publish output into `C:\Azureflow-QA\GithubSync.API`.
6. Re-apply the app pool's environment variables from the four GitHub Actions secrets (see [Secrets](#secrets) below). The step fails the deploy if any secret is empty, before the worker would otherwise start with a blank value.
7. Start the app pool. This step runs even if an earlier step failed (`if: always()`) — leaving the pool stopped on a failed deploy would dark the site for as long as it takes someone to notice.

`concurrency: { group: cd, cancel-in-progress: false }` so a second merge that lands mid-deploy queues behind the first rather than cancelling it half-way through a stop/copy/start.

### Rollback

After any successful or failed deploy, the immediately previous publish is on disk as `C:\Azureflow-QA\GithubSync.API.backup-<timestamp>`. Manual restore:

```powershell
Import-Module WebAdministration
Stop-WebAppPool -Name 'github-sync-api'
Remove-Item -Recurse -Force 'C:\Azureflow-QA\GithubSync.API'
Rename-Item 'C:\Azureflow-QA\GithubSync.API.backup-<timestamp>' 'GithubSync.API'
Start-WebAppPool -Name 'github-sync-api'
```

Only the most recent backup is preserved, so the rollback window covers the **immediately previous** publish, not arbitrary history. If a bad deploy is caught after another deploy has already overwritten the backup, recovery is by re-deploying an earlier commit from `main`, not by disk rollback.

### Database migrations

`dotnet ef database update` is **not** run from CD. Migrations stay a manual, gated step — applied after explicit review against the target environment.

### Failure modes

- **Build or publish fails**: workflow stops before touching the host. No state change.
- **Stop-pool timeout** (worker process won't exit within 30s): workflow fails red and stops. The site keeps serving the previous version. Investigate the worker on the host (`Get-Process w3wp`).
- **Copy fails mid-way**: the workflow still starts the pool, so the site comes up against partial files and will likely return errors. Use the manual rollback above.
- **Missing GitHub Actions secret**: the "Configure app pool environment variables" step throws before any IIS config changes, naming each missing secret. Configure the secret in **Settings → Secrets and variables → Actions** and re-run.
- **App fails to start with "Missing required secrets"**: the runtime validator (`RequiredSecrets.Validate`) ran and found one or more secrets unset on the app pool. The worker logs the missing list before exiting. Confirm the GitHub Actions secrets exist and re-run CD; the deploy step writes them onto the pool fresh on every run.
- **Start-pool fails**: requires manual intervention via IIS Manager. Site is down until resolved.

## Secrets

Four secrets are required at runtime. All live on the `github-sync-api` IIS app pool as environment variables and are read by the API via `IConfiguration`. The CD workflow writes them on every deploy from GitHub Actions repository secrets — they are never committed to the repo and never written to disk in `appsettings*.json`.

The runtime contract is enforced by [`RequiredSecrets.Validate`](../src/GithubSync.Api/Startup/RequiredSecrets.cs), called from [`Program.cs`](../src/GithubSync.Api/Program.cs) after the host builds. In `Production` (and any environment that isn't `Development`), a missing secret throws at startup and the worker exits — no silent empty-string fallback. In `Development`, missing secrets log a warning but do not block startup, so a developer working on an unrelated slice isn't forced to provision credentials they don't need yet.

### Inventory

| Purpose | App-pool env var (runtime) | GitHub Actions secret (build-time) | Used by |
|---|---|---|---|
| Sentry DSN | `SENTRY_DSN` | `SENTRY_DSN` | [`SentryWiring.Configure`](../src/GithubSync.Api/Startup/SentryWiring.cs) (see [Sentry](#sentry) below) |
| GitHub PAT for fetch client | `GITHUB_TOKEN` | `GH_API_TOKEN` | Issue [#11](https://github.com/BluePhoenix91/github-sync/issues/11) (GitHub fetch) |
| Azure DevOps PAT | `ADO_PAT` | `ADO_PAT` | Issues [#14](https://github.com/BluePhoenix91/github-sync/issues/14) / [#15](https://github.com/BluePhoenix91/github-sync/issues/15) (ADO exporter) |
| Postgres connection string | `ConnectionStrings__AppDb` | `APP_DB_CONNECTION_STRING` | `AppDbContext` (today), Hangfire storage (future) |

The GitHub Actions secret name for the GitHub PAT is `GH_API_TOKEN` rather than `GITHUB_TOKEN` because GitHub Actions reserves the `GITHUB_*` prefix for repository-scoped secrets and rejects user-defined names that start with it. The runtime app-pool env var is still `GITHUB_TOKEN` because that's the standard name the GitHub client libraries read.

The connection string is kept under the `AppDb` key (not the issue's draft suggestion `GithubSync`) because the codebase, `Program.cs` reader, and `dotnet user-secrets` instructions in `CLAUDE.md` were already aligned on `AppDb`. Renaming would have churned all three for no behavioural gain.

### Local development

In `Development` the same four config keys are read, but the source is [`dotnet user-secrets`](https://learn.microsoft.com/aspnet/core/security/app-secrets) (the API project has a `UserSecretsId`). Only `ConnectionStrings:AppDb` is required to do anything useful locally; the other three can be left unset and the startup warning is tolerated. The connection-string one-liner is in `CLAUDE.md` under **Commands**.

### Rotation

The same procedure applies to all four secrets:

1. Generate or obtain the new value (regenerate the PAT in GitHub / ADO, regenerate the DSN in Sentry, or change the Postgres role password and rewrite the connection string).
2. Update the corresponding GitHub Actions secret: **Settings → Secrets and variables → Actions → Repository secrets** → edit the value. The secret name does not change.
3. Trigger the CD workflow — either land a merge to `main`, or use **Actions → CD → Run workflow** on `main`. The "Configure app pool environment variables" step clears and re-adds the entire `environmentVariables` collection on the app pool with the fresh value before starting the pool.
4. Verify by checking the next worker process picks it up. For the connection string, a missing or wrong value surfaces immediately as an EF/Npgsql connection error in logs.

The previous value is not retained on the host once the step runs — there is no manual file under `C:\Azureflow-QA\` to also clear, and `Clear-WebConfiguration` removes the old entry before `Add-WebConfigurationProperty` writes the new one.

### Adding a fifth secret later

If the inventory grows, **revisit AWS Systems Manager Parameter Store** as a vault in front of GitHub Actions — this was the explicit deferred trigger captured on epic [#25](https://github.com/BluePhoenix91/github-sync/issues/25). Until then, GitHub Actions repo secrets are sufficient for four values that change at human-scale frequency.

## Sentry

The API wires [Sentry.AspNetCore](https://docs.sentry.io/platforms/dotnet/guides/aspnetcore/) in [`SentryWiring.Configure`](../src/GithubSync.Api/Startup/SentryWiring.cs), called from [`Program.cs`](../src/GithubSync.Api/Program.cs) before the host builds. The SDK captures unhandled exceptions from request pipelines automatically.

| Sentry option | Source | Value at runtime |
|---|---|---|
| `Dsn` | `SENTRY_DSN` env var on the app pool | The DSN from the GitHub Actions secret of the same name. |
| `Environment` | `IHostEnvironment.EnvironmentName` | `Production` on the host (matches the system-wide `ASPNETCORE_ENVIRONMENT`). |
| `Release` | Sentry SDK default: entry assembly's `AssemblyInformationalVersion` | Stamped by CD as `<assembly-version>+<git-sha>` via MSBuild's `SourceRevisionId` parameter on `dotnet publish`. |
| `SendDefaultPii` | SDK default | `false` — no request body, header, or user capture. |

If `SENTRY_DSN` is unset, `SentryWiring.Configure` skips initialization entirely (the SDK is never registered, no events queue). Production cannot reach this branch — `RequiredSecrets.Validate` throws on missing `SENTRY_DSN` before the worker starts. The branch exists for `Development` and tests, where running offline without a DSN is the common case.

### Verifying events land

The repo does not ship a debug-throw route. Live verification happens against the first real exception after deploy: trigger one (or wait for one), then in Sentry confirm the event carries `environment=Production` and a `release` tag of the form `<version>+<sha>` matching the deployed commit.

## Ingestion

The GitHub ingestion runs on a Hangfire recurring schedule. The cron expression is read from `Ingestion:CronExpression` in `appsettings.json` (or any overlaid environment / user-secrets source).

Default: `"*/15 * * * *"` (every 15 minutes).

Setting a different value:

- Per-environment override via `appsettings.Production.json` (committed) or `Ingestion__CronExpression` env var on the IIS app pool (uncommitted).
- Updates take effect on the next app-pool restart. The recurring job uses a stable ID (`ingest-github-scheduler`) so changing the cron re-registers the existing job rather than duplicating.
- The per-config concurrency lock has a 900-second timeout (one default cron interval). If a single run exceeds that, the next tick will surface a `TimeoutException` on the Hangfire dashboard — the right "this repo is too big for the current cron" signal. If you raise the cron interval, raise the timeout in `IssueIngestionJob.RunForConfigurationAsync` to match.

The Hangfire dashboard at `/hangfire` is allowed only in `Development`. In `Production` the authorization filter returns `403`. A production-grade auth surface is tracked as a separate concern under epic [#29](https://github.com/BluePhoenix91/github-sync/issues/29).

Hangfire wiring is gated by `Hangfire:Enabled` (defaults `true`). The flag exists for `WebApplicationFactory<Program>`-based tests that can't reach a real Postgres; leave it unset in production.

## Logging

Structured logging conventions, field shape, and the grep recipe live in [docs/logging.md](logging.md). This section captures the host-side specifics.

- **Where logs land on disk:** `C:\Azureflow-QA\GithubSync.API\logs\app-yyyyMMdd.log`, one JSON line per event. Suffix files (`app-yyyyMMdd_001.log`, ...) appear if a single day hits the 1 GB per-file cap.
- **Retention:** 14 files kept, older files deleted automatically by `Serilog.Sinks.File`.
- **No setup needed on the host.** The `logs/` subdirectory is created on first write. `ApplicationPoolIdentity` already owns the parent `C:\Azureflow-QA\GithubSync.API\` directory under the existing deploy convention, so child directories inherit write access without an explicit ACL grant.
- **ANCM `stdoutLog` is intentionally disabled** (the IIS default). The app's Serilog file sink owns the rolling/retention story. `RequiredSecrets.Validate` covers misconfigured-secret startup failures with a clear thrown message, and any later startup exception goes through `Sentry.AspNetCore`. Leaving ANCM stdout off avoids a second uncapped log stream.

If logs stop appearing on disk: check Sentry for events first — the Serilog Sentry sink is independent of the file sink, so Sentry being silent too narrows the cause to the app itself rather than the file system.

## Seq

[Seq](https://datalust.co/seq) is an **opt-in** structured log aggregator that sits alongside the file sink on the Lightsail box. Conventions, query recipes, and the runtime wiring rationale live in [docs/logging.md → Seq](logging.md#seq); this section captures the host-side install and operate steps.

### Install

1. Download the Seq Windows installer (single-user free tier) from <https://datalust.co/download>.
2. Run as administrator. Accept the default install path (`C:\Program Files\Seq`) and the default storage path (`C:\ProgramData\Seq`). The installer registers Seq as a Windows service set to start automatically.
3. On first launch, open `http://localhost:5341` from an RDP session on the host. Seq prompts to set the admin password — store it in the operator password manager. No additional user accounts are needed; the free tier is single-user.

### Access — IIS reverse proxy on a restricted port

Seq's listener stays bound to `http://localhost:5341` on the box. Operator access goes through a **separate IIS site** that reverse-proxies to it: `https://seq.bart.consulting:8443` → `http://localhost:5341`. Three defenses stack on that path:

1. **Lightsail firewall** — port `8443` is open **only** to the operator home IP `81.244.122.234/32`. The main API's port `443` is unchanged.
2. **IIS "IP and Domain Restrictions"** — second-layer allowlist on the Seq site, in case the Lightsail rule is ever loosened by accident.
3. **Seq admin password** — Seq's own auth gate. Set on first launch.

The rationale for going through the work of an IIS reverse proxy instead of an SSH tunnel: the Windows Lightsail box does **not** run an SSH server (admin access is RDP-only — see the deploy mechanism note at the top of `cd.yml`), so a tunnel would mean installing and exposing OpenSSH Server first, which is itself a new public surface. Standing up a dedicated, IP-restricted, password-gated HTTPS endpoint reuses the box's existing IIS + win-acme machinery and is bookmarkable.

#### One-time setup on the host

1. **Install IIS modules.** *Server Manager → Add Roles and Features → Web Server (IIS) → Web Server → Security* → tick **IP and Domain Restrictions**. Then install **URL Rewrite 2.1** and **Application Request Routing 3.0** by **downloading their MSIs directly** from <https://www.iis.net/downloads> (Web Platform Installer is retired and its links 404). ARR also requires **External Cache 1.1** — install it first if it isn't already, or the ARR install will fail silently with the icon never appearing in IIS Manager. After install, close IIS Manager fully and reopen (`inetmgr`) — ARR's icon only registers in a fresh Manager process.

2. **Enable the ARR proxy.** *IIS Manager → server root → Application Request Routing Cache → Server Proxy Settings (right pane)* → tick **Enable proxy** → Apply. Defaults are fine (HTTP/1.1 pass-through, X-Forwarded-For preserved, 120 s timeout).

3. **DNS.** Add an A record `seq.bart.consulting` → `15.237.68.235`. If the parent domain is on Cloudflare, set it to **DNS only** (gray cloud), not proxied — IIS needs the real client IP for the IP allowlist, and win-acme needs an unproxied HTTP-01 challenge path. Verify with `nslookup seq.bart.consulting` from your workstation before continuing.

4. **Create the Seq site in IIS — HTTP-only initially.** *Sites → Add Website…*
   - Site name: `seq`. App pool: `seq` (auto-created).
   - Physical path: `C:\Azureflow-QA\seq` (create the empty folder first; matches the existing site-root convention on this box). It's a placeholder — the site only ever reverse-proxies.
   - Binding type: **`http`** (not https — IIS refuses to save an HTTPS binding without a cert and we don't have one yet, so chicken-and-egg → defer until win-acme issues one).
   - Port: `80`. Host name: `seq.bart.consulting`. IP: `All Unassigned`.
   - After creation: Application Pools → `seq` → *Basic Settings…* → .NET CLR version: **No Managed Code**.

5. **Issue the certificate via win-acme.** Run `wacs.exe` as administrator and walk through:
   - **M** — Create certificate (full options).
   - **2** — Manual input.
   - Host: `seq.bart.consulting`. Friendly name: accept default.
   - **4** — Single certificate.
   - **1** — HTTP file-system validation. Path: accept the default (`C:\Azureflow-QA\seq`).
   - **Copy default web.config before validation?** **y** — ACME challenge files have no file extension and the default static-content handler refuses them without an explicit MIME map.
   - CSR / store: defaults (RSA, Windows Certificate Store).
   - Installation step: **No (additional) installation steps** — we bind the cert manually so we control the port.
   - Accept LE T&Cs. The HTTP-01 dance runs in ~10–20 seconds and the cert lands in the local machine's *WebHosting* store.

   **If the validation fails with `403 Forbidden`,** the site's IP restriction (if you applied it before this step) is blocking Let's Encrypt's validation servers. Temporarily set *IP and Domain Restrictions → Edit Feature Settings → Allow for unspecified clients*, retry the win-acme prompt with `y`, then re-lock in step 9.

6. **Add the HTTPS:8443 binding** using the new cert. *Sites → `seq` → Bindings… → Add…*: type `https`, port `8443`, host `seq.bart.consulting`, **Require Server Name Indication** ticked, SSL certificate `seq.bart.consulting`. This is the canonical operator-facing port.

7. **Add the HTTPS:443 binding** for the short URL — same dialog, same options, port `443`. With SNI ticked, this coexists with the main API's `:443` binding (different host header → different site). Without SNI, you'd collide with the main API.

8. **Open Windows Firewall for port 8443.** IIS only auto-opens `80`/`443` in Windows Defender Firewall; custom ports need an explicit rule:

   ```powershell
   New-NetFirewallRule -DisplayName "IIS Seq HTTPS 8443" -Direction Inbound -Protocol TCP -LocalPort 8443 -Action Allow
   ```

9. **Apply IP restrictions and the `.well-known` override.** *IIS Manager → `seq` site → IP Address and Domain Restrictions*:
   - *Edit Feature Settings…* → **Deny** for unspecified clients.
   - *Add Allow Entry…* → IP address `81.244.122.234`, mask blank.

   Then create the path override so future win-acme renewals (every ~60 days) can still pass HTTP-01:

   ```powershell
   $p = "C:\Azureflow-QA\seq\.well-known\acme-challenge"
   New-Item -ItemType Directory -Path $p -Force | Out-Null
   @'
   <?xml version="1.0" encoding="UTF-8"?>
   <configuration>
     <system.webServer>
       <security>
         <ipSecurity allowUnlisted="true" />
       </security>
       <staticContent>
         <clear />
         <mimeMap fileExtension="." mimeType="text/plain" />
       </staticContent>
     </system.webServer>
   </configuration>
   '@ | Set-Content -Path "$p\web.config" -Encoding UTF8
   ```

   The override lifts the site-wide deny **only** for `/.well-known/acme-challenge/`, and re-applies the extensionless-file MIME mapping so the token is served as `text/plain`.

10. **Write the reverse-proxy `web.config`** at `C:\Azureflow-QA\seq\web.config`:

    ```xml
    <?xml version="1.0" encoding="UTF-8"?>
    <configuration>
      <system.webServer>
        <rewrite>
          <rules>
            <rule name="Allow ACME challenge" stopProcessing="true">
              <match url="^\.well-known/acme-challenge/.*$" />
              <action type="None" />
            </rule>
            <rule name="Canonicalize to https://seq.bart.consulting:8443" stopProcessing="true">
              <match url="(.*)" />
              <conditions>
                <add input="{SERVER_PORT}" pattern="^8443$" negate="true" />
              </conditions>
              <action type="Redirect" url="https://seq.bart.consulting:8443/{R:1}" redirectType="Permanent" />
            </rule>
            <rule name="Reverse proxy to Seq" stopProcessing="true">
              <match url="(.*)" />
              <action type="Rewrite" url="http://localhost:5341/{R:1}" />
            </rule>
          </rules>
        </rewrite>
      </system.webServer>
    </configuration>
    ```

    Rule order matters: ACME pass-through first (so renewals work), then anything-not-`:8443` gets `301`'d to the canonical URL, then `:8443` traffic reverse-proxies to Seq. The rule on the parent path (deny by default) and the `.well-known/acme-challenge/web.config` override (allow) compose as expected.

11. **Lightsail firewall.** Lightsail console → instance → *Networking → IPv4 Firewall → Add rule*: Application **Custom**, Protocol **TCP**, Port **8443**, Source **Restrict to IP address** = `81.244.122.234`. Save. (Port `443` and `80` are already open from the main API and don't need a separate rule for the Seq site — IIS routes them by host header.)

After all eleven steps:

| URL | Result |
|---|---|
| `http://seq.bart.consulting` | 301 → `https://seq.bart.consulting:8443` |
| `https://seq.bart.consulting` | 301 → `https://seq.bart.consulting:8443` |
| `https://seq.bart.consulting:8443` | Seq UI |
| Any URL from a non-allowlisted IP | timeout (Lightsail firewall on `:8443`) or 403 (IIS on `:80` / `:443`) |

#### Rotating the allowlist when your home IP changes

Residential IPs rotate. Symptom: every URL above times out from home. Update **both** allowlists:

1. Lightsail console → *Networking* → edit the `8443` rule → replace the IP.
2. IIS Manager → `seq` site → *IP Address and Domain Restrictions* → edit the allow entry → replace the IP.

Updating only one silently loses a defense layer.

#### Port 80 sharing

The main API already uses port 80 on the default site (kept around for win-acme HTTP-01 on the API hostname). IIS routes inbound 80 by the `Host` header, so the `seq` site's port-80 binding with host name `seq.bart.consulting` does not conflict — each binding's `Host` is matched independently. Same applies to port 443.

### Wire the API to Seq

CD writes `SEQ_SERVER_URL=http://localhost:5341` onto the `github-sync-api` app pool on every deploy — see the `$config` block in the "Configure app pool environment variables" step of [`cd.yml`](../.github/workflows/cd.yml). It is **not** a GitHub Actions secret (the value is operationally fixed; the box's loopback URL doesn't rotate) and **not** in `RequiredSecrets`. If it is ever unset — by commenting the line in `cd.yml` and redeploying — [`LoggingWiring`](../src/GithubSync.Api/Startup/LoggingWiring.cs) skips the Seq sink and the API logs to file + Sentry exactly as before. That's a supported state for temporarily turning Seq off, not a misconfiguration.

Verifying after the next deploy:

1. Wait for the CD run that includes the change to land.
2. Hit any API endpoint (a `/healthz` probe is enough).
3. In the Seq UI, search `ApplicationName = 'github-sync'`. Events should appear within seconds.

### Retention

Configure a **30-day retention policy** in the Seq UI under **Data → Storage → Retention Policies → Add** (or browse directly to `http://localhost:5341/#/storage/retention/new` if the menu has moved again — Seq has shuffled this between **Settings**, **Data → Retention**, and **Data → Storage** across versions). Set `After` to **30 days / 0 hours / 0 minutes** and leave `Remove` on **All events**. The free tier caps total stored event volume; 30 days is the planned ceiling. If volume ever pushes against the cap before 30 days, drop the policy to whatever fits — the file sink's 14-day window remains the recent-history fallback regardless.

### Backup and upgrade

Seq's data lives in `C:\ProgramData\Seq`. It is *not* part of the API deploy artifact and is not backed up by CD. If the box is rebuilt, Seq is a fresh install with no prior events — acceptable, since the file sink is the durable record. Upgrading Seq is a re-run of the installer; the data directory is preserved across upgrades.

### Troubleshooting

- **Browser hangs / times out on every URL:** your home IP has rotated. Check at <https://ifconfig.me> from your home connection, then update both the Lightsail firewall rule and the IIS IP allowlist (see "Rotating the allowlist" above). The from-the-box test (`https://seq.bart.consulting:8443` on the host itself) **also** times out because Lightsail's NAT hairpins — the box's outbound request to its own public IP arrives back at the firewall from the box, not from `81.244.122.234`, so the IP rule denies it. Use `Test-NetConnection -ComputerName localhost -Port 8443` on the host to confirm IIS is fine independently.
- **`403 Forbidden` from IIS:** the Lightsail firewall let you through but IIS's IP restriction did not — the two allowlists are out of sync. Update the IIS rule to match the Lightsail one.
- **`ERR_CONNECTION_RESET` in the browser:** the binding for that hostname+port doesn't exist on the `seq` site, so `http.sys` rejects the TLS handshake. Most often: the HTTPS:443 binding wasn't added, or **Require SNI** wasn't ticked on it (without SNI, IIS can't route the request to the `seq` site). Verify with `Get-WebBinding -Name seq` — should show three rows (http:80, https:443, https:8443) all with host `seq.bart.consulting`.
- **`502 Bad Gateway` from IIS:** the reverse-proxy rule reached IIS but couldn't talk to Seq on `localhost:5341`. Confirm the Seq Windows service is running (`Get-Service Seq`).
- **Browser loads Seq over plain HTTP without redirecting** (no `:8443` in the address bar): Chrome treats `seq.bart.consulting:8443` (no protocol) as HTTP because the port isn't `443`. Type the full `https://seq.bart.consulting:8443`, or just visit `https://seq.bart.consulting` and let the canonicalize rule redirect.
- **HTTPS:443 loads Seq but the URL doesn't update to `:8443`:** the canonicalize rule isn't firing. Most likely `web.config` is on an older two-rule version that only redirects when `{HTTPS}=off`. Confirm the file has the three-rule version with the `{SERVER_PORT} != 8443` condition.
- **`curl` against the URL works but the browser fails:** browser has a cached bad TLS state for the host from earlier failed attempts. In Chrome: `chrome://net-internals/#hsts` → *Delete domain security policies* → enter `seq.bart.consulting`. Or just test in incognito.
- **"Niet beveiligd" / "Not secure" warning stays after a successful cert install:** the browser remembers an old override from when you clicked through a previous warning. Clear it via the site's *Cookies en sitegegevens* in the lock-icon popover, or reset site data under *chrome://settings/content/all*.
- **win-acme renewal fails with `403`:** the `.well-known/acme-challenge/web.config` override (step 9) is missing or has been overwritten. Recreate it — without it the site-wide IP deny blocks Let's Encrypt's validation IPs.
- **Seq UI loads but no `github-sync` events appear:** confirm `SEQ_SERVER_URL` is present on the `github-sync-api` app pool (`appcmd list apppool github-sync-api /text:*`) and the pool has been recycled since the env var was set.
- **Sink errors in the API's log file:** `Serilog.Sinks.Seq` writes its own ingestion failures via Serilog's `SelfLog` (off by default). It buffers in memory if Seq is briefly down; sustained outages drop events at the sink — Sentry and the file sink are unaffected.

## HTTPS / certificate

- **Edge cert** (seen by clients routed via Cloudflare): issued by Cloudflare automatically.
- **Origin cert** (between Cloudflare and Lightsail, and seen by any client that reaches Lightsail directly): issued by **Let's Encrypt via win-acme** running on the Lightsail host.

win-acme handles renewal automatically (default 60-day schedule, well before the 90-day expiry). The HTTP `:80` binding is kept partly to allow win-acme's HTTP-01 challenge.

If a renewal ever fails, start the investigation in `C:\ProgramData\win-acme\` on the host — it logs there and stores its renewal state there.

## Forward-pointing notes

For future child issues under [#29](https://github.com/BluePhoenix91/github-sync/issues/29):

- **Hangfire keep-alive**: prior Hangfire-bearing services on this host have needed an external health-ping to stay alive despite the disabled idle timeout. Revisit during the future Hangfire epic; if the failure mode recurs, capture it (logs, recycle events) so the next mitigation is informed rather than copied.
- **DNS configuration**: the current records are inherited from a sibling site rather than designed for github-sync; normalising to a simpler configuration is a candidate cleanup.
- **Origin lockdown**: if Cloudflare ever becomes the *only* intended path to the box, the firewall can be tightened to accept HTTPS only from Cloudflare's published IP ranges. Out of scope until that becomes a real requirement.
- **HTTPS redirect**: HTTP `:80` is not redirected to `:443` at the IIS level; add this once the API starts serving anything sensitive, or move to HSTS at the same time.
