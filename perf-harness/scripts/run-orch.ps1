<#
.SYNOPSIS
  Profile orchestration fan-out worker-allocation efficiency (+ optional child-orchestrator dependency).

.DESCRIPTION
  Brings up CRAFT in backend mode + Azurite, enqueues a parent orchestration of N tasks (each optionally
  sleeping -TaskMs to simulate work, and optionally spawning a child orchestration of -ChildN leaf tasks),
  then polls /API/PerfAllocation at high frequency to build a timeline of:
    - BG pool busy vs idle workers
    - the BackgroundTaskLimiter gate (currentMax)  ← the ramp
    - JobManager queued / active
  and reports fan-out completion time, ramp-to-ceiling time, worker utilization, and the "slot held but
  worker idle" gap (concurrency held across the per-task Azure Table writes).

  A/B the limiter ramp: default (-Base 2 -ScaleUp 15) vs immediate (-Base <BgPool> -ScaleUp 1).

.EXAMPLE
  pwsh scripts\run-orch.ps1 -Tasks 1000 -TaskMs 20 -BgPool 8            # default slow ramp
  pwsh scripts\run-orch.ps1 -Tasks 1000 -TaskMs 20 -BgPool 8 -Base 8 -ScaleUp 1   # immediate ceiling
  pwsh scripts\run-orch.ps1 -Tasks 50 -ChildN 20 -TaskMs 20 -BgPool 8   # child-orchestrator fan-out
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [string]$Label    = 'orch',
  [int]$Tasks       = 1000,
  [int]$TaskMs      = 20,
  [int]$ChildN      = 0,
  [int]$BgPool      = 8,
  [double]$Cpus     = 2,
  [int]$Base        = 2,     # limiter baseline concurrency
  [int]$ScaleUp     = 15,    # limiter scale-up-after seconds
  [switch]$Burst,            # limiter burst-to-ceiling (jump to ceiling on queueing)
  [int]$OverSub     = 0,     # limiter over-subscription dial (admit ceiling + N; "pre-fetch as running")
  [switch]$NoBatch,          # disable the batched status writer (#3) — the per-task-write "before"
  [switch]$EventualMarker,   # batched writer in eventual mode (no durable pre-invoke barrier) — max throughput
  [int]$FlushMs     = 25,    # status writer flush interval / barrier latency ceiling
  [int]$Port        = 5298,
  [int]$ReadyTimeoutSec = 180,
  [int]$MaxWaitSec  = 180,
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$root      = Split-Path -Parent $here
$compose   = Join-Path $root 'docker-compose.bg.yml'
$resultsDir= Join-Path $root 'results'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$baseUrl  = "http://127.0.0.1:$Port"

function Info($m){ Write-Host "[orch-harness] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[orch-harness] $m" -ForegroundColor Yellow }

$env:SUT_IMAGE=$SutImage; $env:SUT_PORT="$Port"; $env:SUT_CPUS="$Cpus"; $env:BG_POOL="$BgPool"
$env:BG_BASE="$Base"; $env:BG_SCALEUP="$ScaleUp"; $env:BG_CEILING="$BgPool"
$env:BG_BURST = if($Burst){ 'true' } else { 'false' }; $env:BG_OVERSUB="$OverSub"
$env:BATCH_WRITES = if($NoBatch){ 'false' } else { 'true' }
$env:DURABLE_BARRIER = if($EventualMarker){ 'false' } else { 'true' }; $env:FLUSH_MS="$FlushMs"
$env:DISPATCH_TIMING=''; $env:REUSE_THREAD='true'

Info "image: $SutImage  tasks: $Tasks  taskMs: $TaskMs  childN: $ChildN  bgPool: $BgPool  cpus: $Cpus  limiter(base=$Base scaleUp=${ScaleUp}s ceiling=$BgPool burst=$Burst overSub=$OverSub)"
Info "compose up (azurite + backend sut) ..."
docker compose -f $compose up -d | Out-Host
if ($LASTEXITCODE -ne 0){ throw "compose up failed" }

try {
  Info "waiting for backend ready (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    try { if((Invoke-RestMethod "$baseUrl/healthz" -TimeoutSec 5).status -eq 'ready'){ $ready=$true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if($ready){ Info "ready" } else { Warn "not ready - continuing" }

  # Enqueue the parent fan-out
  Info "enqueuing $Tasks tasks (taskms=$TaskMs childn=$ChildN) ..."
  $enq = & curl.exe -s --max-time 60 "$baseUrl/API/PerfBgEnqueue?n=$Tasks&taskms=$TaskMs&childn=$ChildN" 2>$null
  Info "enqueue -> $enq"
  $t0 = Get-Date

  # High-frequency allocation timeline until sustained idle (queued=0 && active=0)
  $samples = New-Object System.Collections.ArrayList
  $idleStreak = 0; $sawWork = $false; $wdl = (Get-Date).AddSeconds($MaxWaitSec)
  while((Get-Date) -lt $wdl){
    try { $a = Invoke-RestMethod "$baseUrl/API/PerfAllocation" -TimeoutSec 5 } catch { Start-Sleep -Milliseconds 250; continue }
    $t = ((Get-Date) - $t0).TotalSeconds
    [void]$samples.Add([pscustomobject]@{ t=[math]::Round($t,2); busy=$a.pool.bgBusy; total=$a.pool.bgTotal
      max=$a.limiter.currentMax; limActive=$a.limiter.active; limWait=$a.limiter.waiting
      jmActive=$a.jm.active; jmQueued=$a.jm.queued })
    $work = ($a.jm.active + $a.jm.queued)
    if($work -gt 0){ $sawWork=$true; $idleStreak=0 } elseif($sawWork){ $idleStreak++ }
    if($idleStreak -ge 6){ break }   # ~1.5s sustained idle => done (children too)
    Start-Sleep -Milliseconds 250
  }
  $tComplete = [math]::Round(((Get-Date) - $t0).TotalSeconds - 1.5, 1)   # minus the idle-confirm streak

  # ── Analysis ─────────────────────────────────────────────────────────────
  $active = $samples | Where-Object { ($_.jmActive + $_.jmQueued) -gt 0 -or $_.busy -gt 0 }
  $rampSample = $samples | Where-Object { $_.max -ge $BgPool } | Select-Object -First 1
  $rampTo = if($rampSample){ $rampSample.t } else { $null }
  $peakBusy = ($samples | Measure-Object busy -Maximum).Maximum
  $avgBusy = if($active){ [math]::Round(($active | Measure-Object busy -Average).Average,2) } else { 0 }
  $idlePct = if($active){ [math]::Round(100 * ($active | ForEach-Object { ($_.total - $_.busy)/$_.total } | Measure-Object -Average).Average,1) } else { 0 }
  # slot-held-but-worker-idle: limiter admitted (limActive) more than are actually on a worker (busy)
  $slotGap = if($active){ [math]::Round(($active | ForEach-Object { [math]::Max(0, $_.limActive - $_.busy) } | Measure-Object -Average).Average,2) } else { 0 }

  Write-Host ""
  Write-Host "===== fan-out allocation ($Tasks tasks, taskMs=$TaskMs, childN=$ChildN, pool=$BgPool, base=$Base) =====" -ForegroundColor Yellow
  Write-Host ("  completion time     : {0}s" -f $tComplete) -ForegroundColor Green
  Write-Host ("  ramp to ceiling ({0}) : {1}" -f $BgPool, $(if($rampTo){"$rampTo s"}else{"never"})) -ForegroundColor Green
  Write-Host ("  BG workers busy     : avg {0} / peak {1} of {2}" -f $avgBusy,$peakBusy,$BgPool) -ForegroundColor Green
  Write-Host ("  worker idle %%       : {0}%%  (while work pending)" -f $idlePct) -ForegroundColor Green
  Write-Host ("  slot>worker gap     : {0}  (limiter slots held with no worker — table-I/O idle)" -f $slotGap) -ForegroundColor Green
  # compact timeline (every ~2s)
  Write-Host "  timeline (t: busy/pool  limMax  queued):" -ForegroundColor DarkGray
  $last=-99; foreach($s in $samples){ if($s.t - $last -ge 2){ $last=$s.t; Write-Host ("    {0,6}s  {1}/{2}  max={3}  q={4}" -f $s.t,$s.busy,$s.total,$s.max,$s.jmQueued) -ForegroundColor DarkGray } }

  $result = [ordered]@{ label=$Label; timestamp=$stamp; tasks=$Tasks; taskMs=$TaskMs; childN=$ChildN
    bgPool=$BgPool; cpus=$Cpus; limiter=@{ base=$Base; scaleUp=$ScaleUp; ceiling=$BgPool }
    completionSec=$tComplete; rampToCeilingSec=$rampTo; avgBusy=$avgBusy; peakBusy=$peakBusy
    workerIdlePct=$idlePct; slotWorkerGap=$slotGap; samples=$samples }
  $jsonOut = Join-Path $resultsDir "$Label-b$Base-c$ChildN-$stamp.json"
  ($result | ConvertTo-Json -Depth 6) | Set-Content $jsonOut -Encoding utf8
  Info "wrote: $jsonOut"
}
finally {
  if($KeepUp){ Warn "leaving up (-KeepUp). down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}
