# PerfApi — synthetic HTTP endpoints for load-testing CRAFT in http-only mode.
#
# Each function is a CRAFT HTTP endpoint: it takes ($Request, $TriggerMetadata) and returns a hashtable
# @{ StatusCode; Body } (CRAFT's response extractor is duck-typed, so no [HttpResponseContext] is needed).
# The function name (minus the Invoke- prefix) becomes the route, e.g. Invoke-PerfPing -> /API/PerfPing.
#
# The five endpoints deliberately isolate different parts of the dispatch pipeline — see README.

# Pure dispatch overhead: returns immediately. Measures the fixed cost of the whole path
# (Kestrel -> middleware -> worker checkout -> PS invoke -> serialize -> response).
function Invoke-PerfPing {
    param($Request, $TriggerMetadata)
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfPing' } }
}

# Request-marshaling cost: echoes the query and body back, exercising BuildRequestObject.
function Invoke-PerfEcho {
    param($Request, $TriggerMetadata)
    $q = @{}
    if ($Request.Query) {
        foreach ($k in $Request.Query.Keys) { $q[$k] = $Request.Query[$k] }
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfEcho'; query = $q; body = $Request.Body } }
}

# CPU-bound: busy-loops for ~ms milliseconds. Measures per-core throughput and pool saturation
# under CPU load. Query: ?ms=N (default 20).
function Invoke-PerfCpu {
    param($Request, $TriggerMetadata)
    $ms = 20
    if ($Request.Query.ms) { $ms = [int]$Request.Query.ms }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $acc = 0.0
    while ($sw.ElapsedMilliseconds -lt $ms) {
        for ($i = 0; $i -lt 5000; $i++) { $acc += [math]::Sqrt($i) }
    }
    $sw.Stop()
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfCpu'; requestedMs = $ms; actualMs = $sw.ElapsedMilliseconds } }
}

# I/O-wait simulation: sleeps for ~ms milliseconds. A sleeping worker holds its pool slot, so this
# exposes HttpPoolSize as the concurrency ceiling (extra concurrent requests queue on checkout,
# then 503 after 30s). Query: ?ms=N (default 100).
function Invoke-PerfSleep {
    param($Request, $TriggerMetadata)
    $ms = 100
    if ($Request.Query.ms) { $ms = [int]$Request.Query.ms }
    Start-Sleep -Milliseconds $ms
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfSleep'; sleptMs = $ms } }
}

# BG-worker / orchestration driver: enqueue an orchestrator batch of N tasks (the real "orchestrator enqueue"
# path). Each task dispatches to a BG worker as Invoke-CraftTask -> Push-PerfBg, so N tasks = N BG PS invokes.
# Query:
#   n=N        batch size (default 500)
#   taskms=M   per-task work: Start-Sleep M ms inside each task (0 = no-op; simulates Graph-call latency)
#   childn=C   if >0, each task spawns a CHILD orchestration of C no-op tasks (fan-out dependency)
function Invoke-PerfBgEnqueue {
    param($Request, $TriggerMetadata)
    $n = 500;  if ($Request.Query.n) { $n = [int]$Request.Query.n }
    $taskms = 0; if ($Request.Query.taskms) { $taskms = [int]$Request.Query.taskms }
    $childn = 0; if ($Request.Query.childn) { $childn = [int]$Request.Query.childn }
    $allocmb = 0; if ($Request.Query.allocmb) { $allocmb = [int]$Request.Query.allocmb }
    $holdms = 0; if ($Request.Query.holdms) { $holdms = [int]$Request.Query.holdms }
    $batch = @(for ($i = 0; $i -lt $n; $i++) { @{ FunctionName = 'PerfBg'; idx = $i; taskms = $taskms; childn = $childn; allocmb = $allocmb; holdms = $holdms } })
    $run = Start-CraftOrchestrator -InputObject @{
        OrchestratorName = "PerfBg-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Batch            = $batch
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; enqueued = $n; taskms = $taskms; childn = $childn; run = $run } }
}

# The background task each orchestrator batch item runs (Invoke-CraftTask calls Push-{FunctionName}).
# Sleeps taskms to simulate work; allocmb/holdms allocate a large object (LOH) and hold it to create real
# heap pressure across concurrent workers; optionally spawns a child orchestration (fan-out dependency).
function Push-PerfBg {
    param($Item)
    if ($Item.taskms -and [int]$Item.taskms -gt 0) { Start-Sleep -Milliseconds ([int]$Item.taskms) }

    # Real memory pressure: a single large byte[] lands on the Large Object Heap (>85 KB), touched so the
    # pages are actually committed, then held so concurrent tasks pile up live memory at once. Under a GC
    # heap hard limit smaller than (workers x allocmb), the allocation throws OutOfMemoryException — which
    # the orchestrator must catch as a task failure WITHOUT taking the dispatch loop down with it.
    if ($Item.allocmb -and [int]$Item.allocmb -gt 0) {
        $buf = [byte[]]::new([int]$Item.allocmb * 1MB)
        for ($i = 0; $i -lt $buf.Length; $i += 4096) { $buf[$i] = 1 }
        if ($Item.holdms -and [int]$Item.holdms -gt 0) { Start-Sleep -Milliseconds ([int]$Item.holdms) }
        $buf = $null
    }

    if ($Item.childn -and [int]$Item.childn -gt 0) {
        $cn = [int]$Item.childn
        $childBatch = @(for ($j = 0; $j -lt $cn; $j++) { @{ FunctionName = 'PerfBgLeaf'; idx = $j } })
        Start-CraftOrchestrator -InputObject @{
            OrchestratorName = "PerfBgChild-$($Item.idx)-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
            Batch            = $childBatch
        } | Out-Null
    }
    return @{ ok = $true; idx = $Item.idx }
}

# Leaf task for child orchestrations (no further fan-out).
function Push-PerfBgLeaf {
    param($Item)
    return @{ ok = $true; idx = $Item.idx }
}

# Run-COUNT axis driver (run-manyruns.ps1). The OOM harness above tests one run of N tasks (fan-out
# WIDTH); this creates MANY separate runs that stay live (fan-out COUNT), reproducing the production shape
# where thousands of scheduled runs sit in _activeRuns — each pinning a per-run 60s status Timer + its
# task graph, and each re-reading the queue index on every tick. Memory is bounded in run-width but scales
# with run-COUNT, which this exercises.
#
# Each run is a small batch of PerfHold tasks that sleep for the whole observation window, so with a tiny
# BG pool only a few are ever claimed and the rest sit Pending — the run never finalizes, so it stays in
# _activeRuns with its timer firing every 60s. Called in chunks by the harness to avoid a long single HTTP
# request. Query:
#   runs=M      how many runs to create THIS call (default 200)
#   tasks=K     tasks per run (default 4) — K > (claimable) keeps each run with Pending work forever
#   holdms=H    per-task sleep so a claimed task never completes during the window (default 3600000 = 1h)
#   paramkb=P   per-task payload string in KB (default 0) — inflates each OrchestratorTaskItem.Parameters
#               so the retained run graph is production-weight (real runs carry tenant/audit data, not no-ops).
#               Retained memory then scales as M x K x paramkb, which is the axis that reaches the heap ceiling.
#   prefix=P    run-name prefix (default PerfHold) so the harness can group a wave
function Invoke-PerfManyRuns {
    param($Request, $TriggerMetadata)
    $runs = 200;     if ($Request.Query.runs) { $runs = [int]$Request.Query.runs }
    $tasks = 4;      if ($Request.Query.tasks) { $tasks = [int]$Request.Query.tasks }
    $holdms = 3600000; if ($Request.Query.holdms) { $holdms = [int]$Request.Query.holdms }
    $paramkb = 0;    if ($Request.Query.paramkb) { $paramkb = [int]$Request.Query.paramkb }
    $func = 'PerfHold'; if ($Request.Query.func) { $func = [string]$Request.Query.func }  # PerfHold | PerfCheck
    $prefix = 'PerfHold'; if ($Request.Query.prefix) { $prefix = [string]$Request.Query.prefix }
    $seq = ([string]$Request.Query.seq -eq 'true')  # sequential: one task at a time, payload order
    $payload = if ($paramkb -gt 0) { 'x' * ($paramkb * 1024) } else { $null }

    $created = 0
    for ($r = 0; $r -lt $runs; $r++) {
        $batch = @(for ($i = 0; $i -lt $tasks; $i++) {
            # marker = 'm'+idx lets Push-PerfCheck confirm the payload survived shed→rehydrate at dispatch.
            $item = @{ FunctionName = $func; idx = $i; holdms = $holdms; marker = ('m' + $i) }
            if ($payload) { $item['payload'] = $payload }
            $item
        })
        Start-CraftOrchestrator -InputObject @{
            OrchestratorName = "$prefix-$([guid]::NewGuid().ToString('N').Substring(0, 10))"
            Batch            = $batch
            Sequential       = $seq
        } | Out-Null
        $created++
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfManyRuns'; created = $created; tasksPerRun = $tasks; holdms = $holdms; paramkb = $paramkb; sequential = $seq } }
}

# Table manipulation for failure-mode exploration: delete/inspect orchestrator table rows WHILE runs are
# live, to see whether Craft survives losing state under it. Uses the same storage connection the app uses.
#   op=count        rows in a partition (needs table[,pk])
#   op=list         first rows of a partition (table[,pk]) — RowKeys only
#   op=deleteRow    delete one entity (table,pk,rk)
#   op=deletePart   delete every row in a partition (table,pk) — e.g. a run's Tasks or Queue-index partition
#   op=tables       list table names
# Tables (prefix PerfBgOrch): PerfBgOrchRuns (pk 'Run'), PerfBgOrchTasks (pk=runName, rk=taskId, + Counter),
# PerfBgOrchResults, PerfBgOrchQueue, PerfBgOrchQueueIndex.
function Invoke-PerfTableOp {
    param($Request, $TriggerMetadata)
    $op = [string]$Request.Query.op; if (-not $op) { $op = 'count' }
    $table = [string]$Request.Query.table
    $pk = [string]$Request.Query.pk
    $rk = [string]$Request.Query.rk
    try {
        $svc = [Azure.Data.Tables.TableServiceClient]::new($env:AzureWebJobsStorage)
        if ($op -eq 'tables') {
            $names = @($svc.Query() | ForEach-Object { $_.Name })
            return @{ StatusCode = 200; Body = @{ ok = $true; op = $op; tables = $names } }
        }
        $tc = $svc.GetTableClient($table)
        switch ($op) {
            'deleteRow' {
                $tc.DeleteEntity($pk, $rk) | Out-Null
                return @{ StatusCode = 200; Body = @{ ok = $true; op = $op; table = $table; deleted = "$pk/$rk" } }
            }
            'deletePart' {
                $filter = "PartitionKey eq '$pk'"
                $n = 0
                foreach ($e in $tc.Query[Azure.Data.Tables.TableEntity]($filter)) {
                    $tc.DeleteEntity($e.PartitionKey, $e.RowKey) | Out-Null; $n++
                }
                return @{ StatusCode = 200; Body = @{ ok = $true; op = $op; table = $table; pk = $pk; deleted = $n } }
            }
            'deleteAll' {
                $n = 0
                foreach ($e in $tc.Query[Azure.Data.Tables.TableEntity]("PartitionKey gt ''")) {
                    $tc.DeleteEntity($e.PartitionKey, $e.RowKey) | Out-Null; $n++
                }
                return @{ StatusCode = 200; Body = @{ ok = $true; op = $op; table = $table; deleted = $n } }
            }
            'list' {
                $filter = if ($pk) { "PartitionKey eq '$pk'" } else { "PartitionKey gt ''" }
                $rows = @()
                foreach ($e in $tc.Query[Azure.Data.Tables.TableEntity]($filter)) {
                    $rows += @{ pk = $e.PartitionKey; rk = $e.RowKey }
                    if ($rows.Count -ge 25) { break }
                }
                return @{ StatusCode = 200; Body = @{ ok = $true; op = $op; table = $table; rows = $rows } }
            }
            default {
                $filter = if ($pk) { "PartitionKey eq '$pk'" } else { "PartitionKey gt ''" }
                $n = 0
                foreach ($e in $tc.Query[Azure.Data.Tables.TableEntity]($filter)) { $n++ }
                return @{ StatusCode = 200; Body = @{ ok = $true; op = 'count'; table = $table; pk = $pk; count = $n } }
            }
        }
    } catch {
        return @{ StatusCode = 500; Body = @{ ok = $false; op = $op; error = "$_" } }
    }
}

# Pre-seed live runs directly into the orchestrator tables, bypassing Start-CraftOrchestrator's per-run
# enqueue cost — so a HIGH-scale thread comparison (per-run timers vs the single sweep) is not gated by how
# fast the batch/planner path can create runs. Writes the Runs rows (Status=Running), each run's Tasks rows
# (Status=Pending) + the "!!run-counter" row, in Azure Table batch transactions. RESTART the container after
# seeding: ResumeInterruptedRunsAsync reads the Running run rows and resumes them into live _activeRuns
# entries (each with a per-run status timer on the old build, or joined to the sweep on the new one).
# Query: runs=M (default 2000), tasks=K (default 1), holdms=H, prefix=P, tableprefix=T (default PerfBgOrch).
function Invoke-PerfSeedRuns {
    param($Request, $TriggerMetadata)
    $runs = 2000;      if ($Request.Query.runs) { $runs = [int]$Request.Query.runs }
    $tasks = 1;        if ($Request.Query.tasks) { $tasks = [int]$Request.Query.tasks }
    $holdms = 3600000; if ($Request.Query.holdms) { $holdms = [int]$Request.Query.holdms }
    $prefix = 'seed';  if ($Request.Query.prefix) { $prefix = [string]$Request.Query.prefix }
    $tp = 'PerfBgOrch'; if ($Request.Query.tableprefix) { $tp = [string]$Request.Query.tableprefix }
    try {
        $svc = [Azure.Data.Tables.TableServiceClient]::new($env:AzureWebJobsStorage)
        $rc = $svc.GetTableClient("${tp}Runs");  $rc.CreateIfNotExists() | Out-Null
        $tc = $svc.GetTableClient("${tp}Tasks"); $tc.CreateIfNotExists() | Out-Null
        $now = [DateTimeOffset]::UtcNow
        $upsert = [Azure.Data.Tables.TableTransactionActionType]::UpsertReplace
        $runBatch = [System.Collections.Generic.List[Azure.Data.Tables.TableTransactionAction]]::new()
        $created = 0
        for ($i = 0; $i -lt $runs; $i++) {
            $name = "$prefix-$i"
            $r = [Azure.Data.Tables.TableEntity]::new('Run', $name)
            $r['Status'] = 'Running'; $r['Priority'] = [int]4; $r['StartedUtc'] = $now
            $r['TaskScriptName'] = 'Invoke-CraftTask'; $r['TaskCount'] = [int]$tasks; $r['Sequential'] = [int]0
            $runBatch.Add([Azure.Data.Tables.TableTransactionAction]::new($upsert, $r))
            if ($runBatch.Count -eq 100) { $rc.SubmitTransaction($runBatch) | Out-Null; $runBatch.Clear() }

            $taskBatch = [System.Collections.Generic.List[Azure.Data.Tables.TableTransactionAction]]::new()
            for ($j = 0; $j -lt $tasks; $j++) {
                $t = [Azure.Data.Tables.TableEntity]::new($name, "${name}_t$j")
                $t['Status'] = 'Pending'
                $t['ParametersJson'] = "{`"FunctionName`":`"PerfHold`",`"idx`":$j,`"holdms`":$holdms}"
                $t['AttemptCount'] = [int]0; $t['Sequence'] = [int]$j
                $taskBatch.Add([Azure.Data.Tables.TableTransactionAction]::new($upsert, $t))
            }
            $cnt = [Azure.Data.Tables.TableEntity]::new($name, '!!run-counter')
            $cnt['Remaining'] = [int]$tasks; $cnt['Total'] = [int]$tasks
            $taskBatch.Add([Azure.Data.Tables.TableTransactionAction]::new($upsert, $cnt))
            $tc.SubmitTransaction($taskBatch) | Out-Null
            $created++
        }
        if ($runBatch.Count -gt 0) { $rc.SubmitTransaction($runBatch) | Out-Null }
        return @{ StatusCode = 200; Body = @{ ok = $true; seeded = $created; tasksPerRun = $tasks
                note = 'restart the container to resume these into live runs' } }
    } catch {
        return @{ StatusCode = 500; Body = @{ ok = $false; error = "$_" } }
    }
}

# Parameters-integrity task: verifies the payload the run was created with survived the shed→rehydrate round
# trip. Increments a shared 'ok' counter when its marker parameter is present and correct, 'lost' when it is
# missing/empty (payload lost — a shedding race, or the Tasks row was deleted before dispatch so rehydration
# read nothing). Read the tallies via /API/PerfCheckCounts. holdms lets it sit Pending like PerfHold.
function Push-PerfCheck {
    param($Item)
    $cache = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfCheck')
    $marker = [string]$Item.marker
    $key = if ($marker -and $marker -eq ('m' + $Item.idx)) { 'ok' } else { 'lost' }
    # Interlocked-ish: the shared cache is concurrent; a coarse increment is fine for a tally.
    $n = 0; if ($cache[$key]) { $n = [int]$cache[$key] }
    $cache[$key] = $n + 1
    if ($key -eq 'lost') {
        $ln = 0; if ($cache['lostSample']) { $ln = [int]$cache['lostSample'] }
        $cache['lastLost'] = "idx=$($Item.idx) marker='$marker'"
    }
    if ($Item.holdms -and [int]$Item.holdms -gt 0) { Start-Sleep -Milliseconds ([int]$Item.holdms) }
    return @{ ok = $true; idx = $Item.idx; check = $key }
}

function Invoke-PerfCheckCounts {
    param($Request, $TriggerMetadata)
    $cache = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfCheck')
    return @{ StatusCode = 200; Body = @{ ok = $true
        okCount = [int]$cache['ok']; lostCount = [int]$cache['lost']; lastLost = [string]$cache['lastLost'] } }
}

# The hold-open task: sleeps holdms so a claimed task never reaches a terminal state during the test, which
# is what keeps its run live in _activeRuns (and its 60s status timer firing). No allocation, no fan-out —
# this axis is about run COUNT, not per-task work.
function Push-PerfHold {
    param($Item)
    $ms = 3600000; if ($Item.holdms) { $ms = [int]$Item.holdms }
    Start-Sleep -Milliseconds $ms
    return @{ ok = $true; idx = $Item.idx }
}

# Sequential-mode probe: records the ORDER tasks start in and the MAX concurrency observed, into a shared
# cache. A sequential run should show order = payload order (0,1,2,...) and maxActive = 1 (one at a time);
# a fan-out run shows interleaved order and maxActive > 1. Read via /API/PerfSeqResult.
function Push-PerfSeq {
    param($Item)
    $c = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfSeq')
    $c['order'] = "$($c['order'])$($Item.idx),"
    $a = [int]$c['active'] + 1; $c['active'] = $a
    if ($a -gt [int]$c['maxActive']) { $c['maxActive'] = $a }
    if ($Item.holdms -and [int]$Item.holdms -gt 0) { Start-Sleep -Milliseconds ([int]$Item.holdms) }
    $c['active'] = [int]$c['active'] - 1
    return @{ ok = $true; idx = $Item.idx }
}

# Live OS-thread breakdown via the C# bridge: total thread count and a tally by ThreadState, plus the
# processor count and PS worker-pool size. Answers "where are Craft's threads" at a point in time —
# bounded PS pool + thread-pool workers, and whether anything is growing under load.
function Invoke-PerfThreadBreakdown {
    param($Request, $TriggerMetadata)
    $b = [Craft.Services.WorkerMetricsBridge]::GetMemoryBreakdown()
    $states = @{}
    foreach ($k in $b.ThreadStates.Keys) { $states[$k] = $b.ThreadStates[$k] }
    return @{ StatusCode = 200; Body = @{ ok = $true
            threadCount = $b.ThreadCount; processorCount = $b.ProcessorCount
            httpWorkers = $b.HttpWorkers; bgWorkers = $b.BgWorkers; threadStates = $states } }
}

function Invoke-PerfSeqResult {
    param($Request, $TriggerMetadata)
    $c = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfSeq')
    return @{ StatusCode = 200; Body = @{ ok = $true
        order = [string]$c['order']; maxActive = [int]$c['maxActive'] } }
}

# Thread-pool + process-thread telemetry, for the "thread constrained" half of the many-runs harness. The
# per-run timers fire their re-drive as fire-and-forget work onto the .NET thread pool, so PendingWorkItemCount
# climbing (work queued faster than threads drain it) is the thread-starvation signal that inflates the
# client-side wall-time of otherwise-fast table reads.
function Invoke-PerfThreads {
    param($Request, $TriggerMetadata)
    $maxW = 0; $maxIo = 0; $minW = 0; $minIo = 0; $availW = 0; $availIo = 0
    [System.Threading.ThreadPool]::GetMaxThreads([ref]$maxW, [ref]$maxIo) | Out-Null
    [System.Threading.ThreadPool]::GetMinThreads([ref]$minW, [ref]$minIo) | Out-Null
    [System.Threading.ThreadPool]::GetAvailableThreads([ref]$availW, [ref]$availIo) | Out-Null
    $proc = [System.Diagnostics.Process]::GetCurrentProcess()
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfThreads'
        threadPool = @{
            threadCount           = [System.Threading.ThreadPool]::ThreadCount
            pendingWorkItems      = [long][System.Threading.ThreadPool]::PendingWorkItemCount
            completedWorkItems    = [long][System.Threading.ThreadPool]::CompletedWorkItemCount
            maxWorker             = $maxW; maxIo = $maxIo
            minWorker             = $minW; minIo = $minIo
            busyWorker            = ($maxW - $availW); busyIo = ($maxIo - $availIo)
        }
        process = @{
            osThreadCount = $proc.Threads.Count
            # Cumulative re-drive storage verifications (index read + point reads). ②'s backoff holds this down.
            redriveReads = [long][Craft.Orchestration.OrchestratorService]::RedriveStorageReads
        }
    } }
}

# Worker/queue allocation snapshot — the harness's downstream wrapper around the CRAFT bridge, standing
# in for what a real app (e.g. CIPP) does: CRAFT exposes the data as [Craft.Services.WorkerMetricsBridge],
# the app wraps whichever fields it wants into its own endpoint. Returns the shape run-orch.ps1 and the
# time-to-first-work probes read (jm/queue/limiter/pool) plus memory, for the OOM-resilience harness.
function Invoke-PerfAllocation {
    param($Request, $TriggerMetadata)
    $s = [Craft.Services.WorkerMetricsBridge]::GetSnapshot()
    return @{ StatusCode = 200; Body = @{
        jm = @{
            active         = $s.Jobs.Running
            queued         = $s.Jobs.QueuedLocal
            completed      = $s.Jobs.Completed
            failed         = $s.Jobs.Failed
            totalProcessed = $s.Jobs.TotalProcessed
            maxConcurrency = $s.Jobs.MaxConcurrency
        }
        queue = @{
            unclaimed = $s.Jobs.QueuedDurable
            total     = $s.Jobs.Queued
        }
        limiter = @{
            currentMax    = $s.Limiter.CurrentMax
            baseMax       = $s.Limiter.BaseConcurrency
            ceiling       = $s.Limiter.CeilingConcurrency
            active        = $s.Limiter.Active
            waiting       = $s.Limiter.Waiting
            httpThrottled = $s.Limiter.IsHttpThrottled
        }
        pool = @{
            bgBusy    = $s.BgPool.BusyCount
            bgTotal   = $s.BgPool.PoolSize
            bgAvail   = $s.BgPool.Available
            httpAvail = $s.HttpPool.Available
        }
        memory = @{
            heapMB           = $s.Memory.HeapMB
            rssMB            = $s.Memory.RssMB
            committedMB      = $s.Memory.CommittedMB
            containerLimitMB = $s.Memory.ContainerLimitMB
            containerUsedMB  = $s.Memory.ContainerUsedMB
            gcHeapLimitMB    = $s.Memory.GCHeapLimitMB
            usagePct         = $s.Memory.UsagePct
            gc0 = $s.Memory.GC0; gc1 = $s.Memory.GC1; gc2 = $s.Memory.GC2
        }
    } }
}

# Durable run summaries (table-backed via the bridge), so the OOM harness can confirm ALL tasks reached a
# terminal state even across a crash/restart — the in-memory JobManager counters reset, the tables do not.
function Invoke-PerfRuns {
    param($Request, $TriggerMetadata)
    $runs = [Craft.Services.WorkerMetricsBridge]::GetRunSummaries()
    return @{ StatusCode = 200; Body = @{ runs = @($runs | ForEach-Object {
        @{ name = $_.Name; total = $_.Total; completed = $_.Completed; failed = $_.Failed; running = $_.Running; queued = $_.Queued }
    }) } }
}

# Identity reflector: returns the principal CRAFT resolved for this request — the EasyAuth
# x-ms-client-principal (base64 claims), plus X-Forwarded-For. Used to confirm header decoding,
# role lookup, and client-IP pass-through.
function Invoke-PerfWhoami {
    param($Request, $TriggerMetadata)
    $h = $Request.Headers
    $cp = $null
    $b64 = $h.'x-ms-client-principal'
    if ($b64) {
        try {
            $json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$b64))
            $cp = $json | ConvertFrom-Json
        } catch { $cp = "decode-error" }
    }
    return @{ StatusCode = 200; Body = @{
        ok               = $true
        endpoint         = 'PerfWhoami'
        clientPrincipal  = $cp
        principalName    = $h.'x-ms-client-principal-name'
        idp              = $h.'x-ms-client-principal-idp'
        xForwardedFor    = $h.'x-forwarded-for'
    } }
}

# Timer target: a scheduled task increments a process-wide counter (shared cache) so the harness can
# confirm the scheduler actually fired the task on a background worker.
function Invoke-PerfTimerTick {
    param($Request, $TriggerMetadata)
    $cache = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfTimer')
    $n = 0; if ($cache['count']) { $n = [int]$cache['count'] }
    $cache['count'] = $n + 1
    $cache['last'] = (Get-Date).ToUniversalTime().ToString('o')
    return @{ ok = $true; count = $cache['count'] }
}

# HTTP reader for the timer counter — lets the harness poll how many times the timer has fired.
function Invoke-PerfTimerCount {
    param($Request, $TriggerMetadata)
    $cache = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfTimer')
    $n = 0; if ($cache['count']) { $n = [int]$cache['count'] }
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfTimerCount'; count = $n; last = $cache['last'] } }
}

# Cacheable endpoint: the "List" prefix + GET makes CRAFT's response cache engage (stale-while-revalidate).
# Used to profile the disk-backed response cache (hit = fixed query, miss = unique query). Query: ?n=N (default 50).
function Invoke-ListPerf {
    param($Request, $TriggerMetadata)
    $n = 50
    if ($Request.Query.n) { $n = [int]$Request.Query.n }
    $items = for ($i = 0; $i -lt $n; $i++) {
        @{ id = $i; name = "row-$i"; value = ($i * 1.5); tag = "t$($i % 8)"; active = ($i % 2 -eq 0) }
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; count = $n; items = @($items) } }
}

# Serialization + payload cost: builds and returns an N-item array. Measures PS-object -> JSON
# serialization and response size. Query: ?n=N (default 1000).
function Invoke-PerfJson {
    param($Request, $TriggerMetadata)
    $n = 1000
    if ($Request.Query.n) { $n = [int]$Request.Query.n }
    $items = for ($i = 0; $i -lt $n; $i++) {
        @{ id = $i; name = "item-$i"; value = ($i * 3.14159); active = ($i % 2 -eq 0); tag = "tag-$($i % 10)" }
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfJson'; count = $n; items = @($items) } }
}

# Realtime bridge driver: publishes a job event via the C# RealtimeBridge so an SSE consumer of
# /.craft/events can observe it. userId is taken from the caller's identity so it is delivered back to
# the same principal. Query: ?jobId=<guid>&mode=start|update|end&size=<bytes of filler data>.
function Invoke-PerfPublish {
    param($Request, $TriggerMetadata)
    $userId = [string]$Request.Headers.'x-ms-client-principal-name'
    $jobId  = [string]$Request.Query.jobId
    if (-not $jobId) { $jobId = [guid]::NewGuid().ToString() }
    $mode = [string]$Request.Query.mode; if (-not $mode) { $mode = 'update' }
    $data = @{ note = 'perf publish'; ts = (Get-Date).ToUniversalTime().ToString('o') }
    if ($Request.Query.size) { $data['filler'] = ('x' * [int]$Request.Query.size) }
    [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, $mode, $data, "/perf/$jobId", "View job")
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfPublish'; jobId = $jobId; mode = $mode; userId = $userId } }
}
