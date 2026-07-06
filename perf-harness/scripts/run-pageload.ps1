<#
.SYNOPSIS
  Browser-driven per-page load test. Brings up the SUT in static-only + dev-auth mode (canned
  superadmin /.auth/me + /api/me so the SPA renders pages without the PS backend), then drives a
  headless Chromium (Playwright) through a set of real routes and records, per page: wire transfer,
  request/JS counts, and FCP / LCP / DOMContentLoaded / load. Output: results\pageload-<ts>.{json,md}.

.EXAMPLE
  pwsh scripts\run-pageload.ps1 -SutImage cipp-ng:split-public
#>
[CmdletBinding()]
param(
  [string]$SutImage = 'cipp-ng:split-public',
  [int]$Port = 5197,
  [int]$ReadyTimeoutSec = 240,
  [string[]]$Routes = @(
    '/', '/identity/administration/users', '/endpoint/MEM/devices',
    '/endpoint/MEM/list-compliance-policies', '/email/administration/mailboxes',
    '/tenant/administration/tenants', '/tenant/tools/graph-explorer',
    '/security/defender/list-defender', '/cipp/settings', '/tenant/standards/list-standards'
  ),
  [switch]$KeepUp
)
$ErrorActionPreference = 'Stop'
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$root       = Split-Path -Parent $here
$compose    = Join-Path $root 'docker-compose.yml'
$override   = Join-Path $root 'docker-compose.staticonly.yml'
$httpsOverride = Join-Path $root 'docker-compose.https.yml'
$plDir      = Join-Path $root 'pageload'
$resultsDir = Join-Path $root 'results'
$container  = 'craft-perf-sut'
$network    = 'craft-perfnet'
$base       = "http://localhost:$Port"
$stamp      = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
function Info($m){ Write-Host "[pageload] $m" -ForegroundColor Cyan }

$env:SUT_IMAGE = $SutImage; $env:SUT_PORT = "$Port"; $env:SUT_CPUS = '2'
$composeFiles = @('-f', $compose, '-f', $override, '-f', $httpsOverride)
Info "SUT: $SutImage  (static-only + dev-auth, behind Caddy HTTPS proxy)"
docker compose $composeFiles up -d | Out-Host
if ($LASTEXITCODE -ne 0){ throw 'compose up failed' }

try {
  Info "waiting for static serving (timeout ${ReadyTimeoutSec}s) ..."
  $deadline=(Get-Date).AddSeconds($ReadyTimeoutSec); $ready=$false
  while((Get-Date) -lt $deadline){
    $ct = & curl.exe -s -o NUL -w "%{content_type}" --max-time 10 "$base/robots.txt" 2>$null
    if($ct -match 'text/plain'){ $ready=$true; break }
    Start-Sleep -Seconds 2
  }
  if(-not $ready){ Write-Host "[pageload] static not live; measuring anyway" -ForegroundColor Yellow }

  # Wait for the Caddy HTTPS proxy (TLS-terminates in front of the SUT, like Cloudflare).
  $caddyBase = 'https://localhost:5443'; $cad=''; $cadDeadline=(Get-Date).AddSeconds(40)
  while((Get-Date) -lt $cadDeadline){
    $cad = & curl.exe -sk -o NUL -w "%{content_type}" --max-time 8 "$caddyBase/robots.txt" 2>$null
    if($cad -match 'text/plain'){ break }
    Start-Sleep -Seconds 2
  }
  Info "caddy https proxy robots.txt content-type: $cad"

  # Sanity: confirm dev-auth serves a principal over https (else pages render login/offline). Also confirms
  # the SPA will see https + the real CSP, and assets will be served brotli (browsers request br only on https).
  $me = & curl.exe -sk --max-time 10 "$caddyBase/api/me" 2>$null
  Info "/api/me (via https) -> $me"
  if($me -notmatch 'clientPrincipal'){ Write-Host "[pageload] WARNING: /api/me has no clientPrincipal — pages may show ApiOffline. In frontend-only mode CRAFT no longer synthesizes a principal (dev-auth removed); ship a static Frontend/api/me (and /.auth/me) JSON in the image so the static host serves it." -ForegroundColor Yellow }

  # Keep only routes whose prerendered .html exists in the image
  $check = ($Routes | ForEach-Object { if($_ -eq '/'){ 'index' } else { $_.TrimStart('/') } }) -join "`n"
  $existing = $check | docker exec -i $container sh -c 'cd /app/Frontend && while read r; do [ -f "$r.html" ] && echo "$r"; done'
  $active = @('/') + (($existing -split "`n") | Where-Object { $_ -and $_ -ne 'index' } | ForEach-Object { '/' + $_ })
  $active = $active | Select-Object -Unique
  Info "routes to load ($($active.Count)): $($active -join ', ')"
  ($active | ConvertTo-Json -AsArray) | Set-Content (Join-Path $plDir 'routes.json') -Encoding utf8

  # Run Playwright (pinned image + matching pkg; browsers are bundled at /ms-playwright)
  Info "running headless Chromium over $($active.Count) routes ..."
  $plDirD = ($plDir -replace '\\','/')
  & docker run --rm --network $network -e BASE="https://caddy" -e SETTLE_MS=4000 `
      -v "${plDirD}:/work" mcr.microsoft.com/playwright:v1.46.1-jammy `
      sh -c "cd /tmp && npm init -y >/dev/null 2>&1 && npm i playwright@1.46.1 >/dev/null 2>&1 && cp /work/pageload.mjs /tmp/pageload.mjs && node /tmp/pageload.mjs" 2>&1 | Out-Host

  $resJson = Join-Path $plDir 'pageload-results.json'
  if(-not (Test-Path $resJson)){ throw 'pageload-results.json not produced' }
  $rows = Get-Content -Raw $resJson | ConvertFrom-Json
  Copy-Item $resJson (Join-Path $resultsDir "pageload-$stamp.json")

  # Markdown report
  $sb=[System.Text.StringBuilder]::new()
  [void]$sb.AppendLine("# Per-page load — $SutImage ($stamp)")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("Cold-cache (fresh context) load of each route via headless Chromium, static-only + dev-auth.")
  [void]$sb.AppendLine("'Wire KB' = sum of response Content-Length (accurate for precompressed assets). The shared")
  [void]$sb.AppendLine("`_app`/framework chunks (~0.65 MB br) are re-counted per page here but cached across real navigation.")
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("| route | reqs | JS reqs | wire KB | JS KB | FCP ms | LCP ms | DCL ms | load ms |")
  [void]$sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
  foreach($r in $rows){
    [void]$sb.AppendLine("| $($r.route) | $($r.requests) | $($r.jsRequests) | $($r.totalKB) | $($r.jsKB) | $($r.fcpMs) | $($r.lcpMs) | $($r.dclMs) | $($r.loadMs) |")
  }
  [void]$sb.AppendLine("")
  [void]$sb.AppendLine("## Heaviest request per page")
  [void]$sb.AppendLine("| route | top asset | KB | enc |")
  [void]$sb.AppendLine("|---|---|---:|---|")
  foreach($r in $rows){
    $t = $r.topRequests | Select-Object -First 1
    if($t){ [void]$sb.AppendLine("| $($r.route) | $($t.url) | $($t.kb) | $($t.enc) |") }
  }
  $md = Join-Path $resultsDir "pageload-$stamp.md"
  $sb.ToString() | Set-Content $md -Encoding utf8
  Info "wrote: $md"
  Write-Host ""; Get-Content $md | Write-Host
}
finally {
  if($KeepUp){ Write-Host "[pageload] leaving up (-KeepUp)" -ForegroundColor Yellow }
  else { Info 'tearing down ...'; docker compose $composeFiles down -v 2>&1 | Out-Null }
}
