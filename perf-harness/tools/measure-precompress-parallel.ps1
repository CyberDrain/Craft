<#
.SYNOPSIS  Time the parallel precompress.mjs on the deployed frontend at a capped core count
           (default 2, to mirror a standard GitHub-hosted runner) and at all cores.
.EXAMPLE   pwsh tools\measure-precompress-parallel.ps1 -Image cipp-ng:split-all
#>
param([string]$Image = "cipp-ng:split-all")
$ErrorActionPreference = "Stop"
$pc = Join-Path $env:TEMP ("cipp-pcpar-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $pc | Out-Null
$tmp = "cipp-pcpar-tmp"
docker rm -f $tmp *> $null
docker create --name $tmp $Image | Out-Null
docker cp "${tmp}:/app/Frontend" "$pc\Frontend"
docker rm -f $tmp *> $null
Copy-Item (Join-Path $PSScriptRoot "precompress.mjs") "$pc\precompress.mjs"
$pcD = $pc.Replace([char]92, [char]47)

Write-Host "=== 2 cores (mirrors a standard GitHub-hosted runner) ==="
docker run --rm --cpus=2 -v "${pcD}:/work" node:22-slim node /work/precompress.mjs /work/Frontend
Write-Host ""
Write-Host "=== all cores (this machine) ==="
docker run --rm -v "${pcD}:/work" node:22-slim node /work/precompress.mjs /work/Frontend
Write-Host ""
Write-Host "(temp dir: $pc)"
