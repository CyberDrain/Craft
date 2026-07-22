# CRAFT http-only dispatch pipeline — analysis & measurements

**What:** where time goes when CRAFT serves a `/API/{endpoint}` PowerShell request, measured with the
http-only harness (`run-api.ps1`) + an opt-in per-segment profiler (`CRAFT_DISPATCH_TIMING=true`,
`Services/DispatchProfiler.cs`). All numbers: 2 vCPU, `craft:local`, `PerfPing` (a no-op PS function, so the
measurement is pure pipeline overhead, no user work).

## The pipeline (per request)

```
Kestrel ─▶ /API/{endpoint} handler ─▶ ExecuteHttpScript
   ├─ marshal   BuildRequestFromParts  → $Request hashtable (Query/Headers/Params, header ToLowerInvariant)
   ├─ checkout  pool.CheckoutHttp      → borrow an HTTP runspace (blocks if none free)
   ├─ invoke    worker.InvokeAsync     → AddCommand/AddParameter, BeginInvoke/EndInvoke, collect output
   │     ├─ pipeline  the PS engine run itself
   │     └─ cleanup   Commands.Clear + ClearStreams + CleanupGlobalVariables + CleanupJobs (every request)
   ├─ extract   ExtractResponse        → pull {StatusCode,Body}, ConvertPsObjectToJson
   └─ handler   write headers + body; scan body ×2 for _orchestratorTrigger / _scriptTrigger
```

## Measurement 1 — uncontended floor (pool 1, 1 VU, fully serial)

The clean per-request cost with no queueing:

| segment | µs/req | share | note |
|---|--:|--:|---|
| **invoke — PS pipeline** | **493** | **78%** | PowerShell `BeginInvoke`/`EndInvoke` of a no-op function |
| cleanup | 36 | 6% | `CleanupGlobalVariables` walks the runspace var table each request |
| marshal | 11 | 2% | build `$Request` (Query/Headers/Params hashtables) |
| extract | 10 | 2% | response object → JSON |
| checkout | 6 | 1% | uncontended `BlockingCollection.Take` |
| **total (server-side)** | **635** | | k6 end-to-end 1.16 ms (≈0.5 ms is loopback/Kestrel) |

**The dispatch cost is the PowerShell invoke itself (~0.5 ms).** The C# glue — marshaling, checkout, cleanup,
response extraction — is ~63 µs combined (~10% of server time, ~4% of end-to-end). Serial throughput 802 rps.

> The suspected hotspot `CleanupGlobalVariables` (enumerates the global-variable provider every request) is
> only 36 µs in a clean runspace — **not** a bottleneck here. Caveat: it scales with the number of global
> variables, so a runspace that accumulates many globals (e.g. a heavy app module) would pay more.

## Measurement 2 — saturated (pool 4, 20 VUs)

| segment | µs/req |
|---|--:|
| checkout | **8633** |
| invoke (pipeline) | 2119 (2049) |
| marshal / extract / cleanup | 19 / 10 / 64 |
| total | 11013 |

When there are more concurrent requests than workers (20 vs 4), **checkout becomes queue-wait and dominates**
(8.6 ms of 11 ms). This is not overhead — it's requests waiting for a free runspace. The lever is pool size,
not the code path.

## Measurement 3 — pool vs cores (CPU-bound PerfPing, 20 VUs, 2 vCPU)

| pool | req/s | p95 (ms) | vs previous |
|--:|--:|--:|---|
| 1 | 1011 | 49 | — |
| 2 (= cores) | 1291 | 59 | **+28 %** |
| 8 | 1362 | 60 | +5.5 % |

And the "matched" profile (pool 4 on 2 cores) shows the PS pipeline segment inflating **493 → 1316 µs/req** —
oversubscribing the pool past core count makes each invoke slower under CPU contention *without* adding
throughput.

## Conclusions

1. **The dispatch floor is PowerShell, not CRAFT's C#.** ~0.5 ms/call is the PS `BeginInvoke`/`EndInvoke`
   engine cost for even a no-op. Marshaling, checkout, cleanup and serialization are already cheap (~4% of
   end-to-end) — micro-optimizing them won't move the needle for typical endpoints.
2. **Throughput is CPU-bound for CPU/dispatch-bound endpoints** (~1.3 k rps for a no-op on 2 cores). The
   right `HttpPoolSize` is **≈ vCPU count**; going higher gives diminishing returns and inflates latency.
   Pool **> cores only pays off for I/O-bound endpoints** (e.g. `PerfSleep`), where workers block and extra
   runspaces absorb concurrency. (This validates the existing `SkuProfiles` pool-by-tier scaling.)
3. **Under-provisioned pool → queue wait dominates latency.** If throughput is flat as load rises while CPU
   is below the core ceiling, add workers; if CPU is already saturated, add cores (or cut per-call work).

## Where a real optimization would have to come from

Given (1), a meaningful reduction in per-request cost means attacking the ~0.5 ms PS floor, not the glue:

- **Cheapest safe win (~6%): make `cleanup` proportional to work.** Skip `CleanupGlobalVariables` /
  `CleanupJobs` when the invocation created no globals/jobs (track via a cheap pre/post count), instead of
  always walking the provider. Small here (36 µs), larger for global-heavy app modules.
- **Response-body trigger scan.** The handler does two `string.Contains` over the whole response body per
  request for `_orchestratorTrigger`/`_scriptTrigger`. Negligible for small bodies; for large responses
  (`PerfJson`) it's two full scans — gate behind a cheap length/first-char check or a response flag.
- **The pipeline floor itself** (`BeginInvoke`/`EndInvoke`, output-collection copy) is largely intrinsic to
  the PowerShell SDK. Reducing it would need an architectural change (e.g. a persistent pre-built pipeline,
  or bypassing PS for pure-C# endpoints) and should be measured against this baseline before committing.

## Optimization experiments (against the ~493 µs pipeline)

Four candidates, each env-gated (`CRAFT_OPT_*`, default off) and measured **individually** at pool 1 / 1 VU
(uncontended serial) with the `build`/`run`/`copy` invoke split. The split immediately localized the cost:
of the 609 µs total, **`run` (BeginInvoke→EndInvoke) = 472 µs (78%)**; `build` (AddCommand/AddParameter) was
2.7 µs, `copy` 1.6 µs — so anything targeting command building/resolution is chasing noise.

| opt | total µs | run µs | serial rps | verdict |
|---|--:|--:|--:|---|
| none (baseline) | 609 | 472 | 809 | — |
| **#1 `ReuseThread`** | **365 (−40%)** | **235 (−50%)** | **1090 (+35%)** | ✅ **the win** |
| #2 `NoAutoLoad` (`$PSModuleAutoLoadingPreference='None'`) | 617 | 476 | 823 | ✗ no effect — pre-imported functions don't probe modules. **Also breaks CIPP lazy module loading — do not ship.** |
| #3 `CmdInfo` (cache `CommandInfo`, `AddCommand(ci)`) | 631 | 475 | 820 | ✗ no effect — `build` was already 2.7 µs |
| #4 `PersistentStreams` (attach 5 handlers once/worker) | 619 | 481 | 823 | ✗ no effect — per-request stream wiring wasn't a real cost |

**Root cause:** the default runspace `ThreadOptions` spins a **new thread per `BeginInvoke`** (~237 µs). Setting
`Runspace.ThreadOptions = PSThreadOptions.ReuseThread` reuses the runspace's dedicated pipeline thread across
invocations (exactly the pooled-worker pattern), halving the `run` segment.

### ReuseThread under real load (0 failures across all endpoint types)

| scenario | baseline | ReuseThread | change |
|---|--:|--:|--:|
| PerfPing serial (pool 1, 1 VU) | 809 rps | 1090 rps | **+35%** |
| **PerfPing concurrent (pool 2 = cores, 20 VU)** | 1261 rps | **2122 rps** | **+68 %**, latency −41% |
| Mixed endpoints (pool 4, 20 VU) | 275 rps @ 147% CPU | 286 rps @ **127% CPU** | +4% rps, **−13% CPU** |

The gain scales with how dispatch-bound the endpoint is: for light, fast handlers (most real CRUD/API calls
that just marshal + call Graph) the ~237 µs thread saving is a large fraction → big throughput win. For heavy
handlers (busy-loop/serialize) the workload dominates, so the saving shows up as **lower CPU** instead.

### Do #2/#3/#4 help once paired with ReuseThread? — No

Re-tested each **combined with ReuseThread** under the heavy concurrent test (PerfPing, pool 2 = cores,
20 VU, mean of 3 runs), in case removing the thread-creation cost exposed a smaller one:

| config | mean rps | vs rt-alone | CPU |
|---|--:|--:|--:|
| reusethread (paired baseline) | 2233 | — | 171.5% |
| reusethread + noautoload | 2092 | −6% | 171.3% |
| reusethread + cmdinfo | 2057 | −8% (noisy) | 171.1% |
| reusethread + streams | 2232 | ≈0% | 172.9% |

No additive gain — `noautoload`/`cmdinfo` are slightly *worse* (extra work, no benefit; `cmdinfo` was noisy
with a 1876 outlier), `streams` is neutral. **ReuseThread captures the entire available win; #2/#3/#4 are
dead ends both solo and paired.**

**Outcome (applied):** `ReuseThread` is now the config-backed **default** — `App:Worker:ReuseRunspaceThread`
(default `true`, `PowerShellWorker.Initialize`). It matches how Azure Functions' PowerShell worker keeps a
persistent runspace, is safe (each worker owns one runspace and serves one request at a time), and is
validated across ping/echo/cpu/sleep/json and under concurrency. The #2/#3/#4 experiment code was **removed**
as proven no-ops (and #2 is unsafe for lazy loading). The `CRAFT_DISPATCH_TIMING` profiler stays as an
opt-in diagnostic.

## Background workers get the same win (already applied)

The BG path is structurally the HTTP path minus request marshaling: a timer trigger (`SchedulerService`) or an
orchestrator-enqueued task (`OrchestratorService` → `JobManager`) calls `ExecuteScript` /
`ExecuteScriptWithOutput`, which call the **same** `worker.InvokeAsync` on a worker built by the **same**
`Initialize`. So `ReuseThread` applied to BG workers automatically when it became the default — no separate
change.

Measured with `run-bg.ps1` (backend mode + Azurite; enqueues an orchestrator batch of no-op tasks — each task
= one BG invoke of `Invoke-CraftTask` → `Push-PerfBg`), steady-state window, 2 vCPU / BgPool 4:

| segment (µs/invoke) | ReuseThread **on** | **off** | saving |
|---|--:|--:|--:|
| **run (PS pipeline)** | **792** | **1135** | **−343 (−30%)** |
| invoke total | 862 | 1202 | −340 |
| **total dispatch** | **983** | **1314** | **−331 (−25%)** |
| checkout / cleanup / build | 12 / 57 / 5 | 11 / 56 / 3 | ~0 |
| marshal / extract | 0 / 0 | 0 / 0 | — (no HTTP request) |

Same fixed ~340 µs/invoke thread-creation saving as HTTP (BG `run` is higher in absolute terms only because
`Invoke-CraftTask` does real work — JSON parse + nested call + serialize — not because dispatch differs). No
BG-specific dispatch optimization exists beyond this; the remaining per-task wall-time is **orchestrator
Azure Table I/O** (run/task/result rows), which the profiler's `run` segment deliberately excludes.

Reproduce: `pwsh scripts\run-bg.ps1 -Tasks 5000` (after) vs `-NoReuseThread` (before).

## Reproduce

The reused-thread optimization is on by default; `run-api.ps1 -NoReuseThread` runs the "before".

```powershell
# uncontended floor (per-segment breakdown printed + saved):
pwsh scripts\run-api.ps1 -Label floor  -Only PerfPing -Pool 1 -Vus 1  -Duration 20s -DispatchTiming
# A/B the reused pipeline thread (after vs before):
pwsh scripts\run-api.ps1 -Label after  -Only PerfPing -Pool 2 -Vus 20 -Duration 20s
pwsh scripts\run-api.ps1 -Label before -Only PerfPing -Pool 2 -Vus 20 -Duration 20s -NoReuseThread
pwsh scripts\compare-api.ps1 results\before-*.json results\after-*.json
# pool vs cores:
foreach($p in 1,2,8){ pwsh scripts\run-api.ps1 -Label ping-p$p -Only PerfPing -Pool $p -Vus 20 -Duration 15s }
```
