# Deploy

How the github-sync API is hosted. Companion to the Host + CI/CD epic ([#29](https://github.com/BluePhoenix91/github-sync/issues/29)).

## Scope of this document

What's covered:

- IIS site, app pool, bindings, and environment configuration on the Lightsail host ([#31](https://github.com/BluePhoenix91/github-sync/issues/31)).
- HTTPS topology and cert source.

What is **not** covered yet — separate child issues under [#29](https://github.com/BluePhoenix91/github-sync/issues/29) will fill these in:

- Database provisioning on the colocated Postgres instance.
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
   │   A    github-sync.bart.consulting → 15.237.68.235   (DNS only)
   │   AAAA github-sync.bart.consulting → 2a05:…:25a8     (Proxied via Cloudflare)
   ▼
AWS Lightsail (Windows Server)
   │   IIS
   │     ├── app pool: github-sync-api  (No Managed Code, ApplicationPoolIdentity)
   │     └── site:     github-sync-api  →  C:\Azureflow-QA\GithubSync.API
   │   PostgreSQL (colocated; consumed by this app, not exposed externally)
   ▼
ASP.NET Core app (Kestrel, out-of-process via ANCM)
```

**About the DNS split:** the `A` record is `DNS only` and `AAAA` is `Proxied` — inherited from the existing `scan-api.bart.consulting` setup, not a github-sync-specific design choice. github-sync is a backend API and gets no benefit from Cloudflare's path/host-rewriting features. A future cleanup pass can normalise both rows to `DNS only` once nothing else relies on the current behaviour; treat the current state as the documented baseline, not as a target.

## Prerequisites on the host

- **Windows Server** (already in place, hosting other IIS sites).
- **IIS** with the Static Content, ASP.NET Core Module v2 (ANCM), and Default Document features (all included by the Hosting Bundle below).
- **ASP.NET Core 10 Hosting Bundle** installed. Verify with `dotnet --info` — output should list a `Microsoft.AspNetCore.App 10.x` runtime.
- **PostgreSQL** running on the box (separate concern; consumed by this app once DB provisioning lands).
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

- **Edge cert** (seen by browsers when routing via Cloudflare, i.e. on the `AAAA` path): issued by Cloudflare automatically.
- **Origin cert** (between Cloudflare and Lightsail on the `AAAA` path, and seen directly by `A`-path clients): issued by **Let's Encrypt via win-acme** running on the Lightsail host.

win-acme handles renewal automatically (default 60-day schedule, well before the 90-day expiry). The HTTP `:80` binding is kept partly to allow win-acme's HTTP-01 challenge.

If a renewal ever fails, start the investigation in `C:\ProgramData\win-acme\` on the host — it logs there and stores its renewal state there.

## Forward-pointing notes

For future child issues under [#29](https://github.com/BluePhoenix91/github-sync/issues/29):

- **Hangfire keep-alive**: prior Hangfire-bearing services on this host have needed an external health-ping to stay alive despite the disabled idle timeout. Revisit during the future Hangfire epic; if the failure mode recurs, capture it (logs, recycle events) so the next mitigation is informed rather than copied.
- **Secrets**: when the secrets-wiring PR lands, secrets go onto the `github-sync-api` app pool's environment variables at deploy time — not into `appsettings.Production.json` on disk. This was settled on [#25](https://github.com/BluePhoenix91/github-sync/issues/25). This document gains a section then.
- **DNS proxy mismatch**: the `A=DNS only` / `AAAA=Proxied` asymmetry is inherited and not deliberate for github-sync; normalising both rows to `DNS only` is a candidate cleanup.
- **Origin lockdown**: if Cloudflare ever becomes the *only* intended path to the box, the firewall can be tightened to accept HTTPS only from Cloudflare's published IP ranges. Out of scope until that becomes a real requirement.
- **HTTPS redirect**: HTTP `:80` is not redirected to `:443` at the IIS level; add this once the API starts serving anything sensitive, or move to HSTS at the same time.
