# Deploy

How the github-sync API is hosted. Companion to the Host + CI/CD epic ([#29](https://github.com/BluePhoenix91/github-sync/issues/29)).

## Scope of this document

What's covered:

- IIS site, app pool, bindings, and environment configuration on the Lightsail host ([#31](https://github.com/BluePhoenix91/github-sync/issues/31)).
- Database and role provisioning on the colocated Postgres instance ([#33](https://github.com/BluePhoenix91/github-sync/issues/33)).
- Secrets wiring — Sentry DSN, GitHub PAT, ADO PAT, DB connection string ([#35](https://github.com/BluePhoenix91/github-sync/issues/35)).
- HTTPS topology and cert source.

What is **not** covered yet — separate child issues under [#29](https://github.com/BluePhoenix91/github-sync/issues/29) will fill these in:

- Hangfire dashboard auth filter, recurring job registration, keep-alive strategy.

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
3. On first launch, open `http://localhost:5341` *on the host itself* (RDP or the SSH tunnel below). Seq prompts to set the admin password — store it in the operator password manager. No additional user accounts are needed; the free tier is single-user.

### Network

Seq listens on `http://localhost:5341` and is **not exposed publicly**. There is no IIS reverse-proxy rule, no Cloudflare hostname, no Cloudflare origin cert — `5341` stays closed at the Lightsail firewall and bound to loopback on the host. Operators reach the UI over an SSH tunnel:

```bash
ssh -L 5341:localhost:5341 <user>@<lightsail-host>
# then browse http://localhost:5341 from the workstation
```

Trade-off recorded: an IIS reverse-proxy rule + win-acme cert would let operators bookmark a real URL, but it would add one more public surface to keep patched and certificate-renewed. The SSH tunnel reuses access controls that already exist for the box — "who can read logs?" collapses to "who has SSH access?", one ACL instead of two. When someone leaves, removing their SSH key removes Seq access automatically.

#### Tunnel helper script

[`scripts/seq-tunnel.ps1`](../scripts/seq-tunnel.ps1) wraps the `ssh -fNL ...` invocation, opens the browser, and persists the background ssh PID so it can be stopped cleanly. Use it instead of typing the raw `ssh -L` form each time — same tunnel, fewer flags to remember.

**One-time setup** on each operator workstation, in `~/.ssh/config` (or `%USERPROFILE%\.ssh\config` on Windows):

```
Host gh-sync-seq
    HostName <lightsail-public-ip-or-dns>
    User administrator
    IdentityFile ~/.ssh/lightsail.pem
    LocalForward 5341 localhost:5341
    ServerAliveInterval 60
```

**Daily use:**

```powershell
# Open the tunnel and launch the Seq UI in the default browser.
scripts/seq-tunnel.ps1 -SshAlias gh-sync-seq

# When done — or before switching to a different host.
scripts/seq-tunnel.ps1 -Stop
```

If the SSH config alias isn't set up, the script also accepts explicit args (`-SshHost`, `-User`, `-IdentityFile`). See the script's comment-based help (`Get-Help scripts/seq-tunnel.ps1 -Full`) for the full parameter list, including `-LocalPort` (when 5341 is already taken on the workstation) and `-NoBrowser` (when scripting against the tunnel instead of using the UI).

The script is intentionally idempotent on the open side: if it sees something already listening on the local port, it assumes the tunnel is up and just opens the browser. It does **not** install OpenSSH, manage keys, or edit `~/.ssh/config` — those are operator-workstation concerns and out of scope for a tunnel helper.

### Wire the API to Seq

1. Add a `SEQ_SERVER_URL` env var to the `github-sync-api` IIS app pool, value `http://localhost:5341`. The simplest path: extend the CD workflow's "Configure app pool environment variables" step to write `SEQ_SERVER_URL` alongside the four existing secrets (Seq URL is a deploy-time *config value*, not a secret — putting it in the same step keeps the env-var set in one place).
2. Recycle the app pool (next deploy does this automatically).
3. Verify in the Seq UI: a search like `ApplicationName = 'github-sync'` should show recent events within seconds of the next request hitting the API.

`SEQ_SERVER_URL` is **not** added to `RequiredSecrets`. If it is unset, [`LoggingWiring`](../src/GithubSync.Api/Startup/LoggingWiring.cs) skips the Seq sink and the API logs to file + Sentry exactly as before — leaving the var off is a supported state, not a misconfiguration.

### Retention

Configure a **30-day retention policy** in the Seq UI under **Data → Retention** (or browse directly to `http://localhost:5341/#/retention` if the menu has moved again — Seq has shuffled this between **Settings** and **Data** across versions). Add a policy with the filter left blank (applies to all events) and "Delete events older than" set to 30 days. The free tier caps total stored event volume; 30 days is the planned ceiling. If volume ever pushes against the cap before 30 days, drop the policy to whatever fits — the file sink's 14-day window remains the recent-history fallback regardless.

### Backup and upgrade

Seq's data lives in `C:\ProgramData\Seq`. It is *not* part of the API deploy artifact and is not backed up by CD. If the box is rebuilt, Seq is a fresh install with no prior events — acceptable, since the file sink is the durable record. Upgrading Seq is a re-run of the installer; the data directory is preserved across upgrades.

### Troubleshooting

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
