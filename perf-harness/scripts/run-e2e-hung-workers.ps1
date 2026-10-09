<#
.SYNOPSIS
  Hung-worker section of run-e2e.ps1 (dot-sourced; uses its Add-Result/Info/Json and the Azurite network).

.DESCRIPTION
  A pipeline blocked inside a .NET call never sees PowerShell's stop request. Before workers were recycled, a
  timeout on such a pipeline blocked forever in PowerShell.Stop(): the worker never came back, and once every
  worker had hit one the instance shed every HTTP request with 503 and ran no background work, while idle.

  A throwaway container with two HTTP and two BG workers and short timeouts is driven into exactly that state
  with PerfHang / PerfBgHang (each blocks in ManualResetEventSlim.Wait), then must keep serving: every hung
  request returns 504, the pool answers again, background work still drains, /healthz counts the abandoned
  workers, and releasing the blocked calls lets them unwind.
#>

Info "hung-workers: launching a container with 2 HTTP + 2 BG workers and short timeouts ..."
$hungName = 'craft-e2e-hung'
$hungBase = 'http://127.0.0.1:5403'
$hungApi  = (Resolve-Path (Join-Path $root 'api-harness/API')).Path
$hungConn = 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://azurite:10000/devstoreaccount1;QueueEndpoint=http://azurite:10001/devstoreaccount1;TableEndpoint=http://azurite:10002/devstoreaccount1;'
docker rm -f $hungName 2>&1 | Out-Null
docker run -d --name $hungName --network craft-e2e-aznet -p '5403:8080' `
  -v "${hungApi}:/app/API:ro" `
  -e ASPNETCORE_ENVIRONMENT=Production -e CRAFT_SERVE_API=true -e CRAFT_RUN_BACKGROUND=true `
  -e App__Scripts__HttpModules__0=PerfApi -e App__ReadinessMode=Immediate -e App__Setup__Enabled=false `
  -e App__Worker__IgnoreSkuProfiles=true -e App__Worker__HttpPoolSize=2 -e App__Worker__BgPoolSize=2 `
  -e App__Worker__HttpTimeoutSeconds=4 -e App__Worker__BgTimeoutSeconds=4 -e App__Worker__StopGraceSeconds=2 `
  -e App__Worker__HttpQueueTimeoutSeconds=60 `
  -e App__Orchestrator__TablePrefix=E2EHung -e BackgroundBaseConcurrency=2 -e BackgroundHttpPressureThreshold=0 `
  -e App__RateLimit__Enabled=false -e CRAFT_LOG_LEVEL=Information `
  -e "AzureWebJobsStorage=$hungConn" `
  $SutImage 2>&1 | Out-Null
try {
  $up = $false; $dl = (Get-Date).AddSeconds(180)
  while ((Get-Date) -lt $dl) {
    if ((Json "$hungBase/API/PerfPing").ok -eq $true) { $up = $true; break }
    Start-Sleep -Seconds 2
  }
  Add-Result 'hung-workers' 'container-ready' $up '-' "PerfPing on the 2+2 worker container"

  # ── Background: hang every BG worker, then queue ordinary work behind it ─────
  $hangRun = Json "$hungBase/API/PerfBgHangEnqueue?n=2"
  Start-Sleep -Seconds 1
  $work = Json "$hungBase/API/PerfBgEnqueue?n=10&taskms=0"
  $workName = "$($work.run)"
  $sw = [Diagnostics.Stopwatch]::StartNew(); $done = $null; $dl = (Get-Date).AddSeconds(120)
  while ((Get-Date) -lt $dl) {
    $done = @((Json "$hungBase/API/PerfRuns").runs | Where-Object { $_.name -like '*PerfBg-*' -and [int]$_.completed -eq 10 })
    if ($done.Count) { break }
    Start-Sleep -Seconds 1
  }
  $sw.Stop()
  Add-Result 'hung-workers' 'bg-drains-past-hang' ([bool]$done.Count) ("{0:N0}s" -f $sw.Elapsed.TotalSeconds) "10 tasks queued behind 2 hung ones on 2 workers (hang run $($hangRun.run), work run $workName)"

  # ── HTTP: more hung requests than workers ─────────────────────────────────────
  # Three at once against two workers: the third only gets a worker if the first two are replaced.
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $codes = 1..3 | ForEach-Object -ThrottleLimit 3 -Parallel {
    $r = try { Invoke-WebRequest "$using:hungBase/API/PerfHang" -TimeoutSec 90 -SkipHttpErrorCheck } catch { $null }
    if ($r) { [int]$r.StatusCode } else { 0 }
  }
  $sw.Stop()
  $codes = @($codes | Sort-Object)
  Add-Result 'hung-workers' 'http-hang-504' (@($codes | Where-Object { $_ -eq 504 }).Count -eq 3) ("{0:N0}s" -f $sw.Elapsed.TotalSeconds) "3 hung requests on 2 workers -> $($codes -join ',') (0 = no answer in 90s)"

  $ping = $null; $dl = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $dl) {
    $ping = try { Invoke-WebRequest "$hungBase/API/PerfPing" -TimeoutSec 20 -SkipHttpErrorCheck } catch { $null }
    if ($ping -and $ping.StatusCode -eq 200) { break }
    Start-Sleep -Seconds 1
  }
  Add-Result 'hung-workers' 'http-recovers' ($ping -and $ping.StatusCode -eq 200) '-' "PerfPing after the hangs -> HTTP $(if ($ping) { $ping.StatusCode } else { 'none' })"

  $h = Json "$hungBase/healthz"
  $hungTotal = [int]$h.workers.hungTotal
  Add-Result 'hung-workers' 'health-counts-hung' ($hungTotal -ge 5) '-' "healthz workers.hungTotal=$hungTotal hung=$($h.workers.hung) (expect >= 5)"

  # ── Release: the abandoned calls return and the workers are let go ───────────
  $rel = Json "$hungBase/API/PerfHangRelease"
  $h = $null; $dl = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $dl) {
    $h = Json "$hungBase/healthz"
    if ($h -and [int]$h.workers.hung -eq 0) { break }
    Start-Sleep -Seconds 1
  }
  Add-Result 'hung-workers' 'abandoned-unwind' ($h -and $null -ne $h.workers -and [int]$h.workers.hung -eq 0) '-' "released $($rel.released) blocked calls -> workers.hung=$($h.workers.hung)"
}
catch { Add-Result 'hung-workers' 'flow' $false '-' $_.Exception.Message }
finally {
  if ($KeepUp) { Info "hung-workers: leaving $hungName up (-KeepUp)" }
  else { docker rm -f $hungName 2>&1 | Out-Null }
}
