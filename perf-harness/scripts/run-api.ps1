<#
.SYNOPSIS
  Run the CRAFT http-only API perf harness against one CRAFT image.

.DESCRIPTION
  Brings up CRAFT in http-only mode (CRAFT_SERVE_API=true) serving the synthetic PerfApi endpoints
  (mounted into the image's /home/app/API), waits for /healthz, then measures under k6 load:
    1. Throughput / latency  - req/s, overall + per-endpoint p50/p95/p99, error rate (k6)
    2. Server resources      - CPU% / RAM sampled from `docker stats` during load
  Writes results\<label>-<timestamp>.json (machine) and .md (human). Tears down on exit.

  The CRAFT image is generic; http-only mode and the endpoint module come entirely from the compose
  env vars + volume mount — no perf-specific image. Default image craft:local (build once with
  `docker build -f build/Dockerfile -t craft:local .` or pass -Build).

.EXAMPLE
  pwsh scripts\run-api.ps1 -Label baseline
  pwsh scripts\run-api.ps1 -Label pool8 -Pool 8 -Only PerfSleep -Vus 40
  pwsh scripts\run-api.ps1 -Label fixed -Rate 200 -Duration 60s
  pwsh scripts\compare.ps1 results\baseline-*.json results\pool8-*.json
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [string]$Label    = 'api',
  [int]$Vus         = 10,
  [int]$Rate        = 0,
  [string]$Duration = '30s',
  [int]$Pool        = 2,
  [int]$Port        = 5297,
  [double]$Cpus     = 2,
  [string]$Only     = '',
  [int]$CpuMs       = 20,
  [int]$SleepMs     = 100,
  [int]$JsonN       = 1000,
  [int]$ReadyTimeoutSec = 120,
  [switch]$Build,
  [switch]$DispatchTiming,
  # A/B the reused-pipeline-thread optimization (production default = on). -NoReuseThread runs the "before".
  [switch]$NoReuseThread,
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$root       = Split-Path -Parent $here          # perf-harness/
$repoRoot   = Split-Path -Parent $root          # CRAFT repo root
$compose    = Join-Path $root 'docker-compose.api.yml'
$k6Dir      = Join-Path $root 'k6'
$resultsDir = Join-Path $root 'results'
$container  = 'craft-perf-api-sut'
$network    = 'craft-perf-apinet'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
# 127.0.0.1 (not localhost): Docker publishes the port on IPv4, but "localhost" can resolve to ::1 first
# and time out (Invoke-RestMethod/curl would then hang on readiness even though the SUT is up).
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"

function Info($m){ Write-Host "[api-harness] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[api-harness] $m" -ForegroundColor Yellow }

function ConvertTo-Mb([string]$s){
  if ($s -match '([\d.]+)\s*([KMG]i?B)'){
    $n=[double]$Matches[1]; switch($Matches[2]){
      'KiB'{$n/1024}'MiB'{$n}'GiB'{$n*1024}'KB'{$n/1000}'MB'{$n}'GB'{$n*1000}default{$n} } } else { 0 }
}

# ── Optional build (the normal CRAFT image, not a perf tag) ───────────────────
if($Build){
  Info "building CRAFT image $SutImage from build/Dockerfile ..."
  docker build -f (Join-Path $repoRoot 'build/Dockerfile') -t $SutImage $repoRoot | Out-Host
  if($LASTEXITCODE -ne 0){ throw "docker build failed" }
}

# ── Bring up ──────────────────────────────────────────────────────────────────
$env:SUT_IMAGE = $SutImage
$env:SUT_PORT  = "$Port"
$env:SUT_CPUS  = "$Cpus"
$env:POOL      = "$Pool"
$env:DISPATCH_TIMING = if($DispatchTiming){ 'true' } else { '' }
# Reused pipeline thread is on by default in the image; -NoReuseThread forces the "before" for A/B.
$env:REUSE_THREAD = if($NoReuseThread){ 'false' } else { '' }
$optLabel = if($NoReuseThread){ 'no-reusethread' } else { 'reusethread(default)' }
Info "image: $SutImage  port: $Port  cpus: $Cpus  pool: $Pool  only: $(if($Only){$Only}else{'mix'})  opt: $optLabel  label: $Label"
Info "compose up (http-only sut) ..."
docker compose -f $compose up -d | Out-Host
if ($LASTEXITCODE -ne 0){ throw "compose up failed" }

try {
  # ── Wait for readiness (/healthz -> status:ready) ───────────────────────────
  Info "waiting for http pool ready (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    try {
      $h = Invoke-RestMethod "$base/healthz" -TimeoutSec 5
      if($h.status -eq 'ready'){ $ready=$true; break }
    } catch {}
    Start-Sleep -Seconds 2
  }
  if($ready){ Info "http pool ready" } else { Warn "not ready after ${ReadyTimeoutSec}s - measuring anyway" }

  # ── Warm each endpoint once (JIT modules, first-invoke costs) + sanity check ──
  Info "warming endpoints ..."
  $warm = [ordered]@{}
  foreach($u in @('/API/PerfPing', "/API/PerfEcho?hi=1", "/API/PerfCpu?ms=$CpuMs", "/API/PerfSleep?ms=$SleepMs", "/API/PerfJson?n=$JsonN")){
    $code = & curl.exe -s -o NUL -w "%{http_code}" --max-time 30 "$base$u" 2>$null
    $warm[$u] = $code
    Info "  $u -> $code"
  }

  # ── Resource sampler (background) ───────────────────────────────────────────
  $statsFile = Join-Path $resultsDir "$Label-$stamp.stats.jsonl"
  $flagFile  = Join-Path $resultsDir ".sampling-api-$stamp"
  'go' | Set-Content $flagFile
  $sampler = Start-Job -ScriptBlock {
    param($c,$out,$flag)
    while(Test-Path $flag){
      $j = docker stats --no-stream --format '{{json .}}' $c 2>$null
      if($j){ Add-Content -Path $out -Value $j }
    }
  } -ArgumentList $container,$statsFile,$flagFile

  # ── k6 load ─────────────────────────────────────────────────────────────────
  Info "running k6 (vus=$Vus rate=$Rate duration=$Duration) ..."
  $summaryFile = Join-Path $resultsDir "$Label-$stamp.k6.json"
  $k6DirD = ($k6Dir -replace '\\','/'); $resultsDirD = ($resultsDir -replace '\\','/')
  & docker run --rm --network $network `
      -e BASE="http://sut:8080" -e VUS="$Vus" -e RATE="$Rate" -e DURATION="$Duration" `
      -e ONLY="$Only" -e CPU_MS="$CpuMs" -e SLEEP_MS="$SleepMs" -e JSON_N="$JsonN" `
      -v "${k6DirD}:/scripts:ro" -v "${resultsDirD}:/out" `
      grafana/k6 run /scripts/api_load.js --summary-export "/out/$(Split-Path $summaryFile -Leaf)" 2>&1 | Out-Host

  # stop sampler
  Remove-Item $flagFile -ErrorAction SilentlyContinue
  Wait-Job $sampler -Timeout 10 | Out-Null
  Remove-Job $sampler -Force -ErrorAction SilentlyContinue

  # ── Dispatch profiler (opt-in) — pull the windowed per-segment breakdown from container logs ──
  $dispatch = @()
  if($DispatchTiming){
    $dispatch = @(docker logs $container 2>&1 | Select-String 'DispatchProfile' | ForEach-Object { $_.ToString() })
    if($dispatch.Count){
      Info "dispatch profile (last window):"
      Write-Host ("  " + ($dispatch[-1] -replace '.*\[DispatchProfile\]','[DispatchProfile]')) -ForegroundColor Green
    } else { Warn "no DispatchProfile lines captured (need >= 2000 requests in the run)" }
  }

  # ── Parse resource samples ──────────────────────────────────────────────────
  $cpu=@(); $mem=@()
  if(Test-Path $statsFile){
    foreach($line in (Get-Content $statsFile)){
      try{ $s=$line|ConvertFrom-Json } catch { continue }
      if($s.CPUPerc){ $cpu += [double]($s.CPUPerc -replace '%','') }
      if($s.MemUsage){ $mem += (ConvertTo-Mb ($s.MemUsage -split '/')[0]) }
    }
  }
  $cpuAvg = if($cpu){ [math]::Round(($cpu|Measure-Object -Average).Average,1) } else { $null }
  $cpuMax = if($cpu){ [math]::Round(($cpu|Measure-Object -Maximum).Maximum,1) } else { $null }
  $memAvg = if($mem){ [math]::Round(($mem|Measure-Object -Average).Average,1) } else { $null }
  $memMax = if($mem){ [math]::Round(($mem|Measure-Object -Maximum).Maximum,1) } else { $null }

  # ── Parse k6 summary ────────────────────────────────────────────────────────
  $k6=$null; if(Test-Path $summaryFile){ $k6 = Get-Content -Raw $summaryFile | ConvertFrom-Json }
  function MetricVal($name,$field){ try{ $m=$k6.metrics.$name; if($null -ne $m.values){ $m.values.$field } else { $m.$field } } catch { $null } }

  $load = [ordered]@{
    httpReqs    = MetricVal 'http_reqs' 'count'
    reqPerSec   = [math]::Round(([double](MetricVal 'http_reqs' 'rate')),1)
    failRate    = MetricVal 'http_req_failed' 'value'
    durAvgMs    = [math]::Round(([double](MetricVal 'http_req_duration' 'avg')),2)
    durP50Ms    = [math]::Round(([double](MetricVal 'http_req_duration' 'med')),2)
    durP95Ms    = [math]::Round(([double](MetricVal 'http_req_duration' 'p(95)')),2)
    durP99Ms    = [math]::Round(([double](MetricVal 'http_req_duration' 'p(99)')),2)
    waitAvgMs   = [math]::Round(([double](MetricVal 'http_req_waiting' 'avg')),2)
    dataRecvMB  = [math]::Round(([double](MetricVal 'data_received' 'count'))/1MB,2)
  }

  # Per-endpoint latency (only those actually exercised have data)
  $perEndpoint = [ordered]@{}
  foreach($ep in @('PerfPing','PerfEcho','PerfCpu','PerfSleep','PerfJson')){
    $avg = MetricVal "lat_$ep" 'avg'
    if($null -ne $avg){
      $perEndpoint[$ep] = [ordered]@{
        avgMs = [math]::Round(([double]$avg),2)
        p95Ms = [math]::Round(([double](MetricVal "lat_$ep" 'p(95)')),2)
        p99Ms = [math]::Round(([double](MetricVal "lat_$ep" 'p(99)')),2)
      }
    }
  }

  # ── Assemble + write ────────────────────────────────────────────────────────
  $result = [ordered]@{
    label=$Label; timestamp=$stamp; sutImage=$SutImage; mode='http-only'; ready=$ready
    config=@{ vus=$Vus; rate=$Rate; duration=$Duration; pool=$Pool; cpus=$Cpus; port=$Port
              only=$Only; cpuMs=$CpuMs; sleepMs=$SleepMs; jsonN=$JsonN }
    warmup=$warm
    resources=[ordered]@{ cpuAvgPct=$cpuAvg; cpuMaxPct=$cpuMax; memAvgMB=$memAvg; memMaxMB=$memMax; samples=$cpu.Count }
    load=$load
    perEndpoint=$perEndpoint
    dispatchProfile=$dispatch
  }
  $jsonOut = Join-Path $resultsDir "$Label-$stamp.json"
  ($result | ConvertTo-Json -Depth 8) | Set-Content $jsonOut -Encoding utf8

  # ── Human report (markdown) ─────────────────────────────────────────────────
  $mdOut = Join-Path $resultsDir "$Label-$stamp.md"
  $sb = [System.Text.StringBuilder]::new()
  [void]$sb.AppendLine("# API perf result - $Label ($stamp)")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("- **SUT image:** ``$SutImage`` (http-only)   **Ready:** $ready")
  [void]$sb.AppendLine("- **Load:** $Vus VUs / rate=$Rate / $Duration   **Pool:** $Pool   **CPUs:** $Cpus   **Endpoint:** $(if($Only){$Only}else{'mix'})")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Server resources during load")
  [void]$sb.AppendLine("| metric | avg | max |")
  [void]$sb.AppendLine("|---|---:|---:|")
  [void]$sb.AppendLine("| CPU % (of 1 core) | $cpuAvg | $cpuMax |")
  [void]$sb.AppendLine("| RAM (MB) | $memAvg | $memMax |")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Throughput / latency (k6)")
  [void]$sb.AppendLine("| metric | value |")
  [void]$sb.AppendLine("|---|---:|")
  foreach($k in $load.Keys){ [void]$sb.AppendLine("| $k | $($load[$k]) |") }
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Per-endpoint latency (ms)")
  [void]$sb.AppendLine("| endpoint | avg | p95 | p99 |")
  [void]$sb.AppendLine("|---|---:|---:|---:|")
  foreach($ep in $perEndpoint.Keys){
    $e=$perEndpoint[$ep]; [void]$sb.AppendLine("| $ep | $($e.avgMs) | $($e.p95Ms) | $($e.p99Ms) |")
  }
  $sb.ToString() | Set-Content $mdOut -Encoding utf8

  Info "wrote: $jsonOut"
  Info "wrote: $mdOut"
  Write-Host ""
  Get-Content $mdOut | Write-Host
}
finally {
  if($KeepUp){ Warn "leaving containers up (-KeepUp). Tear down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}
