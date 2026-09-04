<#
.SYNOPSIS
  OOM-resilience harness: prove a heap-constrained container still drains a massive fan-out.

.DESCRIPTION
  The durable-queue design keeps the backlog in Azure Table storage; the JobManager holds only a
  worker-pool-sized buffer. So memory should stay BOUNDED regardless of fan-out size — a 20,000-task run
  uses roughly the same heap as a 500-task one. This harness measures that, and then proves it under a
  GC heap hard limit set just above the baseline: if the design were memory-bound (whole backlog in RAM)
  a tight limit would OOM long before the run finished.

  Flow: bring CRAFT up (Http+Background + Azurite), record baseline memory, enqueue N tasks, poll
  /API/PerfAllocation tracking peak memory + completion, and confirm every task reached a terminal state
  via the durable run summary (/API/PerfRuns) — which survives a restart, unlike the in-memory counters.

.EXAMPLE
  # Baseline: unconstrained, find the natural peak for a 20k fan-out.
  pwsh scripts\run-oom.ps1 -Tasks 20000 -Label baseline

  # Constrained: cap the GC heap just above the baseline peak and prove it still finishes.
  pwsh scripts\run-oom.ps1 -Tasks 20000 -HeapLimitMB 320 -Label capped
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [string]$Label    = 'oom',
  [int]$Tasks       = 20000,
  [int]$TaskMs      = 0,
  [int]$BgPool      = 8,
  [double]$Cpus     = 2,
  [int]$HeapLimitMB = 0,       # 0 = unconstrained
  [int]$AllocMB     = 0,       # per-task large-object (LOH) allocation — real heap pressure
  [int]$HoldMs      = 0,       # hold the allocation so concurrent workers pile up live memory
  [int]$Batch       = 100,     # pump claim batch — raises drain throughput for an enqueued backlog
  [int]$PollMs      = 250,
  [int]$Port        = 5298,
  [int]$ReadyTimeoutSec = 240,
  [int]$MaxWaitSec  = 900,
  [int]$StallSec    = 90,      # no dispatch progress this long (with work left) = dispatch loop wedged
  [switch]$KeepUp
)
$ErrorActionPreference = 'Stop'
$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$root     = Split-Path -Parent $here
$compose  = Join-Path $root 'docker-compose.bg.yml'
$resultsDir = Join-Path $root 'results'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"

function Info($m){ Write-Host "[oom-harness] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[oom-harness] $m" -ForegroundColor Yellow }
function Get-Alloc { try { Invoke-RestMethod "$base/API/PerfAllocation" -TimeoutSec 5 } catch { $null } }

$env:SUT_IMAGE=$SutImage; $env:SUT_PORT="$Port"; $env:SUT_CPUS="$Cpus"; $env:BG_POOL="$BgPool"
# Burst to ceiling so the pool fills immediately — we are testing memory under drain, not the ramp.
$env:BG_BURST='true'; $env:BG_BASE="$BgPool"; $env:BG_SCALEUP='1'; $env:BG_CEILING="$BgPool"
$env:JOB_BATCH="$Batch"; $env:JOB_POLL_MS="$PollMs"
$env:GC_HEAP_LIMIT_MB = if($HeapLimitMB -gt 0){ "$HeapLimitMB" } else { '' }

Info "image=$SutImage tasks=$Tasks taskMs=$TaskMs bgPool=$BgPool cpus=$Cpus heapLimitMB=$(if($HeapLimitMB -gt 0){$HeapLimitMB}else{'none'})"
Info "compose up ..."
docker compose -f $compose up -d 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0){ throw "compose up failed" }

$result = [ordered]@{ label=$Label; timestamp=$stamp; tasks=$Tasks; taskMs=$TaskMs; bgPool=$BgPool; cpus=$Cpus
  heapLimitMB=$HeapLimitMB }
try {
  Info "waiting for ready (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $dl=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $dl){
    try { if((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).status -eq 'ready'){ $ready=$true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if(-not $ready){ throw 'SUT never became ready' }

  # Baseline — retry until the bridge reports a real heap reading (the first calls right after ready can
  # land before the PS pool answers, and [double]$null would silently record a 0MB baseline).
  Start-Sleep -Seconds 2
  $b = $null
  for($i=0; $i -lt 30; $i++){ $a=Get-Alloc; if($a -and ([double]$a.memory.heapMB) -gt 0){ $b=$a.memory; break }; Start-Sleep -Milliseconds 500 }
  if(-not $b){ throw 'could not read a baseline memory sample from /API/PerfAllocation' }
  $baseHeap = [double]$b.heapMB; $baseUsed = [double]$b.containerUsedMB; $gcLimit = [double]$b.gcHeapLimitMB
  Info ("baseline: heap={0}MB containerUsed={1}MB gcHeapLimit={2}MB" -f $baseHeap,$baseUsed,$gcLimit)
  $result.baselineHeapMB=$baseHeap; $result.baselineContainerUsedMB=$baseUsed; $result.gcHeapLimitMB=$gcLimit

  # ── Massive fan-out ─────────────────────────────────────────────────────────
  Info "enqueuing $Tasks tasks ..."
  $t0 = Get-Date
  $enq = & curl.exe -s --max-time 180 "$base/API/PerfBgEnqueue?n=$Tasks&taskms=$TaskMs&allocmb=$AllocMB&holdms=$HoldMs" 2>$null
  Info "enqueue -> $enq"
  $runName = $null
  try { $runName = ($enq | ConvertFrom-Json).run } catch {}

  # ── Poll to completion; watch dispatch PROGRESS (does the loop keep going under OOM?) ──────────
  $peakHeap=$baseHeap; $peakUsed=$baseUsed; $unreachable=0; $maxUnreachableStreak=0; $samples=New-Object System.Collections.ArrayList
  $done=$false; $stalled=$false; $crashed=$false; $peakFailed=0
  $lastLog=Get-Date; $prevDone=-1; $lastProgress=Get-Date; $wdl=(Get-Date).AddSeconds($MaxWaitSec)
  while((Get-Date) -lt $wdl){
    $a = Get-Alloc
    if(-not $a){
      $unreachable++
      if($unreachable -gt $maxUnreachableStreak){$maxUnreachableStreak=$unreachable}
      # A sustained no-response means the PROCESS went down — the dispatch loop did not survive the OOM.
      if($unreachable -ge 40){ $crashed=$true; Warn "SUT unreachable for ~20s — process appears to have crashed"; break }
      Start-Sleep -Milliseconds 500; continue
    }
    $unreachable=0
    $t=[math]::Round(((Get-Date)-$t0).TotalSeconds,1)
    $heap=[double]$a.memory.heapMB; $used=[double]$a.memory.containerUsedMB
    if($heap -gt $peakHeap){$peakHeap=$heap}; if($used -gt $peakUsed){$peakUsed=$used}
    $fail=[int]$a.jm.failed; if($fail -gt $peakFailed){$peakFailed=$fail}
    $terminal=[int]$a.jm.completed + $fail
    if($terminal -gt $prevDone){ $prevDone=$terminal; $lastProgress=Get-Date }
    [void]$samples.Add([pscustomobject]@{ t=$t; heapMB=$heap; usedMB=$used; qtotal=[int]$a.queue.total; unclaimed=[int]$a.queue.unclaimed
      bgBusy=[int]$a.pool.bgBusy; active=[int]$a.jm.active; queued=[int]$a.jm.queued; completed=[int]$a.jm.completed; failed=$fail; gc2=[int]$a.memory.gc2 })

    if(((Get-Date)-$lastLog).TotalSeconds -ge 5){
      $lastLog=Get-Date
      Info ("t={0,6}s done={1,6}/{2} (fail={3}) qtotal={4,6} bgBusy={5}/{6} heap={7}MB used={8}MB gc2={9}" -f `
        $t,$terminal,$Tasks,$fail,$a.queue.total,$a.pool.bgBusy,$BgPool,$heap,$used,$a.memory.gc2)
    }
    if($terminal -ge $Tasks){ $done=$true; break }
    # Stall: work still queued but the dispatch loop has marked nothing terminal for StallSec — wedged.
    if(([int]$a.queue.total -gt 0 -or [int]$a.jm.queued -gt 0 -or [int]$a.jm.active -gt 0) -and ((Get-Date)-$lastProgress).TotalSeconds -ge $StallSec){
      $stalled=$true; Warn ("dispatch STALLED: no terminal progress for {0}s with {1} still queued" -f $StallSec,$a.queue.total); break
    }
    Start-Sleep -Milliseconds 800
  }
  $elapsed=[math]::Round(((Get-Date)-$t0).TotalSeconds,1)

  # ── Durable confirmation: every task terminal per the tables (survives a restart) ──
  Start-Sleep -Seconds 2
  $durTotal=0; $durDone=0; $durFailed=0; $durRun=$null
  try {
    $runs = (Invoke-RestMethod "$base/API/PerfRuns" -TimeoutSec 15).runs
    $durRun = if($runName){ $runs | Where-Object { $_.name -eq $runName } | Select-Object -First 1 } else { $runs | Select-Object -First 1 }
    if($durRun){ $durTotal=[int]$durRun.total; $durDone=[int]$durRun.completed; $durFailed=[int]$durRun.failed }
  } catch { Warn "PerfRuns read failed: $_" }

  $peakCompleted = ($samples | Measure-Object completed -Maximum).Maximum
  $allTerminal = ($done) -or ($durTotal -gt 0 -and ($durDone + $durFailed) -ge $Tasks)
  $dispatchSurvived = (-not $crashed) -and (-not $stalled)

  $result.enqueueTaskCount=$Tasks; $result.allocMB=$AllocMB; $result.holdMs=$HoldMs
  $result.completionSec=$elapsed; $result.peakHeapMB=$peakHeap; $result.peakContainerUsedMB=$peakUsed
  $result.heapGrowthMB=[math]::Round($peakHeap-$baseHeap,1)
  $result.peakCompleted=$peakCompleted; $result.peakFailed=$peakFailed
  $result.crashed=$crashed; $result.stalled=$stalled; $result.maxUnreachableStreak=$maxUnreachableStreak
  $result.dispatchSurvived=$dispatchSurvived; $result.allTerminal=$allTerminal
  $result.durable=@{ run=$runName; total=$durTotal; completed=$durDone; failed=$durFailed }
  $result.samples=$samples

  Write-Host ""
  Write-Host "===== OOM-resilience: $Label ($Tasks tasks, alloc=${AllocMB}MB hold=${HoldMs}ms, heapLimit=$(if($HeapLimitMB -gt 0){"${HeapLimitMB}MB"}else{'none'})) =====" -ForegroundColor Yellow
  Write-Host ("  baseline heap        : {0} MB   gc heap hard limit: {1}" -f $baseHeap,$(if($gcLimit -gt 0){"$gcLimit MB"}else{'(none)'})) -ForegroundColor Gray
  Write-Host ("  PEAK heap            : {0} MB   peak container used: {1} MB" -f $peakHeap,$peakUsed) -ForegroundColor Gray
  Write-Host ("  tasks failed (OOM)   : {0} of {1}" -f $peakFailed,$Tasks) -ForegroundColor $(if($peakFailed -gt 0){'Yellow'}else{'Gray'})
  Write-Host ("  completed / terminal : {0} completed, all-terminal={1}" -f $peakCompleted,$allTerminal) -ForegroundColor Gray
  Write-Host ("  completion time      : {0}s" -f $elapsed) -ForegroundColor Gray
  Write-Host ("  process crashed      : {0}   dispatch stalled: {1}" -f $crashed,$stalled) -ForegroundColor $(if($crashed -or $stalled){'Red'}else{'Green'})
  Write-Host ("  >> DISPATCH SURVIVED : {0}  (loop kept dispatching through the OOM pressure)" -f $dispatchSurvived) -ForegroundColor $(if($dispatchSurvived){'Green'}else{'Red'})
  Write-Host ("  >> ALL TASKS TERMINAL: {0}  (every task Completed or Failed, none stranded)" -f $allTerminal) -ForegroundColor $(if($allTerminal){'Green'}else{'Red'})

  $jsonOut = Join-Path $resultsDir "$Label-h$HeapLimitMB-a$AllocMB-$stamp.json"
  ($result | ConvertTo-Json -Depth 6) | Set-Content $jsonOut -Encoding utf8
  Info "wrote $jsonOut"
  if($AllocMB -gt 0 -and $peakFailed -eq 0){ Warn "no task OOMs observed — the heap limit may be too high vs (bgPool x allocMB) to actually force pressure" }
  if(-not $dispatchSurvived){ Warn "DISPATCH DID NOT SURVIVE — the loop crashed/stalled under OOM (see $jsonOut)" }
}
finally {
  if($KeepUp){ Warn "leaving up (-KeepUp). down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}
