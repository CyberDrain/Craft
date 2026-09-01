# Orchestrator memory-boundedness & OOM resilience

**What:** does the durable-queue orchestrator keep memory bounded under a massive fan-out, and does the
job-dispatch machinery keep going when the workload creates real heap pressure — up to and including an
out-of-memory condition? Measured with `run-oom.ps1` (backend mode + Azurite), which brings CRAFT up under
an optional GC heap hard limit (`CRAFT_GC_HEAP_LIMIT_MB`), enqueues N tasks that allocate a large-object
(LOH) buffer and hold it, and polls `/API/PerfAllocation` (a PerfApi wrapper over
`WorkerMetricsBridge.GetSnapshot()`) tracking peak heap, task completions/failures, and whether the
dispatch loop keeps making progress. 2 vCPU, BG pool 8, burst-to-ceiling, pump batch 100.

## 1. Memory is bounded by the buffer, not the backlog

A 20,000-task fan-out (2,500× the 8-worker pool), no-op tasks, unconstrained:

| metric | value |
|---|--:|
| baseline heap (idle) | 15 MB |
| **peak heap while 20k drained** | **64 MB** |
| peak container RSS | 180 MB |
| all tasks completed | 20,000 / 20,000 |

The managed heap oscillated 41–64 MB for the entire drain while the durable backlog fell from 19,900 to
0. **Heap does not scale with fan-out size** — the backlog lives in the `Queue` table and the JobManager
holds only a pool-sized (+batch) buffer. This is the core promise of the durable-queue design, and it
holds: a run of any size costs O(buffer) memory, not O(N).

## 2. A GC heap hard limit is back-pressure, not a crash — while the *live* set fits

Tasks that allocate a 48–90 MB LOH buffer and hold it, 8 concurrent, under a hard limit at or below the
natural working set:

| alloc/task | hold | GC limit | peak heap | task OOMs | outcome |
|--:|--:|--:|--:|--:|---|
| 48 MB | 300 ms | 256 MB | 221 MB | 0 | all 1000 done — GC collected released buffers, stayed under |
| 48 MB | 1500 ms | 256 MB | 217 MB | 0 | all 400 done |
| 48 MB | 300 ms | **160 MB** | **118 MB** | 0 | all 500 done — a *tighter* limit made the GC hold the live set *lower* |
| 90 MB | 500 ms | 160 MB | 112 MB | 0 | all 200 done |

The .NET GC hard limit does **not** OOM as long as the *live* (simultaneously-referenced) set fits: it
collects aggressively and throttles allocation (gc2 counts explode — hundreds of full GCs) to stay under
the cap. Because the harness tasks release their buffer promptly and the dispatch/storage pipeline staggers
the 8 workers, only ~1–3 buffers are ever live at once, so the workload runs to completion at a bounded
heap even when the limit is well below `pool × alloc`. **The limit trades throughput (GC overhead) for a
memory ceiling — it does not, on its own, take the process down.**

## 3. A *fatal* OOM does crash the process — and only restart+recovery finishes the run

Push the per-task allocation close to the whole limit so even one live buffer plus the orchestrator's own
concurrent allocations cannot fit — **230 MB/task against a 256 MB limit, 8 concurrent**:

- **Catchable task OOMs are handled and dispatch keeps going.** Dozens of
  `PS error in Invoke-CraftTask: …OutOfMemoryException` — the 230 MB allocation threw, the task's
  try/catch marked it Failed, and the loop dispatched the next task (completions continue in the log).
  The dispatch guards work for a catchable OOM.
- **But a fatal runtime OOM is uncatchable.** When the GC hard limit is exhausted to where the runtime
  can't allocate for its *own* operation, .NET FailFasts: the process aborted with **exit 139 (SIGSEGV)**,
  `OOMKilled=false`, last line a bare `Out of memory.` — at task 80/150. No user `try/catch` (nor the
  pump's cycle guard, nor the status writer's `LogSafely`) can prevent a runtime FailFast; it takes the
  dispatch loop down with the process.
- **Durable crash-recovery is what finishes the work.** Restarting against the same Azurite state:
  `Found interrupted run … Released 54 stale claim(s) held by the previous process … Resuming interrupted
  run: 50 pending`. With recovery pacing the re-dispatch, the 230 MB allocations ran at low enough
  concurrency to fit (250 < 256 MB) and completed; heap returned to ~21 MB and the process stayed up.

## Conclusions

1. **Memory is bounded** — a 20k fan-out peaks at ~64 MB managed heap; the backlog is in the table, the
   JobManager buffer is O(batch). The design's central claim holds.
2. **The dispatch loop survives everything it *can* survive.** A GC heap hard limit is back-pressure while
   the live set fits (no OOM); a catchable task OOM is caught, the task fails, and dispatch continues.
3. **A fatal .NET OOM is the one thing no guard can catch** — the runtime FailFasts the whole process.
   The resilience boundary is therefore *process restart + durable crash-recovery*, which is implemented
   and proven: interrupted runs resume, stale claims are released, and the run reaches a terminal state.
4. **Practical guidance:** keep per-task peak allocation a small fraction of `GCHeapLimit ÷ BgPoolSize` so
   the GC's back-pressure (not a fatal OOM) is what bounds memory. When a task genuinely can OOM, the
   safety net is the container restart policy plus recovery — so a heap limit should be paired with a
   restart-on-exit host (Azure App Service restarts on crash) and the per-task `MaxRetries` cap, which
   turns a poison (repeatedly-OOMing) task Failed after a few attempts instead of crash-looping forever.

## Reproduce

```powershell
# 1. Bounded memory under a massive fan-out (no OOM):
pwsh scripts\run-oom.ps1 -Tasks 20000 -Label bounded

# 2. GC hard limit as back-pressure (real LOH work, still completes):
pwsh scripts\run-oom.ps1 -Tasks 500 -AllocMB 48 -HoldMs 300 -HeapLimitMB 160 -Label backpressure

# 3. Force a fatal OOM and observe crash + recovery (KeepUp to inspect, then `docker start` the container):
pwsh scripts\run-oom.ps1 -Tasks 150 -AllocMB 230 -HoldMs 200 -HeapLimitMB 256 -Label fatal -KeepUp
```
