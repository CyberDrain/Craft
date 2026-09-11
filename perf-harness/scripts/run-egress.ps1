<#
.SYNOPSIS
  End-to-end test of the per-instance API egress cap against a real CRAFT image running real PS endpoints.

.DESCRIPTION
  Brings up CRAFT in http-only mode (the api-harness PerfApi endpoints) with egress accounting forced on
  (docker-compose.egress.yml), then drives real app-only API-client traffic through the full pipeline —
  CraftAuthMiddleware normalises an inbound app-only principal to idp=aad + AppId, so ApiEgressLimiterMiddleware
  counts it exactly as it would a hosted client-credentials caller. After the background flush interval it
  docker-cp's the ledger file (egress-ledger.json, which sits in the log directory) back out and asserts:

    * both properties are present            (dateUtc, bytes)
    * the day stamp is today (UTC)           (dateUtc == yyyy-MM-dd)
    * the byte counter is non-zero           (bytes > 0)

  and cross-checks the server-side total against the bytes curl actually received. With -Cap it also sets a
  daily budget and asserts that traffic past the budget is shed with 429 + Retry-After.

  This exercises the parts the unit tests can't: the real file location + serialization, real byte counting
  through the actual response stream, and the real auth->classify->count path. Tears down on exit.

.EXAMPLE
  pwsh scripts\run-egress.ps1                      # accounting-only: flush + non-zero properties
  pwsh scripts\run-egress.ps1 -Cap 200000          # also assert enforcement (429 past 200 KB/day)
  pwsh scripts\run-egress.ps1 -Requests 100 -JsonN 2000 -KeepUp
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:local',
  [int]$Port        = 5297,
  [int]$Pool        = 2,
  [double]$Cpus     = 2,
  [int]$Requests    = 50,
  [int]$JsonN       = 1000,
  [long]$Cap        = 0,          # bytes/day; 0 = accounting only. >0 also tests 429 enforcement.
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
$resultsDir = Join-Path $root 'results'
$container  = 'craft-perf-api-sut'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base  = "http://127.0.0.1:$Port"

function Info($m){ Write-Host "[egress-e2e] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[egress-e2e] $m" -ForegroundColor Yellow }
function Ok($m){   Write-Host "[egress-e2e] PASS  $m" -ForegroundColor Green }
function Bad($m){  Write-Host "[egress-e2e] FAIL  $m" -ForegroundColor Red }

# An app-only (client-credentials) principal, exactly the shape CraftAuthMiddleware transforms into
# idp=aad + AppId. No EasyAuth/token needed — the harness has no upstream to strip it, so the middleware
# honours it and CallerClassifier sees an API client.
$appId = '11111111-2222-3333-4444-555555555555'
$principalJson = '{"claims":[{"typ":"appid","val":"' + $appId + '"}]}'
$principal = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($principalJson))

if($Build){
  Info "building CRAFT image $SutImage ..."
  docker build -f (Join-Path $repoRoot 'build/Dockerfile') -t $SutImage $repoRoot | Out-Host
  if($LASTEXITCODE -ne 0){ throw "docker build failed" }
}

$env:SUT_IMAGE = $SutImage
$env:SUT_PORT  = "$Port"
$env:SUT_CPUS  = "$Cpus"
$env:POOL      = "$Pool"
$env:EGRESS_CAP   = "$Cap"
$env:EGRESS_FLUSH = "$FlushSec"

Info "image: $SutImage  port: $Port  pool: $Pool  requests: $Requests (PerfJson?n=$JsonN)  cap: $(if($Cap -gt 0){"$Cap bytes/day"}else{'accounting-only'})"
Info "compose up (http-only + egress accounting) ..."
docker compose -f $composeApi -f $composeEg up -d | Out-Host
if($LASTEXITCODE -ne 0){ throw "compose up failed" }

$failures = New-Object System.Collections.Generic.List[string]

try {
  # ── readiness ───────────────────────────────────────────────────────────────
  Info "waiting for http pool ready (timeout ${ReadyTimeoutSec}s) ..."
  # http-only harness has no storage, so the aggregate status stays 'starting'; the real signal that the
  # dispatch pipeline is accepting requests is ready.http.
  $ready=$false; $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec)
  while((Get-Date) -lt $deadline){
    try { if((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).ready.http){ $ready=$true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if($ready){ Info "HTTP pool ready" } else { throw "SUT HTTP pool not ready after ${ReadyTimeoutSec}s" }

  # ── drive real API-client traffic ─────────────────────────────────────────────
  Info "sending $Requests API-client requests ..."
  $ok200=0; $got429=0; $otherCodes=@{}; $clientBytes=0L; $retryAfterSeen=$null
  for($i=0; $i -lt $Requests; $i++){
    # http_code + size_download (body bytes received), plus retry-after when present.
    $out = & curl.exe -s -o NUL --max-time 30 -w "%{http_code} %{size_download} %{header_json}" `
             -H "x-ms-client-principal: $principal" "$base/API/PerfJson?n=$JsonN" 2>$null
    $parts = ("$out").Trim() -split '\s+', 3
    $code  = $parts[0]; $size = if($parts.Count -ge 2){ [long]$parts[1] } else { 0L }
    switch($code){
      '200' { $ok200++; $clientBytes += $size }
      '429' {
        $got429++
        if(-not $retryAfterSeen -and $parts.Count -ge 3){
          try { $retryAfterSeen = ([regex]::Match($parts[2],'(?i)"retry-after"\s*:\s*\[\s*"?(\d+)').Groups[1].Value) } catch {}
        }
      }
      default { $otherCodes[$code] = 1 + ($otherCodes[$code] ?? 0) }
    }
  }
  Info "responses: 200=$ok200  429=$got429  other=$(if($otherCodes.Count){($otherCodes.GetEnumerator()|ForEach-Object{"$($_.Key):$($_.Value)"}) -join ','}else{'none'})"
  Info "client received $clientBytes bytes across the 200s"
  if($otherCodes.Count){ $failures.Add("unexpected status codes: $(($otherCodes.Keys) -join ',')") }
  if($ok200 -eq 0){ $failures.Add("no successful (200) API responses — nothing to account") }

  # ── wait for a background flush, then pull the ledger back out ─────────────────
  $wait = $FlushSec + 3
  Info "waiting ${wait}s for a background flush to disk ..."
  Start-Sleep -Seconds $wait

  $ledgerOut = Join-Path $resultsDir "egress-ledger-$stamp.json"
  Info "docker cp ${container}:/app/_logs/egress-ledger.json ..."
  docker cp "${container}:/app/_logs/egress-ledger.json" $ledgerOut 2>&1 | Out-Host
  if(-not (Test-Path $ledgerOut)){
    throw "ledger file not found in the container — accounting never flushed (check the log dir is writable)"
  }

  $raw = Get-Content -Raw $ledgerOut
  Info "ledger file contents: $raw"
  $ledger = $raw | ConvertFrom-Json
  $props  = $ledger.PSObject.Properties.Name

  # ── assertions: all properties present, dated today, non-zero ─────────────────
  foreach($p in @('dateUtc','bytes')){
    if($props -contains $p){ Ok "property '$p' present" } else { $failures.Add("missing property '$p'") }
  }

  $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
  if($ledger.dateUtc -eq $today){ Ok "dateUtc is today ($today)" }
  else { $failures.Add("dateUtc '$($ledger.dateUtc)' is not today ($today)") }

  if([long]$ledger.bytes -gt 0){ Ok "bytes is non-zero ($($ledger.bytes))" }
  else { $failures.Add("bytes is not > 0 (got '$($ledger.bytes)')") }

  # Cross-check: the server's count should match what the client actually received (identity encoding,
  # body only). Informational — headers aren't counted and timing of the last in-flight write can differ.
  if($ok200 -gt 0 -and $clientBytes -gt 0){
    $ratio = [math]::Round(([double]$ledger.bytes / $clientBytes), 3)
    Info "server/client byte ratio: $ratio (ledger $($ledger.bytes) vs client $clientBytes)"
    if($ratio -lt 0.5 -or $ratio -gt 1.5){ Warn "server count diverges from client-received bytes (>50%) — investigate" }
  }

  # ── enforcement (only when a cap was set) ─────────────────────────────────────
  if($Cap -gt 0){
    if($got429 -gt 0){
      Ok "enforcement fired: $got429 request(s) shed with 429 once over the ${Cap}-byte budget"
      if($retryAfterSeen){ Ok "429 carried Retry-After: $retryAfterSeen s" }
      else { Warn "could not read Retry-After from curl (older curl without header_json?) — unit tests assert it directly" }
    } else {
      $failures.Add("cap was $Cap bytes but no request was shed with 429 (raise -Requests/-JsonN so total egress exceeds the cap)")
    }
  }
}
finally {
  if($KeepUp){ Warn "leaving containers up (-KeepUp). Tear down: docker compose -f `"$composeApi`" -f `"$composeEg`" down -v" }
  else { Info "tearing down ..."; docker compose -f $composeApi -f $composeEg down -v 2>&1 | Out-Null }
}

Write-Host ""
if($failures.Count -eq 0){
  Ok "egress e2e: all checks passed"
  exit 0
} else {
  foreach($f in $failures){ Bad $f }
  Bad "egress e2e: $($failures.Count) check(s) failed"
  exit 1
}
