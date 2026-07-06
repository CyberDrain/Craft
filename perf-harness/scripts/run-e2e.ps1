<#
.SYNOPSIS
  Azure-native end-to-end regression for CRAFT in the COMBINED role over Azurite. Gives a PASS/FAIL (+ a
  perf number) for each subsystem: health/readiness, storage, API dispatch, orchestrator fan-out,
  scheduler/timer, realtime SSE, and security headers. Exits non-zero if any check fails (CI gate).

.DESCRIPTION
  Brings up docker-compose.e2e-azure.yml (Azurite + the SUT with PerfApi + a 5s timer), waits for
  readiness, runs the checks, prints a summary table, and tears the stack down. Cross-platform
  (Windows dev + Linux CI).

.EXAMPLE
  pwsh scripts/run-e2e.ps1 -Build            # build craft:ci first, then run
  pwsh scripts/run-e2e.ps1 -SutImage craft:ci
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'craft:ci',
  [int]$Port = 5399,
  [int]$ReadyTimeoutSec = 180,
  [switch]$Build,
  [switch]$KeepUp
)

$ErrorActionPreference = 'Stop'
$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$root     = Split-Path -Parent $here             # perf-harness/
$repoRoot = Split-Path -Parent $root             # repo root
$compose  = Join-Path $root 'docker-compose.e2e-azure.yml'
$base     = "http://127.0.0.1:$Port"
$curl     = if ($IsWindows) { 'curl.exe' } else { 'curl' }
$results  = New-Object System.Collections.ArrayList

function Info($m) { Write-Host "[e2e] $m" -ForegroundColor Cyan }
function Add-Result($area, $name, $pass, $perf, $detail) {
  [void]$results.Add([pscustomobject]@{ area = $area; name = $name; pass = [bool]$pass; perf = $perf; detail = $detail })
  $tag = if ($pass) { 'PASS' } else { 'FAIL' }
  Write-Host ("  [{0}] {1,-12} {2,-18} {3,-8} {4}" -f $tag, $area, $name, $perf, $detail) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}
function Api($path) { try { return Invoke-RestMethod "$base$path" -TimeoutSec 20 } catch { return $null } }
# Low-level fetch via curl (control over Accept-Encoding + raw response headers). Returns status, request
# time, transfer size, the raw header block, and the body. Cross-platform.
function Fetch($url, $extra = @()) {
  $hf = New-TemporaryFile; $bf = New-TemporaryFile
  $m = & $curl @extra '-s' '-D' $hf.FullName '-o' $bf.FullName '-w' '%{http_code} %{time_total} %{size_download}' $url 2>$null
  $p = "$m".Trim() -split '\s+'
  $ms = 0.0; if ($p.Count -ge 2) { [void][double]::TryParse($p[1], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$ms) }
  $r = [pscustomobject]@{
    Code    = [int]($p[0])
    TimeMs  = [math]::Round($ms * 1000)
    Size    = if ($p.Count -ge 3) { [int]$p[2] } else { 0 }
    Headers = (Get-Content $hf.FullName -Raw -ErrorAction SilentlyContinue)
    Body    = (Get-Content $bf.FullName -Raw -ErrorAction SilentlyContinue)
  }
  Remove-Item $hf, $bf -ErrorAction SilentlyContinue
  return $r
}

if ($Build) {
  Info "building $SutImage ..."
  docker build -f (Join-Path $repoRoot 'build/Dockerfile') -t $SutImage $repoRoot | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
}

$env:SUT_IMAGE = $SutImage; $env:SUT_PORT = "$Port"
Info "compose up (azurite + combined sut) ..."
docker compose -f $compose up -d | Out-Host
if ($LASTEXITCODE -ne 0) { throw "compose up failed" }

try {
  # ── Health / readiness ──────────────────────────────────────────────────────
  Info "waiting for readiness (timeout ${ReadyTimeoutSec}s) ..."
  $ready = $false; $deadline = (Get-Date).AddSeconds($ReadyTimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $h = Api '/healthz'
    if ($h -and $h.status -eq 'ready') { $ready = $true; break }
    Start-Sleep -Seconds 2
  }
  $h = Api '/healthz'
  Add-Result 'health' 'readiness'    $ready                    '-' "status=$($h.status)"
  Add-Result 'storage' 'azurite-ready' ($h.ready.storage -eq $true) '-' "storageReady=$($h.ready.storage)"

  # ── API dispatch ────────────────────────────────────────────────────────────
  $sw = [Diagnostics.Stopwatch]::StartNew(); $ping = Api '/API/PerfPing'; $sw.Stop()
  Add-Result 'api' 'dispatch-ping' ($ping.ok -eq $true) ("{0}ms" -f $sw.ElapsedMilliseconds) "endpoint=$($ping.endpoint)"
  $echo = Api '/API/PerfEcho?hello=world'
  Add-Result 'api' 'dispatch-echo' ($echo.ok -eq $true -and $echo.query.hello -eq 'world') '-' "query round-tripped"

  # ── Orchestrator fan-out ────────────────────────────────────────────────────
  # Enqueue 100 small tasks; poll the allocation snapshot until the queue drains (active+queued == 0
  # sustained). taskms gives the work a visible window on Azurite's table-write latency.
  $N = 100
  $enq = Api "/API/PerfBgEnqueue?n=$N&taskms=25"
  $t0 = Get-Date; $sawWork = $false; $idle = 0; $wdl = (Get-Date).AddSeconds(120)
  while ((Get-Date) -lt $wdl) {
    $a = Api '/API/jobs/allocation'
    if (-not $a) { Start-Sleep -Milliseconds 250; continue }
    $work = [int]$a.jm.active + [int]$a.jm.queued
    if ($work -gt 0) { $sawWork = $true; $idle = 0 } elseif ($sawWork) { $idle++ }
    if ($idle -ge 6) { break }   # ~1.5s sustained idle => fan-out (and children) done
    Start-Sleep -Milliseconds 250
  }
  $orchSec = [math]::Round(((Get-Date) - $t0).TotalSeconds - 1.5, 1)
  $orchOk  = $sawWork -and ($idle -ge 6) -and ([int]$enq.enqueued -eq $N)
  Add-Result 'orchestrator' 'fanout-100' $orchOk ("{0}s" -f $orchSec) "enqueued=$($enq.enqueued) sawWork=$sawWork drained=$($idle -ge 6)"

  # ── Scheduler / timer ───────────────────────────────────────────────────────
  # The 5s timer increments a shared-cache counter on a background worker.
  $c0 = [int](Api '/API/PerfTimerCount').count
  Start-Sleep -Seconds 14
  $c1 = [int](Api '/API/PerfTimerCount').count
  Add-Result 'scheduler' 'timer-fires' ($c1 -gt $c0) '-' "count $c0 -> $c1"

  # ── Realtime SSE ────────────────────────────────────────────────────────────
  # Publish start+update as a user (the bridge stores the current frame under (userId, jobId)), then
  # connect the SSE stream — on connect the bridge replays the stored current frame. A bounded connect
  # (--max-time) captures it synchronously, so there's no background-process timing to be flaky in CI.
  $jobId = [guid]::NewGuid().ToString()
  $hdr   = 'x-ms-client-principal-name: ciuser'
  foreach ($mode in 'start', 'update') {
    try { Invoke-RestMethod "$base/API/PerfPublish?jobId=$jobId&mode=$mode" -Headers @{ 'x-ms-client-principal-name' = 'ciuser' } -TimeoutSec 20 | Out-Null } catch {}
  }
  $sseTxt = (& $curl '-N' '-s' '--max-time' '4' '-H' $hdr "$base/.craft/events" 2>$null | Out-String)
  try { Invoke-RestMethod "$base/API/PerfPublish?jobId=$jobId&mode=end" -Headers @{ 'x-ms-client-principal-name' = 'ciuser' } -TimeoutSec 20 | Out-Null } catch {}
  $sseOk = ($sseTxt -match [regex]::Escape($jobId)) -and ($sseTxt -match '"mode":"update"')
  Add-Result 'realtime' 'sse-deliver' $sseOk '-' "publish -> stored -> SSE replay delivered"

  # ── Security headers ────────────────────────────────────────────────────────
  try {
    $resp = Invoke-WebRequest "$base/API/PerfPing" -TimeoutSec 20 -SkipHttpErrorCheck
    $csp  = $resp.Headers['Content-Security-Policy']
    Add-Result 'security' 'csp-header' ([bool]$csp) '-' "CSP present"
  } catch { Add-Result 'security' 'csp-header' $false '-' $_.Exception.Message }

  # ── Frontend static serving ─────────────────────────────────────────────────
  # Two static HTML pages served correctly + fast, and a compressible asset served precompressed
  # (Content-Encoding: br) when the client accepts brotli, plus its identity content verified.
  $fastMs = 2000
  $homePage = Fetch "$base/index.html"
  Add-Result 'frontend' 'serve-home' ($homePage.Code -eq 200 -and $homePage.Body -match 'E2E_HOME_MARKER' -and $homePage.TimeMs -lt $fastMs) ("{0}ms" -f $homePage.TimeMs) "index.html 200 + marker"
  $about = Fetch "$base/about.html"
  Add-Result 'frontend' 'serve-about' ($about.Code -eq 200 -and $about.Body -match 'E2E_ABOUT_MARKER' -and $about.TimeMs -lt $fastMs) ("{0}ms" -f $about.TimeMs) "about.html 200 + marker"
  $br = Fetch "$base/bundle.js" @('-H', 'Accept-Encoding: br')
  $brOk = ($br.Code -eq 200) -and ($br.Headers -match '(?im)^content-encoding:\s*br') -and ($br.TimeMs -lt $fastMs)
  Add-Result 'frontend' 'brotli-served' $brOk ("{0}ms {1}B" -f $br.TimeMs, $br.Size) "precompressed .br served (Content-Encoding: br)"
  $id = Fetch "$base/bundle.js" @('-H', 'Accept-Encoding: identity')
  Add-Result 'frontend' 'identity-content' ($id.Code -eq 200 -and $id.Body -match 'E2E_BUNDLE_MARKER') ("{0}ms {1}B" -f $id.TimeMs, $id.Size) "identity bundle content correct"

  # ── Summary ─────────────────────────────────────────────────────────────────
  $fail = @($results | Where-Object { -not $_.pass })
  $pass = @($results | Where-Object { $_.pass })
  Write-Host ""
  Write-Host ("===== E2E: {0} passed, {1} failed =====" -f $pass.Count, $fail.Count) -ForegroundColor $(if ($fail.Count) { 'Red' } else { 'Green' })
}
finally {
  if ($KeepUp) { Info "leaving stack up (-KeepUp). down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}

if (@($results | Where-Object { -not $_.pass }).Count -gt 0) { exit 1 }
exit 0
