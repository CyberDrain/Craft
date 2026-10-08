<#
.SYNOPSIS
  Orchestration-engine section of the e2e regression. Dot-sourced by run-e2e.ps1 (needs $base, Info,
  Add-Result and Add-Skip from it); not run on its own.

.DESCRIPTION
  Every check queues its own uniquely named runs into the SUT through PerfApi (/API/PerfE2EStart), lets the
  PerfE2E task / PerfE2EPost PostExecution probes record what they saw (start/end ticks, worker, stamped
  priority, received results and parameters), and asserts on those records plus the durable run state read
  straight from the {TablePrefix}Work table (/API/PerfE2ERuns). All waits are bounded.

  Checks that need MaxConcurrency / StopOnFailure are capability-gated and reported SKIP on an image without
  them. The restart check runs last: it restarts the SUT container.

  $OrchChecks (from run-e2e.ps1) limits the run to the named groups:
    post fail child prio collide attrib cancel seq order status maxconc stopfail perf realtime restart
#>

# Perf gates. Calibrated from 3 full runs on the reference dev box (combined role, BgPoolSize=4, cpus=2,
# Azurite) on craft:orch-v2-a4d72cf: 2x the median. Short tasks are bounded by the pump (about BgPoolSize claims
# per 1s poll, so ~4 tasks/s here), which is what the fan-out numbers measure.
$OrchGates = @{
  # Provisional after the pump wake-up and bulk-cancel fixes (2026-10-06, one run: 15.6s / 9s / 1371ms / 238MB /
  # 84ms; cancel-5000 measured after the fix). About 2.5x the measured value; re-calibrate from 3 runs.
  Fanout1000Sec  = 40     # 1,000 no-op tasks + PostExecution, enqueue -> PostExecution ran
  ManyRuns300Sec = 25     # 300 single-task runs queued at once, enqueue -> all 300 Done
  Ttfs5000Ms     = 3500   # 5,000-task run on a warm pump, enqueue -> first task started
  RssMB          = 470    # SUT RSS after the perf runs (median 233MB)
  IdleClaimMs    = 1500   # run queued into an idle engine -> first start (the pump wakes on a new run)
  Cancel5000Sec  = 30     # CancelRun on a 5,000-task run -> run finished with every pending task cancelled
  SeqStepMs      = 20     # sequential no-op steps, first start -> last end per step (9.6ms; 32.6ms before finish+claim shared a txn)
}

$OrchSkipped = @{}
# pwsh -File hands "a,b" over as one string.
$OrchChecks = @($OrchChecks | ForEach-Object { "$_" -split ',' } | Where-Object { $_ })
$OrchContainer = 'craft-e2e-az-sut'

function New-OrchId { [guid]::NewGuid().ToString('N').Substring(0, 8) }

function Invoke-OrchGet([string]$Path, [int]$TimeoutSec = 30) { Invoke-RestMethod "$base$Path" -TimeoutSec $TimeoutSec }

function Start-OrchRuns([string]$Ns, $Runs, [string]$Sink = 'cache', [int]$TimeoutSec = 120) {
  $Body = @{ ns = $Ns; sink = $Sink; runs = @($Runs) } | ConvertTo-Json -Depth 30 -Compress
  $R = Invoke-RestMethod "$base/API/PerfE2EStart" -Method Post -ContentType 'application/json' -Body $Body -TimeoutSec $TimeoutSec
  return , @($R.runs)
}

function Get-OrchRecords([string]$Ns, [string]$Source = 'cache') {
  return , @((Invoke-OrchGet "/API/PerfE2EState?ns=$Ns&source=$Source" 60).rows)
}

# Per-run-label counts (tasks recorded, ended, posts, min start / max end ticks) without shipping every record.
function Get-OrchSummary([string]$Ns, [string]$Source = 'cache') {
  $Map = @{}
  foreach ($R in @((Invoke-OrchGet "/API/PerfE2EState?ns=$Ns&source=$Source&summary=1" 60).runs)) { $Map[$R.run] = $R }
  return $Map
}

function Get-OrchTasks($Rows, [string]$Run) { return , @($Rows.Where({ $_.kind -eq 'T' -and $_.run -eq $Run })) }
function Get-OrchPosts($Rows, [string]$Run) { return , @($Rows.Where({ $_.kind -eq 'P' -and $_.run -eq $Run })) }
function Get-OrchRuns([string]$Name, [switch]$Detail) {
  return , @((Invoke-OrchGet "/API/PerfE2ERuns?name=$Name$(if ($Detail) { '&detail=1' })").runs)
}
function Get-OrchRunsByPrefix([string]$Prefix) { return , @((Invoke-OrchGet "/API/PerfE2ERuns?prefix=$Prefix" 60).runs) }
function Invoke-OrchBridge([string]$Op, [string]$Name = '') { Invoke-OrchGet "/API/PerfE2EBridge?op=$Op&name=$Name" }

# Poll $Condition until it returns something truthy or the timeout lapses. Locals are prefixed so they cannot
# shadow the caller's variables the condition reads (scriptblocks resolve variables dynamically).
function Wait-Orch([scriptblock]$Condition, [int]$TimeoutSec, [int]$IntervalMs = 500) {
  $WaitSw = [Diagnostics.Stopwatch]::StartNew()
  while ($true) {
    $WaitVal = try { & $Condition } catch { $null }
    if ($WaitVal) { return [pscustomobject]@{ ok = $true; sec = [math]::Round($WaitSw.Elapsed.TotalSeconds, 1); value = $WaitVal } }
    if ($WaitSw.Elapsed.TotalSeconds -ge $TimeoutSec) { break }
    Start-Sleep -Milliseconds $IntervalMs
  }
  return [pscustomobject]@{ ok = $false; sec = [math]::Round($WaitSw.Elapsed.TotalSeconds, 1); value = $null }
}

function Wait-OrchPosts([string]$Ns, [string[]]$Labels, [int]$TimeoutSec, [string]$Source = 'cache') {
  Wait-Orch {
    $S = Get-OrchSummary $Ns $Source
    if (@($Labels.Where({ -not $S[$_] -or $S[$_].posts -lt 1 })).Count -eq 0) { $true }
  } $TimeoutSec
}

function Wait-OrchRunsDone([string]$Name, [int]$Count, [int]$TimeoutSec, [int]$IntervalMs = 1000) {
  Wait-Orch {
    $R = Get-OrchRuns $Name
    if ($R.Count -ge $Count -and @($R.Where({ $_.phase -ne 'Done' })).Count -eq 0) { , $R }
  } $TimeoutSec $IntervalMs
}

function ConvertTo-OrchMs([long]$Ticks) { [math]::Round($Ticks / 10000) }

# Highest number of intervals open at once. Ends sort before starts at the same tick.
function Get-OrchMaxOverlap($Tasks) {
  $Events = [System.Collections.Generic.List[object]]::new()
  foreach ($T in $Tasks) {
    if ([long]$T.end -le 0) { continue }
    $Events.Add([pscustomobject]@{ t = [long]$T.start; d = 1 })
    $Events.Add([pscustomobject]@{ t = [long]$T.end; d = -1 })
  }
  $Max = 0; $Cur = 0
  foreach ($E in ($Events | Sort-Object t, d)) { $Cur = $Cur + $E.d; if ($Cur -gt $Max) { $Max = $Cur } }
  return $Max
}

function Get-OrchMin($Values) { ($Values | Measure-Object -Minimum).Minimum }
function Get-OrchMax($Values) { ($Values | Measure-Object -Maximum).Maximum }

function Invoke-OrchCheck([string]$Group, [scriptblock]$Body) {
  if ($OrchChecks -and $Group -notin $OrchChecks) { return }
  if ($OrchSkipped[$Group]) {
    foreach ($N in $OrchSkipped[$Group].names) { Add-Skip "orch-$Group" $N $OrchSkipped[$Group].reason }
    return
  }
  Info "orchestration: $Group ..."
  try { & $Body }
  catch { Add-Result "orch-$Group" 'check-error' $false '-' "exception: $($_.Exception.Message) @ line $($_.InvocationInfo.ScriptLineNumber)" }
}

function Get-OrchBig([int]$Kb) { ('0123456789abcdef' * ($Kb * 64)) + 'END' }

# Capability gate for the features built after a4d72cf.
$OrchCaps = try { Invoke-OrchBridge 'caps' } catch { $null }
$OrchHasNew = $OrchCaps -and ([int]$OrchCaps.queueFromFileParams -ge 11) -and $OrchCaps.startHasMaxConcurrency -and $OrchCaps.startHasStopOnFailure
if (-not $OrchHasNew) {
  $Why = "image lacks the feature (QueueOrchestrationFromFile has $($OrchCaps.queueFromFileParams) params; Start-CraftOrchestrator MaxConcurrency=$($OrchCaps.startHasMaxConcurrency) StopOnFailure=$($OrchCaps.startHasStopOnFailure))"
  $OrchSkipped['maxconc'] = @{ reason = $Why; names = @('limit-2', 'unlimited-reaches-pool', 'limit-1-lets-p1-in', 'ignored-when-sequential') }
  $OrchSkipped['stopfail'] = @{ reason = $Why; names = @('stops-at-failure', 'rest-cancelled', 'postexec-runs', 'default-runs-all') }
}
$OrchPool = [int](Invoke-OrchGet '/API/PerfAllocation').pool.bgTotal

# -- 1. PostExecution contract ----------------------------------------------------------------------------
Invoke-OrchCheck 'post' {
  $Id = New-OrchId; $Ns = "post-$Id"; $Name = "E2EPost-$Id"
  $null = Start-OrchRuns $Ns @(@{
      name = $Name; label = 'p'; tasks = 6; task = @{ holdms = 300; out = 'v' }; overrides = @{ '2' = @{ outKb = 80 } }
      post = @{ marker = "mk-$Id"; bigKb = 150; nested = @{ a = 1; b = @('x', 'y'); c = @{ d = 'e' } } }
    })
  $W = Wait-OrchPosts $Ns @('p') 90
  Start-Sleep -Seconds 3   # window in which a duplicate PostExecution would show up
  $Rows = Get-OrchRecords $Ns
  $T = Get-OrchTasks $Rows 'p'; $P = Get-OrchPosts $Rows 'p'; $H = (Get-OrchRuns $Name)[0]
  if (-not $W.ok -or $P.Count -eq 0) {
    Add-Result 'orch-post' 'postexec-ran' $false "$($W.sec)s" "no PostExecution within 90s; tasks=$($T.Count) status=$($H.status) phase=$($H.phase) post=$($H.postExecStatus)"
    return
  }
  $Post = $P[0]
  $Got = @($Post.idxs -split ',' | Sort-Object) -join ','
  $Exp = @(0..5 | ForEach-Object { "p/$_" } | Sort-Object) -join ','
  Add-Result 'orch-post' 'one-line-per-task' ($Post.lines -eq 6 -and $Got -eq $Exp) "$($W.sec)s" "lines=$($Post.lines) entries=$($Post.entries) idxs=$($Post.idxs) lineType=$($Post.firstType)"
  $Lens = @($Post.outLens -split ',' | ForEach-Object { [int]$_ } | Sort-Object) -join ','
  Add-Result 'orch-post' 'task-output-over-64k' ($Lens -eq '1,1,1,1,1,81920') '80KB' "output lengths=$($Post.outLens) (task 2 returns 80 KB)"
  $Big = Get-OrchBig 150
  $Sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Big)))
  Add-Result 'orch-post' 'params-over-64k' ($Post.bigLen -eq $Big.Length -and $Post.bigSha -eq $Sha) "$([math]::Round($Big.Length / 1KB))KB" "received len=$($Post.bigLen) expected=$($Big.Length) sha-match=$($Post.bigSha -eq $Sha)"
  $Nested = if ($Post.nested) { $Post.nested | ConvertFrom-Json } else { $null }
  $NestOk = $Nested -and $Nested.a -eq 1 -and (@($Nested.b) -join ',') -eq 'x,y' -and $Nested.c.d -eq 'e'
  Add-Result 'orch-post' 'params-intact' ($Post.marker -eq "mk-$Id" -and $NestOk) '-' "marker=$($Post.marker) nested=$($Post.nested)"
  $MaxEnd = Get-OrchMax $T.end
  $Gap = ConvertTo-OrchMs ($Post.ticks - $MaxEnd)
  $OnceOk = $P.Count -eq 1 -and $Post.ticks -ge $MaxEnd -and $T.Count -eq 6 -and $H.status -eq 'Completed' -and $H.postExecStatus -eq 'Completed'
  Add-Result 'orch-post' 'once-after-all-tasks' $OnceOk "+${Gap}ms" "posts=$($P.Count) tasks=$($T.Count) post-minus-last-task-end=${Gap}ms run=$($H.status)/$($H.postExecStatus)"
}

# -- 2. A failing task -------------------------------------------------------------------------------------
Invoke-OrchCheck 'fail' {
  $Id = New-OrchId; $Ns = "fail-$Id"; $Name = "E2EFail-$Id"
  $null = Start-OrchRuns $Ns @(@{ name = $Name; label = 'f'; tasks = 6; task = @{ holdms = 200 }; overrides = @{ '3' = @{ fail = $true } }; post = @{ marker = 'f' } })
  $W = Wait-OrchPosts $Ns @('f') 90
  Start-Sleep -Seconds 2
  $Rows = Get-OrchRecords $Ns
  $T = Get-OrchTasks $Rows 'f'; $P = Get-OrchPosts $Rows 'f'; $H = (Get-OrchRuns $Name -Detail)[0]
  $Failed = @($H.tasks.Where({ $_.status -eq 'Failed' }))
  $RunOk = $H.phase -eq 'Done' -and $H.status -eq 'CompletedWithErrors' -and $H.failed -eq 1 -and $H.done -eq 6 -and $H.total -eq 6 -and
    $Failed.Count -eq 1 -and $Failed[0].seq -eq 3 -and $Failed[0].error -match 'failed on purpose'
  Add-Result 'orch-fail' 'run-completes-with-errors' $RunOk "$($W.sec)s" "status=$($H.status) phase=$($H.phase) done=$($H.done)/$($H.total) failed=$($H.failed) failedSeq=$($Failed.seq) err='$($Failed.error)'"
  $Post = $P | Select-Object -First 1
  $PostOk = $P.Count -eq 1 -and $Post.lines -eq 5 -and ($Post.idxs -split ',') -notcontains 'f/3' -and $H.postExecStatus -eq 'Completed'
  Add-Result 'orch-fail' 'postexec-still-runs' $PostOk '-' "posts=$($P.Count) lines=$($Post.lines) idxs=$($Post.idxs) postExec=$($H.postExecStatus)"
  $PerIdx = @($T | Group-Object idx | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ','
  $Attempts = @($H.tasks | ForEach-Object { $_.attempt } | Sort-Object -Unique) -join ','
  $OnceOk = $T.Count -eq 6 -and @($T | Group-Object idx).Count -eq 6 -and $Attempts -eq '1'
  Add-Result 'orch-fail' 'every-task-ran-once' $OnceOk '-' "starts per idx=$PerIdx attempts=$Attempts"
}

# -- 3. Child-run gating ------------------------------------------------------------------------------------
Invoke-OrchCheck 'child' {
  $Id = New-OrchId; $Ns = "child-$Id"
  # Blocker for the collision-off child: a run of the child's name that is still going when the child is queued.
  $null = Start-OrchRuns $Ns @(@{ name = "E2EBlk-$Id"; label = 'blk'; tasks = 1; task = @{ holdms = 15000 } })
  $null = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['blk'].tasks -ge 1) { $true } } 30
  $null = Start-OrchRuns $Ns @(
    @{ name = "E2EChP-$Id"; label = 'chP'; tasks = 2; task = @{ holdms = 200 }; post = @{ marker = 'chP' }
      overrides = @{ '0' = @{ child = @{ name = "E2EChK-$Id"; label = 'chK'; tasks = 3; holdms = 3000 } } } }
    @{ name = "E2EZ-$Id"; label = 'z'; tasks = 2; post = @{ marker = 'z' }
      overrides = @{ '0' = @{ child = @{ name = "E2EZs-$Id"; label = 'zs'; tasks = 0 } }
                     '1' = @{ child = @{ name = "E2EZb-$Id"; label = 'zb'; tasks = 0; via = 'bridge' } } } }
    @{ name = "E2ECs-$Id"; label = 'cs'; tasks = 1; post = @{ marker = 'cs' }
      overrides = @{ '0' = @{ child = @{ name = "E2EBlk-$Id"; label = 'blk2'; tasks = 1; via = 'bridge'; allowCollision = $false } } } }
    @{ name = "E2ESelf-$Id"; label = 'self'; tasks = 1; post = @{ marker = 'self' }
      overrides = @{ '0' = @{ child = @{ name = "E2ESelf-$Id"; label = 'self2'; tasks = 1; holdms = 8000 } } } }
    @{ name = "E2EFol-$Id"; label = 'fol'; tasks = 1
      post = @{ marker = 'fol'; followOn = @{ name = "E2EFolK-$Id"; label = 'folK'; tasks = 1; holdms = 8000 } } }
  )
  $W = Wait-OrchPosts $Ns @('chP', 'z', 'cs', 'self', 'fol') 90
  $null = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['self2'].ended -ge 1 -and $S['folK'].ended -ge 1 -and $S['blk'].ended -ge 1) { $true } } 40
  $Rows = Get-OrchRecords $Ns
  $PostOf = @{}; foreach ($L in 'chP', 'z', 'cs', 'self', 'fol') { $PostOf[$L] = (Get-OrchPosts $Rows $L) | Select-Object -First 1 }

  # a) a child holds its parent's PostExecution until the child's last task finished
  $Kid = Get-OrchTasks $Rows 'chK'; $KidEnd = Get-OrchMax $Kid.end
  $Par = (Get-OrchRuns "E2EChP-$Id")[0]; $KidRun = (Get-OrchRuns "E2EChK-$Id")[0]
  $Gap = if ($PostOf['chP']) { ConvertTo-OrchMs ($PostOf['chP'].ticks - $KidEnd) } else { 'n/a' }
  $Ok = $PostOf['chP'] -and $Kid.Count -eq 3 -and @($Kid.Where({ $_.end -le 0 })).Count -eq 0 -and $PostOf['chP'].ticks -gt $KidEnd -and
    $Par.total -eq 3 -and $KidRun.parentRunKey -eq $Par.runKey
  Add-Result 'orch-child' 'child-gates-parent' $Ok "+${Gap}ms" "parent post - child last end=${Gap}ms childTasks=$($Kid.Count) parentTotal=$($Par.total) (2 tasks+1 child) childParentKey-match=$($KidRun.parentRunKey -eq $Par.runKey)"

  # b) a child that is never created releases its parent: via Start-CraftOrchestrator (0 tasks -> NoTasks, never
  #    reaches the bridge) and via the bridge (registered with the parent, then abandoned when the batch is empty)
  $ZRun = (Get-OrchRuns "E2EZ-$Id")[0]
  $Zs = @($Rows.Where({ $_.kind -eq 'Q' -and $_.run -eq 'zs' })) | Select-Object -First 1
  $NoKids = (Get-OrchRuns "E2EZs-$Id").Count + (Get-OrchRuns "E2EZb-$Id").Count
  $Ok = $PostOf['z'] -and $ZRun.phase -eq 'Done' -and $ZRun.status -eq 'Completed' -and $ZRun.total -eq 3 -and $ZRun.done -eq 3 -and
    $Zs.result -like '*-NoTasks' -and $NoKids -eq 0
  Add-Result 'orch-child' 'zero-task-child-releases' $Ok '-' "post=$([bool]$PostOf['z']) status=$($ZRun.status) total/done=$($ZRun.total)/$($ZRun.done) (2 tasks + 1 bridge-registered child) startResult=$($Zs.result) childRunsCreated=$NoKids"

  # c) a collision-off child skipped because its name is active releases the parent without waiting for the blocker
  $BlkEnd = Get-OrchMax (Get-OrchTasks $Rows 'blk').end
  $BlkRuns = Get-OrchRuns "E2EBlk-$Id"
  $Ok = $PostOf['cs'] -and $PostOf['cs'].ticks -lt $BlkEnd -and $BlkRuns.Count -eq 1 -and (Get-OrchTasks $Rows 'blk2').Count -eq 0
  $Lead = if ($PostOf['cs']) { ConvertTo-OrchMs ($BlkEnd - $PostOf['cs'].ticks) } else { 'n/a' }
  Add-Result 'orch-child' 'skipped-child-releases' $Ok "-${Lead}ms" "parent post ran ${Lead}ms before the blocker ended; runs named E2EBlk=$($BlkRuns.Count) skippedChildTasks=$((Get-OrchTasks $Rows 'blk2').Count)"

  # d) a run re-queueing its own name is not its child
  $Self2 = (Get-OrchTasks $Rows 'self2') | Select-Object -First 1
  $SelfRuns = Get-OrchRuns "E2ESelf-$Id"
  $Ok = $PostOf['self'] -and $Self2 -and $PostOf['self'].ticks -lt $Self2.end -and $SelfRuns.Count -eq 2 -and @($SelfRuns.Where({ $_.parentRunKey })).Count -eq 0
  $Lead = if ($PostOf['self'] -and $Self2) { ConvertTo-OrchMs ($Self2.end - $PostOf['self'].ticks) } else { 'n/a' }
  Add-Result 'orch-child' 'self-requeue-not-child' $Ok "-${Lead}ms" "parent post ${Lead}ms before the re-queued run ended; runs=$($SelfRuns.Count) withParent=$(@($SelfRuns.Where({ $_.parentRunKey })).Count)"

  # e) a run queued from a PostExecution is not a child (the parent is aggregating, not running tasks)
  $Fol = (Get-OrchRuns "E2EFol-$Id")[0]; $FolK = (Get-OrchTasks $Rows 'folK') | Select-Object -First 1; $FolKRun = (Get-OrchRuns "E2EFolK-$Id")[0]
  $Ok = $Fol.phase -eq 'Done' -and $FolK -and $Fol.completedTicks -lt $FolK.end -and -not $FolKRun.parentRunKey
  $Lead = if ($FolK) { ConvertTo-OrchMs ($FolK.end - $Fol.completedTicks) } else { 'n/a' }
  Add-Result 'orch-child' 'postexec-run-not-child' $Ok "-${Lead}ms" "parent finished ${Lead}ms before the follow-on ended; parent=$($Fol.status)/$($Fol.postExecStatus) followOnParent='$($FolKRun.parentRunKey)'"
  if (-not $W.ok) { Add-Result 'orch-child' 'all-parents-finished' $false "$($W.sec)s" "not every parent PostExecution ran within 90s: $((@('chP','z','cs','self','fol').Where({ -not $PostOf[$_] })) -join ',') missing" }
}

# -- 4. What a child inherits -------------------------------------------------------------------------------
Invoke-OrchCheck 'prio' {
  $Id = New-OrchId; $Ns = "prio-$Id"
  $null = Start-OrchRuns $Ns @(
    @{ name = "E2EPri-$Id"; label = 'pri'; Priority = 7; tasks = 2; post = @{ marker = 'pri' }
      overrides = @{ '0' = @{ child = @{ name = "E2EPriK1-$Id"; label = 'k1'; tasks = 1 } }
                     '1' = @{ child = @{ name = "E2EPriK2-$Id"; label = 'k2'; tasks = 1; priority = 2 } } } }
    @{ name = "E2ESq-$Id"; label = 'sq'; Sequential = $true; tasks = 2; task = @{ holdms = 200 }; post = @{ marker = 'sq' }
      overrides = @{ '0' = @{ child = @{ name = "E2ESqK-$Id"; label = 'sqk'; tasks = 3; holdms = 2000 } } } }
    @{ name = "E2ENc-$Id"; label = 'nc'; AllowCollision = $false; tasks = 2; post = @{ marker = 'nc' }
      overrides = @{ '0' = @{ child = @{ name = "E2ENcK-$Id"; label = 'nck'; tasks = 1; holdms = 2000 } }
                     '1' = @{ child = @{ name = "E2ENcK-$Id"; label = 'nck'; tasks = 1; holdms = 2000 } } } }
  )
  $W = Wait-OrchPosts $Ns @('pri', 'sq', 'nc') 90
  $Rows = Get-OrchRecords $Ns
  $ParPrio = @((Get-OrchTasks $Rows 'pri').prio | Sort-Object -Unique) -join ','
  $K1 = (Get-OrchTasks $Rows 'k1') | Select-Object -First 1; $K1Run = (Get-OrchRuns "E2EPriK1-$Id")[0]
  Add-Result 'orch-prio' 'child-inherits-priority' ($K1.prio -eq 7 -and $K1Run.priority -eq 7 -and $ParPrio -eq '7') "$($W.sec)s" "parent task prio=$ParPrio; child (no Priority) task ctx prio=$($K1.prio) run P=$($K1Run.priority)"
  $K2 = (Get-OrchTasks $Rows 'k2') | Select-Object -First 1; $K2Run = (Get-OrchRuns "E2EPriK2-$Id")[0]
  Add-Result 'orch-prio' 'explicit-child-priority-wins' ($K2.prio -eq 2 -and $K2Run.priority -eq 2) '-' "child Priority=2 under a P7 parent: task ctx prio=$($K2.prio) run P=$($K2Run.priority)"
  $SqRun = (Get-OrchRuns "E2ESq-$Id")[0]; $SqkRun = (Get-OrchRuns "E2ESqK-$Id")[0]; $Sqk = Get-OrchTasks $Rows 'sqk'
  $Ov = Get-OrchMaxOverlap $Sqk
  Add-Result 'orch-prio' 'child-not-sequential' ($SqRun.sequential -eq 1 -and $SqkRun.sequential -eq 0 -and $Ov -ge 2) "overlap=$Ov" "parent sequential=$($SqRun.sequential) child sequential=$($SqkRun.sequential) child tasks max concurrent=$Ov (3 tasks x 2s)"
  $NcRuns = Get-OrchRuns "E2ENcK-$Id"
  $Ok = $NcRuns.Count -eq 2 -and @($NcRuns.Where({ $_.phase -ne 'Done' })).Count -eq 0
  Add-Result 'orch-prio' 'child-not-collision-off' $Ok '-' "collision-off parent queued two same-name children: runs created=$($NcRuns.Count) done=$(@($NcRuns.Where({ $_.phase -eq 'Done' })).Count)"
}

# -- 5. Collisions --------------------------------------------------------------------------------------------
Invoke-OrchCheck 'collide' {
  $Id = New-OrchId; $Ns = "col-$Id"
  $null = Start-OrchRuns $Ns @(
    @{ name = "E2EStk-$Id"; label = 'stkA'; tasks = 3; task = @{ holdms = 1000 }; post = @{ marker = 'a' } }
    @{ name = "E2EStk-$Id"; label = 'stkB'; tasks = 3; task = @{ holdms = 1000 }; post = @{ marker = 'b' } }
  )
  $W = Wait-OrchPosts $Ns @('stkA', 'stkB') 60
  $Rows = Get-OrchRecords $Ns
  $Stk = Get-OrchRuns "E2EStk-$Id"
  $Ok = $W.ok -and $Stk.Count -eq 2 -and @($Stk.Where({ $_.status -eq 'Completed' })).Count -eq 2 -and
    ((Get-OrchTasks $Rows 'stkA').Count + (Get-OrchTasks $Rows 'stkB').Count) -eq 6
  Add-Result 'orch-collide' 'default-stacks' $Ok "$($W.sec)s" "same-name runs=$($Stk.Count) completed=$(@($Stk.Where({ $_.status -eq 'Completed' })).Count) tasks=$((Get-OrchTasks $Rows 'stkA').Count)+$((Get-OrchTasks $Rows 'stkB').Count) posts=$((Get-OrchPosts $Rows 'stkA').Count)+$((Get-OrchPosts $Rows 'stkB').Count)"

  $Name = "E2ENoC-$Id"
  $R1 = (Start-OrchRuns $Ns @(@{ name = $Name; label = 'noc1'; AllowCollision = $false; tasks = 2; task = @{ holdms = 4000 }; post = @{ marker = '1' } }))[0]
  $Active = (Invoke-OrchBridge 'active' $Name).active
  $R2 = (Start-OrchRuns $Ns @(@{ name = $Name; label = 'noc2'; AllowCollision = $false; tasks = 2; post = @{ marker = '2' } }))[0]
  Add-Result 'orch-collide' 'active-while-running' ($Active -eq $true) '-' "IsRunActive=$Active right after queueing"
  Add-Result 'orch-collide' 'collision-off-skips' ($R1.result -eq "Craft-$Name" -and $R2.result -eq "Craft-$Name-Skipped" -and $R2.warning) '-' "first=$($R1.result) second=$($R2.result) warning='$($R2.warning)'"
  $null = Wait-OrchPosts $Ns @('noc1') 60
  $Idle = Wait-Orch { if ((Invoke-OrchBridge 'active' $Name).active -eq $false) { $true } } 15
  $R3 = (Start-OrchRuns $Ns @(@{ name = $Name; label = 'noc3'; AllowCollision = $false; tasks = 1; post = @{ marker = '3' } }))[0]
  $W3 = Wait-OrchPosts $Ns @('noc3') 60
  $Rows = Get-OrchRecords $Ns
  $Runs = Get-OrchRuns $Name
  $Ok = $Idle.ok -and $R3.result -eq "Craft-$Name" -and $W3.ok -and $Runs.Count -eq 2 -and (Get-OrchTasks $Rows 'noc2').Count -eq 0
  Add-Result 'orch-collide' 'restarts-after-finish' $Ok "$($W3.sec)s" "inactive after finish=$($Idle.ok) third=$($R3.result) runs of name=$($Runs.Count) (skipped one never created; its tasks=$((Get-OrchTasks $Rows 'noc2').Count))"
}

# -- 6. Parent attribution with overlapping same-name parents ----------------------------------------------
Invoke-OrchCheck 'attrib' {
  $Id = New-OrchId; $Ns = "att-$Id"
  $null = Start-OrchRuns $Ns @(
    @{ name = "E2EOvl-$Id"; label = 'ovlA'; tasks = 1; post = @{ marker = 'A' }; overrides = @{ '0' = @{ child = @{ name = "E2EOvlKA-$Id"; label = 'kA'; tasks = 1; holdms = 2000 } } } }
    @{ name = "E2EOvl-$Id"; label = 'ovlB'; tasks = 1; post = @{ marker = 'B' }; overrides = @{ '0' = @{ child = @{ name = "E2EOvlKB-$Id"; label = 'kB'; tasks = 1; holdms = 9000 } } } }
  )
  $W = Wait-OrchPosts $Ns @('ovlA', 'ovlB') 60
  $Rows = Get-OrchRecords $Ns
  $PA = (Get-OrchPosts $Rows 'ovlA') | Select-Object -First 1; $PB = (Get-OrchPosts $Rows 'ovlB') | Select-Object -First 1
  $KA = (Get-OrchTasks $Rows 'kA') | Select-Object -First 1; $KB = (Get-OrchTasks $Rows 'kB') | Select-Object -First 1
  $KeyA = ((Get-OrchTasks $Rows 'ovlA') | Select-Object -First 1).runKey; $KeyB = ((Get-OrchTasks $Rows 'ovlB') | Select-Object -First 1).runKey
  $KARun = (Get-OrchRuns "E2EOvlKA-$Id")[0]; $KBRun = (Get-OrchRuns "E2EOvlKB-$Id")[0]
  $Ok = $PA -and $PB -and $PA.ticks -gt $KA.end -and $PB.ticks -gt $KB.end -and $PA.ticks -lt $KB.end
  Add-Result 'orch-attrib' 'waits-for-own-child' $Ok "$($W.sec)s" ("A post-kA end={0}ms, B post-kB end={1}ms, A post before kB end by {2}ms" -f
      $(if ($PA) { ConvertTo-OrchMs ($PA.ticks - $KA.end) }), $(if ($PB) { ConvertTo-OrchMs ($PB.ticks - $KB.end) }), $(if ($PA) { ConvertTo-OrchMs ($KB.end - $PA.ticks) }))
  Add-Result 'orch-attrib' 'child-linked-to-exact-run' ($KeyA -ne $KeyB -and $KARun.parentRunKey -eq $KeyA -and $KBRun.parentRunKey -eq $KeyB) '-' "kA parent=$($KARun.parentRunKey) (A=$KeyA) kB parent=$($KBRun.parentRunKey) (B=$KeyB)"
}

# -- 7. CancelRun by name -------------------------------------------------------------------------------------
Invoke-OrchCheck 'cancel' {
  $Id = New-OrchId; $Ns = "can-$Id"; $Name = "E2ECan-$Id"
  $null = Start-OrchRuns $Ns @(
    @{ name = $Name; label = 'canA'; tasks = 8; task = @{ holdms = 2500 }; post = @{ marker = 'A' } }
    @{ name = $Name; label = 'canB'; tasks = 8; task = @{ holdms = 2500 }; post = @{ marker = 'B' } }
  )
  $null = Wait-Orch { $S = Get-OrchSummary $Ns; if (($S['canA'].tasks + $S['canB'].tasks) -ge 3) { $true } } 30 250
  $Count = (Invoke-OrchBridge 'cancel' $Name).cancelled
  $D = Wait-OrchRunsDone $Name 2 90
  $null = Wait-OrchPosts $Ns @('canA', 'canB') 30
  $Rows = Get-OrchRecords $Ns
  $Runs = Get-OrchRuns $Name
  $Desc = @($Runs | ForEach-Object { "$($_.status) done=$($_.done) cancelled=$($_.cancelled) failed=$($_.failed)" }) -join '; '
  $Ok = $D.ok -and $Count -gt 0 -and $Runs.Count -eq 2 -and @($Runs.Where({ $_.status -eq 'CompletedWithErrors' -and $_.cancelled -gt 0 })).Count -eq 2
  Add-Result 'orch-cancel' 'cancels-every-outing' $Ok "$($D.sec)s" "CancelRun returned $Count; $Desc"
  # The run keys are in start order; the outings were queued A then B.
  $PostOk = $true; $Parts = [System.Collections.Generic.List[string]]::new()
  $I = 0
  foreach ($L in 'canA', 'canB') {
    $P = Get-OrchPosts $Rows $L; $T = Get-OrchTasks $Rows $L; $Run = $Runs[$I]; $I = $I + 1
    $Ran = $Run.done - $Run.cancelled - $Run.failed
    $One = $P.Count -eq 1 -and $P[0].lines -eq $Ran -and $T.Count -eq $Ran -and $Run.postExecStatus -eq 'Completed'
    if (-not $One) { $PostOk = $false }
    $Parts.Add("${L}: posts=$($P.Count) lines=$($P[0].lines) started=$($T.Count) ran=$Ran postExec=$($Run.postExecStatus)")
  }
  Add-Result 'orch-cancel' 'postexec-still-runs' $PostOk '-' ($Parts -join '; ')
}

# -- 8. Sequential runs ---------------------------------------------------------------------------------------
Invoke-OrchCheck 'seq' {
  $Enq = Invoke-OrchGet '/API/PerfSeqWorkerEnqueue?runs=4&steps=5&holdms=500'
  $W = Wait-Orch { $R = Invoke-OrchGet '/API/PerfSeqWorkerResult'; if ($R.count -ge 20) { $R } } 90
  Start-Sleep -Milliseconds 500
  $Rows = @((Invoke-OrchGet '/API/PerfSeqWorkerResult').rows)
  $OrderOk = $true; $PinOk = $true; $Workers = [System.Collections.Generic.List[string]]::new(); $Parts = [System.Collections.Generic.List[string]]::new()
  foreach ($N in @($Enq.names)) {
    $Steps = @($Rows.Where({ $_.run -eq $N }) | Sort-Object ticks)
    $Order = @($Steps.idx) -join ''
    $Ws = @($Steps.worker | Sort-Object -Unique)
    if ($Order -ne '01234') { $OrderOk = $false }
    if ($Ws.Count -ne 1) { $PinOk = $false }
    foreach ($X in $Ws) { $Workers.Add($X) }
    $Parts.Add("$($N.Substring(0, 4)):$Order@$($Ws -join '/')")
  }
  $Distinct = @($Workers | Sort-Object -Unique).Count
  Add-Result 'orch-seq' 'payload-order' ($W.ok -and $OrderOk -and $Rows.Count -eq 20) "$($W.sec)s" "steps=$($Rows.Count) $($Parts -join ' ')"
  Add-Result 'orch-seq' 'one-pinned-worker-per-run' ($PinOk -and $Distinct -eq [math]::Min(4, $OrchPool)) '-' "workers per run all 1=$PinOk; distinct workers across 4 concurrent runs=$Distinct"

  $Id = New-OrchId; $Ns = "seq-$Id"; $Name = "E2ESeqF-$Id"
  $null = Start-OrchRuns $Ns @(@{ name = $Name; label = 'sf'; Sequential = $true; tasks = 5; task = @{ holdms = 300 }; overrides = @{ '2' = @{ fail = $true } }; post = @{ marker = 'sf' } })
  $W = Wait-OrchPosts $Ns @('sf') 60
  $Rows = Get-OrchRecords $Ns
  $T = @((Get-OrchTasks $Rows 'sf') | Sort-Object start); $P = (Get-OrchPosts $Rows 'sf') | Select-Object -First 1; $H = (Get-OrchRuns $Name)[0]
  $Ok = $T.Count -eq 5 -and (@($T.idx) -join '') -eq '01234' -and @($T.worker | Sort-Object -Unique).Count -eq 1 -and (Get-OrchMaxOverlap $T) -eq 1 -and
    @($T.Where({ $_.end -le 0 })).Count -eq 0 -and $H.status -eq 'CompletedWithErrors' -and $H.failed -eq 1 -and $H.done -eq 5
  Add-Result 'orch-seq' 'failed-step-continues' $Ok "$($W.sec)s" "order=$(@($T.idx) -join '') workers=$(@($T.worker | Sort-Object -Unique) -join '/') overlap=$(Get-OrchMaxOverlap $T) status=$($H.status) done=$($H.done) failed=$($H.failed)"
  # Every step that succeeded must reach the PostExecution, including the one right after the failed step.
  $Got = @($P.idxs -split ',' | Sort-Object) -join ','
  Add-Result 'orch-seq' 'postexec-gets-every-success' ($P.lines -eq 4 -and $Got -eq 'sf/0,sf/1,sf/3,sf/4') '-' "expected sf/0,sf/1,sf/3,sf/4 (step 2 throws); PostExecution received lines=$($P.lines) idxs=$($P.idxs)"

  # Every step runs inside the one job that drives the run, but worker stats and the queue page must show
  # each step on its own: the worker under the step it is on, the queue with one task per step.
  $Id = New-OrchId; $Ns = "seqv-$Id"; $Name = "E2ESeqV-$Id"
  $null = Start-OrchRuns $Ns @(@{ name = $Name; label = 'sv'; Sequential = $true; tasks = 4; task = @{ holdms = 1500 } })
  $Labels = [System.Collections.Generic.HashSet[string]]::new()
  $null = Wait-Orch {
    foreach ($B in @((Invoke-OrchBridge 'workers').busy)) { if ($B.fn -like "$Name-*") { [void]$Labels.Add($B.fn) } }
    $S = Get-OrchSummary $Ns; if ($S['sv'].ended -ge 4) { $true }
  } 60 250
  $Q = Wait-Orch { $E = @((Invoke-OrchBridge 'queue' $Name).entries)[0]; if ($E.status -eq 'Completed') { $E } } 30
  $E = $Q.value
  Add-Result 'orch-seq' 'each-step-visible' ($Labels.Count -ge 3 -and $E.total -eq 4 -and $E.completed -eq 4 -and @($E.tasks).Count -eq 4) '-' "worker labels seen=$($Labels.Count) (of 4 steps); queue total=$($E.total) completed=$($E.completed) tasks listed=$(@($E.tasks).Count)"
}

# -- 9. Priority and start-order ------------------------------------------------------------------------------
# A hold run of pool+3 tasks fills every worker and leaves 3 claims in the local buffer (above the pump's low
# water mark of 2), so nothing else is claimed until the hold wave ends. The runs under test are queued in one
# call while that is the case; whatever is claimed first after it is down to the store's ordering.
function Start-OrchSaturation([string]$Ns, [string]$Name) {
  $null = Start-OrchRuns $Ns @(@{ name = $Name; label = 'hold'; tasks = ($OrchPool + 3); task = @{ holdms = 6000 } })
  Wait-Orch {
    $S = Get-OrchSummary $Ns
    $A = Invoke-OrchGet '/API/PerfAllocation'
    if ($S['hold'].tasks -ge $OrchPool -and [int]$A.jm.queued -ge 3) { $true }
  } 30 250
}

Invoke-OrchCheck 'order' {
  $Id = New-OrchId; $Ns = "ord-$Id"
  $Sat = Start-OrchSaturation $Ns "E2EHold-$Id"
  $null = Start-OrchRuns $Ns @(
    @{ name = "E2EP9-$Id"; label = 'p9'; Priority = 9; tasks = 4; task = @{ holdms = 300 } }
    @{ name = "E2EP1-$Id"; label = 'p1'; Priority = 1; tasks = 4; task = @{ holdms = 300 } }
  )
  $W = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['p9'].ended -ge 4 -and $S['p1'].ended -ge 4) { $true } } 90
  $Rows = Get-OrchRecords $Ns
  $P1 = Get-OrchTasks $Rows 'p1'; $P9 = Get-OrchTasks $Rows 'p9'
  $Ok = $Sat.ok -and $W.ok -and (Get-OrchMax $P1.start) -lt (Get-OrchMin $P9.start)
  Add-Result 'orch-order' 'lower-priority-first' $Ok ("{0}ms" -f (ConvertTo-OrchMs ((Get-OrchMin $P9.start) - (Get-OrchMax $P1.start)))) "saturated=$($Sat.ok); P9 queued first, then P1: last P1 start precedes first P9 start by $(ConvertTo-OrchMs ((Get-OrchMin $P9.start) - (Get-OrchMax $P1.start)))ms"

  $Id = New-OrchId; $Ns = "ordb-$Id"
  $Sat = Start-OrchSaturation $Ns "E2EHold-$Id"
  $null = Start-OrchRuns $Ns @(
    @{ name = "E2EZulu-$Id"; label = 'zulu'; Priority = 6; tasks = 4; task = @{ holdms = 300 } }
    @{ name = "E2EAlpha-$Id"; label = 'alpha'; Priority = 6; tasks = 4; task = @{ holdms = 300 } }
  )
  $W = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['zulu'].ended -ge 4 -and $S['alpha'].ended -ge 4) { $true } } 90
  $S = Get-OrchSummary $Ns
  $Lead = ConvertTo-OrchMs ($S['alpha'].minStart - $S['zulu'].minStart)
  Add-Result 'orch-order' 'older-run-first-in-band' ($Sat.ok -and $W.ok -and $S['zulu'].minStart -lt $S['alpha'].minStart) "${Lead}ms" "saturated=$($Sat.ok); Zulu queued before Alpha (same band): Zulu first start leads Alpha by ${Lead}ms"
}

# -- 11. Status consistency ---------------------------------------------------------------------------------
Invoke-OrchCheck 'status' {
  $Id = New-OrchId; $Ns = "st-$Id"; $Name = "E2EStat-$Id"
  $null = Start-OrchRuns $Ns @(@{ name = $Name; label = 'st'; tasks = 200; task = @{ holdms = 300 } })
  $Samples = 0; $Bad = [System.Collections.Generic.List[string]]::new(); $SumBad = [System.Collections.Generic.List[string]]::new()
  $Sw = [Diagnostics.Stopwatch]::StartNew()
  while ($Sw.Elapsed.TotalSeconds -lt 120) {
    $H = (Get-OrchRuns $Name)[0]
    if ($H.phase -eq 'Done') { break }
    $Sm = (Invoke-OrchBridge 'summaries' $Name).runs | Select-Object -First 1
    $G = Invoke-OrchBridge 'summary'
    if ($Sm -and ($Sm.queued + $Sm.running) -gt 0) {
      $Samples = $Samples + 1
      if ($Sm.total -ne 200 -or ($Sm.completed + $Sm.queued + $Sm.running) -gt $Sm.total) {
        $Bad.Add("t=$([math]::Round($Sw.Elapsed.TotalSeconds,1))s total=$($Sm.total) c=$($Sm.completed) q=$($Sm.queued) r=$($Sm.running)")
      }
      # The global summary also counts unrelated local work (the e2e timer, leftovers of other checks), so bound
      # the durable part: what is waiting in storage can never exceed this run's 200 tasks.
      if ($G.jobsQueuedDurable -gt 200 -or ($G.jobsQueued - $G.jobsQueuedLocal) -ne $G.jobsQueuedDurable) { $SumBad.Add("q=$($G.jobsQueued) local=$($G.jobsQueuedLocal) durable=$($G.jobsQueuedDurable) a=$($G.jobsActive)") }
    }
    Start-Sleep -Milliseconds 400
  }
  $Done = (Get-OrchRuns $Name)[0]
  Add-Result 'orch-status' 'summaries-consistent' ($Samples -ge 5 -and $Bad.Count -eq 0) "$Samples samples" "in-flight samples=$Samples violations=$($Bad.Count) $(@($Bad | Select-Object -First 3) -join ' | ')"
  Add-Result 'orch-status' 'summary-bounded' ($Samples -ge 5 -and $SumBad.Count -eq 0) '-' "GetSummary durable queue > batch in $($SumBad.Count) samples $(@($SumBad | Select-Object -First 3) -join ' | ')"
  $Left = Wait-Orch {
    $Sm = (Invoke-OrchBridge 'summaries' $Name).runs | Select-Object -First 1
    if ((-not $Sm -or ($Sm.queued + $Sm.running) -eq 0) -and (Invoke-OrchBridge 'active' $Name).active -eq $false) { $true }
  } 20
  $Sm = (Invoke-OrchBridge 'summaries' $Name).runs | Select-Object -First 1
  $S = Get-OrchSummary $Ns
  $Ok = $Done.phase -eq 'Done' -and $Done.status -eq 'Completed' -and $Done.done -eq 200 -and $S['st'].tasks -eq 200 -and $Left.ok
  Add-Result 'orch-status' 'leaves-active-set' $Ok "$([math]::Round($Sw.Elapsed.TotalSeconds,1))s" "run=$($Done.status) done=$($Done.done) recorded=$($S['st'].tasks); after: summary q=$($Sm.queued) r=$($Sm.running) c=$($Sm.completed) total=$($Sm.total) active=$(-not $Left.ok)"
}

# -- 13. MaxConcurrency (gated) -------------------------------------------------------------------------------
Invoke-OrchCheck 'maxconc' {
  $Id = New-OrchId; $Ns = "mc-$Id"
  $null = Start-OrchRuns $Ns @(@{ name = "E2EMc2-$Id"; label = 'mc2'; MaxConcurrency = 2; tasks = 12; task = @{ holdms = 1500 }; post = @{ marker = 'mc2' } })
  $W = Wait-OrchPosts $Ns @('mc2') 120
  $Ov = Get-OrchMaxOverlap (Get-OrchTasks (Get-OrchRecords $Ns) 'mc2')
  Add-Result 'orch-maxconc' 'limit-2' ($W.ok -and $Ov -eq 2) "$($W.sec)s" "MaxConcurrency=2, 12 tasks x 1.5s: max concurrent=$Ov"

  $null = Start-OrchRuns $Ns @(@{ name = "E2EMc0-$Id"; label = 'mc0'; MaxConcurrency = 0; tasks = 12; task = @{ holdms = 1500 }; post = @{ marker = 'mc0' } })
  $W = Wait-OrchPosts $Ns @('mc0') 120
  $Ov = Get-OrchMaxOverlap (Get-OrchTasks (Get-OrchRecords $Ns) 'mc0')
  Add-Result 'orch-maxconc' 'unlimited-reaches-pool' ($W.ok -and $Ov -eq $OrchPool) "$($W.sec)s" "MaxConcurrency=0: max concurrent=$Ov pool=$OrchPool"

  $null = Start-OrchRuns $Ns @(@{ name = "E2EMc1-$Id"; label = 'mc1'; MaxConcurrency = 1; tasks = 6; task = @{ holdms = 1000 }; post = @{ marker = 'mc1' } })
  $null = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['mc1'].tasks -ge 2) { $true } } 30 250
  $null = Start-OrchRuns $Ns @(@{ name = "E2EMcP1-$Id"; label = 'mcp1'; Priority = 1; tasks = 1 })
  $W = Wait-OrchPosts $Ns @('mc1') 60
  $Rows = Get-OrchRecords $Ns
  $Mc1 = Get-OrchTasks $Rows 'mc1'; $P1 = (Get-OrchTasks $Rows 'mcp1') | Select-Object -First 1
  $Ok = $W.ok -and (Get-OrchMaxOverlap $Mc1) -eq 1 -and $P1 -and $P1.start -lt (Get-OrchMax $Mc1.start)
  Add-Result 'orch-maxconc' 'limit-1-lets-p1-in' $Ok '-' "N=1 overlap=$(Get-OrchMaxOverlap $Mc1); P1 task started $(if ($P1) { ConvertTo-OrchMs ((Get-OrchMax $Mc1.start) - $P1.start) })ms before the N=1 run's last step"

  $R = (Start-OrchRuns $Ns @(@{ name = "E2EMcSq-$Id"; label = 'mcsq'; Sequential = $true; MaxConcurrency = 3; tasks = 3; task = @{ holdms = 300 }; post = @{ marker = 'mcsq' } }))[0]
  $W = Wait-OrchPosts $Ns @('mcsq') 60
  $Sq = Get-OrchTasks (Get-OrchRecords $Ns) 'mcsq'
  $Ok = $W.ok -and $R.warning -match 'MaxConcurrency' -and (Get-OrchMaxOverlap $Sq) -eq 1 -and $Sq.Count -eq 3
  Add-Result 'orch-maxconc' 'ignored-when-sequential' $Ok '-' "result=$($R.result) warning='$($R.warning)' overlap=$(Get-OrchMaxOverlap $Sq) steps=$($Sq.Count)"
}

# -- 14. StopOnFailure (gated) --------------------------------------------------------------------------------
Invoke-OrchCheck 'stopfail' {
  $Id = New-OrchId; $Ns = "sof-$Id"; $Name = "E2ESof-$Id"
  $null = Start-OrchRuns $Ns @(@{ name = $Name; label = 'sof'; Sequential = $true; StopOnFailure = $true; tasks = 5; task = @{ holdms = 200 }; overrides = @{ '2' = @{ fail = $true } }; post = @{ marker = 'sof' } })
  $W = Wait-OrchPosts $Ns @('sof') 60
  Start-Sleep -Seconds 2
  $Rows = Get-OrchRecords $Ns
  $T = @((Get-OrchTasks $Rows 'sof') | Sort-Object start); $P = (Get-OrchPosts $Rows 'sof') | Select-Object -First 1; $H = (Get-OrchRuns $Name -Detail)[0]
  Add-Result 'orch-stopfail' 'stops-at-failure' ((@($T.idx) -join '') -eq '012') "$($W.sec)s" "steps that ran=$(@($T.idx) -join ',')"
  $Cancelled = @($H.tasks.Where({ $_.status -eq 'Cancelled' }).seq) -join ','
  Add-Result 'orch-stopfail' 'rest-cancelled' ($Cancelled -eq '3,4' -and $H.status -eq 'CompletedWithErrors') '-' "cancelled seqs=$Cancelled status=$($H.status) failed=$($H.failed) cancelled=$($H.cancelled)"
  Add-Result 'orch-stopfail' 'postexec-runs' ($P -and $P.lines -eq 2 -and $H.postExecStatus -eq 'Completed') '-' "posts=$(@(Get-OrchPosts $Rows 'sof').Count) lines=$($P.lines) idxs=$($P.idxs) postExec=$($H.postExecStatus)"
  $null = Start-OrchRuns $Ns @(@{ name = "E2ESofD-$Id"; label = 'sofd'; Sequential = $true; tasks = 5; task = @{ holdms = 200 }; overrides = @{ '2' = @{ fail = $true } }; post = @{ marker = 'sofd' } })
  $W = Wait-OrchPosts $Ns @('sofd') 60
  $T = Get-OrchTasks (Get-OrchRecords $Ns) 'sofd'
  Add-Result 'orch-stopfail' 'default-runs-all' ($T.Count -eq 5) "$($W.sec)s" "without StopOnFailure steps that ran=$($T.Count)"
}

# -- 12. Performance gates -------------------------------------------------------------------------------------
Invoke-OrchCheck 'perf' {
  $Id = New-OrchId; $Ns = "pf-$Id"
  $Sw = [Diagnostics.Stopwatch]::StartNew()
  $null = Start-OrchRuns $Ns @(@{ name = "E2EFan-$Id"; label = 'fan'; tasks = 1000; post = @{ marker = 'fan' } })
  $W = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['fan'].posts -ge 1) { $S['fan'] } } ($OrchGates.Fanout1000Sec + 60) 500
  $Sec = [math]::Round($Sw.Elapsed.TotalSeconds, 1)
  $Rows = Get-OrchRecords $Ns
  $T = Get-OrchTasks $Rows 'fan'; $P = (Get-OrchPosts $Rows 'fan') | Select-Object -First 1
  $Once = $T.Count -eq 1000 -and @($T | Group-Object idx).Count -eq 1000 -and $P.lines -eq 1000
  Add-Result 'orch-perf' 'fanout-1000' ($W.ok -and $Once -and $Sec -le $OrchGates.Fanout1000Sec) "${Sec}s" ("{0:N0} tasks/s; recorded={1} distinct={2} postLines={3}; gate {4}s" -f (1000 / [math]::Max(0.1, $Sec)), $T.Count, @($T | Group-Object idx).Count, $P.lines, $OrchGates.Fanout1000Sec)
  $null = Invoke-OrchGet "/API/PerfE2EState?ns=$Ns&wipe=1"

  $Id = New-OrchId; $Ns = "pm-$Id"
  $Runs = @(for ($I = 0; $I -lt 300; $I++) { @{ name = "E2EMany-$Id-$I"; label = "m$I"; tasks = 1 } })
  $Sw = [Diagnostics.Stopwatch]::StartNew()
  $null = Start-OrchRuns $Ns $Runs -TimeoutSec 300
  $W = Wait-Orch { $R = Get-OrchRunsByPrefix "E2EMany-$Id-"; if ($R.Count -ge 300 -and @($R.Where({ $_.phase -ne 'Done' })).Count -eq 0) { , $R } } ($OrchGates.ManyRuns300Sec + 60) 1000
  $Sec = [math]::Round($Sw.Elapsed.TotalSeconds, 1)
  $R = Get-OrchRunsByPrefix "E2EMany-$Id-"
  $S = Get-OrchSummary $Ns
  $Ran = @($S.Values.Where({ $_.tasks -eq 1 })).Count
  Add-Result 'orch-perf' 'runs-300' ($W.ok -and $Ran -eq 300 -and $Sec -le $OrchGates.ManyRuns300Sec) "${Sec}s" "runs=$($R.Count) done=$(@($R.Where({ $_.phase -eq 'Done' })).Count) tasks ran once=$Ran; gate $($OrchGates.ManyRuns300Sec)s"
  $null = Invoke-OrchGet "/API/PerfE2EState?ns=$Ns&wipe=1"

  # Storage cost between sequential steps: each step's finish and the next step's claim share one transaction.
  $Id = New-OrchId; $Ns = "ps-$Id"
  $null = Start-OrchRuns $Ns @(@{ name = "E2ESeqPerf-$Id"; label = 'sq'; Sequential = $true; tasks = 100 })
  $W = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['sq'].ended -ge 100) { $S['sq'] } } 120 250
  $StepMs = if ($W.ok) { [math]::Round((ConvertTo-OrchMs ($W.value.maxEnd - $W.value.minStart)) / 99, 1) } else { -1 }
  Add-Result 'orch-perf' 'sequential-step' ($W.ok -and $W.value.tasks -eq 100 -and $StepMs -le $OrchGates.SeqStepMs) "${StepMs}ms" "100 steps, first start -> last end per step ${StepMs}ms; gate $($OrchGates.SeqStepMs)ms"
  $null = Invoke-OrchGet "/API/PerfE2EState?ns=$Ns&wipe=1"

  # An idle pump backs off its poll to JobQueueIdlePollIntervalMs (10s default), so a run queued into an idle
  # engine waits for the next poll. Pin that bound, then keep the pump warm (a held task in flight keeps it on
  # the 1s poll) so the size comparison below measures claiming, not the idle backoff.
  $Id = New-OrchId; $Ns = "pt-$Id"
  Start-Sleep -Seconds 20
  $EIdle = (Start-OrchRuns $Ns @(@{ name = "E2ETfIdle-$Id"; label = 'tfidle'; tasks = 1 }))[0]
  $WIdle = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['tfidle'].tasks -ge 1) { $S['tfidle'] } } 60 100
  $TtfsIdle = if ($WIdle.ok) { ConvertTo-OrchMs ($WIdle.value.minStart - $EIdle.enqueueTicks) } else { -1 }
  Add-Result 'orch-perf' 'idle-claim-latency' ($WIdle.ok -and $TtfsIdle -le $OrchGates.IdleClaimMs) "${TtfsIdle}ms" "1-task run queued into an idle engine -> first start ${TtfsIdle}ms; gate $($OrchGates.IdleClaimMs)ms (idle poll cap 10s)"
  $null = Start-OrchRuns $Ns @(@{ name = "E2ETfKeep-$Id"; label = 'keep'; tasks = 1; task = @{ holdms = 60000 } })
  $null = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['keep'].tasks -ge 1) { $true } } 30 250
  Start-Sleep -Seconds 2
  $E10 = (Start-OrchRuns $Ns @(@{ name = "E2ETf10-$Id"; label = 'tf10'; tasks = 10 }))[0]
  $W10 = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['tf10'].tasks -ge 1) { $S['tf10'] } } 60 100
  $Ttfs10 = if ($W10.ok) { ConvertTo-OrchMs ($W10.value.minStart - $E10.enqueueTicks) } else { -1 }
  $null = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['tf10'].ended -ge 10) { $true } } 60
  $E5k = (Start-OrchRuns $Ns @(@{ name = "E2ETf5k-$Id"; label = 'tf5k'; tasks = 5000 }) -TimeoutSec 300)[0]
  $W5k = Wait-Orch { $S = Get-OrchSummary $Ns; if ($S['tf5k'].tasks -ge 1) { $S['tf5k'] } } 180 100
  $Ttfs5k = if ($W5k.ok) { ConvertTo-OrchMs ($W5k.value.minStart - $E5k.enqueueTicks) } else { -1 }
  Add-Result 'orch-perf' 'ttfs-5000' ($W5k.ok -and $Ttfs5k -le $OrchGates.Ttfs5000Ms) "${Ttfs5k}ms" "enqueue->first task start: 5,000-task run ${Ttfs5k}ms vs 10-task run ${Ttfs10}ms (x$([math]::Round($Ttfs5k / [math]::Max(1, $Ttfs10), 1))); gate $($OrchGates.Ttfs5000Ms)ms"
  $CancelSw = [Diagnostics.Stopwatch]::StartNew()
  $Cancelled = (Invoke-OrchBridge 'cancel' "E2ETf5k-$Id").cancelled
  $D = Wait-OrchRunsDone "E2ETf5k-$Id" 1 240 500
  $CancelSec = [math]::Round($CancelSw.Elapsed.TotalSeconds, 1)
  $H = (Get-OrchRuns "E2ETf5k-$Id")[0]
  # CancelRun reports what the run records, so it must match the header (it once returned 0 while 4,995 were cancelled).
  Add-Result 'orch-perf' 'cancel-5000' ($D.ok -and $H.status -eq 'CompletedWithErrors' -and $H.done -eq 5000 -and $H.cancelled -gt 0 -and [int]$Cancelled -eq [int]$H.cancelled -and $CancelSec -le $OrchGates.Cancel5000Sec) "${CancelSec}s" "CancelRun returned $Cancelled; run=$($H.status) done=$($H.done)/$($H.total) cancelled=$($H.cancelled); gate $($OrchGates.Cancel5000Sec)s"
  $null = Invoke-OrchGet "/API/PerfE2EState?ns=$Ns&wipe=1"

  Start-Sleep -Seconds 5
  $M = Invoke-OrchBridge 'summary'
  Add-Result 'orch-perf' 'memory-after-perf' ($M.rssMB -le $OrchGates.RssMB) "$($M.rssMB)MB" "rss=$($M.rssMB)MB workingSet=$($M.workingSetMB)MB heap=$($M.heapMB)MB committed=$($M.committedMB)MB; gate rss $($OrchGates.RssMB)MB"
}

# -- 15. Realtime run status ---------------------------------------------------------------------------------
# A user granted a queue id (RealtimeBridge.WatchRun) gets Craft's run roll-up over /.craft/events: progress while
# the runs work, then one "end" once every task of every run carrying the id has ended, children and grandchildren
# included (a grandchild queued through the bridge, i.e. drained after the task that queued it returned). Nobody
# else sees those frames.
function Get-OrchPrincipal([string]$User) {
  $Json = @{ identityProvider = 'aad'; userId = $User; userDetails = $User; userRoles = @('authenticated') } | ConvertTo-Json -Compress
  @{ 'x-ms-client-principal-name' = $User; 'x-ms-client-principal' = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Json)) }
}

function Start-OrchSse([string]$User, [string]$Path) {
  $H = Get-OrchPrincipal $User
  $ArgLine = "-N -s --compressed --max-time 300 -o `"$Path`" -H `"x-ms-client-principal-name: $User`" -H `"x-ms-client-principal: $($H['x-ms-client-principal'])`" $base/.craft/events"
  Start-Process -FilePath $curl -ArgumentList $ArgLine -PassThru -NoNewWindow
}

function Get-OrchSseFrames([string]$Path) {
  $Txt = try {
    $Sr = [IO.StreamReader]::new([IO.FileStream]::new($Path, 'Open', 'Read', 'ReadWrite'))
    try { $Sr.ReadToEnd() } finally { $Sr.Dispose() }
  } catch { '' }
  return , @(foreach ($L in ($Txt -split "`n")) { if ($L.StartsWith('data: ')) { $L.Substring(6) | ConvertFrom-Json } })
}

function ConvertTo-OrchUnixMs([long]$Ticks) { [long](($Ticks - 621355968000000000) / 10000) }

# The end frame alone must fill the queue tracker: every task listed, each in its final state.
function Test-OrchFrameTasks($Frame, [int]$Count) {
  $T = @($Frame.data.tasks)
  $T.Count -eq $Count -and @($T.Where({ $_.status -notin 'Completed', 'Failed' })).Count -eq 0
}

$RtNames = @('needs-signed-in-user', 'stream-compressed', 'fanout-progress', 'nested-ends-after-all', 'failure-ends-with-errors', 'other-user-sees-nothing')
Invoke-OrchCheck 'realtime' {
  $Caps = try { Invoke-RestMethod "$base/API/PerfWatchRun" -Headers (Get-OrchPrincipal 'rt-probe') -TimeoutSec 20 } catch { $null }
  if (-not $Caps.ok) { foreach ($N in $RtNames) { Add-Skip 'orch-realtime' $N 'image lacks RealtimeBridge.WatchRun' }; return }

  $Bare = Fetch "$base/.craft/events" @('--max-time', '3', '-H', 'x-ms-client-principal-name: rt-bare')
  Add-Result 'orch-realtime' 'needs-signed-in-user' ($Bare.Code -eq 401) '-' "principal-name header alone -> HTTP $($Bare.Code)"

  $P = Get-OrchPrincipal 'rt-probe'
  $Comp = Fetch "$base/.craft/events" @('--max-time', '2', '-H', 'Accept-Encoding: gzip', '-H', "x-ms-client-principal-name: rt-probe", '-H', "x-ms-client-principal: $($P['x-ms-client-principal'])")
  Add-Result 'orch-realtime' 'stream-compressed' ($Comp.Headers -match '(?im)^content-encoding:\s*gzip') '-' "Accept-Encoding: gzip -> $((([regex]'(?im)^content-encoding:.*$').Match([string]$Comp.Headers).Value).Trim())"

  $Id = New-OrchId; $Ns = "rt-$Id"
  $Fan = [guid]::NewGuid().ToString(); $Tree = [guid]::NewGuid().ToString(); $Bad = [guid]::NewGuid().ToString()
  $Mine = New-TemporaryFile; $Theirs = New-TemporaryFile
  $Streams = @((Start-OrchSse 'rt-alice' $Mine.FullName), (Start-OrchSse 'rt-mallory' $Theirs.FullName))
  try {
    Start-Sleep -Milliseconds 1000
    foreach ($Q in $Fan, $Tree, $Bad) { $null = Invoke-RestMethod "$base/API/PerfWatchRun?jobId=$Q" -Headers (Get-OrchPrincipal 'rt-alice') -TimeoutSec 20 }
    $null = Start-OrchRuns $Ns @(
      @{ name = "E2ERtFan-$Fan"; label = 'fan'; tasks = 40; holdms = 1000 }
      @{ name = "E2ERtP-$Tree"; label = 'rtP'; tasks = 3; holdms = 300
        overrides = @{ '0' = @{ child = @{ name = "E2ERtC-$Tree"; label = 'rtC'; tasks = 2; holdms = 1500
              overrides = @{ '0' = @{ child = @{ name = "E2ERtG-$Tree"; label = 'rtG'; tasks = 4; holdms = 2500; via = 'bridge' } } } } } } }
      @{ name = "E2ERtBad-$Bad"; label = 'bad'; tasks = 4; holdms = 300; overrides = @{ '2' = @{ fail = $true } } }
    )
    $W = Wait-Orch {
      $F = Get-OrchSseFrames $Mine.FullName
      if (@($F.Where({ $_.mode -eq 'end' -and $_.jobId -in @($Fan, $Tree, $Bad) }) | Select-Object -ExpandProperty jobId -Unique).Count -eq 3) { $true }
    } 180
    Start-Sleep -Seconds 4   # window in which a second "end" or a late frame would show up
    $Frames = Get-OrchSseFrames $Mine.FullName
    $Rows = Get-OrchRecords $Ns
    $Of = { param($Q) , @($Frames.Where({ $_.jobId -eq $Q })) }
    $LastEnd = { param([string[]]$Labels) ConvertTo-OrchUnixMs (Get-OrchMax @(foreach ($L in $Labels) { (Get-OrchTasks $Rows $L).end })) }

    $F = & $Of $Fan; $Ends = @($F.Where({ $_.mode -eq 'end' })); $Ups = @($F.Where({ $_.mode -eq 'update' }))
    $Done = @($Ups | ForEach-Object { [int]$_.data.completed + [int]$_.data.failed })
    $Mid = @($Ups.Where({ [int]$_.data.completed + [int]$_.data.failed -lt [int]$_.data.total })).Count
    $E = $Ends | Select-Object -First 1
    $Ok = $W.ok -and $Ends.Count -eq 1 -and $Mid -ge 1 -and (($Done -join ',') -eq (@($Done | Sort-Object) -join ',')) -and
      $E.data.status -eq 'Completed' -and [int]$E.data.completed -eq [int]$E.data.total -and [int]$E.data.total -eq 40 -and
      $F[-1].mode -eq 'end' -and $E.ts -ge (& $LastEnd @('fan')) -and (Test-OrchFrameTasks $E 40) -and $E.data.runName
    Add-Result 'orch-realtime' 'fanout-progress' $Ok "$($W.sec)s" "updates=$($Ups.Count) (mid-run $Mid) completed=[$($Done -join ',')] ends=$($Ends.Count) end=$($E.data.status) $($E.data.completed)/$($E.data.total) endTasks=$(@($E.data.tasks).Count) run=$($E.data.runName)"

    $F = & $Of $Tree; $Ends = @($F.Where({ $_.mode -eq 'end' })); $E = $Ends | Select-Object -First 1
    $Counts = @('rtP', 'rtC', 'rtG' | ForEach-Object { "$_=$((Get-OrchTasks $Rows $_).Count)" }) -join ' '
    $Lead = if ($E) { $E.ts - (& $LastEnd @('rtP', 'rtC', 'rtG')) } else { 'n/a' }
    $Ok = $Ends.Count -eq 1 -and $Counts -eq 'rtP=3 rtC=2 rtG=4' -and $E.data.status -eq 'Completed' -and
      [int]$E.data.completed -eq [int]$E.data.total -and [int]$E.data.total -ge 9 -and $Lead -ge 0 -and (Test-OrchFrameTasks $E 9)
    Add-Result 'orch-realtime' 'nested-ends-after-all' $Ok "+${Lead}ms" "end minus last grandchild/child/parent task end=${Lead}ms ends=$($Ends.Count) tasks $Counts end=$($E.data.status) $($E.data.completed)/$($E.data.total) endTasks=$(@($E.data.tasks).Count) frames=$($F.Count)"

    $F = & $Of $Bad; $Ends = @($F.Where({ $_.mode -eq 'end' })); $E = $Ends | Select-Object -First 1
    $Ok = $Ends.Count -eq 1 -and $E.data.status -eq 'CompletedWithErrors' -and [int]$E.data.failed -eq 1 -and $E.ts -ge (& $LastEnd @('bad')) -and
      (Test-OrchFrameTasks $E 4) -and @(@($E.data.tasks).Where({ $_.status -eq 'Failed' })).Count -eq 1
    Add-Result 'orch-realtime' 'failure-ends-with-errors' $Ok '-' "ends=$($Ends.Count) end=$($E.data.status) failed=$($E.data.failed) $($E.data.completed)/$($E.data.total)"

    $Leak = @((Get-OrchSseFrames $Theirs.FullName).Where({ $_.jobId -in @($Fan, $Tree, $Bad) })).Count
    Add-Result 'orch-realtime' 'other-user-sees-nothing' ($Leak -eq 0) '-' "frames for alice's queues on mallory's stream=$Leak"
  } finally {
    $Streams | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item $Mine, $Theirs -ErrorAction SilentlyContinue
  }
}

# -- 10. Restart recovery + legacy table drop (restarts the SUT; keep last) -------------------------------------
Invoke-OrchCheck 'restart' {
  $Id = New-OrchId; $Ns = "rst-$Id"; $Name = "E2ERst-$Id"
  $N = $OrchPool + 2   # pool running + 2 buffered claims for the graceful stop to hand back
  # Earlier checks may still hold workers (the perf keeper task): start from an idle engine so the whole pool
  # goes to this run and the running/buffered split is the intended one.
  $Quiet = Wait-Orch { $G = Invoke-OrchBridge 'summary'; if ($G.jobsActive -eq 0 -and $G.jobsQueued -eq 0) { $true } } 120 1000
  $Seed = Invoke-OrchGet '/API/PerfE2ELegacy?op=seed'
  $null = Start-OrchRuns $Ns @(@{ name = $Name; label = 'rst'; tasks = $N; task = @{ holdms = 20000 }; post = @{ marker = 'rst' } }) -Sink 'table'
  $Up = Wait-Orch { $S = Get-OrchSummary $Ns 'table'; if ($S['rst'].tasks -ge $OrchPool) { $true } } 60 250
  Start-Sleep -Seconds 2   # let the pump buffer the remaining claims
  $Pre = Get-OrchTasks (Get-OrchRecords $Ns 'table') 'rst'
  $Running = @($Pre.Where({ $_.end -le 0 }).idx | Sort-Object -Unique)
  $Unstarted = @((0..($N - 1)).Where({ $_ -notin $Pre.idx }))
  Info "restart: docker restart $OrchContainer with $($Running.Count) tasks running, $($Unstarted.Count) not started ..."
  $Sw = [Diagnostics.Stopwatch]::StartNew()
  docker restart $OrchContainer 2>&1 | Out-Null
  $Ready = Wait-Orch { $H = Invoke-OrchGet '/healthz' 5; if ($H.status -eq 'ready') { $true } } 180 1000
  $Legacy = Invoke-OrchGet '/API/PerfE2ELegacy?op=list'
  Add-Result 'orch-restart' 'legacy-tables-dropped' ($Ready.ok -and @($Seed.present).Count -eq 5 -and @($Legacy.present).Count -eq 0) "$($Ready.sec)s" "seeded+present before=$(@($Seed.present).Count) present after restart=$(@($Legacy.present).Count) $(@($Legacy.present) -join ',')"
  $D = Wait-OrchRunsDone $Name 1 480 3000
  $RestartSec = [math]::Round($Sw.Elapsed.TotalSeconds, 1)
  $H = (Get-OrchRuns $Name -Detail)[0]
  $Rows = Get-OrchRecords $Ns 'table'
  $T = Get-OrchTasks $Rows 'rst'; $P = Get-OrchPosts $Rows 'rst'
  $AttemptOf = @{}; foreach ($X in $H.tasks) { $AttemptOf[[int]$X.seq] = [int]$X.attempt }
  $Attempts = @($H.tasks | ForEach-Object { "$($_.seq):$($_.attempt)" }) -join ','
  $Starts = @($T | Group-Object idx | Sort-Object { [int]$_.Name } | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ','
  $Ok = $Quiet.ok -and $Up.ok -and $D.ok -and $H.status -eq 'Completed' -and $H.done -eq $N -and @($H.tasks.Where({ $_.status -eq 'Completed' })).Count -eq $N
  Add-Result 'orch-restart' 'run-completes' $Ok "${RestartSec}s" "restart->done ${RestartSec}s; run=$($H.status)/$($H.phase) done=$($H.done)/$($H.total) open=P$($H.open.P)/R$($H.open.R) (engine idle first=$($Quiet.ok))"
  $Finished = @($T.Where({ $_.end -gt 0 }) | Group-Object idx)
  $OnceOk = $Finished.Count -eq $N -and @($Finished.Where({ $_.Count -ne 1 })).Count -eq 0
  Add-Result 'orch-restart' 'each-task-completes-once' $OnceOk '-' "finished records per idx=$(@($Finished | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ','); starts per idx=$Starts; D-row attempts=$Attempts"
  $ReBad = @($Running.Where({ $I = $_; $AttemptOf[[int]$I] -lt 2 -or @($T.Where({ $_.idx -eq $I })).Count -lt 2 }))
  Add-Result 'orch-restart' 'interrupted-rerun' ($Running.Count -ge 1 -and $ReBad.Count -eq 0) '-' "running at restart=$($Running -join ','); their attempts=$(@($Running | ForEach-Object { "${_}:$($AttemptOf[[int]$_])" }) -join ',')"
  $UnBad = @($Unstarted.Where({ $AttemptOf[[int]$_] -ne 1 }))
  Add-Result 'orch-restart' 'unstarted-claims-released' ($Unstarted.Count -ge 1 -and $UnBad.Count -eq 0) '-' "not started at restart=$($Unstarted -join ','); attempts=$(@($Unstarted | ForEach-Object { "${_}:$($AttemptOf[[int]$_])" }) -join ',') (1 = handed back on graceful stop, 2 = lease lapsed)"
  Add-Result 'orch-restart' 'postexec-once' ($P.Count -eq 1 -and $P[0].lines -eq $N -and $H.postExecStatus -eq 'Completed') '-' "posts=$($P.Count) lines=$($P[0].lines) postExec=$($H.postExecStatus)"
}

Info ("orchestration gates: fanout-1000 <= {0}s, runs-300 <= {1}s, ttfs-5000 <= {2}ms, rss <= {3}MB, idle claim <= {4}ms, cancel-5000 <= {5}s, sequential step <= {6}ms" -f $OrchGates.Fanout1000Sec, $OrchGates.ManyRuns300Sec, $OrchGates.Ttfs5000Ms, $OrchGates.RssMB, $OrchGates.IdleClaimMs, $OrchGates.Cancel5000Sec, $OrchGates.SeqStepMs)
