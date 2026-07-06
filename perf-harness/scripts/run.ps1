<#
.SYNOPSIS
  Run the CRAFT static-content perf + correctness harness against one SUT image.

.DESCRIPTION
  Brings up Azurite + the SUT (docker compose), waits for the PowerShell pool to be
  ready, then measures three things while serving static assets:
    1. Content correctness  - per-asset Cache-Control / ETag / Vary / Content-Encoding / Set-Cookie
    2. Content serving       - TTFB, throughput, error rate, compression ratio (k6 + curl)
    3. Server resources      - CPU% / RAM / net / block-IO sampled from `docker stats` during load
  Writes results\<label>-<timestamp>.json (machine) and .md (human). Tears down on exit.

.EXAMPLE
  pwsh scripts\run.ps1 -SutImage cipp-ng:perf-baseline -Label baseline
  pwsh scripts\run.ps1 -SutImage cipp-ng:perf-optimized -Label optimized
  pwsh scripts\compare.ps1 results\baseline-*.json results\optimized-*.json
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'cipp-ng:perf-baseline',
  [string]$Label    = 'baseline',
  [int]$Vus         = 10,
  [int]$Rate        = 0,
  [string]$Duration = '30s',
  [int]$Port        = 5197,
  [double]$Cpus     = 2,
  [int]$ReadyTimeoutSec = 900,
  [switch]$StaticOnly,
  [switch]$NoCompression,
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$root       = Split-Path -Parent $here
$compose    = Join-Path $root 'docker-compose.yml'
$k6Dir      = Join-Path $root 'k6'
$resultsDir = Join-Path $root 'results'
$container  = 'craft-perf-sut'
$network    = 'craft-perfnet'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://localhost:$Port"

function Info($m){ Write-Host "[harness] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[harness] $m" -ForegroundColor Yellow }

function ConvertTo-Mb([string]$s){
  if ($s -match '([\d.]+)\s*([KMG]i?B)'){
    $n=[double]$Matches[1]; switch($Matches[2]){
      'KiB'{$n/1024}'MiB'{$n}'GiB'{$n*1024}'KB'{$n/1000}'MB'{$n}'GB'{$n*1000}default{$n} } } else { 0 }
}

# ── Bring up ────────────────────────────────────────────────────────────────
$env:SUT_IMAGE = $SutImage
$env:SUT_PORT  = "$Port"
$env:SUT_CPUS  = "$Cpus"
$composeFiles = @('-f', $compose)
if($StaticOnly){ $composeFiles += @('-f', (Join-Path $root 'docker-compose.staticonly.yml')) }
if($NoCompression){ $composeFiles += @('-f', (Join-Path $root 'docker-compose.rawserve.yml')) }
Info "SUT image: $SutImage   port: $Port   cpus: $Cpus   staticOnly: $StaticOnly   compression: $(if($NoCompression){'OFF'}else{'on'})   label: $Label"
Info "compose up (azurite + sut) ..."
docker compose $composeFiles up -d | Out-Host
if ($LASTEXITCODE -ne 0){ throw "compose up failed" }

try {
  # ── Wait for readiness ────────────────────────────────────────────────────
  # Probe a real static asset rather than the health endpoint: during PS module import the
  # CPU is saturated and the health request can exceed any short timeout, but the gate state
  # is exactly what we care about. robots.txt -> text/plain once static serving is live;
  # text/html means the startup loading page is still being returned.
  Info "waiting for static serving to go live (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $phase=''; $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    $ct = & curl.exe -s -o NUL -w "%{content_type}" --max-time 10 "$base/robots.txt" 2>$null
    if($ct -match 'text/plain'){ $ready=$true; break }
    Start-Sleep -Seconds 3
  }
  try{ $phase = (Invoke-RestMethod "$base/api/setup/health" -TimeoutSec 10).phase } catch {}
  if($ready){ Info "static serving live (phase=$phase)" } else { Warn "static not live after ${ReadyTimeoutSec}s - measuring anyway" }

  # ── Discover bundle / targets ─────────────────────────────────────────────
  Info "discovering bundle ..."
  $discoverSh = (Get-Content -Raw (Join-Path $here 'discover.sh')) -replace "`r",""
  $discRaw = $discoverSh | docker exec -i $container sh
  $disc = @{}
  foreach($line in ($discRaw -split "`n")){ if($line -match '^([A-Z_]+)=(.*)$'){ $disc[$Matches[1]]=$Matches[2].Trim() } }
  $appjs = $disc['APPJS']; $css = $disc['CSS']; $img = $disc['IMG']
  Info "  app chunk : $appjs ($([math]::Round([double]($disc['APPJS_SIZE'])/1MB,1)) MB)  precompressed=$($disc['APPJS_BR'])"
  Info "  css       : $css"
  Info "  .br files : $($disc['BR_COUNT'])   .gz files: $($disc['GZ_COUNT'])"

  # k6 target list (only existing ones)
  $targets = New-Object System.Collections.ArrayList
  if($appjs -and $appjs -ne '/'){ [void]$targets.Add(@{url=$appjs;kind='immutable_js'}) }
  if($css   -and $css   -ne '/'){ [void]$targets.Add(@{url=$css;kind='css'}) }
  [void]$targets.Add(@{url='/';kind='html'})
  [void]$targets.Add(@{url='/permissionsList.json';kind='data_json'})
  if($img   -and $img   -ne '/'){ [void]$targets.Add(@{url=$img;kind='image'}) }
  if($disc['CHUNK2']    -and $disc['CHUNK2']    -ne '/'){ [void]$targets.Add(@{url=$disc['CHUNK2'];kind='immutable_js'}) }
  if($disc['ROUTEHTML'] -and $disc['ROUTEHTML'] -ne '/'){ [void]$targets.Add(@{url=$disc['ROUTEHTML'];kind='html'}) }
  ($targets | ConvertTo-Json -Depth 5 -AsArray) | Set-Content (Join-Path $resultsDir 'targets.json') -Encoding utf8

  # ── Header / correctness audit (curl) ─────────────────────────────────────
  Info "auditing response headers ..."
  $auditSet = @(
    @{url='/';                     kind='html';         desire='no-cache, storable'}
    @{url='/index.html';           kind='html';         desire='no-cache, storable'}
    @{url='/sw.js';                kind='sw';           desire='no-cache (NOT immutable)'}
    @{url='/version.json';         kind='version';      desire='no-cache (NOT long max-age)'}
    @{url='/manifest.json';        kind='manifest';     desire='no-cache'}
    @{url='/robots.txt';           kind='text';         desire='any'}
    @{url='/permissionsList.json'; kind='data_json';    desire='ETag + revalidatable, NOT immutable'}
    @{url='/intuneCollection.json';kind='data_json';    desire='ETag + revalidatable, NOT immutable (public/)'}
    @{url='/logo.png';             kind='image';        desire='public + long max-age'}
  )
  if($appjs -and $appjs -ne '/'){ $auditSet += @{url=$appjs; kind='immutable_js'; desire='public, immutable, max-age 1y'} }
  if($css   -and $css   -ne '/'){ $auditSet += @{url=$css;   kind='css';          desire='public, immutable'} }

  $audit = foreach($a in $auditSet){
    $u = $a.url
    $hdrRaw = & curl.exe -s -o NUL -D - -H "Accept-Encoding: br, gzip" -w "`nXSIZE=%{size_download}`nXTYPE=%{content_type}`nXCODE=%{http_code}" "$base$u" 2>$null
    $H=@{}; $size=0; $ctype=''; $code=0
    foreach($l in ($hdrRaw -split "`n")){
      $l=$l.TrimEnd("`r")
      if($l -match '^XSIZE=(.*)$'){ $size=[long]$Matches[1] }
      elseif($l -match '^XTYPE=(.*)$'){ $ctype=$Matches[1] }
      elseif($l -match '^XCODE=(.*)$'){ $code=[int]$Matches[1] }
      elseif($l -match '^([A-Za-z0-9-]+):\s*(.*)$'){ $H[$Matches[1].ToLower()]=$Matches[2].Trim() }
    }
    $cc=$H['cache-control']; $enc=$H['content-encoding']; $vary=$H['vary']; $etag=$H['etag']; $sc=$H['set-cookie']
    # raw (identity) size for compressible kinds -> compression ratio
    $rawSize=0
    if($a.kind -in 'immutable_js','css','html','data_json'){
      $rawSize=[long](& curl.exe -s -o NUL -H "Accept-Encoding: identity" -w "%{size_download}" "$base$u" 2>$null)
    }
    $ratio = if($rawSize -gt 0 -and $size -gt 0){ [math]::Round($rawSize/$size,2) } else { $null }

    # desired-state pass/fail (per docs/static-serving-optimization.md)
    $ccl = ("$cc").ToLower()
    $pass = switch($a.kind){
      'immutable_js' { $ccl -match 'immutable' }
      'css'          { $ccl -match 'immutable' }
      'html'         { ($ccl -match 'no-cache') -and ($ccl -notmatch 'no-store') }
      'sw'           { ($ccl -match 'no-cache') -and ($ccl -notmatch 'immutable') }
      'version'      { ($ccl -match 'no-cache') -and ($ccl -notmatch 'immutable') }
      'manifest'     { $ccl -match 'no-cache' }
      'data_json'    { ($etag) -and ($ccl -notmatch 'immutable') }
      'image'        { ($ccl -match 'public') -and ($ccl -match 'max-age') }
      default        { $true }
    }
    if($sc){ $pass=$false }  # any Set-Cookie on a static asset is a hard fail

    [pscustomobject]@{
      url=$u; kind=$a.kind; code=$code; contentType=$ctype
      cacheControl=$cc; contentEncoding=$enc; vary=$vary
      hasETag=[bool]$etag; setCookie=[bool]$sc
      wireBytes=$size; rawBytes=$rawSize; compressionRatio=$ratio
      desired=$a.desire; pass=$pass
    }
  }
  $passCount = ($audit | Where-Object pass).Count
  Info "header audit: $passCount/$($audit.Count) match desired end-state"

  # ── Resource sampler (background) ─────────────────────────────────────────
  $statsFile = Join-Path $resultsDir "$Label-$stamp.stats.jsonl"
  $flagFile  = Join-Path $resultsDir ".sampling-$stamp"
  'go' | Set-Content $flagFile
  $sampler = Start-Job -ScriptBlock {
    param($c,$out,$flag)
    while(Test-Path $flag){
      $j = docker stats --no-stream --format '{{json .}}' $c 2>$null
      if($j){ Add-Content -Path $out -Value $j }
    }
  } -ArgumentList $container,$statsFile,$flagFile

  # ── k6 load ───────────────────────────────────────────────────────────────
  Info "running k6 (vus=$Vus duration=$Duration) ..."
  $summaryFile = Join-Path $resultsDir "$Label-$stamp.k6.json"
  $k6DirD = ($k6Dir -replace '\\','/'); $resultsDirD = ($resultsDir -replace '\\','/')
  & docker run --rm --network $network `
      -e BASE="http://sut:8080" -e VUS="$Vus" -e RATE="$Rate" -e DURATION="$Duration" `
      -v "${k6DirD}:/scripts:ro" -v "${resultsDirD}:/out" `
      grafana/k6 run /scripts/static_load.js --summary-export "/out/$(Split-Path $summaryFile -Leaf)" 2>&1 | Out-Host

  # stop sampler
  Remove-Item $flagFile -ErrorAction SilentlyContinue
  Wait-Job $sampler -Timeout 10 | Out-Null
  Remove-Job $sampler -Force -ErrorAction SilentlyContinue

  # ── Parse resource samples ────────────────────────────────────────────────
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

  # ── Parse k6 summary ──────────────────────────────────────────────────────
  $k6=$null; if(Test-Path $summaryFile){ $k6 = Get-Content -Raw $summaryFile | ConvertFrom-Json }
  # NOTE: do NOT name this MV/MB/etc. — PowerShell aliases (mv->Move-Item) outrank functions.
  # --summary-export stores values flat under the metric; handleSummary nests them under .values. Handle both.
  function MetricVal($name,$field){ try{ $m=$k6.metrics.$name; if($null -ne $m.values){ $m.values.$field } else { $m.$field } } catch { $null } }
  $load = [ordered]@{
    httpReqs       = MetricVal 'http_reqs' 'count'
    reqPerSec      = [math]::Round(([double](MetricVal 'http_reqs' 'rate')),1)
    failRate       = MetricVal 'http_req_failed' 'value'
    ttfbAvgMs      = [math]::Round(([double](MetricVal 'http_req_waiting' 'avg')),1)
    ttfbP95Ms      = [math]::Round(([double](MetricVal 'http_req_waiting' 'p(95)')),1)
    durP95Ms       = [math]::Round(([double](MetricVal 'http_req_duration' 'p(95)')),1)
    dataRecvMB     = [math]::Round(([double](MetricVal 'data_received' 'count'))/1MB,1)
    dataRateMBs    = [math]::Round(([double](MetricVal 'data_received' 'rate'))/1MB,2)
    ttfb_immutable_js_ms = [math]::Round(([double](MetricVal 'ttfb_immutable_js' 'avg')),1)
    ttfb_html_ms         = [math]::Round(([double](MetricVal 'ttfb_html' 'avg')),1)
    ttfb_data_json_ms    = [math]::Round(([double](MetricVal 'ttfb_data_json' 'avg')),1)
    servedBrotli   = MetricVal 'served_brotli' 'count'
    servedGzip     = MetricVal 'served_gzip' 'count'
    servedIdentity = MetricVal 'served_identity' 'count'
    responsesChunked = MetricVal 'responses_chunked' 'count'
  }

  # ── Disk / image ──────────────────────────────────────────────────────────
  $imageBytes=[long](& docker image inspect $SutImage --format '{{.Size}}' 2>$null)

  # ── Assemble + write ──────────────────────────────────────────────────────
  $result = [ordered]@{
    label=$Label; timestamp=$stamp; sutImage=$SutImage; ready=$ready; readyPhase=$phase
    config=@{ vus=$Vus; rate=$Rate; duration=$Duration; port=$Port; cpus=$Cpus; staticOnly=[bool]$StaticOnly; compression=(-not $NoCompression) }
    disk=[ordered]@{
      imageBytes=$imageBytes; imageMB=[math]::Round($imageBytes/1MB,1)
      frontendBytes=[long]($disc['FRONTEND_BYTES']); frontendMB=[math]::Round([double]($disc['FRONTEND_BYTES'])/1MB,1)
      precompressionApplied=([int]$disc['BR_COUNT'] -gt 0); brFiles=[int]$disc['BR_COUNT']; gzFiles=[int]$disc['GZ_COUNT']
    }
    resources=[ordered]@{ cpuAvgPct=$cpuAvg; cpuMaxPct=$cpuMax; memAvgMB=$memAvg; memMaxMB=$memMax; samples=$cpu.Count }
    load=$load
    headerAudit=@($audit)
    headerAuditPass="$passCount/$($audit.Count)"
  }
  $jsonOut = Join-Path $resultsDir "$Label-$stamp.json"
  ($result | ConvertTo-Json -Depth 8) | Set-Content $jsonOut -Encoding utf8

  # ── Human report (markdown) ───────────────────────────────────────────────
  $mdOut = Join-Path $resultsDir "$Label-$stamp.md"
  $sb = [System.Text.StringBuilder]::new()
  [void]$sb.AppendLine("# Perf result - $Label ($stamp)")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("- **SUT image:** ``$SutImage``")
  [void]$sb.AppendLine("- **Ready:** $ready (phase=$phase)   **Load:** $Vus VUs / $Duration")
  [void]$sb.AppendLine("- **Image size:** $($result.disk.imageMB) MB   **Frontend on disk:** $($result.disk.frontendMB) MB   **Pre-compressed:** $($result.disk.precompressionApplied) (br=$($result.disk.brFiles), gz=$($result.disk.gzFiles))")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Server resources during static load")
  [void]$sb.AppendLine("| metric | avg | max |")
  [void]$sb.AppendLine("|---|---:|---:|")
  [void]$sb.AppendLine("| CPU % (of 1 core) | $cpuAvg | $cpuMax |")
  [void]$sb.AppendLine("| RAM (MB) | $memAvg | $memMax |")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Content serving (k6)")
  [void]$sb.AppendLine("| metric | value |")
  [void]$sb.AppendLine("|---|---:|")
  foreach($k in $load.Keys){ [void]$sb.AppendLine("| $k | $($load[$k]) |") }
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Header / correctness audit ($passCount/$($audit.Count) match desired end-state)")
  [void]$sb.AppendLine("| url | kind | code | cache-control | enc | ratio | etag | set-cookie | desired | pass |")
  [void]$sb.AppendLine("|---|---|---:|---|---|---:|:--:|:--:|---|:--:|")
  foreach($a in $audit){
    $p = if($a.pass){'✅'}else{'❌'}
    $et= if($a.hasETag){'y'}else{'-'}
    $scc=if($a.setCookie){'YES'}else{'-'}
    $rr= if($a.compressionRatio){"$($a.compressionRatio)x"}else{'-'}
    [void]$sb.AppendLine("| $($a.url) | $($a.kind) | $($a.code) | $($a.cacheControl) | $($a.contentEncoding) | $rr | $et | $scc | $($a.desired) | $p |")
  }
  $sb.ToString() | Set-Content $mdOut -Encoding utf8

  Info "wrote: $jsonOut"
  Info "wrote: $mdOut"
  Write-Host ""
  Get-Content $mdOut | Write-Host
}
finally {
  if($KeepUp){ Warn "leaving containers up (-KeepUp). Tear down: docker compose $composeFiles down -v" }
  else { Info "tearing down ..."; docker compose $composeFiles down -v 2>&1 | Out-Null }
}
