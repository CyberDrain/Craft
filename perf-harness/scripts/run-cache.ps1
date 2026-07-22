<#
.SYNOPSIS
  Profile the CRAFT response cache + auth header middleware in frontend+http mode.

.DESCRIPTION
  Brings up CRAFT with the Frontend + Http roles (so the disk-backed response cache is on and the auth header
  middleware runs), warms the cache, then drives k6 load at /API/ListPerf with an x-ms-client-principal
  header. Reads the cache/auth profiler's windowed breakdown (auth / roleHash / keyBuild / get / disk / set)
  plus k6 latency + the X-Cache hit ratio.

  -Mode hit  : fixed query → all cache hits after the first (profiles the hit path: roleHash + get + disk read)
  -Mode miss : unique query per iteration → all misses (profiles PS invoke + cache Set disk write)

.EXAMPLE
  pwsh scripts\run-cache.ps1 -Mode hit  -N 50
  pwsh scripts\run-cache.ps1 -Mode miss -N 50
  pwsh scripts\run-cache.ps1 -Mode hit  -N 2000   # large bodies — stress the per-hit disk read
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [ValidateSet('hit','miss')][string]$Mode = 'hit',
  [int]$Vus         = 20,
  [string]$Duration = '20s',
  [int]$N           = 50,
  [int]$Pool        = 2,
  [int]$Port        = 5299,
  [double]$Cpus     = 2,
  [int]$ReadyTimeoutSec = 120,
  [switch]$DiskOnly,   # A/B: disable the in-memory body tier (disk-only "before")
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$root      = Split-Path -Parent $here
$compose   = Join-Path $root 'docker-compose.api.yml'
$k6Dir     = Join-Path $root 'k6'
$resultsDir= Join-Path $root 'results'
$container = 'craft-perf-api-sut'
$network   = 'craft-perf-apinet'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"   # IPv4 (Docker publishes on 127.0.0.1; 'localhost' may pick ::1 and hang)

function Info($m){ Write-Host "[cache-harness] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[cache-harness] $m" -ForegroundColor Yellow }

# SWA-format principal identical to the k6 script (so the warm request keys the same as the load).
$principalJson = '{"identityProvider":"aad","userId":"perf-user-1","userDetails":"perf@test.local","userRoles":["admin","editor","reader"]}'
$principal = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($principalJson))

$env:SUT_IMAGE=$SutImage; $env:SUT_PORT="$Port"; $env:SUT_CPUS="$Cpus"; $env:POOL="$Pool"
$env:SERVE_FRONTEND='true'          # frontend+http → response cache defaults on + auth middleware runs
$env:CACHE_TIMING='true'
$env:REUSE_THREAD='true'; $env:DISPATCH_TIMING=''
$env:CACHE_MEM_BYTES = if($DiskOnly){ '0' } else { '67108864' }   # 0 = disk-only (before), 64 MiB = mem tier (after)
$memLabel = if($DiskOnly){ 'disk-only' } else { 'mem-tier' }

Info "image: $SutImage  mode: $Mode  N: $N  vus: $Vus  pool: $Pool  cpus: $Cpus  tier: $memLabel (frontend+http)"
Info "compose up ..."
docker compose -f $compose up -d | Out-Host
if ($LASTEXITCODE -ne 0){ throw "compose up failed" }

try {
  Info "waiting for ready (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    try { if((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).status -eq 'ready'){ $ready=$true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if($ready){ Info "ready" } else { Warn "not ready after ${ReadyTimeoutSec}s - continuing" }

  # Warm the cache (hit mode): one request with the same key populates the entry so the load is ~100% hits.
  $warm = & curl.exe -s -D - -o NUL -H "x-ms-client-principal: $principal" "$base/API/ListPerf?n=$N" 2>$null
  $xcache = ($warm | Select-String -Pattern 'X-Cache:' | Select-Object -First 1)
  Info "warm  X-Cache: $(( "$xcache" -replace '.*X-Cache:\s*','' ).Trim())"

  # k6 load
  Info "running k6 (mode=$Mode vus=$Vus duration=$Duration) ..."
  $summaryFile = Join-Path $resultsDir "cache-$Mode-$stamp.k6.json"
  $k6DirD = ($k6Dir -replace '\\','/'); $resultsDirD = ($resultsDir -replace '\\','/')
  & docker run --rm --network $network `
      -e BASE="http://sut:8080" -e VUS="$Vus" -e DURATION="$Duration" -e N="$N" -e MODE="$Mode" `
      -v "${k6DirD}:/scripts:ro" -v "${resultsDirD}:/out" `
      grafana/k6 run /scripts/cache_load.js --summary-export "/out/$(Split-Path $summaryFile -Leaf)" 2>&1 | Out-Host

  # Cache/auth profiler windows from the container logs
  $prof = @(docker logs $container 2>&1 | Select-String 'CacheProfile' | ForEach-Object { ($_ -replace '.*\[CacheProfile\]','[CacheProfile]') })
  Write-Host ""
  Write-Host "===== cache + auth profile (mode=$Mode, N=$N, $memLabel) =====" -ForegroundColor Yellow
  if($prof.Count){ $prof | ForEach-Object { Write-Host "  $_" -ForegroundColor Green } }
  else { Warn "no CacheProfile windows (need >= 2000 cacheable requests — raise -Vus/-Duration)" }

  # k6 latency + hit ratio
  $k6=$null; if(Test-Path $summaryFile){ $k6 = Get-Content -Raw $summaryFile | ConvertFrom-Json }
  # NB: do NOT name this MV/MB/etc — PowerShell aliases (mv->Move-Item) outrank functions.
  function MetricVal($n,$f){ try{ $m=$k6.metrics.$n; if($null -ne $m.values){ $m.values.$f } else { $m.$f } } catch { $null } }
  $rps=[math]::Round([double](MetricVal 'http_reqs' 'rate'),1); $p95=[math]::Round([double](MetricVal 'http_req_duration' 'p(95)'),2)
  $avg=[math]::Round([double](MetricVal 'http_req_duration' 'avg'),2)
  Write-Host ("  k6: req/s=$rps  latency avg=$avg ms  p95=$p95 ms") -ForegroundColor Green

  $result = [ordered]@{ label="cache-$Mode"; timestamp=$stamp; mode=$Mode; n=$N; ready=$ready
    config=@{ vus=$Vus; duration=$Duration; pool=$Pool; cpus=$Cpus }
    k6=@{ reqPerSec=$rps; latAvgMs=$avg; latP95Ms=$p95 }; cacheProfile=$prof }
  $jsonOut = Join-Path $resultsDir "cache-$Mode-$memLabel-$stamp.json"
  ($result | ConvertTo-Json -Depth 6) | Set-Content $jsonOut -Encoding utf8
  Info "wrote: $jsonOut"
}
finally {
  if($KeepUp){ Warn "leaving containers up (-KeepUp). Tear down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}
