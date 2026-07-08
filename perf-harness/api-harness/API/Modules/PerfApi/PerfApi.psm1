# PerfApi — synthetic HTTP endpoints for load-testing CRAFT in http-only mode.
#
# Each function is a CRAFT HTTP endpoint: it takes ($Request, $TriggerMetadata) and returns a hashtable
# @{ StatusCode; Body } (CRAFT's response extractor is duck-typed, so no [HttpResponseContext] is needed).
# The function name (minus the Invoke- prefix) becomes the route, e.g. Invoke-PerfPing -> /API/PerfPing.
#
# The five endpoints deliberately isolate different parts of the dispatch pipeline — see README.

# Pure dispatch overhead: returns immediately. Measures the fixed cost of the whole path
# (Kestrel -> middleware -> worker checkout -> PS invoke -> serialize -> response).
function Invoke-PerfPing {
    param($Request, $TriggerMetadata)
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfPing' } }
}

# Request-marshaling cost: echoes the query and body back, exercising BuildRequestObject.
function Invoke-PerfEcho {
    param($Request, $TriggerMetadata)
    $q = @{}
    if ($Request.Query) {
        foreach ($k in $Request.Query.Keys) { $q[$k] = $Request.Query[$k] }
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfEcho'; query = $q; body = $Request.Body } }
}

# CPU-bound: busy-loops for ~ms milliseconds. Measures per-core throughput and pool saturation
# under CPU load. Query: ?ms=N (default 20).
function Invoke-PerfCpu {
    param($Request, $TriggerMetadata)
    $ms = 20
    if ($Request.Query.ms) { $ms = [int]$Request.Query.ms }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $acc = 0.0
    while ($sw.ElapsedMilliseconds -lt $ms) {
        for ($i = 0; $i -lt 5000; $i++) { $acc += [math]::Sqrt($i) }
    }
    $sw.Stop()
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfCpu'; requestedMs = $ms; actualMs = $sw.ElapsedMilliseconds } }
}

# I/O-wait simulation: sleeps for ~ms milliseconds. A sleeping worker holds its pool slot, so this
# exposes HttpPoolSize as the concurrency ceiling (extra concurrent requests queue on checkout,
# then 503 after 30s). Query: ?ms=N (default 100).
function Invoke-PerfSleep {
    param($Request, $TriggerMetadata)
    $ms = 100
    if ($Request.Query.ms) { $ms = [int]$Request.Query.ms }
    Start-Sleep -Milliseconds $ms
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfSleep'; sleptMs = $ms } }
}

# BG-worker / orchestration driver: enqueue an orchestrator batch of N tasks (the real "orchestrator enqueue"
# path). Each task dispatches to a BG worker as Invoke-CraftTask -> Push-PerfBg, so N tasks = N BG PS invokes.
# Query:
#   n=N        batch size (default 500)
#   taskms=M   per-task work: Start-Sleep M ms inside each task (0 = no-op; simulates Graph-call latency)
#   childn=C   if >0, each task spawns a CHILD orchestration of C no-op tasks (fan-out dependency)
function Invoke-PerfBgEnqueue {
    param($Request, $TriggerMetadata)
    $n = 500;  if ($Request.Query.n) { $n = [int]$Request.Query.n }
    $taskms = 0; if ($Request.Query.taskms) { $taskms = [int]$Request.Query.taskms }
    $childn = 0; if ($Request.Query.childn) { $childn = [int]$Request.Query.childn }
    $batch = @(for ($i = 0; $i -lt $n; $i++) { @{ FunctionName = 'PerfBg'; idx = $i; taskms = $taskms; childn = $childn } })
    $run = Start-CraftOrchestrator -InputObject @{
        OrchestratorName = "PerfBg-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Batch            = $batch
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; enqueued = $n; taskms = $taskms; childn = $childn; run = $run } }
}

# The background task each orchestrator batch item runs (Invoke-CraftTask calls Push-{FunctionName}).
# Sleeps taskms to simulate real work, and optionally spawns a child orchestration (fan-out dependency).
function Push-PerfBg {
    param($Item)
    if ($Item.taskms -and [int]$Item.taskms -gt 0) { Start-Sleep -Milliseconds ([int]$Item.taskms) }
    if ($Item.childn -and [int]$Item.childn -gt 0) {
        $cn = [int]$Item.childn
        $childBatch = @(for ($j = 0; $j -lt $cn; $j++) { @{ FunctionName = 'PerfBgLeaf'; idx = $j } })
        Start-CraftOrchestrator -InputObject @{
            OrchestratorName = "PerfBgChild-$($Item.idx)-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
            Batch            = $childBatch
        } | Out-Null
    }
    return @{ ok = $true; idx = $Item.idx }
}

# Leaf task for child orchestrations (no further fan-out).
function Push-PerfBgLeaf {
    param($Item)
    return @{ ok = $true; idx = $Item.idx }
}

# Identity reflector: returns the principal CRAFT resolved for this request — the EasyAuth
# x-ms-client-principal (base64 claims), plus X-Forwarded-For. Used to confirm header decoding,
# role lookup, and client-IP pass-through.
function Invoke-PerfWhoami {
    param($Request, $TriggerMetadata)
    $h = $Request.Headers
    $cp = $null
    $b64 = $h.'x-ms-client-principal'
    if ($b64) {
        try {
            $json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$b64))
            $cp = $json | ConvertFrom-Json
        } catch { $cp = "decode-error" }
    }
    return @{ StatusCode = 200; Body = @{
        ok               = $true
        endpoint         = 'PerfWhoami'
        clientPrincipal  = $cp
        principalName    = $h.'x-ms-client-principal-name'
        idp              = $h.'x-ms-client-principal-idp'
        xForwardedFor    = $h.'x-forwarded-for'
    } }
}

# Timer target: a scheduled task increments a process-wide counter (shared cache) so the harness can
# confirm the scheduler actually fired the task on a background worker.
function Invoke-PerfTimerTick {
    param($Request, $TriggerMetadata)
    $cache = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfTimer')
    $n = 0; if ($cache['count']) { $n = [int]$cache['count'] }
    $cache['count'] = $n + 1
    $cache['last'] = (Get-Date).ToUniversalTime().ToString('o')
    return @{ ok = $true; count = $cache['count'] }
}

# HTTP reader for the timer counter — lets the harness poll how many times the timer has fired.
function Invoke-PerfTimerCount {
    param($Request, $TriggerMetadata)
    $cache = [Craft.Services.PowerShellRunnerService]::GetSharedCache('PerfTimer')
    $n = 0; if ($cache['count']) { $n = [int]$cache['count'] }
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfTimerCount'; count = $n; last = $cache['last'] } }
}

# Cacheable endpoint: the "List" prefix + GET makes CRAFT's response cache engage (stale-while-revalidate).
# Used to profile the disk-backed response cache (hit = fixed query, miss = unique query). Query: ?n=N (default 50).
function Invoke-ListPerf {
    param($Request, $TriggerMetadata)
    $n = 50
    if ($Request.Query.n) { $n = [int]$Request.Query.n }
    $items = for ($i = 0; $i -lt $n; $i++) {
        @{ id = $i; name = "row-$i"; value = ($i * 1.5); tag = "t$($i % 8)"; active = ($i % 2 -eq 0) }
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; count = $n; items = @($items) } }
}

# Serialization + payload cost: builds and returns an N-item array. Measures PS-object -> JSON
# serialization and response size. Query: ?n=N (default 1000).
function Invoke-PerfJson {
    param($Request, $TriggerMetadata)
    $n = 1000
    if ($Request.Query.n) { $n = [int]$Request.Query.n }
    $items = for ($i = 0; $i -lt $n; $i++) {
        @{ id = $i; name = "item-$i"; value = ($i * 3.14159); active = ($i % 2 -eq 0); tag = "tag-$($i % 10)" }
    }
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfJson'; count = $n; items = @($items) } }
}

# Realtime bridge driver: publishes a job event via the C# RealtimeBridge so an SSE consumer of
# /.craft/events can observe it. userId is taken from the caller's identity so it is delivered back to
# the same principal. Query: ?jobId=<guid>&mode=start|update|end&size=<bytes of filler data>.
function Invoke-PerfPublish {
    param($Request, $TriggerMetadata)
    $userId = [string]$Request.Headers.'x-ms-client-principal-name'
    $jobId  = [string]$Request.Query.jobId
    if (-not $jobId) { $jobId = [guid]::NewGuid().ToString() }
    $mode = [string]$Request.Query.mode; if (-not $mode) { $mode = 'update' }
    $data = @{ note = 'perf publish'; ts = (Get-Date).ToUniversalTime().ToString('o') }
    if ($Request.Query.size) { $data['filler'] = ('x' * [int]$Request.Query.size) }
    [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, $mode, $data, "/perf/$jobId", "View job")
    return @{ StatusCode = 200; Body = @{ ok = $true; endpoint = 'PerfPublish'; jobId = $jobId; mode = $mode; userId = $userId } }
}
