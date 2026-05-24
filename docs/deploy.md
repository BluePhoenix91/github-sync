# Deploy

How the github-sync API is hosted. Companion to the Host + CI/CD epic ([#29](https://github.com/BluePhoenix91/github-sync/issues/29)).

## Scope of this document

What's covered:

- IIS site, app pool, bindings, and environment configuration on the Lightsail host ([#31](https://github.com/BluePhoenix91/github-sync/issues/31)).
- Database and role provisioning on the colocated Postgres instance ([#33](https://github.com/BluePhoenix91/github-sync/issues/33)).
- HTTPS topology and cert source.

What is **not** covered yet — separate child issues under [#29](https://github.com/BluePhoenix91/github-sync/issues/29) will fill these in:

- GitHub Actions CI (`dotnet build` + `dotnet test` on PR).
- GitHub Actions CD (`dotnet publish` + ship artifact to the host).
- Secrets wiring (Sentry DSN, GitHub PAT, ADO PAT, DB connection string).
- Hangfire dashboard auth filter, recurring job registration, keep-alive strategy.

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

If memory pressure ever becomes a real problem on this box, "Private Memory Limit" is the lever to reach for — not the periodic recycle.

### Environment variables

- **`ASPNETCORE_ENVIRONMENT=Production`** — set as a **system-wide** environment variable on the host. The host serves this single environment, so per-pool scoping isn't needed and the existing setting is left in place.
- **`ASPNETCORE_URLS`** — **not set anywhere**. Under the IIS in-process ANCM hosting model, IIS hands the port to Kestrel via internal environment variables (`ASPNETCORE_PORT`, `ASPNETCORE_TOKEN`). Setting `ASPNETCORE_URLS` would compete with that and is a known source of misconfiguration. Resist the urge to add it "just in case".
- **App-pool-scoped environment variables**: none in this PR. The future secrets-wiring child issue under [#29](https://github.com/BluePhoenix91/github-sync/issues/29) introduces these for things like `Sentry__Dsn`, `ConnectionStrings__AppDb`, etc.

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

## HTTPS / certificate

- **Edge cert** (seen by clients routed via Cloudflare): issued by Cloudflare automatically.
- **Origin cert** (between Cloudflare and Lightsail, and seen by any client that reaches Lightsail directly): issued by **Let's Encrypt via win-acme** running on the Lightsail host.

win-acme handles renewal automatically (default 60-day schedule, well before the 90-day expiry). The HTTP `:80` binding is kept partly to allow win-acme's HTTP-01 challenge.

If a renewal ever fails, start the investigation in `C:\ProgramData\win-acme\` on the host — it logs there and stores its renewal state there.

## Forward-pointing notes

For future child issues under [#29](https://github.com/BluePhoenix91/github-sync/issues/29):

- **Hangfire keep-alive**: prior Hangfire-bearing services on this host have needed an external health-ping to stay alive despite the disabled idle timeout. Revisit during the future Hangfire epic; if the failure mode recurs, capture it (logs, recycle events) so the next mitigation is informed rather than copied.
- **Secrets**: when the secrets-wiring PR lands, secrets go onto the `github-sync-api` app pool's environment variables at deploy time — not into `appsettings.Production.json` on disk. This was settled on [#25](https://github.com/BluePhoenix91/github-sync/issues/25). This document gains a section then.
- **DNS configuration**: the current records are inherited from a sibling site rather than designed for github-sync; normalising to a simpler configuration is a candidate cleanup.
- **Origin lockdown**: if Cloudflare ever becomes the *only* intended path to the box, the firewall can be tightened to accept HTTPS only from Cloudflare's published IP ranges. Out of scope until that becomes a real requirement.
- **HTTPS redirect**: HTTP `:80` is not redirected to `:443` at the IIS level; add this once the API starts serving anything sensitive, or move to HSTS at the same time.
