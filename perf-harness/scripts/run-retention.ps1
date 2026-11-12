<#
.SYNOPSIS
  Retention harness: prove finished work leaves nothing behind in memory.

.DESCRIPTION
  run-oom.ps1 measures the PEAK while a fan-out drains. This measures what is still held AFTER it has
  drained: a warm-up batch first (so lazy initialisation is in the baseline), then a forced compacting GC and
  a snapshot of the heap plus the Count of every collection reachable from Craft's statics (/API/PerfSettle).
  Then -Jobs production-shaped tasks (payload in, result out, -FailPct failures, a post-exec step per run,
  optional child runs), wait until everything is terminal and quiet, and snapshot again. Any collection whose
  count grew, and the heap delta, are printed; -GcDump adds a dotnet-gcdump retained-type diff from a sidecar.
  Exits 1 when the heap grows more than -MaxHeapGrowthMB or any collection grows by -MaxHolderGrowth or more.

.EXAMPLE
  pwsh scripts\run-retention.ps1 -Jobs 100000 -GcDump
  pwsh scripts\run-retention.ps1 -Jobs 5000 -WarmupJobs 500 -Label smoke
#>
[CmdletBinding()]
param(
  [string]$SutImage   = 'craft:local',
  [string]$Label      = 'retention',
  [ValidateSet('Retain', 'Leak')][string]$Workload = 'Retain',  # Leak: each task drives -LeakN iterations of the process-lifetime leak paths (Push-PerfLeak)
  [int]$LeakN         = 20,
  [int]$Jobs          = 100000,
  [int]$TasksPerRun   = 50,
  [int]$ChunkRuns     = 40,       # runs created per enqueue call
  [int]$WaveJobs      = 10000,    # main load goes in waves this size, each drained before the next (Azurite slows badly past ~20k pending)
  [int]$ResultKB      = 2,
  [int]$ParamKB       = 1,
  [int]$FailPct       = 2,
  [int]$Child         = 0,        # tasks in a child run started by each run's first task
  [switch]$NoPostExec,
  [int]$WarmupJobs    = 2000,
  [int]$BgPool        = 8,
  [int]$RecycleAfter  = 0,        # replace a worker after N invocations (CIPP prod: 250)
  [double]$Cpus       = 2,
  [int]$Port          = 5298,
  [int]$QuietSec      = 90,       # idle time after the last task before the snapshot (timers, finalisers)
  [int]$MaxWaitSec    = 7200,
  [double]$MaxHeapGrowthMB = 10,
  [int]$MaxHolderGrowth    = 50,
  [switch]$GcDump,
  [string]$CippRoot,              # a CIPP checkout: load its built modules into the workers (docker-compose.bg-cipp.yml)
  [switch]$KeepUp
)
$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$root    = Split-Path -Parent $here
$compose = Join-Path $root 'docker-compose.bg.yml'
$files = @('-f', $compose)
if ($CippRoot) { $env:CIPP_ROOT = (Resolve-Path $CippRoot).Path; $files += @('-f', (Join-Path $root 'docker-compose.bg-cipp.yml')) }
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir  = Join-Path $root "results/$Label-$stamp"
New-Item -ItemType Directory -Force $outDir | Out-Null
$base    = "http://127.0.0.1:$Port"

function Info($m) { Write-Host "[retention] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[retention] $m" -ForegroundColor Yellow }
function Get-Alloc { try { Invoke-RestMethod "$base/API/PerfAllocation" -TimeoutSec 10 } catch { $null } }

function Add-Jobs([int]$Count, [string]$Prefix) {
  $Runs = [math]::Ceiling($Count / $TasksPerRun)
  $Post = if ($NoPostExec) { 0 } else { 1 }
  for ($Done = 0; $Done -lt $Runs; $Done += $ChunkRuns) {
    $N = [math]::Min($ChunkRuns, $Runs - $Done)
    $Uri = if ($Workload -eq 'Leak' -and $Prefix -eq 'PerfMain') { "$base/API/PerfLeakEnqueue?runs=$N&tasks=$TasksPerRun&n=$LeakN&payloadkb=$ParamKB" }
    else { "$base/API/PerfRetentionEnqueue?runs=$N&tasks=$TasksPerRun&resultkb=$ResultKB&paramkb=$ParamKB&failpct=$FailPct&post=$Post&child=$Child&prefix=$Prefix" }
    $null = Invoke-RestMethod $Uri -TimeoutSec 300
  }
  Info "enqueued $($Runs * $TasksPerRun) tasks in $Runs runs ($Prefix)"
}

# Idle = nothing queued, claimed or running for 3 polls in a row, and every run of $Prefix terminal.
function Wait-Idle([string]$Prefix) {
  $Deadline = (Get-Date).AddSeconds($MaxWaitSec); $Calm = 0; $Last = Get-Date
  while ((Get-Date) -lt $Deadline) {
    $A = Get-Alloc
    if ($A -and [int]$A.queue.total -eq 0 -and [int]$A.jm.active -eq 0 -and [int]$A.jm.queued -eq 0 -and [int]$A.pool.bgBusy -eq 0) { $Calm++ } else { $Calm = 0 }
    if (((Get-Date) - $Last).TotalSeconds -ge 15 -and $A) {
      $Last = Get-Date
      Info ("  done={0} failed={1} queue={2} active={3} heap={4}MB gc2={5}" -f $A.jm.completed, $A.jm.failed, $A.queue.total, $A.jm.active, $A.memory.heapMB, $A.memory.gc2)
    }
    if ($Calm -ge 3) {
      $Like = if ($Workload -eq 'Leak' -and $Prefix -eq 'PerfMain') { 'PerfLeak*' } else { "$Prefix*" }
      $Runs = @((Invoke-RestMethod "$base/API/PerfRuns" -TimeoutSec 60).runs | Where-Object { $_.name -like $Like })
      $Open = @($Runs | Where-Object { [int]$_.running -gt 0 -or [int]$_.queued -gt 0 -or ([int]$_.completed + [int]$_.failed) -lt [int]$_.total })
      if ($Open.Count -eq 0) { Info "idle: $($Runs.Count) $Prefix runs terminal"; return }
      $Calm = 0
    }
    Start-Sleep -Seconds 2
  }
  throw "work did not go idle within $MaxWaitSec s"
}

function Get-Snapshot([string]$Name) {
  # Each HTTP worker answers in turn; the last call is taken once every worker has run the endpoint.
  $S = $null; foreach ($i in 1..6) { $S = Invoke-RestMethod "$base/API/PerfSettle" -TimeoutSec 300 }
  $Snap = [ordered]@{ heapMB = [double]$S.heapMB; committedMB = [double]$S.committedMB; gen2 = [int]$S.gen2; holders = @{}; types = @{} }
  foreach ($P in $S.holders.PSObject.Properties) { $Snap.holders[$P.Name] = [long]$P.Value }
  if ($GcDump) {
    # A full heap dump, not dotnet-gcdump: gcdump's event buffering is lossy against a .NET 8 target and drops
    # types. The target's createdump writes the file inside the SUT, read back through /proc/1/root.
    $Cmd = "[ -x /root/.dotnet/tools/dotnet-dump ] || dotnet tool install -g dotnet-dump >/dev/null; " +
      "/root/.dotnet/tools/dotnet-dump collect -p 1 --type Heap -o /tmp/$Name.dmp >/dev/null && " +
      "/root/.dotnet/tools/dotnet-dump analyze /proc/1/root/tmp/$Name.dmp -c 'dumpheap -stat' -c exit > /out/$Name.txt; rm -f /proc/1/root/tmp/$Name.dmp"
    docker run --rm --pid=container:craft-perf-bg-sut --privileged -e TMPDIR=/proc/1/root/tmp -v "${outDir}:/out" -v craft-dotnet-tools:/root/.dotnet/tools mcr.microsoft.com/dotnet/sdk:8.0 bash -c $Cmd | Out-Host
    foreach ($Line in Get-Content (Join-Path $outDir "$Name.txt") -ErrorAction SilentlyContinue) {
      if ($Line -match '^\s*[0-9a-f]{8,}\s+([\d,]+)\s+([\d,]+)\s+(\S.*)$') { $Snap.types[$Matches[3].Trim()] = @([long]($Matches[2] -replace ',', ''), [long]($Matches[1] -replace ',', '')) }
    }
  }
  Info ("{0}: heap={1}MB committed={2}MB holders={3} types={4}" -f $Name, $Snap.heapMB, $Snap.committedMB, $Snap.holders.Count, $Snap.types.Count)
  $Snap
}

$env:SUT_IMAGE = $SutImage; $env:SUT_PORT = "$Port"; $env:SUT_CPUS = "$Cpus"; $env:BG_POOL = "$BgPool"
$env:BG_BURST = 'true'; $env:BG_BASE = "$BgPool"; $env:BG_SCALEUP = '1'; $env:BG_CEILING = "$BgPool"
$env:RECYCLE_AFTER = "$RecycleAfter"; $env:JOB_BATCH = '100'; $env:JOB_POLL_MS = '250'; $env:GC_HEAP_LIMIT_MB = ''
Info "workload=$Workload leakN=$LeakN image=$SutImage jobs=$Jobs tasks/run=$TasksPerRun result=${ResultKB}KB param=${ParamKB}KB fail=$FailPct% postexec=$(-not $NoPostExec) child=$Child recycleAfter=$RecycleAfter"
docker compose @files up -d --force-recreate 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'compose up failed' }

try {
  $Ready = $false; $Deadline = (Get-Date).AddSeconds(300)
  while ((Get-Date) -lt $Deadline) {
    try { if ((Invoke-RestMethod "$base/healthz" -TimeoutSec 5).status -eq 'ready') { $Ready = $true; break } } catch {}
    Start-Sleep -Seconds 2
  }
  if (-not $Ready) { throw 'SUT never became ready' }

  Add-Jobs $WarmupJobs 'PerfWarm'; Wait-Idle 'PerfWarm'; Start-Sleep -Seconds $QuietSec
  $Before = Get-Snapshot 'before'
  $RecyclesBefore = @(docker logs craft-perf-bg-sut 2>&1 | Select-String 'Recycling W').Count

  $T0 = Get-Date
  for ($Sent = 0; $Sent -lt $Jobs; $Sent += $WaveJobs) {
    Add-Jobs ([math]::Min($WaveJobs, $Jobs - $Sent)) 'PerfMain'; Wait-Idle 'PerfMain'
  }
  $Elapsed = [math]::Round(((Get-Date) - $T0).TotalSeconds)
  Start-Sleep -Seconds $QuietSec
  $After = Get-Snapshot 'after'
  $Recycles = @(docker logs craft-perf-bg-sut 2>&1 | Select-String 'Recycling W').Count - $RecyclesBefore

  $Grew = @(foreach ($K in $After.holders.Keys) {
      $B = if ($Before.holders.ContainsKey($K)) { $Before.holders[$K] } else { 0 }
      if ($After.holders[$K] -ne $B) { [pscustomobject]@{ holder = $K; before = $B; after = $After.holders[$K]; delta = $After.holders[$K] - $B } }
    }) | Sort-Object delta -Descending
  $Types = @(foreach ($K in $After.types.Keys) {
      $B = if ($Before.types.ContainsKey($K)) { $Before.types[$K] } else { @(0, 0) }
      $D = $After.types[$K][0] - $B[0]
      if ($D -ne 0) { [pscustomobject]@{ type = $K; bytesDelta = $D; countDelta = $After.types[$K][1] - $B[1] } }
    }) | Sort-Object bytesDelta -Descending
  $HeapDelta = [math]::Round($After.heapMB - $Before.heapMB, 1)

  Write-Host "`n===== retention: $Label ($Jobs jobs in ${Elapsed}s) =====" -ForegroundColor Yellow
  Write-Host ("  heap after settle : {0} MB -> {1} MB ({2:+0.0;-0.0} MB)" -f $Before.heapMB, $After.heapMB, $HeapDelta)
  Write-Host ("  worker recycles   : {0}{1}" -f $Recycles, $(if ($Recycles) { ' ({0:N1} KB of heap per recycle, before subtracting job records)' -f ($HeapDelta * 1024 / $Recycles) }))
  Write-Host "  collections that changed size:"
  $Grew | Select-Object -First 40 | ForEach-Object { Write-Host ("    {0,8:+#;-#} {1,8} -> {2,-8} {3}" -f $_.delta, $_.before, $_.after, $_.holder) }
  if ($GcDump) {
    Write-Host "  retained types that grew most (gcdump):"
    $Types | Select-Object -First 25 | ForEach-Object { Write-Host ("    {0,12:N0} B {1,8:+#;-#} objs  {2}" -f $_.bytesDelta, $_.countDelta, $_.type) }
  }
  $Failed = $HeapDelta -gt $MaxHeapGrowthMB -or @($Grew | Where-Object delta -ge $MaxHolderGrowth).Count -gt 0
  @{ label = $Label; jobs = $Jobs; elapsedSec = $Elapsed; heapDeltaMB = $HeapDelta; recycles = $Recycles; before = $Before; after = $After; grew = $Grew; types = $Types; failed = $Failed } |
    ConvertTo-Json -Depth 6 | Set-Content (Join-Path $outDir 'result.json') -Encoding utf8
  Info "wrote $outDir"
  if ($Failed) { Write-Host '  >> RETAINED: memory grew with the work done' -ForegroundColor Red; $global:LASTEXITCODE = 1 }
  else { Write-Host '  >> FLAT: nothing retained beyond the thresholds' -ForegroundColor Green }
}
finally {
  if ($KeepUp) { Warn "left up (-KeepUp). down: docker compose -f `"$compose`" down -v" }
  else { docker compose @files down -v 2>&1 | Out-Null }
}
if ($Failed) { exit 1 }
