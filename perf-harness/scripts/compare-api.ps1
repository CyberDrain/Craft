<#
.SYNOPSIS  Diff two http-only API harness result JSONs into a before/after table.
.EXAMPLE   pwsh scripts\compare-api.ps1 results\baseline-*.json results\pool8-*.json
.NOTE      Pass the result .json (not the .k6.json). Globs match the newest non-k6 result file.
#>
param(
  [Parameter(Mandatory)][string]$Before,
  [Parameter(Mandatory)][string]$After
)
$ErrorActionPreference = 'Stop'

function Load($pat){
  # Exclude the raw k6 export + stats sidecars; take the newest matching result JSON.
  $f = Get-ChildItem $pat -ErrorAction Stop |
       Where-Object { $_.Name -notmatch '\.k6\.json$' -and $_.Name -notmatch '\.stats\.' } |
       Sort-Object LastWriteTime | Select-Object -Last 1
  if(-not $f){ throw "no result file matched: $pat" }
  Write-Host "  $($f.Name)" -ForegroundColor DarkGray
  Get-Content -Raw $f.FullName | ConvertFrom-Json
}
Write-Host "Loading results:" -ForegroundColor Cyan
$b = Load $Before
$a = Load $After

function Row($m,$bv,$av,$lower){ [pscustomobject]@{ M=$m; B=$bv; A=$av; Lower=$lower } }

$rows = @(
  Row 'Requests/sec'         $b.load.reqPerSec  $a.load.reqPerSec  $false
  Row 'Fail rate'            $b.load.failRate   $a.load.failRate   $true
  Row 'Latency avg (ms)'     $b.load.durAvgMs   $a.load.durAvgMs   $true
  Row 'Latency p50 (ms)'     $b.load.durP50Ms   $a.load.durP50Ms   $true
  Row 'Latency p95 (ms)'     $b.load.durP95Ms   $a.load.durP95Ms   $true
  Row 'Latency p99 (ms)'     $b.load.durP99Ms   $a.load.durP99Ms   $true
  Row 'CPU avg % (1 core)'   $b.resources.cpuAvgPct $a.resources.cpuAvgPct $true
  Row 'CPU max % (1 core)'   $b.resources.cpuMaxPct $a.resources.cpuMaxPct $true
  Row 'RAM avg (MB)'         $b.resources.memAvgMB  $a.resources.memAvgMB  $true
)

# Per-endpoint p95 (only endpoints present in both)
foreach($ep in @('PerfPing','PerfEcho','PerfCpu','PerfSleep','PerfJson')){
  $bv = $b.perEndpoint.$ep.p95Ms; $av = $a.perEndpoint.$ep.p95Ms
  if($null -ne $bv -and $null -ne $av){ $rows += Row "  $ep p95 (ms)" $bv $av $true }
}

Write-Host ""
Write-Host ("{0,-22} {1,12} {2,12} {3,14}" -f 'metric','before','after','change')
Write-Host ('-' * 62)
foreach($r in $rows){
  $chg=''; $color='Gray'
  if($null -ne $r.B -and $null -ne $r.A -and [double]$r.B -ne 0){
    $pct = ([double]$r.A - [double]$r.B) / [math]::Abs([double]$r.B) * 100
    $chg = "{0:+0.0;-0.0;0}%" -f $pct
    $better = if($r.Lower){ $pct -lt 0 } else { $pct -gt 0 }
    $color = if([math]::Abs($pct) -lt 1){ 'Gray' } elseif($better){ 'Green' } else { 'Red' }
  }
  Write-Host ("{0,-22} {1,12} {2,12} {3,14}" -f $r.M, $r.B, $r.A, $chg) -ForegroundColor $color
}
Write-Host ""
$cfgB = "$($b.config.pool)pool/$($b.config.vus)vu/$($b.config.cpus)cpu"
$cfgA = "$($a.config.pool)pool/$($a.config.vus)vu/$($a.config.cpus)cpu"
Write-Host "config:  before=$cfgB  after=$cfgA" -ForegroundColor DarkGray
