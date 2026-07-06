<#
.SYNOPSIS
  Profile the CRAFT background worker dispatch path.

.DESCRIPTION
  Brings up CRAFT in backend mode (Http + Background) + Azurite, enqueues an orchestrator batch of N no-op
  tasks (the real "orchestrator enqueue" path), and reads the dispatch profiler's windowed per-segment
  breakdown for the background invokes (each task = one BG worker.InvokeAsync of Invoke-CraftTask -> Push-PerfBg).
  Isolates the BG PS-invoke cost (esp. the `run` segment) independent of orchestrator table I/O.

  Dispatch timing is always on here (that's the point). -NoReuseThread runs the "before" for the A/B.

.EXAMPLE
  pwsh scripts\run-bg.ps1 -Tasks 3000
  pwsh scripts\run-bg.ps1 -Tasks 3000 -NoReuseThread
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [string]$Label    = 'bg',
  [int]$Tasks       = 3000,
  [int]$HttpPool    = 2,
  [int]$BgPool      = 4,
  [int]$Port        = 5298,
  [double]$Cpus     = 2,
  [int]$ReadyTimeoutSec = 180,
  [int]$WaitSec     = 150,
  [switch]$NoReuseThread,
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$root      = Split-Path -Parent $here
$compose   = Join-Path $root 'docker-compose.bg.yml'
$resultsDir= Join-Path $root 'results'
$container = 'craft-perf-bg-sut'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"   # IPv4 — Docker publishes on 127.0.0.1, 'localhost' can pick ::1 and hang

function Info($m){ Write-Host "[bg-harness] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[bg-harness] $m" -ForegroundColor Yellow }

$env:SUT_IMAGE = $SutImage; $env:SUT_PORT = "$Port"; $env:SUT_CPUS = "$Cpus"
$env:HTTP_POOL = "$HttpPool"; $env:BG_POOL = "$BgPool"
$env:DISPATCH_TIMING = 'true'
$env:REUSE_THREAD = if($NoReuseThread){ 'false' } else { 'true' }
$reuseLabel = if($NoReuseThread){ 'no-reusethread' } else { 'reusethread(default)' }

Info "image: $SutImage  port: $Port  cpus: $Cpus  httpPool: $HttpPool  bgPool: $BgPool  tasks: $Tasks  reuse: $reuseLabel"
Info "compose up (azurite + backend sut) ..."
docker compose -f $compose up -d | Out-Host
if ($LASTEXITCODE -ne 0){ throw "compose up failed" }

try {
  # ── Wait for readiness (http + background both ready) ───────────────────────
  Info "waiting for backend ready (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    try { if((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).status -eq 'ready'){ $ready=$true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if($ready){ Info "backend ready" } else { Warn "not ready after ${ReadyTimeoutSec}s - continuing" }

  # ── Enqueue the orchestrator batch ──────────────────────────────────────────
  Info "enqueuing $Tasks no-op tasks ..."
  $enq = & curl.exe -s --max-time 60 "$base/API/PerfBgEnqueue?n=$Tasks" 2>$null
  Info "enqueue -> $enq"

  # ── Wait for the BG pool to grind through the batch; watch the profiler windows ──
  Info "processing (up to ${WaitSec}s); watching dispatch profiler windows ..."
  $seen=0; $wdeadline=(Get-Date).AddSeconds($WaitSec)
  while((Get-Date) -lt $wdeadline){
    Start-Sleep -Seconds 5
    $lines = @(docker logs $container 2>&1 | Select-String 'DispatchProfile' | ForEach-Object { $_.ToString() })
    if($lines.Count -gt $seen){
      $seen = $lines.Count
      Info "  windows so far: $seen  latest: $(($lines[-1] -replace '.*\[DispatchProfile\]','[DispatchProfile]'))"
    }
    if($seen -ge 2){ break }   # 2 windows (~4000 invokes) is plenty for a stable average
  }

  $dispatch = @(docker logs $container 2>&1 | Select-String 'DispatchProfile' | ForEach-Object { ($_ -replace '.*\[DispatchProfile\]','[DispatchProfile]') })
  Write-Host ""
  Write-Host "===== BG dispatch profile ($reuseLabel) =====" -ForegroundColor Yellow
  if($dispatch.Count){ $dispatch | ForEach-Object { Write-Host "  $_" -ForegroundColor Green } }
  else { Warn "no DispatchProfile windows captured — try more -Tasks or a longer -WaitSec (orchestrator table I/O is slow on Azurite)" }

  $result = [ordered]@{
    label=$Label; timestamp=$stamp; sutImage=$SutImage; mode='backend'; reuseThread=(-not $NoReuseThread)
    config=@{ tasks=$Tasks; httpPool=$HttpPool; bgPool=$BgPool; cpus=$Cpus }
    dispatchProfile=$dispatch
  }
  $jsonOut = Join-Path $resultsDir "$Label-$reuseLabel-$stamp.json"
  ($result | ConvertTo-Json -Depth 6) | Set-Content $jsonOut -Encoding utf8
  Info "wrote: $jsonOut"
}
finally {
  if($KeepUp){ Warn "leaving containers up (-KeepUp). Tear down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}
