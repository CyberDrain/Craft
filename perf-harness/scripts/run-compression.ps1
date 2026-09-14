<#
.SYNOPSIS
  Prove /api response compression is correct and correctly billed, then measure its CPU cost.

.DESCRIPTION
  Brings up CRAFT in http-only mode (the PerfApi endpoints) with egress accounting forced on
  (docker-compose.egress.yml), then runs two things against the same stack:

    Phase 1 - correctness + accounting proof (curl, exact bytes, two isolated cases).
      Drives app-only API-client traffic (an x-ms-client-principal blob with an appid claim, which
      CraftAuthMiddleware normalises to idp=aad + AppId, so the egress ledger charges it) and reads the
      ledger file back out around each case to get the per-case byte delta. Measures the on-the-wire
      size with curl's %{size_download} (no --compressed, so a gzip body is NOT decompressed and the
      number is the compressed body) and the Content-Encoding via %{header_json}.

        1a. WITHOUT Accept-Encoding  → asserts the response is NOT compressed (no Content-Encoding), and
            the ledger delta == the bytes curl received.
        1b. WITH Accept-Encoding: gzip → asserts the response IS compressed (Content-Encoding: gzip) and
            materially smaller, and the ledger delta == the bytes curl received.

      Together: the server compresses only when asked, and the accounting file bills exactly the bytes
      that went over the wire in both cases (the cap charges the compressed size, not the raw body).

    Phase 2 - CPU / ratio under load, per encoding (k6 + docker stats).
      Runs the same PerfJson payload under k6 at a fixed arrival rate three times - identity, gzip, br -
      sampling docker stats CPU during each, so the CPU hit of each encoding is directly comparable at
      equal load. This is the number to weigh against the bytes saved on a small (1-2 vCPU) container.

  The CRAFT image must include this code. Build it first (or pass -Build):
    docker build -f ..\build\Dockerfile -t craft:local ..

  Tears down on exit. Exits non-zero if any Phase 1 assertion fails (CI-friendly).

.EXAMPLE
  pwsh scripts\run-compression.ps1                          # build once first, or add -Build
  pwsh scripts\run-compression.ps1 -JsonN 4000 -Rate 200 -Duration 30s
  pwsh scripts\run-compression.ps1 -Requests 60 -KeepUp
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [int]$Port        = 5297,
  [int]$Pool        = 2,
  [double]$Cpus     = 2,
  [int]$Requests    = 30,        # requests per counting-proof case (identity, then gzip)
  [int]$JsonN       = 2000,      # PerfJson array size — bigger = more compressible, clearer ratio
  [int]$Rate        = 10,        # k6 fixed arrival rate (req/s). Kept low on purpose: Craft rate-limits
                                 # and the small HTTP pool sheds/resets under a burst (EOF storms at 150),
                                 # which pollutes the CPU sample. 10 rps keeps every request served so the
                                 # per-encoding CPU delta is clean. Raise it only against a bigger pool.
  [string]$Duration = '20s',
  [int]$FlushSec    = 2,
  [int]$ReadyTimeoutSec = 120,
  [switch]$Build,
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$root       = Split-Path -Parent $here          # perf-harness/
$repoRoot   = Split-Path -Parent $root          # CRAFT repo root
$composeApi = Join-Path $root 'docker-compose.api.yml'
$composeEg  = Join-Path $root 'docker-compose.egress.yml'
$k6Dir      = Join-Path $root 'k6'
$resultsDir = Join-Path $root 'results'
$container  = 'craft-perf-api-sut'
$network    = 'craft-perf-apinet'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"

function Info($m){ Write-Host "[compression] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[compression] $m" -ForegroundColor Yellow }
function Ok($m){   Write-Host "[compression] PASS  $m" -ForegroundColor Green }
function Bad($m){  Write-Host "[compression] FAIL  $m" -ForegroundColor Red }

# App-only principal (idp=aad + AppId after CraftAuthMiddleware). Charged by the egress ledger.
$appId = '11111111-2222-3333-4444-555555555555'
$principal = [Convert]::ToBase64String(
  [Text.Encoding]::UTF8.GetBytes('{"claims":[{"typ":"appid","val":"' + $appId + '"}]}'))

if($Build){
  Info "building CRAFT image $SutImage ..."
  docker build -f (Join-Path $repoRoot 'build/Dockerfile') -t $SutImage $repoRoot | Out-Host
  if($LASTEXITCODE -ne 0){ throw "docker build failed" }
}

$env:SUT_IMAGE = $SutImage
$env:SUT_PORT  = "$Port"
$env:SUT_CPUS  = "$Cpus"
$env:POOL      = "$Pool"
$env:EGRESS_CAP   = '0'          # accounting-only — never shed, we just want the counter
$env:EGRESS_FLUSH = "$FlushSec"

Info "image: $SutImage  port: $Port  pool: $Pool  PerfJson n=$JsonN  k6 rate=$Rate/$Duration"
Info "compose up (http-only + egress accounting) ..."
docker compose -f $composeApi -f $composeEg up -d | Out-Host
if($LASTEXITCODE -ne 0){ throw "compose up failed" }

$failures = New-Object System.Collections.Generic.List[string]

# One curl to /API/PerfJson. $Enc '' = identity (no Accept-Encoding), else negotiate it. Returns the
# on-the-wire body size (%{size_download}; no --compressed, so a gzip/br body is NOT decompressed) and
# the response Content-Encoding (from %{header_json}). $WithPrincipal true = app-only caller (charged).
function Invoke-PerfJson([string]$Enc, [bool]$WithPrincipal){
  $curlArgs = @('-s','-o','NUL','--max-time','30','-w','%{http_code}|%{size_download}|%{header_json}')
  if($Enc){ $curlArgs += @('-H', "Accept-Encoding: $Enc") }
  if($WithPrincipal){ $curlArgs += @('-H', "x-ms-client-principal: $principal") }
  $curlArgs += "$base/API/PerfJson?n=$JsonN"
  $out = & curl.exe @curlArgs 2>$null
  $p = ("$out").Trim() -split '\|', 3
  $ce = ''
  if($p.Count -ge 3){
    $m = [regex]::Match($p[2], '(?i)"content-encoding"\s*:\s*\[\s*"?([^"\]]*)')
    if($m.Success){ $ce = $m.Groups[1].Value.Trim() }
  }
  [pscustomobject]@{
    Code     = $p[0]
    Bytes    = if($p.Count -ge 2 -and $p[1]){ [long]$p[1] } else { 0L }
    Encoding = $ce
  }
}

# The ledger's flushed byte total (0 if not written yet). docker-cp'd fresh each call.
function Read-LedgerBytes {
  $tmp = Join-Path $resultsDir "ledger-probe-$stamp.json"
  docker cp "${container}:/app/_logs/egress-ledger.json" $tmp 2>$null | Out-Null
  if(-not (Test-Path $tmp)){ return 0L }
  try { return [long]((Get-Content -Raw $tmp | ConvertFrom-Json).bytes) } catch { return 0L }
}

# Send N charged requests at one encoding, summing the exact wire bytes curl received.
# NB: the observed-encoding accumulator must NOT be named $enc — PowerShell variable names are
# case-insensitive, so $enc and the $Enc parameter are the same variable, and assigning it would
# clobber the requested encoding (every request then went out as "Accept-Encoding: <that value>").
function Measure-Case([string]$Enc){
  $sum=0L; $count=0; $obsEnc='?'; $last=0L
  for($i=0; $i -lt $Requests; $i++){
    $r = Invoke-PerfJson -Enc $Enc -WithPrincipal $true
    if($r.Code -eq '200'){ $sum += $r.Bytes; $count++; $obsEnc=$r.Encoding; $last=$r.Bytes }
  }
  [pscustomobject]@{ Sum=$sum; Count=$count; Encoding=$obsEnc; PerResp=$last }
}

try {
  # ── readiness ───────────────────────────────────────────────────────────────
  Info "waiting for http pool ready (timeout ${ReadyTimeoutSec}s) ..."
  $ready=$false; $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    try { if((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).ready.http){ $ready=$true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if($ready){ Info "HTTP pool ready" } else { throw "SUT HTTP pool not ready after ${ReadyTimeoutSec}s" }

  # Print the running image's own compression config so a stale image (built before /api compression, or
  # with it off) is obvious rather than looking like a compression bug. If this says '/api: off' or the
  # line is absent, the image predates the feature — rebuild with -Build.
  $cfgLine = docker logs $container 2>&1 | Select-String 'System\] Compression' | Select-Object -Last 1
  if($cfgLine){ Info "image compression config: $($cfgLine.ToString().Trim())" }
  else { Warn "no '[System] Compression' line in the image log — this image predates the /api compression feature; rebuild with -Build" }

  # Warm anonymously (not charged) so JIT/first-invoke cost doesn't skew the ledger or the first sample.
  1..2 | ForEach-Object { [void](Invoke-PerfJson -Enc '' -WithPrincipal $false) }

  # ══ Phase 1: correctness + accounting, two isolated cases with a ledger delta each ════════════
  $b0 = Read-LedgerBytes

  # 1a — WITHOUT Accept-Encoding → must be identity, ledger delta == received bytes.
  Info "Phase 1a - $Requests requests WITHOUT Accept-Encoding (expect identity) ..."
  $idc = Measure-Case ''
  Start-Sleep -Seconds ($FlushSec + 3)
  $b1 = Read-LedgerBytes
  $deltaId = $b1 - $b0
  if($idc.Count -eq 0){ $failures.Add("no successful identity responses") }
  if([string]::IsNullOrEmpty($idc.Encoding)){
    Ok "no Accept-Encoding → response NOT compressed (no Content-Encoding)"
  } else {
    $failures.Add("no Accept-Encoding but response came back Content-Encoding '$($idc.Encoding)' — compressed without being asked")
  }
  if($idc.Count -gt 0 -and [math]::Abs($deltaId - $idc.Sum) -le [math]::Max(64, $idc.Sum * 0.01)){
    Ok "identity: accounting delta == bytes received ($deltaId ≈ $($idc.Sum))"
  } else {
    $failures.Add("identity: accounting delta $deltaId != bytes received $($idc.Sum)")
  }

  # 1b — WITH Accept-Encoding: gzip → must be compressed + smaller, ledger delta == received bytes.
  Info "Phase 1b - $Requests requests WITH Accept-Encoding: gzip (expect gzip) ..."
  $gzc = Measure-Case 'gzip'
  Start-Sleep -Seconds ($FlushSec + 3)
  $b2 = Read-LedgerBytes
  $deltaGz = $b2 - $b1
  if($gzc.Count -eq 0){ $failures.Add("no successful gzip responses") }
  if($gzc.Encoding -match 'gzip'){
    Ok "Accept-Encoding: gzip → response compressed (Content-Encoding: $($gzc.Encoding))"
  } else {
    $failures.Add("Accept-Encoding: gzip but response came back Content-Encoding '$($gzc.Encoding)' — expected gzip")
  }
  $ratio = if($gzc.PerResp -gt 0){ [math]::Round($idc.PerResp / $gzc.PerResp, 2) } else { 0 }
  if($gzc.PerResp -gt 0 -and $gzc.PerResp -lt ($idc.PerResp * 0.9)){
    Ok "gzip response materially smaller than identity ($($gzc.PerResp) < $($idc.PerResp), ${ratio}x)"
  } else {
    $failures.Add("gzip response not materially smaller than identity ($($gzc.PerResp) vs $($idc.PerResp)) — is /api compression on in the image?")
  }
  if($gzc.Count -gt 0 -and [math]::Abs($deltaGz - $gzc.Sum) -le [math]::Max(64, $gzc.Sum * 0.01)){
    Ok "gzip: accounting delta == bytes received ($deltaGz ≈ $($gzc.Sum))  ← billing the COMPRESSED wire bytes"
  } else {
    $failures.Add("gzip: accounting delta $deltaGz != bytes received $($gzc.Sum) — counting may be on the wrong side of compression")
  }
  # If counting were on the wrong (pre-compression) side, this case would have added ≈ Requests*identity.
  Info "(pre-compression counting would have added ≈ $([long]($Requests * $idc.PerResp)) B for case 1b; it added $deltaGz)"

  # ══ Phase 2: CPU / ratio under load, per encoding (k6) ═══════════════════════
  $k6DirD = ($k6Dir -replace '\\','/'); $resultsDirD = ($resultsDir -replace '\\','/')
  function MetricVal($k6,$name,$field){ try{ $m=$k6.metrics.$name; if($null -ne $m.values){ $m.values.$field } else { $m.$field } } catch { $null } }

  $encRows = [ordered]@{}
  foreach($enc in @('identity','gzip','br')){
    $encEnv = if($enc -eq 'identity'){ '' } else { $enc }
    Info "Phase 2 - k6 @ rate=$Rate for '$enc' ..."

    $statsFile = Join-Path $resultsDir "compression-$enc-$stamp.stats.jsonl"
    $flagFile  = Join-Path $resultsDir ".sampling-$enc-$stamp"
    'go' | Set-Content $flagFile
    $sampler = Start-Job -ScriptBlock {
      param($c,$out,$flag)
      while(Test-Path $flag){ $j = docker stats --no-stream --format '{{json .}}' $c 2>$null; if($j){ Add-Content $out $j } }
    } -ArgumentList $container,$statsFile,$flagFile

    $summary = Join-Path $resultsDir "compression-$enc-$stamp.k6.json"
    & docker run --rm --network $network `
        -e BASE="http://sut:8080" -e RATE="$Rate" -e DURATION="$Duration" `
        -e ONLY='PerfJson' -e JSON_N="$JsonN" -e ENC="$encEnv" `
        -v "${k6DirD}:/scripts:ro" -v "${resultsDirD}:/out" `
        grafana/k6 run /scripts/api_load.js --summary-export "/out/$(Split-Path $summary -Leaf)" 2>&1 | Out-Host

    Remove-Item $flagFile -ErrorAction SilentlyContinue
    Wait-Job $sampler -Timeout 10 | Out-Null
    Remove-Job $sampler -Force -ErrorAction SilentlyContinue

    $cpu=@()
    if(Test-Path $statsFile){
      foreach($line in (Get-Content $statsFile)){
        try{ $s=$line|ConvertFrom-Json } catch { continue }
        if($s.CPUPerc){ $cpu += [double]($s.CPUPerc -replace '%','') }
      }
    }
    $k6 = if(Test-Path $summary){ Get-Content -Raw $summary | ConvertFrom-Json } else { $null }
    $reqs = [double](MetricVal $k6 'http_reqs' 'count')
    $recv = [double](MetricVal $k6 'data_received' 'count')

    $encRows[$enc] = [ordered]@{
      cpuAvgPct  = if($cpu){ [math]::Round(($cpu|Measure-Object -Average).Average,1) } else { $null }
      cpuMaxPct  = if($cpu){ [math]::Round(($cpu|Measure-Object -Maximum).Maximum,1) } else { $null }
      reqPerSec  = [math]::Round(([double](MetricVal $k6 'http_reqs' 'rate')),1)
      p95Ms      = [math]::Round(([double](MetricVal $k6 'http_req_duration' 'p(95)')),2)
      wireBytesPerResp = if($reqs -gt 0){ [long]($recv / $reqs) } else { 0 }
      totalRecvMB = [math]::Round($recv/1MB,2)
    }
  }

  # Ratio vs identity (per-response wire bytes)
  $baseWire = $encRows['identity'].wireBytesPerResp
  foreach($enc in @('gzip','br')){
    $w = $encRows[$enc].wireBytesPerResp
    $encRows[$enc].ratioVsIdentity = if($w -gt 0){ [math]::Round($baseWire / $w, 2) } else { $null }
  }

  # ── assemble + write ──────────────────────────────────────────────────────────
  $result = [ordered]@{
    label='compression'; timestamp=$stamp; sutImage=$SutImage; mode='http-only + egress accounting'
    config=@{ pool=$Pool; cpus=$Cpus; jsonN=$JsonN; rate=$Rate; duration=$Duration; requests=$Requests }
    countingProof=[ordered]@{
      withoutAcceptEncoding=[ordered]@{ contentEncoding=$idc.Encoding; perRespBytes=$idc.PerResp
                                        received=$idc.Sum; ledgerDelta=$deltaId }
      withGzip             =[ordered]@{ contentEncoding=$gzc.Encoding; perRespBytes=$gzc.PerResp
                                        received=$gzc.Sum; ledgerDelta=$deltaGz; ratio=$ratio }
    }
    encodings=$encRows
  }
  $jsonOut = Join-Path $resultsDir "compression-$stamp.json"
  ($result | ConvertTo-Json -Depth 8) | Set-Content $jsonOut -Encoding utf8

  # ── human report ───────────────────────────────────────────────────────────────
  $mdOut = Join-Path $resultsDir "compression-$stamp.md"
  $sb = [System.Text.StringBuilder]::new()
  [void]$sb.AppendLine("# /api compression - correctness, accounting, CPU ($stamp)")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("- **SUT:** ``$SutImage`` (http-only + egress accounting)   **CPUs:** $Cpus   **Pool:** $Pool")
  [void]$sb.AppendLine("- **Payload:** PerfJson n=$JsonN   **Load:** rate=$Rate for $Duration   **Proof requests/case:** $Requests")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Correctness + accounting (curl, exact bytes)")
  [void]$sb.AppendLine("| case | Content-Encoding | wire B/resp | received (Σ) | ledger delta |")
  [void]$sb.AppendLine("|---|---|---:|---:|---:|")
  [void]$sb.AppendLine("| no Accept-Encoding | $(if($idc.Encoding){$idc.Encoding}else{'(none)'}) | $($idc.PerResp) | $($idc.Sum) | $deltaId |")
  [void]$sb.AppendLine("| Accept-Encoding: gzip | $($gzc.Encoding) | $($gzc.PerResp) (${ratio}x) | $($gzc.Sum) | $deltaGz |")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("Ledger delta == bytes received in **both** cases → the cap bills the on-the-wire size, compressed or not.")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## CPU vs bandwidth at rate=$Rate (k6 + docker stats)")
  [void]$sb.AppendLine("| encoding | CPU% avg | CPU% max | wire B/resp | ratio | req/s | p95 ms |")
  [void]$sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|")
  foreach($enc in @('identity','gzip','br')){
    $r=$encRows[$enc]; $rt = if($r.ratioVsIdentity){ "$($r.ratioVsIdentity)x" } else { '1.00x' }
    [void]$sb.AppendLine("| $enc | $($r.cpuAvgPct) | $($r.cpuMaxPct) | $($r.wireBytesPerResp) | $rt | $($r.reqPerSec) | $($r.p95Ms) |")
  }
  $sb.ToString() | Set-Content $mdOut -Encoding utf8

  Info "wrote: $jsonOut"
  Info "wrote: $mdOut"
  Write-Host ""
  Get-Content $mdOut | Write-Host
}
finally {
  if($KeepUp){ Warn "leaving containers up (-KeepUp). Tear down: docker compose -f `"$composeApi`" -f `"$composeEg`" down -v" }
  else { Info "tearing down ..."; docker compose -f $composeApi -f $composeEg down -v 2>&1 | Out-Null }
}

Write-Host ""
if($failures.Count -eq 0){
  Ok "compression harness: all correctness + accounting checks passed"
  exit 0
} else {
  foreach($f in $failures){ Bad $f }
  Bad "compression harness: $($failures.Count) check(s) failed"
  exit 1
}
