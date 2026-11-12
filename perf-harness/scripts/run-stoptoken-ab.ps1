<#
.SYNOPSIS
  Stop-token A/B: does a timed-out task blocked in an HTTP call stop and free its memory, or get abandoned and pin it?

.DESCRIPTION
  -Tasks background tasks each hold -HoldMB of live memory and then block in CIPP's Invoke-CIPPRestMethod on a
  server that accepts connections and never answers (no client timeout). The BG timeout (-BgTimeout) cancels them.
  Without a stop token PowerShell cannot interrupt the call: after -StopGrace Craft abandons the worker and the
  memory stays reachable from the blocked thread. With Craft's per-job token cancelled before Stop and CIPPSharp
  passing it into the request, the call throws, the pipeline stops and the memory is freed.
  Run it per combination: -SutImage old/new Craft x -CippRoot a CIPP checkout with the old/new CIPPSharp.dll.

.EXAMPLE
  pwsh scripts\run-stoptoken-ab.ps1 -SutImage ghcr.io/cyberdrain/craft:dev -CippRoot C:\GitHub\CIPP -Label base
  pwsh scripts\run-stoptoken-ab.ps1 -SutImage craft:local -CippRoot C:\GitHub\CIPP\.claude\worktrees\bound-memos -Label new
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [Parameter(Mandatory)][string]$CippRoot,
  [string]$Label    = 'stoptoken',
  [int]$Tasks       = 4,
  [int]$HoldMB      = 200,
  [int]$BgTimeout   = 20,
  [int]$StopGrace   = 10,
  [int]$Port        = 5298,
  [switch]$KeepUp
)
$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$files = @('-f', (Join-Path $root 'docker-compose.bg.yml'), '-f', (Join-Path $root 'docker-compose.bg-cipp.yml'), '-f', (Join-Path $root 'docker-compose.blackhole.yml'))
$base  = "http://127.0.0.1:$Port"
function Info($m) { Write-Host "[stoptoken] $m" -ForegroundColor Cyan }
function Get-Settled { $S = $null; foreach ($i in 1..6) { $S = Invoke-RestMethod "$base/API/PerfSettle" -TimeoutSec 300 }; [double]$S.heapMB }

$env:CIPP_ROOT = (Resolve-Path $CippRoot).Path
$env:SUT_IMAGE = $SutImage; $env:SUT_PORT = "$Port"; $env:SUT_CPUS = '2'; $env:BG_POOL = "$([math]::Max($Tasks, 2))"
$env:BG_BURST = 'true'; $env:BG_BASE = $env:BG_POOL; $env:BG_SCALEUP = '1'; $env:BG_CEILING = $env:BG_POOL
$env:JOB_BATCH = '100'; $env:JOB_POLL_MS = '250'; $env:RECYCLE_AFTER = '0'; $env:GC_HEAP_LIMIT_MB = ''
$env:BG_TIMEOUT = "$BgTimeout"; $env:STOP_GRACE = "$StopGrace"
Info "image=$SutImage cipp=$($env:CIPP_ROOT) tasks=$Tasks hold=${HoldMB}MB bgTimeout=${BgTimeout}s stopGrace=${StopGrace}s"
docker compose @files up -d --force-recreate 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'compose up failed' }
try {
  $Deadline = (Get-Date).AddSeconds(300)
  while ((Get-Date) -lt $Deadline) { try { if ((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).status -eq 'ready') { break } } catch {}; Start-Sleep 2 }
  $Before = Get-Settled
  Info "baseline heap ${Before} MB; enqueuing $Tasks blocked tasks"
  $null = Invoke-RestMethod "$base/API/PerfHangHttpEnqueue?n=$Tasks&holdmb=$HoldMB" -TimeoutSec 60
  Start-Sleep -Seconds ($BgTimeout + $StopGrace + 20)
  $After = Get-Settled
  $Log = docker logs craft-perf-bg-sut 2>&1
  $Abandoned = @($Log | Select-String 'Abandoning the worker').Count
  $Returned = @($Log | Select-String 'returned .* after it was abandoned').Count
  $TimedOut = @($Log | Select-String 'Invoke-CraftTask timed out').Count
  $Health = Invoke-RestMethod "$base/healthz" -TimeoutSec 10
  $Delta = [math]::Round($After - $Before, 1)
  $Pinned = $Delta -gt ($Tasks * $HoldMB * 0.5)
  Write-Host "`n===== stop-token A/B: $Label =====" -ForegroundColor Yellow
  Write-Host "  tasks timed out     : $TimedOut of $Tasks"
  Write-Host "  workers abandoned   : $Abandoned (returned since: $Returned)"
  Write-Host "  health workers      : $($Health.workers | ConvertTo-Json -Compress)"
  Write-Host ("  heap after settle   : {0} MB -> {1} MB ({2:+0.0;-0.0} MB; {3} MB was held by the tasks)" -f $Before, $After, $Delta, ($Tasks * $HoldMB))
  if ($Pinned -or $Abandoned -gt $Returned) { Write-Host '  >> PINNED: blocked tasks were abandoned and kept their memory' -ForegroundColor Red }
  else { Write-Host '  >> FREED: blocked tasks stopped and their memory was released' -ForegroundColor Green }
  [ordered]@{ label = $Label; image = $SutImage; cippRoot = $env:CIPP_ROOT; tasks = $Tasks; holdMB = $HoldMB; timedOut = $TimedOut; abandoned = $Abandoned; returned = $Returned; heapBeforeMB = $Before; heapAfterMB = $After; deltaMB = $Delta; pinned = $Pinned } |
    ConvertTo-Json | Set-Content (Join-Path $root "results/stoptoken-$Label-$(Get-Date -Format yyyyMMdd-HHmmss).json") -Encoding utf8
}
finally {
  if ($KeepUp) { Info 'left up (-KeepUp)' } else { docker compose @files down -v 2>&1 | Out-Null }
}
