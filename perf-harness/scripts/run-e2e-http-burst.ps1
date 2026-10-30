<#
.SYNOPSIS
  HTTP burst section of run-e2e.ps1 (dot-sourced; uses its Add-Result/Info/Json and the Azurite network).

.DESCRIPTION
  Interactive requests that find every HTTP worker busy grow the pool past HttpPoolSize (up to HttpBurstMax), and
  API clients stay capped at HttpPoolSize workers so that capacity is only ever the users'. Idle burst workers are
  disposed again.

  A throwaway container with two HTTP workers and up to two burst workers is flooded with four slow API-client
  requests. A user request made during the flood must be answered on a burst worker instead of waiting for one of
  them, the flood must run two at a time even though a burst worker is free, and once idle the pool must shrink
  back to two.
#>

Info "http-burst: launching a container with 2 HTTP workers + up to 2 burst workers ..."
$burstName = 'craft-e2e-burst'
$burstBase = 'http://127.0.0.1:5404'
$burstApi  = (Resolve-Path (Join-Path $root 'api-harness/API')).Path
$burstConn = 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://azurite:10000/devstoreaccount1;QueueEndpoint=http://azurite:10001/devstoreaccount1;TableEndpoint=http://azurite:10002/devstoreaccount1;'
docker rm -f $burstName 2>&1 | Out-Null
docker run -d --name $burstName --network craft-e2e-aznet -p '5404:8080' `
  -v "${burstApi}:/app/API:ro" `
  -e ASPNETCORE_ENVIRONMENT=Production -e CRAFT_SERVE_API=true -e CRAFT_RUN_BACKGROUND=true `
  -e App__Scripts__HttpModules__0=PerfApi -e App__ReadinessMode=Immediate -e App__Setup__Enabled=false `
  -e App__Worker__IgnoreSkuProfiles=true -e App__Worker__HttpPoolSize=2 -e App__Worker__BgPoolSize=1 `
  -e App__Worker__HttpBurstMax=2 -e App__Worker__HttpBurstIdleSeconds=5 -e App__Worker__HttpQueueTimeoutSeconds=60 `
  -e App__Orchestrator__TablePrefix=E2EBurst -e App__RateLimit__Enabled=false -e CRAFT_LOG_LEVEL=Information `
  -e "AzureWebJobsStorage=$burstConn" `
  $SutImage 2>&1 | Out-Null
try {
  $up = $false; $dl = (Get-Date).AddSeconds(180)
  while ((Get-Date) -lt $dl) {
    if ((Json "$burstBase/API/PerfPing").ok -eq $true) { $up = $true; break }
    Start-Sleep -Seconds 2
  }
  $h = Json "$burstBase/healthz"
  Add-Result 'http-burst' 'container-ready' ($up -and [int]$h.workers.http -eq 2) '-' "PerfPing ok=$up, healthz workers.http=$($h.workers.http) (expect 2)"

  # An app-only principal, the shape CraftAuthMiddleware turns into idp=aad + AppId (an API client).
  $appPrincipal = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"claims":[{"typ":"appid","val":"11111111-2222-3333-4444-555555555555"}]}'))

  # ── Four 5 s API-client requests: two run, two queue behind the API-client cap ─
  $flood = 1..4 | ForEach-Object {
    Start-ThreadJob -ArgumentList $burstBase, $appPrincipal -ScriptBlock {
      param($Base, $Principal)
      $sw = [Diagnostics.Stopwatch]::StartNew()
      $r = try { Invoke-WebRequest "$Base/API/PerfSleep?ms=5000" -Headers @{ 'x-ms-client-principal' = $Principal } -TimeoutSec 90 -SkipHttpErrorCheck } catch { $null }
      [pscustomobject]@{ Code = if ($r) { [int]$r.StatusCode } else { 0 }; Ms = $sw.ElapsedMilliseconds }
    }
  }
  Start-Sleep -Seconds 1

  # ── A user request during the flood gets a burst worker ─────────────────────────
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $user = try { Invoke-WebRequest "$burstBase/API/PerfPing" -TimeoutSec 30 -SkipHttpErrorCheck } catch { $null }
  $sw.Stop()
  $userOk = $user -and $user.StatusCode -eq 200 -and $sw.ElapsedMilliseconds -lt 3000
  Add-Result 'http-burst' 'user-not-queued' $userOk ("{0}ms" -f $sw.ElapsedMilliseconds) "user PerfPing while API clients hold both workers -> HTTP $(if ($user) { $user.StatusCode } else { 'none' }) (a pool worker frees only after ~4 s)"

  $h = Json "$burstBase/healthz"
  Add-Result 'http-burst' 'pool-grew' ([int]$h.workers.http -gt 2) '-' "healthz workers.http=$($h.workers.http) during the flood (expect > 2)"

  # ── The flood ran two at a time although a burst worker was free ─────────────────
  $runs = @($flood | Wait-Job -Timeout 120 | Receive-Job)
  $flood | Remove-Job -Force
  $slowest = ($runs | Measure-Object Ms -Maximum).Maximum
  $floodOk = $runs.Count -eq 4 -and @($runs | Where-Object Code -ne 200).Count -eq 0 -and $slowest -ge 9000
  Add-Result 'http-burst' 'api-clients-capped' $floodOk ("{0:N1}s" -f ($slowest / 1000)) "4 x 5 s API-client requests on a 2-worker cap -> codes $(($runs.Code | Sort-Object) -join ','), slowest $slowest ms (expect >= 9000: two waves)"

  # ── Idle: burst workers are disposed back down to HttpPoolSize ──────────────────
  $sw = [Diagnostics.Stopwatch]::StartNew(); $h = $null; $dl = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $dl) {
    $h = Json "$burstBase/healthz"
    if ($h -and [int]$h.workers.http -eq 2) { break }
    Start-Sleep -Seconds 2
  }
  $sw.Stop()
  Add-Result 'http-burst' 'trimmed-when-idle' ($h -and [int]$h.workers.http -eq 2) ("{0:N0}s" -f $sw.Elapsed.TotalSeconds) "healthz workers.http=$($h.workers.http) after 5 s idle (expect 2)"
}
catch { Add-Result 'http-burst' 'flow' $false '-' $_.Exception.Message }
finally {
  if ($KeepUp) { Info "http-burst: leaving $burstName up (-KeepUp)" }
  else { docker rm -f $burstName 2>&1 | Out-Null }
}
