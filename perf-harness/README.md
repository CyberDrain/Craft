# CRAFT static-content perf harness

A self-contained, Docker-based test suite that measures how the CRAFT origin serves the
CIPP-NG static frontend — **server resources** (CPU/RAM/disk) and **content serving**
(TTFB, throughput, compression, header correctness) — so static-serving optimizations can be
validated as a **before/after**. Nothing here changes application code.

See `../docs/static-serving-optimization.md` for the analysis these measurements back up.

## What it does

For one SUT image it brings up **Azurite + the SUT** (`docker-compose.yml`), waits for the
PowerShell pool to be ready, then while serving static assets it captures:

1. **Content correctness** — per-asset `Cache-Control` / `ETag` / `Vary` / `Content-Encoding`
   / `Set-Cookie`, scored against the desired end-state in the optimization doc (curl).
2. **Content serving** — TTFB (overall + per asset kind), requests/sec, error rate,
   bytes-on-wire, and compression ratio of the big app chunk (k6 + curl).
3. **Server resources** — CPU% / RAM / net / block-IO sampled from `docker stats` *during* the load.

Outputs go to `results/<label>-<timestamp>.json` (machine) and `.md` (human).

## Requirements

- Docker Desktop running (Windows/Mac/Linux). Pulls `grafana/k6` and `azurite` on first run.
- PowerShell 7 (`pwsh`). `curl.exe` (built into Windows 11).
- A built SUT image. Build it with the project's existing process, e.g.:
  ```powershell
  docker build -f ..\..\CRAFT\build\Dockerfile  -t ghcr.io/cyberdrain/craft:dev          ..\..\CRAFT
  docker build -f ..\..\CIPP-NG\build\Dockerfile -t cipp-ng:perf-baseline                 ..\..\CIPP-NG
  ```

## Usage

```powershell
# Baseline (current code)
pwsh scripts\run.ps1 -SutImage cipp-ng:perf-baseline -Label baseline

# After applying the optimization doc + rebuilding to cipp-ng:perf-optimized
pwsh scripts\run.ps1 -SutImage cipp-ng:perf-optimized -Label optimized

# Before/after diff
pwsh scripts\compare.ps1 results\baseline-*.json results\optimized-*.json

# Frontend-only: disable the PowerShell workers for pure static perf.
# Near-instant startup (no ~5 min CIPP module import) and no pool RAM/CPU.
pwsh scripts\run.ps1 -StaticOnly -Label frontend-only

# Per-page browser load test (real routes via headless Chromium): per-page transfer + FCP/LCP.
# Runs over HTTPS through a Caddy TLS proxy (stands in for Cloudflare) with static-only + dev-auth
# (canned superadmin /.auth/me + /api/me) so pages render without the PS backend — giving the REAL
# behavior: brotli assets + the production CSP (default-src https:) active. Browse it yourself at
# https://localhost:5443 while it runs (accept the self-signed cert).
pwsh scripts\run-pageload.ps1 -SutImage cipp-ng:split-public
```

`-StaticOnly` layers `docker-compose.staticonly.yml`, which sets **`CRAFT_SERVE_FRONTEND=true`** (the
Frontend deployment role) — with only that role set, the worker pool, scheduler, job manager and background
services are all disabled. The host
boots in seconds and `/api`,`/API`,`/.auth`,`/login`,`/logout` return 503. (Requires a CRAFT base built
with the flag.)

To isolate the pre-compression effect, build matched control images on the **same** base and compare:
```powershell
docker build -f ..\..\CIPP-NG\build\Dockerfile --build-arg PRECOMPRESS=0 -t cipp-ng:uncompressed ..\..\CIPP-NG
docker build -f ..\..\CIPP-NG\build\Dockerfile --build-arg PRECOMPRESS=1 -t cipp-ng:compressed   ..\..\CIPP-NG
pwsh scripts\run.ps1 -StaticOnly -Vus 20 -Duration 60s -SutImage cipp-ng:uncompressed -Label unc
pwsh scripts\run.ps1 -StaticOnly -Vus 20 -Duration 60s -SutImage cipp-ng:compressed   -Label cmp
pwsh scripts\compare.ps1 results\unc-*.json results\cmp-*.json
```

Or — with the runtime **compression toggle** (the `CRAFT_COMPRESSION` config value the host now exposes) —
A/B compressed vs raw serving on the **same** image with no rebuild. `-NoCompression` layers
`docker-compose.rawserve.yml`, which sets `CRAFT_COMPRESSION=false` so the host serves everything
raw/identity (no `.br`/`.gz` served, no on-the-fly compression):
```powershell
pwsh scripts\run.ps1 -StaticOnly                -Label comp-on  -SutImage cipp-ng:split-final
pwsh scripts\run.ps1 -StaticOnly -NoCompression -Label comp-raw -SutImage cipp-ng:split-final
pwsh scripts\compare.ps1 results\comp-on-*.json results\comp-raw-*.json
```
A downstream app can set the same value (`App:Frontend:Compression=false` or `CRAFT_COMPRESSION=false`) to
turn host compression off — e.g. when an upstream CDN already compresses, or the content doesn't benefit.

`run.ps1` parameters: `-SutImage`, `-Label`, `-Vus` (10), `-Duration` (30s), `-Port` (5197),
`-Cpus` (2), `-ReadyTimeoutSec` (900), `-StaticOnly`, `-NoCompression` (serve raw —
`CRAFT_COMPRESSION=false`), `-KeepUp` (leave containers running for manual poking).

Pre-compression size check (no app code, no SUT run needed):
```powershell
pwsh tools\measure-precompress.ps1 -Image cipp-ng:perf-baseline
```

## Why these settings (so before/after is fair and meaningful)

- **`ASPNETCORE_ENVIRONMENT=Production`** — the real Cloudflare-origin path. In `Development` the
  host runs a dev proxy that attempts `localhost:3000` on every asset request, which would pollute
  the measurement.
- **Azurite + small fixed worker pool (`HttpPoolSize=2`, `BgPoolSize=1`, `IgnoreSkuProfiles=true`)** —
  the PS pool must initialize before static assets are served (a startup gate in `Program.cs`).
  Pinning it small and identical across runs keeps the constant PS overhead out of the static-serving
  delta you're trying to see.
- **`CRAFT_LOG_LEVEL=Warning`** — the baked dev config logs at Trace to disk; that is neither
  production-representative nor constant under load. Warning keeps disk-IO noise out of the numbers.
- **`ReadinessMode=Immediate` + readiness probe** — Kestrel comes up immediately and the harness polls
  `robots.txt` until it returns `text/plain` (the real asset) rather than the `text/html` startup loading
  page, so measurement only starts once static serving is actually live. (Probing a static asset is more
  robust than the health endpoint, which can't respond within a short timeout while module import saturates CPU.)
- **`-Cpus 2` (cgroup pin)** — caps the SUT at 2 vCPU (≈ Azure B2 / P1v3) so CPU% is consistent and
  comparable across before/after runs. .NET honors the quota, so the app sees ~2 cores. Raise/lower with `-Cpus`.

These are **test-harness** settings (env overrides + a cgroup cap), not application changes.

## Interpreting the output

- **CPU %** is `docker stats` CPU, normalized to one core (e.g. `180` = 1.8 cores). The pre-compression
  change should drop CPU during the load because the origin stops Brotli-compressing the 23.5 MB chunk
  on the fly.
- **Responses chunked (on-the-fly)** counts responses with no `Content-Length` — i.e. compressed at
  request time. After pre-compression this should fall toward zero for static assets (they're sent
  as fixed-length precompressed files).
- **Header audit pass** rises as the `Cache-Control`/`ETag` fixes from the doc are applied
  (`sw.js`/`version.json` → `no-cache`, HTML `no-store`→`no-cache`, ETag on fallback, etc.).
- **App-chunk compression ratio** is `raw ÷ wire`; Brotli q11 (precomputed) should beat the current
  on-the-fly `Fastest`.

## Files

```
docker-compose.yml      azurite + SUT (SUT_IMAGE / SUT_PORT / SUT_CPUS parameterized)
docker-compose.staticonly.yml  override for -StaticOnly (CRAFT_SERVE_FRONTEND=true)
docker-compose.rawserve.yml    override for -NoCompression (CRAFT_COMPRESSION=false → raw/identity)
docker-compose.https.yml       Caddy TLS proxy override — HTTPS for the page-load test (br + real CSP)
pageload/Caddyfile             Caddy config (tls internal → reverse_proxy sut:8080)
k6/static_load.js       load test; targets read from /out/targets.json
scripts/run.ps1         orchestrator (up → ready → discover → audit → sample → k6 → report → down)
scripts/run-pageload.ps1  browser per-page load test (Playwright): real routes → transfer + FCP/LCP
pageload/pageload.mjs   Playwright script (cold-cache load of each route, records wire + timings)
pageload/routes.json    routes to load (regenerated per run from -Routes, filtered to existing pages)
scripts/discover.sh     in-container bundle/target discovery (piped via docker exec)
scripts/compare.ps1     before/after diff of two static result JSONs
tools/precompress.mjs   Brotli q11 + gzip 9 generator (the exact script the doc recommends shipping)
tools/measure-precompress.ps1  measure q11/gzip sizes on the real deployed assets
results/                outputs (gitignored)

── HTTP API mode (below) ──
docker-compose.api.yml  http-only SUT (CRAFT_SERVE_API=true) + PerfApi module mount
api-harness/API/Modules/PerfApi/  synthetic PS HTTP endpoints (dependency-free; not for prod)
k6/api_load.js          API load test (weighted endpoint mix or single-endpoint focus)
scripts/run-api.ps1     API orchestrator (up → /healthz → warm → sample → k6 → report → down)
scripts/compare-api.ps1 before/after diff of two API result JSONs (incl. per-endpoint p95)
```

---

## HTTP API mode (http-only)

Measures CRAFT in the **Http deployment role** (`CRAFT_SERVE_API=true`) — the PowerShell HTTP
dispatch pipeline `Kestrel → middleware → worker checkout → PS invoke → serialize → response`,
with no frontend, background, cache, or auth in the way. Same CRAFT image as everything else; the
mode is pure env vars and the test endpoints are mounted in — nothing is baked into a special image.

```powershell
# Build the generic CRAFT image once (or pass -Build / point -SutImage at any CRAFT image):
docker build -f ..\build\Dockerfile -t craft:local ..

pwsh scripts\run-api.ps1 -Label baseline                 # 10 VUs / 30s, pool 2, endpoint mix
pwsh scripts\run-api.ps1 -Label pool8 -Pool 8 -Vus 20    # more HTTP runspaces
pwsh scripts\run-api.ps1 -Label sleep -Only PerfSleep -Vus 40   # isolate one endpoint
pwsh scripts\run-api.ps1 -Label fixed -Rate 200 -Duration 60s   # fixed arrival rate (CPU at load)
pwsh scripts\compare-api.ps1 results\baseline-*.json results\pool8-*.json
```

**Synthetic endpoints** (`PerfApi` module → `/API/{name}`), each isolating one cost:

| endpoint | shape | isolates |
|---|---|---|
| `PerfPing` | returns immediately | dispatch-overhead floor |
| `PerfEcho` | echoes query/body | request marshaling |
| `PerfCpu?ms=N` | busy-loops N ms | CPU throughput / core scaling |
| `PerfSleep?ms=N` | `Start-Sleep` N ms | concurrency ceiling = `HttpPoolSize` (slow workers hold pool slots) |
| `PerfJson?n=N` | returns N-item array | serialization + payload size |

**Key lever — `-Pool` (`HttpPoolSize`):** the number of PS runspaces is the concurrency ceiling. If
throughput is flat as VUs rise while CPU sits below the core ceiling, the pool is the bottleneck, not
CPU. (Measured 2vCPU: pool 2 → 8 gave +169% req/s and −52% p95.) `-Rate N` drives a fixed arrival
rate so CPU-at-equal-load is comparable before/after; omit it (`-Vus N`) for peak throughput/latency.

Outputs, like the static harness, are `results/<label>-<timestamp>.json` + `.md` (adds a per-endpoint
latency table). No Azurite/storage — bare endpoints touch none; the response cache is off in http-only.
