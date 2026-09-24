<#
.SYNOPSIS
  Sweep the on-the-fly /api compression level and measure the CPU / latency / ratio trade-off.

.DESCRIPTION
  The response-compression level (App:Api:CompressionLevel / CRAFT_API_COMPRESSION_LEVEL) is read once at
  startup, so this recreates the container for each level and, per encoding, measures:
    * compression ratio  - raw identity size / compressed wire size (one curl each)
    * CPU%               - docker stats sampled during a fixed-rate k6 load
    * latency            - k6 p95
  It prints a matrix (level x encoding) so the knee — where extra ratio stops being worth the CPU and
  latency — is visible. Fastest is the shipped default; this is the evidence for changing it or not.

  The CRAFT image must include the compression-level config. Build it first (or pass -Build):
    docker build -f ..\build\Dockerfile -t craft:local ..

  Rate is deliberately low (Craft rate-limits and the small pool sheds under a burst); raise -Rate only
  against a bigger -Pool. Tears down on exit.

.EXAMPLE
  pwsh scripts\run-compression-levels.ps1 -Build
  pwsh scripts\run-compression-levels.ps1 -Levels Fastest,Optimal,SmallestSize -Encodings gzip,br
  pwsh scripts\run-compression-levels.ps1 -JsonN 8000 -Rate 20 -Pool 4
  # A captured real response (served verbatim by PerfFile from -PayloadDir), all four encodings:
  pwsh scripts\run-compression-levels.ps1 -PayloadDir C:\captures -Url '/API/PerfFile?name=listlogs.json' `
    -Encodings br,gzip,identity
#>
[CmdletBinding()]
param(
  [string]$SutImage   = 'craft:local',
  [int]$Port          = 5297,
  [int]$Pool          = 2,
  [double]$Cpus       = 2,
  [string[]]$Levels   = @('Fastest','Optimal','SmallestSize'),
  [string[]]$Encodings= @('gzip','br'),
  [int]$Rate          = 10,
  [string]$Duration   = '20s',
  [int]$JsonN         = 2000,
  [string]$Url        = '',      # path to drive instead of PerfJson, e.g. /API/PerfFile?name=listlogs.json
  [string]$PayloadDir = '',      # host folder mounted at /payloads for PerfFile
  [int]$ReadyTimeoutSec = 120,
  [switch]$Build,
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$root       = Split-Path -Parent $here
$repoRoot   = Split-Path -Parent $root
$composeApi = Join-Path $root 'docker-compose.api.yml'
$k6Dir      = Join-Path $root 'k6'
$resultsDir = Join-Path $root 'results'
$container  = 'craft-perf-api-sut'
$network    = 'craft-perf-apinet'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"

function Info($m){ Write-Host "[levels] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[levels] $m" -ForegroundColor Yellow }

if($Build){
  Info "building CRAFT image $SutImage ..."
  docker build -f (Join-Path $repoRoot 'build/Dockerfile') -t $SutImage $repoRoot | Out-Host
  if($LASTEXITCODE -ne 0){ throw "docker build failed" }
}

if(-not $Url){ $Url = "/API/PerfJson?n=$JsonN" }
if($PayloadDir){ $env:PAYLOAD_DIR = (Resolve-Path $PayloadDir).Path }
$env:SUT_IMAGE = $SutImage; $env:SUT_PORT = "$Port"; $env:SUT_CPUS = "$Cpus"; $env:POOL = "$Pool"

# One curl to /API/PerfJson at the given encoding ('' = identity). Returns the on-the-wire body size
# (%{size_download}, no --compressed so a compressed body is not decompressed).
function WireBytes([string]$Enc){
  $a = @('-s','-o','NUL','--max-time','30','-w','%{size_download}')
  if($Enc){ $a += @('-H', "Accept-Encoding: $Enc") }
  $a += "$base$Url"
  [long](& curl.exe @a 2>$null)
}

function WaitReady {
  $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    try { if((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).ready.http){ return $true } } catch {}
    Start-Sleep -Seconds 2
  }
  return $false
}

function MetricVal($k6,$name,$field){ try{ $m=$k6.metrics.$name; if($null -ne $m.values){ $m.values.$field } else { $m.$field } } catch { $null } }
$k6DirD = ($k6Dir -replace '\\','/'); $resultsDirD = ($resultsDir -replace '\\','/')

# k6 at fixed rate for one encoding, sampling docker stats CPU. Returns cpu avg/max, p95, req/s, wire/resp.
function LoadOne([string]$Enc, [string]$tag){
  $statsFile = Join-Path $resultsDir "levels-$tag-$stamp.stats.jsonl"
  $flagFile  = Join-Path $resultsDir ".levels-$tag-$stamp"
  'go' | Set-Content $flagFile
  $sampler = Start-Job -ScriptBlock {
    param($c,$out,$flag)
    while(Test-Path $flag){ $j = docker stats --no-stream --format '{{json .}}' $c 2>$null; if($j){ Add-Content $out $j } }
  } -ArgumentList $container,$statsFile,$flagFile

  $summary = Join-Path $resultsDir "levels-$tag-$stamp.k6.json"
  & docker run --rm --network $network `
      -e BASE="http://sut:8080" -e RATE="$Rate" -e DURATION="$Duration" `
      -e URL="$Url" -e ENC="$Enc" `
      -v "${k6DirD}:/scripts:ro" -v "${resultsDirD}:/out" `
      grafana/k6 run /scripts/api_load.js --summary-export "/out/$(Split-Path $summary -Leaf)" 2>&1 | Out-Null

  Remove-Item $flagFile -ErrorAction SilentlyContinue
  Wait-Job $sampler -Timeout 10 | Out-Null
  Remove-Job $sampler -Force -ErrorAction SilentlyContinue

  $cpu=@()
  if(Test-Path $statsFile){ foreach($line in (Get-Content $statsFile)){ try{ $s=$line|ConvertFrom-Json } catch { continue }; if($s.CPUPerc){ $cpu += [double]($s.CPUPerc -replace '%','') } } }
  $k6 = if(Test-Path $summary){ Get-Content -Raw $summary | ConvertFrom-Json } else { $null }
  $reqs = [double](MetricVal $k6 'http_reqs' 'count'); $recv = [double](MetricVal $k6 'data_received' 'count')
  [ordered]@{
    cpuAvgPct = if($cpu){ [math]::Round(($cpu|Measure-Object -Average).Average,1) } else { $null }
    cpuMaxPct = if($cpu){ [math]::Round(($cpu|Measure-Object -Maximum).Maximum,1) } else { $null }
    reqPerSec = [math]::Round(([double](MetricVal $k6 'http_reqs' 'rate')),1)
    p50Ms     = [math]::Round(([double](MetricVal $k6 'http_req_duration' 'med')),2)
    p95Ms     = [math]::Round(([double](MetricVal $k6 'http_req_duration' 'p(95)')),2)
    wirePerResp = if($reqs -gt 0){ [long]($recv/$reqs) } else { 0 }
  }
}

$rows = New-Object System.Collections.Generic.List[object]
$rawBytes = 0L

try {
  foreach($level in $Levels){
    $env:API_COMPRESSION_LEVEL = $level
    Info "=== level: $level — recreating container ==="
    docker compose -f $composeApi up -d --force-recreate | Out-Host
    if($LASTEXITCODE -ne 0){ throw "compose up failed for level $level" }
    if(-not (WaitReady)){ throw "SUT not ready for level $level" }

    $cfg = docker logs $container 2>&1 | Select-String 'System\] Compression' | Select-Object -Last 1
    if($cfg){ Info ("config: " + ($cfg.ToString() -replace '.*\[System\]','[System]').Trim()) }

    # Warm once, capture the identity baseline once (level-independent).
    [void](WireBytes '')
    # Discarded load pass over every encoding: a fresh container JITs its compressors and grows its
    # runspace pool under the first load, which otherwise lands on whichever encoding is measured first.
    Info "  warm-up pass (discarded) ..."
    foreach($enc in $Encodings){ [void](LoadOne $enc "$level-warm-$enc") }
    if($rawBytes -eq 0){ $rawBytes = WireBytes '' }

    foreach($enc in $Encodings){
      $wire = WireBytes $enc
      $ratio = if($wire -gt 0){ [math]::Round($rawBytes / $wire, 2) } else { 0 }
      Info "  ${enc}: wire $wire B (${ratio}x) - running k6 @ $Rate rps ..."
      $load = LoadOne $enc "$level-$enc"
      $rows.Add([ordered]@{
        level=$level; encoding=$enc; wireBytes=$wire; ratio=$ratio
        cpuAvgPct=$load.cpuAvgPct; cpuMaxPct=$load.cpuMaxPct; p50Ms=$load.p50Ms; p95Ms=$load.p95Ms; reqPerSec=$load.reqPerSec
      })
    }
  }

  # ── report ────────────────────────────────────────────────────────────────────
  $result = [ordered]@{
    label='compression-levels'; timestamp=$stamp; sutImage=$SutImage
    config=@{ pool=$Pool; cpus=$Cpus; url=$Url; rate=$Rate; duration=$Duration; levels=$Levels; encodings=$Encodings }
    identityBytes=$rawBytes
    rows=$rows
  }
  $jsonOut = Join-Path $resultsDir "compression-levels-$stamp.json"
  ($result | ConvertTo-Json -Depth 8) | Set-Content $jsonOut -Encoding utf8

  $mdOut = Join-Path $resultsDir "compression-levels-$stamp.md"
  $sb = [System.Text.StringBuilder]::new()
  [void]$sb.AppendLine("# /api compression level sweep ($stamp)")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("- **SUT:** ``$SutImage``   **CPUs:** $Cpus   **Pool:** $Pool   **Payload:** ``$Url`` ($rawBytes B raw)")
  [void]$sb.AppendLine("- **Load:** rate=$Rate for $Duration per (level x encoding)")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("| level | encoding | wire B/resp | ratio | CPU% avg | CPU% max | p50 ms | p95 ms | req/s |")
  [void]$sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|")
  foreach($r in $rows){
    [void]$sb.AppendLine("| $($r.level) | $($r.encoding) | $($r.wireBytes) | $($r.ratio)x | $($r.cpuAvgPct) | $($r.cpuMaxPct) | $($r.p50Ms) | $($r.p95Ms) | $($r.reqPerSec) |")
  }
  $sb.ToString() | Set-Content $mdOut -Encoding utf8

  Info "wrote: $jsonOut"
  Info "wrote: $mdOut"
  Write-Host ""
  Get-Content $mdOut | Write-Host
}
finally {
  Remove-Item Env:\API_COMPRESSION_LEVEL, Env:\PAYLOAD_DIR -ErrorAction SilentlyContinue
  if($KeepUp){ Warn "leaving containers up (-KeepUp). Tear down: docker compose -f `"$composeApi`" down -v" }
  else { Info "tearing down ..."; docker compose -f $composeApi down -v 2>&1 | Out-Null }
}
