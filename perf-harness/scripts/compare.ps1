<#
.SYNOPSIS  Diff two harness result JSONs into a before/after table.
.EXAMPLE   pwsh scripts\compare.ps1 results\baseline-2cpu-*.json results\optimized-*.json
#>
param(
  [Parameter(Mandatory)][string]$Before,
  [Parameter(Mandatory)][string]$After
)
$ErrorActionPreference='Stop'

function Load($pat){
  $f = Get-ChildItem $pat -ErrorAction Stop | Sort-Object LastWriteTime | Select-Object -Last 1
  if(-not $f){ throw "no file matched: $pat" }
  Write-Host "  $($f.Name)" -ForegroundColor DarkGray
  Get-Content -Raw $f.FullName | ConvertFrom-Json
}
Write-Host "Loading results:" -ForegroundColor Cyan
$b = Load $Before
$a = Load $After
function AppRatio($r){ ($r.headerAudit | Where-Object kind -eq 'immutable_js' | Select-Object -First 1).compressionRatio }

# Metric, Before, After, lower-is-better
$rows = @(
  [pscustomobject]@{ M='Image size (MB)';              B=$b.disk.imageMB;             A=$a.disk.imageMB;             Lower=$true }
  [pscustomobject]@{ M='Frontend on disk (MB)';        B=$b.disk.frontendMB;          A=$a.disk.frontendMB;          Lower=$true }
  [pscustomobject]@{ M='Pre-compressed (.br files)';   B=$b.disk.brFiles;             A=$a.disk.brFiles;             Lower=$false }
  [pscustomobject]@{ M='CPU avg % (1 core)';           B=$b.resources.cpuAvgPct;      A=$a.resources.cpuAvgPct;      Lower=$true }
  [pscustomobject]@{ M='CPU max % (1 core)';           B=$b.resources.cpuMaxPct;      A=$a.resources.cpuMaxPct;      Lower=$true }
  [pscustomobject]@{ M='RAM avg (MB)';                 B=$b.resources.memAvgMB;       A=$a.resources.memAvgMB;       Lower=$true }
  [pscustomobject]@{ M='Requests/sec';                 B=$b.load.reqPerSec;           A=$a.load.reqPerSec;           Lower=$false }
  [pscustomobject]@{ M='Data rate (MB/s)';             B=$b.load.dataRateMBs;         A=$a.load.dataRateMBs;         Lower=$false }
  [pscustomobject]@{ M='TTFB avg (ms)';                B=$b.load.ttfbAvgMs;           A=$a.load.ttfbAvgMs;           Lower=$true }
  [pscustomobject]@{ M='TTFB p95 (ms)';                B=$b.load.ttfbP95Ms;           A=$a.load.ttfbP95Ms;           Lower=$true }
  [pscustomobject]@{ M='Latency p95 dur (ms)';         B=$b.load.durP95Ms;            A=$a.load.durP95Ms;            Lower=$true }
  [pscustomobject]@{ M='TTFB app-chunk avg (ms)';      B=$b.load.ttfb_immutable_js_ms;A=$a.load.ttfb_immutable_js_ms;Lower=$true }
  [pscustomobject]@{ M='App-chunk compression x';      B=(AppRatio $b);               A=(AppRatio $a);               Lower=$false }
  [pscustomobject]@{ M='Responses chunked (otf)';      B=$b.load.responsesChunked;    A=$a.load.responsesChunked;    Lower=$true }
)

Write-Host ""
Write-Host ("{0,-28} {1,12} {2,12} {3,16}" -f 'metric','before','after','change') -ForegroundColor White
Write-Host ('-'*70)
foreach($row in $rows){
  $bv=$row.B; $av=$row.A; $delta=''; $color='Gray'
  $numeric = ($bv -is [double] -or $bv -is [int] -or $bv -is [long]) -and ($av -is [double] -or $av -is [int] -or $av -is [long])
  if($numeric -and $bv){
    $pct=[math]::Round((($av-$bv)/[math]::Abs($bv))*100,1)
    if($av -eq $bv){ $tag='=' } elseif( ($row.Lower -and $av -lt $bv) -or (-not $row.Lower -and $av -gt $bv) ){ $tag='improved' } else { $tag='worse' }
    $delta = ('{0:+0.#;-0.#;0}% {1}' -f $pct,$tag)
    $color = if($tag -eq 'improved'){'Green'} elseif($tag -eq 'worse'){'Red'} else {'Gray'}
  }
  Write-Host ("{0,-28} {1,12} {2,12} {3,16}" -f $row.M,$bv,$av,$delta) -ForegroundColor $color
}
Write-Host ""
Write-Host ("Header audit: {0} -> {1}" -f $b.headerAuditPass,$a.headerAuditPass) -ForegroundColor White
Write-Host ""
