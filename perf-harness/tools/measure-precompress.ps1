<#
.SYNOPSIS  Measure Brotli q11 / gzip 9 sizes on the REAL deployed assets (no app code touched).
           Substantiates the pre-compression win vs the current on-the-fly Fastest path.
.EXAMPLE   pwsh tools\measure-precompress.ps1 -Image cipp-ng:perf-baseline
#>
param([string]$Image = 'cipp-ng:perf-baseline')
$ErrorActionPreference = 'Stop'
$pc  = Join-Path $env:TEMP ("cipp-pc-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force $pc | Out-Null
$tmp = 'cipp-pc-tmp'
docker rm -f $tmp *> $null
docker create --name $tmp $Image | Out-Null
docker cp "${tmp}:/app/Frontend/_next/static/chunks/pages" "$pc\pages"
docker cp "${tmp}:/app/Frontend/_next/static/css"          "$pc\css"
docker cp "${tmp}:/app/Frontend/permissionsList.json"      "$pc\permissionsList.json"
docker cp "${tmp}:/app/Frontend/index.html"                "$pc\index.html"
docker rm -f $tmp *> $null
Copy-Item (Join-Path $PSScriptRoot 'precompress.mjs') "$pc\precompress.mjs"
$fwd = [char]47
$pcD = $pc.Replace([char]92, $fwd)
Write-Host "Staged raw assets (recursing):"
Get-ChildItem $pc -Recurse -File | Where-Object { $_.Extension -notin '.br','.gz','.mjs' } |
  Sort-Object Length -Descending | Select-Object -First 8 |
  ForEach-Object { "  {0,-34} {1,12:N0} bytes" -f $_.Name, $_.Length }
Write-Host "`n=== Brotli q11 / gzip 9 (precomputed, zero per-request CPU) ==="
docker run --rm -v "${pcD}:/work" node:22-slim node /work/precompress.mjs /work
Write-Host "`n(temp dir: $pc)"
