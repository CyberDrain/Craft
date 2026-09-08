<#
.SYNOPSIS
  Run-COUNT axis harness: prove (or, after the fix, disprove) that the per-run status/re-drive tick
  storm makes memory + GC + thread-pool pressure scale with the number of concurrently-live runs.

.DESCRIPTION
  oom-analysis.md proved memory is bounded on the fan-out WIDTH axis: one run of 20,000 tasks peaks at
  ~64 MB because the backlog lives in the Queue table and the JobManager holds an O(batch) buffer. This
  harness exercises the axis that was never tested — the number of concurrently-live RUNS.

  Each live run pins a per-run System.Threading.Timer that fires every 60s (OrchestratorService), and on
  every tick it: (a) LogRunStatus — walks the run's task list 4x and logs a status line, unconditionally;
  (b) RedrivePendingTasksAsync — reads the queue index + a point-read per pending candidate, every tick,
  even when nothing is orphaned. With M live runs that is ~M/60 callbacks/second of fire-and-forget work
  on the thread pool plus M pinned run graphs. This reproduces the production shape (thousands of runs
  parked at "0 running / N pending" for hours) that climbs to the GC heap ceiling and OOM-crashes (exit
  139), where the fast (~8 ms) index reads show up as multi-second [HTTP-SLOW] purely because the thread
  is frozen by continuous full GCs / starved of the thread pool.

  Flow: bring CRAFT up (Http+Background + Azurite) at Information log level (so the per-run status flood is
  part of the measured cost) under an optional GC heap hard limit and a constrained CPU/thread budget;
  create M runs of K hold-open tasks (they sleep the whole window so no run finalizes → M stays live);
  then OBSERVE for -WatchSec across several 60s tick waves, sampling heap / gc2 / thread-pool depth, and
  finally scrape the container log for [HTTP-SLOW], tick failures, and (if it died) the exit code.

.EXAMPLE
  # Baseline repro against the current image — climb to the heap ceiling and crash.
  pwsh scripts\run-manyruns.ps1 -Runs 2500 -TasksPerRun 4 -HeapLimitMB 400 -Cpus 1 -Label baseline

  # After the fix (rebuild craft:local first) — same M, heap should stay flat, no HTTP-SLOW, no crash.
  pwsh scripts\run-manyruns.ps1 -Runs 2500 -TasksPerRun 4 -HeapLimitMB 400 -Cpus 1 -Label fixed
#>
[CmdletBinding()]
param(
  [string]$SutImage      = 'craft:local',
  [string]$Label         = 'manyruns',
  [int]$Runs             = 2500,      # M — concurrently-live runs (the independent variable)
  [int]$TasksPerRun      = 4,         # K — tasks per run; K > claimable keeps each run permanently Pending
  [int]$ParamKB          = 0,         # per-task payload KB — makes the retained run graph production-weight
  [int]$HoldMs           = 3600000,   # per-task sleep (1h) — longer than the test, so no run finalizes
  [int]$ChunkRuns        = 200,       # runs created per PerfManyRuns HTTP call (avoids a long single request)
  [int]$BgPool           = 4,
  [double]$Cpus          = 1,         # constrain CPU → small thread pool (the "thread constrained" half)
  [int]$HeapLimitMB      = 400,       # 0 = unconstrained; set just above the live-graph baseline to force the ceiling
  [string]$LogLevel      = 'Information',
  [int]$StatusIntervalSec = 60,       # per-run status/re-drive tick cadence; lower to compress the backoff
  [ValidateSet('true','false')][string]$RedriveBackoff = 'true',  # ②: 'false' A/Bs the pre-backoff behaviour
  [ValidateSet('true','false')][string]$ShedParameters = 'true',  # retained-memory fix: 'false' A/Bs the old resident-payload behaviour
  [int]$WatchSec         = 300,       # observe several tick waves after enqueue
  [int]$PollMs           = 1500,
  [int]$Port             = 5298,
  [int]$ReadyTimeoutSec  = 240,
  [int]$EnqueueTimeoutSec = 120,
  [switch]$KeepUp
)
$ErrorActionPreference = 'Stop'
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$root       = Split-Path -Parent $here
$compose    = Join-Path $root 'docker-compose.bg.yml'
$resultsDir = Join-Path $root 'results'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"
$sutContainer = 'craft-perf-bg-sut'

function Info($m){ Write-Host "[manyruns] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[manyruns] $m" -ForegroundColor Yellow }
function Get-Alloc   { try { Invoke-RestMethod "$base/API/PerfAllocation" -TimeoutSec 5 } catch { $null } }
function Get-Threads { try { Invoke-RestMethod "$base/API/PerfThreads"    -TimeoutSec 5 } catch { $null } }

$env:SUT_IMAGE=$SutImage; $env:SUT_PORT="$Port"; $env:SUT_CPUS="$Cpus"; $env:BG_POOL="$BgPool"
$env:GC_HEAP_LIMIT_MB = if($HeapLimitMB -gt 0){ "$HeapLimitMB" } else { '' }
$env:LOG_LEVEL = $LogLevel
# JobQueueBatchSize/PollIntervalMs bind as int — the compose's ${JOB_BATCH:-} yields an EMPTY string when
# unset, which the config binder rejects at startup (JobQueuePump ctor). Give them real values like run-oom.
$env:JOB_BATCH = "$BgPool"; $env:JOB_POLL_MS = "1000"
$env:STATUS_INTERVAL = "$StatusIntervalSec"
$env:REDRIVE_BACKOFF = $RedriveBackoff
$env:SHED_PARAMS = $ShedParameters
# Nothing about this test depends on the limiter ramp — the tick storm is C# thread-pool work, not BG-pool
# work. Leave the BG defaults; the hold-open tasks only need a couple of slots.

Info "image=$SutImage runs=$Runs tasksPerRun=$TasksPerRun bgPool=$BgPool cpus=$Cpus heapLimitMB=$(if($HeapLimitMB -gt 0){$HeapLimitMB}else{'none'}) logLevel=$LogLevel watchSec=$WatchSec"
Info "compose up ..."
docker compose -f $compose up -d 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0){ throw "compose up failed" }

$result = [ordered]@{ label=$Label; timestamp=$stamp; runs=$Runs; tasksPerRun=$TasksPerRun; paramKB=$ParamKB; holdMs=$HoldMs
  bgPool=$BgPool; cpus=$Cpus; heapLimitMB=$HeapLimitMB; logLevel=$LogLevel; statusIntervalSec=$StatusIntervalSec; redriveBackoff=$RedriveBackoff; shedParameters=$ShedParameters; watchSec=$WatchSec }
try {
  Info "waiting for ready (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $dl=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $dl){
    try { if((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).status -eq 'ready'){ $ready=$true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if(-not $ready){ throw 'SUT never became ready' }

  # Baseline heap (retry until the PS pool answers with a real reading).
  Start-Sleep -Seconds 2
  $b=$null
  for($i=0; $i -lt 30; $i++){ $a=Get-Alloc; if($a -and ([double]$a.memory.heapMB) -gt 0){ $b=$a.memory; break }; Start-Sleep -Milliseconds 500 }
  if(-not $b){ throw 'could not read a baseline memory sample from /API/PerfAllocation' }
  $baseHeap=[double]$b.heapMB; $baseUsed=[double]$b.containerUsedMB; $gcLimit=[double]$b.gcHeapLimitMB
  $th0=Get-Threads
  Info ("baseline: heap={0}MB containerUsed={1}MB gcHeapLimit={2}MB threadPool={3} osThreads={4}" -f `
    $baseHeap,$baseUsed,$gcLimit,$(if($th0){$th0.threadPool.threadCount}else{'?'}),$(if($th0){$th0.process.osThreadCount}else{'?'}))
  $result.baselineHeapMB=$baseHeap; $result.baselineContainerUsedMB=$baseUsed; $result.gcHeapLimitMB=$gcLimit

  # ── Create M live runs (chunked) ────────────────────────────────────────────
  Info "creating $Runs runs x $TasksPerRun tasks (chunks of $ChunkRuns) ..."
  $t0=Get-Date; $created=0; $enqCrashed=$false
  while($created -lt $Runs){
    $chunk=[math]::Min($ChunkRuns, $Runs-$created)
    $u="$base/API/PerfManyRuns?runs=$chunk&tasks=$TasksPerRun&holdms=$HoldMs&paramkb=$ParamKB&prefix=$Label"
    $resp = & curl.exe -s --max-time $EnqueueTimeoutSec $u 2>$null
    $ok=$null; try { $ok=($resp | ConvertFrom-Json).created } catch {}
    if($null -eq $ok){
      # A crash mid-enqueue is itself a result (the graph didn't even fit to build) — record and stop.
      if(-not (Get-Alloc)){ $enqCrashed=$true; Warn "SUT unreachable during enqueue at created=$created — crashed while building the run set"; break }
      Warn "enqueue chunk returned no count (resp: $resp) — retrying"; Start-Sleep -Seconds 2; continue
    }
    $created+=[int]$ok
    $a=Get-Alloc
    Info ("  created={0,6}/{1}  qtotal={2,7}  heap={3}MB  gc2={4}" -f `
      $created,$Runs,$(if($a){[int]$a.queue.total}else{'?'}),$(if($a){[double]$a.memory.heapMB}else{'?'}),$(if($a){[int]$a.memory.gc2}else{'?'}))
  }
  $enqSec=[math]::Round(((Get-Date)-$t0).TotalSeconds,1)
  $result.runsCreated=$created; $result.enqueueSec=$enqSec

  # ── Observe: watch the 60s tick waves drive heap / gc2 / thread-pool ─────────
  Info "observing tick waves for ${WatchSec}s ..."
  $samples=New-Object System.Collections.ArrayList
  $peakHeap=$baseHeap; $peakUsed=$baseUsed; $peakGc2=[int]$b.GC2
  $baseGc2=[int]$b.GC2; $peakPending=0; $peakOsThreads=0; $peakTpThreads=0
  $unreachable=0; $maxUnreachable=0; $crashed=$false
  $redriveReadsFirst=$null; $redriveReadsLast=$null
  $lastLog=Get-Date; $wStart=Get-Date; $wdl=(Get-Date).AddSeconds($WatchSec)
  $firstGc2=$null; $firstGc2At=$null; $lastGc2=$null; $lastGc2At=$null
  while((Get-Date) -lt $wdl){
    $a=Get-Alloc
    if(-not $a){
      $unreachable++; if($unreachable -gt $maxUnreachable){$maxUnreachable=$unreachable}
      if($unreachable -ge 40){ $crashed=$true; Warn "SUT unreachable ~20s — process crashed (OOM repro)"; break }
      Start-Sleep -Milliseconds 500; continue
    }
    $unreachable=0
    $th=Get-Threads
    $t=[math]::Round(((Get-Date)-$wStart).TotalSeconds,1)
    $heap=[double]$a.memory.heapMB; $used=[double]$a.memory.containerUsedMB; $gc2=[int]$a.memory.gc2
    if($heap -gt $peakHeap){$peakHeap=$heap}; if($used -gt $peakUsed){$peakUsed=$used}; if($gc2 -gt $peakGc2){$peakGc2=$gc2}
    if($null -eq $firstGc2){ $firstGc2=$gc2; $firstGc2At=Get-Date }
    $lastGc2=$gc2; $lastGc2At=Get-Date
    $pending=if($th){[long]$th.threadPool.pendingWorkItems}else{0}
    $osThreads=if($th){[int]$th.process.osThreadCount}else{0}
    $tpThreads=if($th){[int]$th.threadPool.threadCount}else{0}
    if($th -and $null -ne $th.process.redriveReads){ if($null -eq $redriveReadsFirst){$redriveReadsFirst=[long]$th.process.redriveReads}; $redriveReadsLast=[long]$th.process.redriveReads }
    if($pending -gt $peakPending){$peakPending=$pending}
    if($osThreads -gt $peakOsThreads){$peakOsThreads=$osThreads}
    if($tpThreads -gt $peakTpThreads){$peakTpThreads=$tpThreads}
    [void]$samples.Add([pscustomobject]@{ t=$t; heapMB=$heap; usedMB=$used; gc2=$gc2
      qtotal=[int]$a.queue.total; bgBusy=[int]$a.pool.bgBusy
      tpPending=$pending; tpThreads=$tpThreads; osThreads=$osThreads })
    if(((Get-Date)-$lastLog).TotalSeconds -ge 10){
      $lastLog=Get-Date
      Info ("t={0,6}s heap={1}MB used={2}MB gc2={3} tpPending={4} tpThreads={5} osThreads={6} qtotal={7}" -f `
        $t,$heap,$used,$gc2,$pending,$tpThreads,$osThreads,[int]$a.queue.total)
    }
    Start-Sleep -Milliseconds $PollMs
  }

  # gc2 rate over the observation window = the GC-thrash signal (full compacting collections / minute).
  $gc2Rate=$null
  if($firstGc2 -ne $null -and $lastGc2At -gt $firstGc2At){
    $mins=((($lastGc2At)-($firstGc2At)).TotalMinutes)
    if($mins -gt 0){ $gc2Rate=[math]::Round((($lastGc2-$firstGc2)/$mins),1) }
  }

  # ── Scrape the container log for the symptoms + exit code ─────────────────────
  $log = (& docker logs $sutContainer 2>&1) -join "`n"
  $httpSlow  = ([regex]::Matches($log,'HTTP-SLOW')).Count
  $tickFail  = ([regex]::Matches($log,'status tick failed')).Count
  $redriveSkip = ([regex]::Matches($log,'skipping re-drive')).Count
  $oomLines  = ([regex]::Matches($log,'OutOfMemoryException')).Count
  # Per-run status lines emitted ("[Scheduler] Run <name> T+...") — the flood ① is meant to collapse.
  $statusLines = ([regex]::Matches($log,'\] Run .*T\+')).Count
  $exitCode=$null
  try { $exitCode=[int](& docker inspect $sutContainer --format '{{.State.ExitCode}}' 2>$null) } catch {}
  $isRunning=$null
  try { $isRunning=(& docker inspect $sutContainer --format '{{.State.Running}}' 2>$null) } catch {}

  $result.enqueueCrashed=$enqCrashed; $result.crashed=($crashed -or $enqCrashed)
  $result.peakHeapMB=$peakHeap; $result.peakContainerUsedMB=$peakUsed
  $result.heapGrowthMB=[math]::Round($peakHeap-$baseHeap,1)
  $result.baseGc2=$baseGc2; $result.peakGc2=$peakGc2; $result.gc2PerMin=$gc2Rate
  $result.peakThreadPoolPending=$peakPending; $result.peakThreadPoolThreads=$peakTpThreads; $result.peakOsThreads=$peakOsThreads
  $redriveReadsWindow = if($null -ne $redriveReadsFirst -and $null -ne $redriveReadsLast){ $redriveReadsLast-$redriveReadsFirst } else { $null }
  $result.httpSlowCount=$httpSlow; $result.tickFailCount=$tickFail; $result.redriveSkipCount=$redriveSkip; $result.oomExceptionCount=$oomLines; $result.statusLogLines=$statusLines
  $result.redriveStorageReadsInWindow=$redriveReadsWindow
  $result.containerExitCode=$exitCode; $result.containerRunning=$isRunning; $result.maxUnreachableStreak=$maxUnreachable
  $result.samples=$samples

  Write-Host ""
  Write-Host "===== run-count axis: $Label ($created runs x $TasksPerRun, heapLimit=$(if($HeapLimitMB -gt 0){"${HeapLimitMB}MB"}else{'none'}), cpus=$Cpus) =====" -ForegroundColor Yellow
  Write-Host ("  baseline heap         : {0} MB    gc heap hard limit: {1}" -f $baseHeap,$(if($gcLimit -gt 0){"$gcLimit MB"}else{'(none)'})) -ForegroundColor Gray
  Write-Host ("  PEAK heap             : {0} MB    (growth {1} MB)   peak container used: {2} MB" -f $peakHeap,$result.heapGrowthMB,$peakUsed) -ForegroundColor Gray
  Write-Host ("  Gen2 full GCs         : {0} -> {1}   rate: {2}/min  <-- GC-thrash signal" -f $baseGc2,$peakGc2,$(if($null -ne $gc2Rate){$gc2Rate}else{'?'})) -ForegroundColor Gray
  Write-Host ("  thread-pool pending   : peak {0}   tpThreads peak {1}   osThreads peak {2}  <-- thread starvation" -f $peakPending,$peakTpThreads,$peakOsThreads) -ForegroundColor Gray
  Write-Host ("  [HTTP-SLOW] log lines : {0}   tick-failures: {1}   OOMException: {2}" -f $httpSlow,$tickFail,$oomLines) -ForegroundColor $(if($httpSlow -gt 0 -or $oomLines -gt 0){'Yellow'}else{'Gray'})
  Write-Host ("  status log lines      : {0}   <-- per-run 'T+' flood (① collapses this)" -f $statusLines) -ForegroundColor Gray
  Write-Host ("  re-drive storage reads: {0}   in window (backoff={1})  <-- ② holds this down" -f $(if($null -ne $redriveReadsWindow){$redriveReadsWindow}else{'?'}),$RedriveBackoff) -ForegroundColor Gray
  Write-Host ("  process crashed       : {0}   exitCode={1}  running={2}" -f $result.crashed,$exitCode,$isRunning) -ForegroundColor $(if($result.crashed){'Red'}else{'Green'})

  $jsonOut = Join-Path $resultsDir "$Label-m$Runs-h$HeapLimitMB-$stamp.json"
  ($result | ConvertTo-Json -Depth 6) | Set-Content $jsonOut -Encoding utf8
  Info "wrote $jsonOut"
}
finally {
  if($KeepUp){ Warn "leaving up (-KeepUp). down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}
