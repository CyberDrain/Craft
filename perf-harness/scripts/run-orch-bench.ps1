<#
.SYNOPSIS
  Orchestration benchmark: the same workloads against any Craft image, on Azurite or a real storage account, so
  engines can be compared like for like.

.DESCRIPTION
  Brings up docker-compose.e2e-azure.yml (Azurite + the SUT with PerfApi, BgPoolSize=4, 2 CPUs), optionally pointing
  the SUT at a real account (-Storage azure, connection from CRAFT_TEST_TABLE_CONNECTION) under a unique table
  prefix, runs the scenarios below one after another, writes one JSON result file, and tears the stack down.
  Uses only Start-CraftOrchestrator, the PerfE2E recording task/PostExecution and WorkerMetricsBridge.CancelRun,
  so it runs unchanged on engines before and after the storage-first redesign. Timings come from task start/end
  ticks recorded inside the SUT.

.EXAMPLE
  pwsh scripts/run-orch-bench.ps1 -SutImage craft:pre-v2-c98a091 -Storage azurite -Label pre-azurite
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$SutImage,
  [ValidateSet('azurite', 'azure')][string]$Storage = 'azurite',
  [string]$Label = 'bench',
  [int]$Port = 5399,
  [string]$OutDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'craft-bench'),
  [string[]]$Only
)

$ErrorActionPreference = 'Stop'
# pwsh -File passes "a,b" as one string.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$compose = @('-f', (Join-Path $root 'docker-compose.e2e-azure.yml'), '-f', (Join-Path $root 'docker-compose.bench.yml'))
$base = "http://127.0.0.1:$Port"
$Azurite = 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://azurite:10000/devstoreaccount1;QueueEndpoint=http://azurite:10001/devstoreaccount1;TableEndpoint=http://azurite:10002/devstoreaccount1;'

$Prefix = 'Bench' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$env:SUT_IMAGE = $SutImage
$env:SUT_PORT = "$Port"
$env:BENCH_PREFIX = $Prefix
$env:BENCH_STORAGE = if ($Storage -eq 'azure') {
  if (-not $env:CRAFT_TEST_TABLE_CONNECTION) { throw 'CRAFT_TEST_TABLE_CONNECTION is not set' }
  $env:CRAFT_TEST_TABLE_CONNECTION
} else { $Azurite }

$Results = [ordered]@{
  label = $Label; image = $SutImage; storage = $Storage; prefix = $Prefix
  account = if ($Storage -eq 'azure') { [regex]::Match($env:CRAFT_TEST_TABLE_CONNECTION, 'AccountName=([^;]+)').Groups[1].Value } else { 'azurite' }
  startedUtc = [DateTime]::UtcNow.ToString('o'); scenarios = [ordered]@{}
}

function Info($m) { Write-Host "[bench $Label] $m" -ForegroundColor Cyan }
function Get-Api([string]$Path, [int]$TimeoutSec = 60) { Invoke-RestMethod "$base$Path" -TimeoutSec $TimeoutSec }
function New-Id { [guid]::NewGuid().ToString('N').Substring(0, 6) }
function Ms([long]$Ticks) { [math]::Round($Ticks / 10000.0) }

function Start-Runs([string]$Ns, $Runs, [string]$Sink = 'cache', [int]$TimeoutSec = 600) {
  $Body = @{ ns = $Ns; sink = $Sink; runs = @($Runs) } | ConvertTo-Json -Depth 30 -Compress
  $Sw = [Diagnostics.Stopwatch]::StartNew()
  $R = Invoke-RestMethod "$base/API/PerfE2EStart" -Method Post -ContentType 'application/json' -Body $Body -TimeoutSec $TimeoutSec
  return [pscustomobject]@{ runs = @($R.runs); callMs = $Sw.ElapsedMilliseconds }
}

function Get-Summary([string]$Ns, [string]$Source = 'cache') {
  $Map = @{}
  foreach ($R in @((Get-Api "/API/PerfE2EState?ns=$Ns&source=$Source&summary=1").runs)) { $Map[$R.run] = $R }
  return $Map
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec, [int]$IntervalMs = 250) {
  $Sw = [Diagnostics.Stopwatch]::StartNew()
  while ($Sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
    $V = try { & $Condition } catch { $null }
    if ($V) { return [pscustomobject]@{ ok = $true; value = $V; sec = $Sw.Elapsed.TotalSeconds } }
    Start-Sleep -Milliseconds $IntervalMs
  }
  return [pscustomobject]@{ ok = $false; value = $null; sec = $Sw.Elapsed.TotalSeconds }
}

function Wait-Ready([int]$TimeoutSec = 240) {
  $W = Wait-Until { $H = Get-Api '/healthz' 10; if ($H.status -eq 'ready') { $true } } $TimeoutSec 1000
  if (-not $W.ok) { throw 'SUT never became ready' }
}

function Add-Scenario([string]$Name, [hashtable]$Data) {
  $Results.scenarios[$Name] = $Data
  Info ("{0,-22} {1}" -f $Name, (($Data.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '))
}

function Invoke-Scenario([string]$Name, [scriptblock]$Body) {
  if ($Only -and $Name -notin $Only) { return }
  try { & $Body }
  catch { Add-Scenario $Name @{ error = $_.Exception.Message } }
}

Info "compose up: $SutImage on $Storage (prefix $Prefix)"
docker compose @compose up -d | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'compose up failed' }

try {
  Wait-Ready
  Start-Sleep -Seconds 5

  # 1. Throughput of short tasks, one run, with an aggregation.
  Invoke-Scenario 'fanout-1000' {
    $Ns = "f1k-$(New-Id)"
    $S = Start-Runs $Ns @(@{ name = "BenchFan-$Ns"; label = 'fan'; tasks = 1000; post = @{ marker = 'm' } })
    $T0 = $S.runs[0].enqueueTicks
    $W = Wait-Until { $M = Get-Summary $Ns; if ($M['fan'].posts -ge 1) { $M['fan'] } } 1800 500
    $Rows = @((Get-Api "/API/PerfE2EState?ns=$Ns").rows)
    $Post = @($Rows.Where({ $_.kind -eq 'P' }))[0]
    $Sec = if ($Post) { (Ms ($Post.ticks - $T0)) / 1000.0 } else { -1 }
    Add-Scenario 'fanout-1000' @{ ok = $W.ok; createMs = $S.callMs; endToEndSec = [math]::Round($Sec, 1); tasksPerSec = [math]::Round(1000 / [math]::Max(0.1, $Sec), 1); ran = $W.value.tasks }
  }

  # 2. Efficiency with real work: 200 tasks of 250 ms on 4 workers (ideal 12.5 s).
  Invoke-Scenario 'fanout-200x250ms' {
    $Ns = "f200-$(New-Id)"
    $S = Start-Runs $Ns @(@{ name = "BenchWork-$Ns"; label = 'w'; tasks = 200; task = @{ holdms = 250 }; post = @{ marker = 'm' } })
    $T0 = $S.runs[0].enqueueTicks
    $W = Wait-Until { $M = Get-Summary $Ns; if ($M['w'].posts -ge 1) { $M['w'] } } 1800 500
    $Sec = if ($W.ok) { (Ms ($W.value.maxEnd - $T0)) / 1000.0 } else { -1 }
    Add-Scenario 'fanout-200x250ms' @{ ok = $W.ok; tasksDoneSec = [math]::Round($Sec, 1); efficiencyPct = [math]::Round(12.5 / [math]::Max(0.1, $Sec) * 100); idealSec = 12.5 }
  }

  # 3./4. Many small runs queued at once (one task each, no aggregation).
  foreach ($N in 300, 1000) {
    Invoke-Scenario "runs-$N" {
      $Ns = "r$N-$(New-Id)"
      $Specs = @(for ($I = 0; $I -lt $N; $I++) { @{ name = "BenchMany-$Ns-$I"; label = "r$I"; tasks = 1 } })
      $S = Start-Runs $Ns $Specs 'cache' 1800
      $T0 = $S.runs[0].enqueueTicks
      $W = Wait-Until { $M = Get-Summary $Ns; $Done = @($M.Values.Where({ $_.ended -ge 1 })).Count; if ($Done -ge $N) { $M } } 1800 1000
      $Last = if ($W.ok) { ($W.value.Values | Measure-Object -Property maxEnd -Maximum).Maximum } else { 0 }
      $Sec = if ($W.ok) { (Ms ($Last - $T0)) / 1000.0 } else { -1 }
      Add-Scenario "runs-$N" @{ ok = $W.ok; createMs = $S.callMs; allDoneSec = [math]::Round($Sec, 1); runsPerSec = [math]::Round($N / [math]::Max(0.1, $Sec), 1) }
    }
  }

  # 5. Creating a big run and how soon its first task starts.
  Invoke-Scenario 'ttfs-5000' {
    $Ns = "t5k-$(New-Id)"
    $S = Start-Runs $Ns @(@{ name = "BenchBig-$Ns"; label = 'big'; tasks = 5000; task = @{ holdms = 50 } }) 'cache' 900
    $T0 = $S.runs[0].enqueueTicks
    $W = Wait-Until { $M = Get-Summary $Ns; if ($M['big'].tasks -ge 1) { $M['big'] } } 600 50
    $First = if ($W.ok) { Ms ($W.value.minStart - $T0) } else { -1 }
    $Cancel = Get-Api "/API/PerfE2EBridge?op=cancel&name=BenchBig-$Ns" 900
    Add-Scenario 'ttfs-5000' @{ ok = $W.ok; createMs = $S.callMs; firstStartMs = $First }
    Start-Sleep -Seconds 5
  }

  # 6. An idle engine picking up a new run.
  Invoke-Scenario 'idle-claim' {
    Start-Sleep -Seconds 20
    $Ns = "idle-$(New-Id)"
    $S = Start-Runs $Ns @(@{ name = "BenchIdle-$Ns"; label = 'i'; tasks = 1 })
    $T0 = $S.runs[0].enqueueTicks
    $W = Wait-Until { $M = Get-Summary $Ns; if ($M['i'].tasks -ge 1) { $M['i'] } } 120 20
    Add-Scenario 'idle-claim' @{ ok = $W.ok; firstStartMs = $(if ($W.ok) { Ms ($W.value.minStart - $T0) } else { -1 }) }
  }

  # 7. Per-step overhead of a sequential run.
  Invoke-Scenario 'sequential-50' {
    $Ns = "seq-$(New-Id)"
    $S = Start-Runs $Ns @(@{ name = "BenchSeq-$Ns"; label = 's'; tasks = 50; Sequential = $true; post = @{ marker = 'm' } })
    $T0 = $S.runs[0].enqueueTicks
    $W = Wait-Until { $M = Get-Summary $Ns; if ($M['s'].posts -ge 1) { $M['s'] } } 900 250
    $Sec = if ($W.ok) { (Ms ($W.value.maxEnd - $T0)) / 1000.0 } else { -1 }
    Add-Scenario 'sequential-50' @{ ok = $W.ok; stepsDoneSec = [math]::Round($Sec, 1); msPerStep = [math]::Round($Sec * 1000 / 50) }
  }

  # 8. A high-priority run arriving behind a big backlog in a lower band.
  Invoke-Scenario 'priority-jump' {
    $Ns = "pj-$(New-Id)"
    $null = Start-Runs $Ns @(@{ name = "BenchBacklog-$Ns"; label = 'bl'; tasks = 3000; task = @{ holdms = 100 }; Priority = 6 }) 'cache' 900
    $null = Wait-Until { $M = Get-Summary $Ns; if ($M['bl'].tasks -ge 8) { $true } } 300 100
    $S = Start-Runs $Ns @(@{ name = "BenchUrgent-$Ns"; label = 'u'; tasks = 4; Priority = 1 })
    $T0 = $S.runs[0].enqueueTicks
    $W = Wait-Until { $M = Get-Summary $Ns; if ($M['u'].ended -ge 4) { $M['u'] } } 600 50
    $null = Get-Api "/API/PerfE2EBridge?op=cancel&name=BenchBacklog-$Ns" 900
    Add-Scenario 'priority-jump' @{ ok = $W.ok; firstStartMs = $(if ($W.ok) { Ms ($W.value.minStart - $T0) } else { -1 }); allDoneMs = $(if ($W.ok) { Ms ($W.value.maxEnd - $T0) } else { -1 }) }
    Start-Sleep -Seconds 5
  }

  # 9. Cancelling a large backlog while it runs, to the run finalising (its aggregation running).
  Invoke-Scenario 'cancel-5000' {
    $Ns = "cx-$(New-Id)"
    $null = Start-Runs $Ns @(@{ name = "BenchCancel-$Ns"; label = 'c'; tasks = 5000; task = @{ holdms = 200 }; post = @{ marker = 'm' } }) 'cache' 900
    $null = Wait-Until { $M = Get-Summary $Ns; if ($M['c'].tasks -ge 8) { $true } } 300 100
    $Sw = [Diagnostics.Stopwatch]::StartNew()
    $Cancel = Get-Api "/API/PerfE2EBridge?op=cancel&name=BenchCancel-$Ns" 1800
    $CallMs = $Sw.ElapsedMilliseconds
    $W = Wait-Until { $M = Get-Summary $Ns; if ($M['c'].posts -ge 1) { $M['c'] } } 1800 250
    Add-Scenario 'cancel-5000' @{ ok = $W.ok; callMs = $CallMs; finalisedSec = [math]::Round($Sw.Elapsed.TotalSeconds, 1); reported = $Cancel.cancelled; tasksRan = $W.value.tasks }
  }

  # 10. Memory after the work above.
  Invoke-Scenario 'memory' {
    $M = Get-Api '/API/PerfE2EBridge?op=summary'
    Add-Scenario 'memory' @{ rssMB = $M.rssMB; heapMB = $M.heapMB; committedMB = $M.committedMB }
  }

  # 11./12. Recovery mid-run (200 x 1 s tasks): a graceful recycle (SIGTERM, 30 s grace) and a crash (SIGKILL).
  # Time from the stop to every task done and the aggregation run; executions > 200 means tasks ran twice.
  foreach ($Mode in 'graceful', 'crash') {
    Invoke-Scenario "restart-$Mode" {
      $Ns = "rs-$(New-Id)"
      $null = Start-Runs $Ns @(@{ name = "BenchRestart-$Ns"; label = 'rs'; tasks = 200; task = @{ holdms = 1000 }; post = @{ marker = 'm' } }) 'table'
      $null = Wait-Until { $M = Get-Summary $Ns 'table'; if ($M['rs'].tasks -ge 20) { $true } } 300 500
      $Sw = [Diagnostics.Stopwatch]::StartNew()
      if ($Mode -eq 'graceful') { docker stop -t 30 craft-e2e-az-sut | Out-Null } else { docker kill craft-e2e-az-sut | Out-Null }
      $Exit = docker inspect craft-e2e-az-sut --format '{{.State.ExitCode}}'
      $StopSec = $Sw.Elapsed.TotalSeconds
      docker start craft-e2e-az-sut | Out-Null
      Wait-Ready
      $ReadySec = $Sw.Elapsed.TotalSeconds
      $W = Wait-Until { $M = Get-Summary $Ns 'table'; if ($M['rs'].posts -ge 1) { $M['rs'] } } 1800 2000
      $Rows = @((Get-Api "/API/PerfE2EState?ns=$Ns&source=table" 120).rows.Where({ $_.kind -eq 'T' }))
      $Distinct = @($Rows | Group-Object idx).Count
      Add-Scenario "restart-$Mode" @{ ok = $W.ok; stopSec = [math]::Round($StopSec, 1); exitCode = $Exit; readySec = [math]::Round($ReadySec, 1); recoveredSec = [math]::Round($Sw.Elapsed.TotalSeconds, 1); distinctTasks = $Distinct; executions = $Rows.Count }
    }
  }
}
finally {
  $Results.endedUtc = [DateTime]::UtcNow.ToString('o')
  $null = New-Item -ItemType Directory -Force -Path $OutDir
  $File = Join-Path $OutDir "$Label.json"
  $Results | ConvertTo-Json -Depth 10 | Set-Content -Path $File -Encoding utf8
  Info "results: $File"
  docker compose @compose logs --no-color sut 2>$null | Set-Content -Path (Join-Path $OutDir "$Label.sut.log") -Encoding utf8
  docker compose @compose down -v | Out-Null
  if ($Storage -eq 'azure') {
    # Only this run's tables: every one starts with its unique prefix. The connection string stays in the environment.
    Info "dropping $Prefix* tables from $($Results.account)"
    $Tables = @(az storage table list --connection-string $env:CRAFT_TEST_TABLE_CONNECTION --query "[?starts_with(name, '$Prefix')].name" -o tsv 2>$null)
    foreach ($T in $Tables) { if ($T) { az storage table delete --name $T --connection-string $env:CRAFT_TEST_TABLE_CONNECTION -o none 2>$null } }
    Info "dropped $($Tables.Count) table(s)"
  }
}
