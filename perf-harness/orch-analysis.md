# Orchestration fan-out & child-run worker-allocation profile

**What:** how efficiently a fan-out orchestration (and its child orchestrations) uses the BG worker pool.
Measured with `run-orch.ps1` — enqueues a parent orchestration of N tasks (each optionally sleeping `-TaskMs`
and/or spawning a `-ChildN`-task child), and polls `/API/jobs/allocation` at 4 Hz to build a timeline of BG
workers busy, the `BackgroundTaskLimiter` gate, and the JobManager queue. 2 vCPU, BG pool 8.

## The allocation model (from code)

```
parent orchestration → DispatchPendingTasks enqueues ALL N tasks to JobManager at once (one priority)
JobManager dispatch loop → BackgroundTaskLimiter.Acquire (the concurrency gate) → run task
   each task's work (holds a limiter slot for its whole duration):
     await UpsertTaskAsync("Running")   ← Azure Table write   (slot held, NO worker)
     ExecuteScript → CheckoutBackground ← borrows a BG worker  (slot held, worker busy)
     await StoreResultAsync(...)         ← Azure Table write   (slot held, NO worker)   [post-exec runs]
     fire-and-forget final table write
   child orchestration: a task calls Start-CraftOrchestrator → after the task, a new run is created and
     TryRegisterChildRun(parent, child); children enqueue their OWN tasks into the SAME pool/limiter.
   parent finalization waits until AllChildRunsComplete (CheckRunCompletion).
```

- **Limiter gate:** baseline `clamp(cores,2,4)`, ceiling = `BgPoolSize`. Scales up only after the queue has
  been backed up for `ScaleUpAfter` (default **15 s**), doubling each 10 s monitor tick. HTTP pressure can
  throttle it back to 2.

## Findings

### 1. The limiter ramp is too slow for bursty fan-out — short bursts never scale up

1000 tasks (20 ms each), pool 8:

| limiter | completion | ramp to ceiling | BG busy (avg/peak of 8) | worker idle |
|---|--:|--:|--:|--:|
| **default (base 2, scale-up 15 s)** | **12.5 s** | **never** | **1.7 / 2** | **78.6 %** |
| immediate (base 8, scale-up 1 s) | **4.6 s** | 0.03 s | 4.9 / 7 | 38.2 % |

The whole 1000-task fan-out finished (12.5 s) **before** the 15 s scale-up threshold, so the limiter **never
scaled past baseline 2** — the burst ran 2-wide on an 8-worker pool with **~78 % of workers idle**. Starting
at the ceiling is **2.7× faster**. Any orchestration that fans out and drains in under `ScaleUpAfter` (most of
them) is silently throttled to baseline concurrency.

### 2. The limiter slot is held across the per-task Azure Table writes → workers idle even at ceiling

`slot>worker gap` = limiter-admitted tasks that are **not** on a worker (they're in the awaited table writes).
Even at immediate ceiling it averaged **3.06** — i.e. ~3 of the 8 admitted slots were blocked on table I/O at
any moment, so only ~5 workers were busy. The concurrency gate counts *total task wall-time* (table writes +
invoke), but the scarce resource is the *worker*, borrowed only for the invoke. With the ceiling pinned to
pool size there's no headroom to fill the I/O gaps.

### 3. High child fan-out is table-I/O-bound — 93 % worker idle

50 parents × 20-task children (1050 task invokes), immediate ceiling:

| metric | value |
|---|--:|
| BG busy (avg/peak of 8) | **0.56 / 5** |
| worker idle | **93.1 %** |
| slot>worker gap | **7.44** (≈ all 8 slots held with no worker) |

The timeline ends with **124 tasks queued and 0 workers busy** — the 8 limiter slots are entirely consumed by
per-task table-write latency (each task does a "Running" marker + result write under Azurite load), so the
fan-out is gated by **Azure Table write throughput, not worker throughput**. This is exactly CIPP's shape
(thousands of short per-tenant tasks), and it means the BG pool is nearly idle while the orchestration is
"busy". The per-task writes serialize the whole fan-out.

## Optimization opportunities (ranked)

1. **Fill the pool on a fan-out burst instead of waiting 15 s to ramp.** Options: much lower `ScaleUpAfter`,
   higher baseline, or (best) scale straight to ceiling when a large batch is enqueued (burst detection). Free
   ~2.7× on typical fan-outs. Low risk — the ceiling is still `BgPoolSize`.
2. **Gate the limiter on worker use, not total task wall-time.** Either move the awaited table writes off the
   slot-held critical section (fire-and-forget the "Running" marker + result, keeping crash-recovery via the
   existing idempotent re-read), or let the limiter admit more than `BgPoolSize` tasks so workers stay full
   while others do I/O. Recovers the `slot>worker gap` (3–7 idle workers).
3. **Get the remaining per-task status writes off the critical path.** Creation is *already* batched
   (`UpsertTaskBatchAsync`, 100/txn) and the final Completed/Failed + run writes are *already* fire-and-forget
   (`PersistTaskAndRunAsync`). What still holds the limiter slot is the per-task **"Running" marker** (always,
   awaited) and the **result** write (post-exec, awaited) — single-entity upserts, ~1–2 per task, the actual
   bottleneck under child fan-out. Replace these with a **durable coalescing writer** (batch status
   transitions per-partition, ≤100) with flush-on-interval / on-shutdown / before-finalize so no status
   transition or `AttemptCount` (the `MaxRetries` cap) is ever dropped — i.e. batch without missing orc-status
   writes. This is the biggest lever for CIPP-scale fan-out, and it composes with #2 (over-subscribe) above.

## Applied: #1 burst-to-ceiling (win) + over-subscription dial (marginal — confirms the real bottleneck)

Both are configurable (default off/0, preserving today's conservative behavior):
`BackgroundBurstToCeiling` (bool) and `BackgroundOverSubscribe` (int), alongside the existing
`BackgroundBaseConcurrency` / `BackgroundScaleUpAfterSeconds` / `BackgroundMaxConcurrency`.

**#1 Burst-to-ceiling — clear 2.7× win.** Jumps `_currentMax` straight to the ceiling the moment tasks queue,
instead of the 15 s ramp. 1000 tasks / pool 8: **12.5 s → 4.6 s**, identical to pinning baseline=ceiling, but
opt-in and it still falls back to baseline when idle.

**#2 Over-subscription — marginal, and it tells us why.** The dial admits `ceiling + N` tasks so N can do their
pre-invoke "Running" table write and queue at the worker checkout while the pool stays full. Swept 0→8
(400 tasks, taskMs 20, pool 8):

| overSub | avg BG busy /8 | worker idle |
|--:|--:|--:|
| 0 | 5.0 | 37.5 % |
| 2 | 5.14 | 35.7 % |
| 4–8 | 5.14 | 35.7 % (flat) |

It recovers only ~1 idle worker and **plateaus at N=2** — because the per-task Azure Table write is
**throughput-bound** (on Azurite here), not latency-per-slot. Admitting more tasks just deepens the write
queue (the `slot>worker gap` grows 3→11) without freeing workers faster. Worker utilization for short-task
fan-out is capped at ~64 % by table-write throughput. (When the write is a *small* fraction — taskMs 100 —
over-subscription does reach ~98 % utilization, idle 8 %→2.4 %; it only helps to the extent there's idle that
isn't itself write-throughput-bound.)

**Conclusion:** #1 is a real, shippable win. Over-subscription is a safe, configurable knob that helps
lightly and does no harm at low values, but it **confirms that the per-task table writes (#3), not the
concurrency gate, are the true bottleneck** for CIPP-scale short-task fan-out. (On real Azure Table Storage —
higher write throughput/concurrency than the local emulator — over-subscription would likely recover more;
the robust fix remains reducing the writes.)

## Applied: #3 batched status writer (the throughput fix)

`OrchestratorStatusWriter` coalesces per-task/run **status** transitions and flushes them in ≤100-entity,
byte-budgeted Azure Table transactions, off the fan-out critical path. **Results are untouched** — the
`StoreResultAsync` property-chunking / multi-row large-payload path is left exactly as-is (results aren't even
involved in no-post-exec fan-out, and their size makes them unsafe to batch). Config under `App:Orchestrator`:
`BatchStatusWrites` (default true), `DurableRunningBarrier` (default true), `StatusFlushIntervalMs` (25).

Two durability modes for the pre-invoke "Running" marker:
- **Durable barrier (default):** the marker is written under a synchronous barrier — batched across
  concurrently-starting tasks but still persisted *before* the invoke, so `AttemptCount`/`MaxRetries` still
  bound poison tasks. Terminal states + run status are guaranteed flushed **before finalize** and **on
  shutdown**, so no status write is ever lost.
- **Eventual (`DurableRunningBarrier=false`):** the marker rides the periodic flush; the task doesn't wait for
  it. Terminal/run states are still durable (flush-before-finalize). Trades the strict poison-before-invoke
  guarantee for maximum throughput.

Results (1000 tasks, burst, taskMs 20, pool 8; correctness verified separately: 500/500 completed, 0 failed):

| mode | completion | BG busy /8 | worker idle |
|---|--:|--:|--:|
| per-task write (before) | 4.6 s | 4.7 | 42 % |
| batched, durable barrier | 4.5 s | 4.5 | 44 % |
| **batched, eventual** | **3.0 s** | **8.0** | **0 %** |

**Eventual mode reaches 100 % pool utilization (−35 % completion)** — the per-task write no longer gates the
worker; tasks go straight to a runspace. **The durable barrier is a wash *on Azurite*** because the barrier
wait replaces the individual-write latency, and Azurite's transaction cost scales with entity count. On real
Azure Table Storage a 100-entity transaction is ~one round-trip, so the durable barrier should approach the
eventual throughput while keeping the guarantee — but that can't be shown against the local emulator, so the
harness understates the default mode (same Azurite caveat as over-subscription).

**Net for the stack (all default-on, opt-in for eventual):** burst-to-ceiling gets the pool full immediately
(2.7×), and the batched writer — in eventual mode, or durable on real Azure Table — takes the fan-out from
~40 % idle to worker-bound (100 % utilization). #2 over-subscription remains a minor knob.

## Reproduce

```powershell
pwsh scripts\run-orch.ps1 -Tasks 1000 -TaskMs 20 -BgPool 8                 # default ramp (2-wide, idle pool)
pwsh scripts\run-orch.ps1 -Tasks 1000 -TaskMs 20 -BgPool 8 -Base 8 -ScaleUp 1   # immediate ceiling (2.7x)
pwsh scripts\run-orch.ps1 -Tasks 50 -ChildN 20 -TaskMs 20 -BgPool 8        # child fan-out (table-I/O bound)
```
