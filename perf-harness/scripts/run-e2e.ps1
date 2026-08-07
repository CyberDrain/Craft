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
# Same, but against an absolute URL — the throwaway containers further down run on their own ports.
function Json($url) { try { return Invoke-RestMethod $url -TimeoutSec 20 } catch { return $null } }
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
  # Prefer upstream MCR until the GHCR CyberDrain mirror carries .NET 10 tags. --pull re-resolves
  # floating 10.0-* bases so runtime CVEs aren't baked from a stale local cache.
  $registry = if ($env:DOTNET_REGISTRY) { $env:DOTNET_REGISTRY } else { 'mcr.microsoft.com' }
  docker build --pull -f (Join-Path $repoRoot 'build/Dockerfile') `
    --build-arg "DOTNET_REGISTRY=$registry" `
    -t $SutImage $repoRoot | Out-Host
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
    $csp  = "$($resp.Headers['Content-Security-Policy'])"
    Add-Result 'security' 'csp-header' ([bool]$csp) '-' "CSP present"

    # connect-src falls back to default-src. A policy gated on the https: scheme alone refuses the
    # app's own same-origin fetches the moment it is served over http — which is how the setup
    # wizard's status call ended up blocked in the browser while curl saw a healthy 200.
    $dsrc = ($csp -split ';' | Where-Object { $_.Trim().StartsWith('default-src') }) -join ''
    $selfOk = $dsrc -match "'self'"
    Add-Result 'security' 'csp-allows-self' $selfOk '-' "default-src allows the app's own origin"
  } catch { Add-Result 'security' 'csp-header' $false '-' $_.Exception.Message }

  # ── Dev-mode principal injection ──────────────────────────────────────────────
  # In local dev, Craft returns an injected dev principal from /.auth/me so the SPA boots without a
  # login (in production EasyAuth serves it at the edge and this handler is shadowed). The main stack
  # runs Production, so verify on a throwaway Development-mode container. /.auth/me is a C# endpoint, so
  # it needs no PS pool or storage.
  Info "dev-auth: launching a Development-mode container ..."
  docker rm -f craft-e2e-devauth 2>&1 | Out-Null
  docker run -d --name craft-e2e-devauth -p '5401:8080' `
    -e ASPNETCORE_ENVIRONMENT=Development -e CRAFT_SERVE_API=true -e App__Setup__Enabled=false `
    $SutImage 2>&1 | Out-Null
  try {
    $devBase = 'http://127.0.0.1:5401'
    $me = $null; $dl = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $dl) {
      $me = try { Invoke-RestMethod "$devBase/.auth/me" -TimeoutSec 5 } catch { $null }
      if ($me -and $me.clientPrincipal) { break }
      Start-Sleep -Seconds 2
    }
    $meOk = [bool]($me.clientPrincipal -and $me.clientPrincipal.userDetails)
    Add-Result 'dev-auth' 'auth-me-dev-principal' $meOk '-' "Development /.auth/me -> userDetails=$($me.clientPrincipal.userDetails)"
  }
  finally { docker rm -f craft-e2e-devauth 2>&1 | Out-Null }

  # ── First-run setup wizard ────────────────────────────────────────────────────
  # The main stack runs with App__Setup__Enabled=false (setup is a first-run-only path), so the wizard
  # gets its own throwaway container on the Azurite network, with its own users table so the run is
  # repeatable.
  #
  # Setup mode is opt-in — the hosted app calls RequestSetupMode() when it works out it cannot
  # configure itself — and there is no env var for it, so this container asks for it the same way a
  # real app does: a WarmupScript on the first worker. Which means the check below also covers the
  # part an operator actually experiences first, the redirect onto the wizard.
  #
  # From there it walks the whole contract the wizard's UI state is derived from: serve the page,
  # read status on an empty table, seed a superadmin, and see the status flip. A regression in any of
  # these is what leaves an operator staring at a greyed-out Add Superadmin button.
  Info "setup: launching a wizard-enabled container ..."
  docker rm -f craft-e2e-setup 2>&1 | Out-Null
  $azConn = 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://azurite:10000/devstoreaccount1;QueueEndpoint=http://azurite:10001/devstoreaccount1;TableEndpoint=http://azurite:10002/devstoreaccount1;'
  docker run -d --name craft-e2e-setup --network craft-e2e-aznet -p '5402:8080' `
    -e ASPNETCORE_ENVIRONMENT=Production -e CRAFT_SERVE_API=true `
    -e App__Setup__Enabled=true -e App__ReadinessMode=Immediate `
    -e App__Worker__IgnoreSkuProfiles=true -e App__Worker__HttpPoolSize=1 -e App__Worker__BgPoolSize=1 `
    -e 'App__Worker__WarmupScripts__0=[Craft.Services.AppLifecycleBridge]::RequestSetupMode("e2e")' `
    -e Auth__UserTableName=e2esetupusers -e App__RateLimit__Enabled=false `
    -e DOTNET_gcServer=0 `
    -e "AzureWebJobsStorage=$azConn" `
    $SutImage 2>&1 | Out-Null
  try {
    $setupBase = 'http://127.0.0.1:5402'
    $upn = 'e2e-admin@contoso.com'

    # Setup mode is live once a browser hitting / is steered to the wizard. Polling on that rather
    # than on a fixed sleep also makes the redirect itself an assertion.
    $redir = $null; $dl = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $dl) {
      $r = try { Invoke-WebRequest $setupBase -TimeoutSec 5 -MaximumRedirection 0 -SkipHttpErrorCheck } catch { $null }
      if ($r -and $r.StatusCode -eq 302 -and "$($r.Headers.Location)" -match '/setup') { $redir = $r; break }
      Start-Sleep -Seconds 2
    }
    Add-Result 'setup' 'enters-setup-mode' ([bool]$redir) '-' "RequestSetupMode() -> / redirects to $($redir.Headers.Location)"

    # The wizard's own API stays reachable while the rest of the app is refused — an operator reaches
    # this page precisely because nothing else works yet.
    $blocked = Invoke-WebRequest "$setupBase/api/PerfPing" -TimeoutSec 20 -SkipHttpErrorCheck
    Add-Result 'setup' 'app-api-refused' ($blocked.StatusCode -eq 503) '-' "app API -> HTTP $($blocked.StatusCode) while setup pending"

    # The page itself. The markers assert the fixed page shipped: the branch table is present and the
    # seed button is not hard-disabled in the markup (it used to be, and was only ever re-enabled by
    # the status fetch below — so one failed request killed the whole wizard silently).
    $pg = Fetch "$setupBase/setup"
    $btn = if ($pg.Body -match '<button[^>]*id="btn-seed"[^>]*>') { $Matches[0] } else { '' }
    $pgOk = ($pg.Code -eq 200) -and ($pg.Body -match 'Add Superadmin') -and
            ($pg.Body -match 'craft:wizard-state') -and $btn -and ($btn -notmatch '\bdisabled\b')
    Add-Result 'setup' 'serve-wizard' $pgOk ("{0}ms" -f $pg.TimeMs) "/setup 200 + branch table + button not hard-disabled"

    # Empty table: storage reachable, no users. This is what unlocks step 1 and keeps step 2 locked.
    $st = Json "$setupBase/api/setup/status"
    $emptyOk = $st -and $st.usersStatus -and ($st.usersStatus.connected -eq $true) -and ($st.usersStatus.hasUsers -eq $false)
    Add-Result 'setup' 'status-empty' $emptyOk '-' "connected=$($st.usersStatus.connected) hasUsers=$($st.usersStatus.hasUsers)"

    # Seed the first superadmin.
    $seed = try {
      Invoke-RestMethod "$setupBase/api/setup/seed-user" -Method Post -TimeoutSec 30 `
        -ContentType 'application/json' -Body (@{ upn = $upn } | ConvertTo-Json)
    } catch { $null }
    Add-Result 'setup' 'seed-first-user' ($seed.success -eq $true) '-' "message=$($seed.message)"

    # The round trip the page polls for: after a successful seed, status must report hasUsers, which
    # is what ungreys the authentication step.
    $after = Json "$setupBase/api/setup/status"
    $flipOk = $after -and $after.usersStatus -and ($after.usersStatus.hasUsers -eq $true)
    Add-Result 'setup' 'status-flips' $flipOk '-' "hasUsers=$($after.usersStatus.hasUsers) after seed"

    # The server-side guard the page deliberately fails open against: with status unreadable the
    # wizard leaves the button clickable, so this endpoint — not a greyed button — has to be what
    # protects a table that already has users. The message is asserted, not just the status code:
    # bad JSON and an unreachable store also produce a 400, and neither of those proves the guard ran.
    $again = Invoke-WebRequest "$setupBase/api/setup/seed-user" -Method Post -TimeoutSec 30 `
      -ContentType 'application/json' -Body (@{ upn = 'second@contoso.com' } | ConvertTo-Json) -SkipHttpErrorCheck
    $body = try { $again.Content | ConvertFrom-Json } catch { $null }
    $guardOk = ($again.StatusCode -eq 400) -and ($body.success -eq $false) -and
               ($body.message -match 'already contains users')
    Add-Result 'setup' 'reseed-refused' $guardOk '-' "HTTP $($again.StatusCode) — $($body.message)"

    # ...and the refusal is not a one-off: the table is still guarded on a retry, and the first user
    # is still the one that is there. The e2e cannot enumerate the table over HTTP — that "nothing was
    # written" invariant is pinned by SetupWizardStatusTests — but a table that had silently accepted
    # the second write would still be reporting hasUsers here, so this is the reachable half.
    $third = Invoke-WebRequest "$setupBase/api/setup/seed-user" -Method Post -TimeoutSec 30 `
      -ContentType 'application/json' -Body (@{ upn = $upn } | ConvertTo-Json) -SkipHttpErrorCheck
    $thirdBody = try { $third.Content | ConvertFrom-Json } catch { $null }
    $stillOk = ($third.StatusCode -eq 400) -and ($thirdBody.message -match 'already contains users') -and
               ((Json "$setupBase/api/setup/status").usersStatus.hasUsers -eq $true)
    Add-Result 'setup' 'reseed-stays-refused' $stillOk '-' "re-seeding the original UPN is refused too"
  }
  catch { Add-Result 'setup' 'wizard-flow' $false '-' $_.Exception.Message }
  finally { docker rm -f craft-e2e-setup 2>&1 | Out-Null }

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

  # GitHub Actions job summary — renders the PASS/FAIL table on the run page (no-op locally). Written
  # before the non-zero exit so a failing run still shows exactly which checks failed.
  if ($env:GITHUB_STEP_SUMMARY) {
    $md = [System.Text.StringBuilder]::new()
    [void]$md.AppendLine("## CRAFT E2E — combined role (Azurite)").AppendLine()
    [void]$md.AppendLine($(if ($fail.Count) { "❌ **$($fail.Count) failed**, $($pass.Count) passed" } else { "✅ **All $($pass.Count) checks passed**" })).AppendLine()
    [void]$md.AppendLine("| Result | Area | Check | Perf | Detail |")
    [void]$md.AppendLine("|:------:|------|-------|------|--------|")
    foreach ($r in $results) {
      [void]$md.AppendLine("| $(if ($r.pass) { '✅' } else { '❌' }) | $($r.area) | $($r.name) | $($r.perf) | $($r.detail) |")
    }
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $md.ToString()
  }
}
finally {
  if ($KeepUp) { Info "leaving stack up (-KeepUp). down: docker compose -f `"$compose`" down -v" }
  else { Info "tearing down ..."; docker compose -f $compose down -v 2>&1 | Out-Null }
}

if (@($results | Where-Object { -not $_.pass }).Count -gt 0) { exit 1 }
exit 0
