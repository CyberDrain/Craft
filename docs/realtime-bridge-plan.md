# Craft Realtime Bridge (SSE) — design & plan

A host-owned, in-memory, identity-gated realtime channel. Downstream code (CIPP PowerShell or
Craft's own C#) **publishes** job lifecycle events; browsers **consume** them over Server-Sent Events.
Craft is a thin, bounded pipe — it validates, stores/updates/evicts one current message per job, and
delivers to the right identity. All job intelligence lives downstream.

## Scope & non-goals (M1)
- **Single instance, in-memory only.** No backplane, no multi-instance, no Redis/Azure Web PubSub.
- **No channels/topics, no per-tenant ACLs.** Authorization falls out of identity binding.
- **Craft does not know** when a job is "done", who owns it, or how fan-out workers correlate — that is
  the downstream app's responsibility. Craft only exposes *publish* + *consume*.
- Assumes the publishing worker and the SSE connection are in the **same process** (combined role /
  one container). Splitting roles or scaling out would need a backplane — a clean future add that does
  not change the bridge API.

## Model: an identity-gated `(userId, jobId)` matrix
A subscription is the pair **`(userId, jobId)`**. The entry is created by the publisher's `start`/`update`,
where `userId` is the server-resolved principal of the request that ran the work. Delivery goes only to
that `userId`'s SSE connections. Therefore:
- A different user **cannot** cause a `(userB, jobId)` entry — it exists only because a worker ran under
  that identity. They can open an SSE stream, but no worker ever publishes events tagged with their id
  for someone else's job, so they receive nothing.
- The SSE endpoint needs **no per-job authorization call and no subscribe endpoint**: opening the stream
  (authenticated → `userId`) plus the matrix entry from the publisher *is* the subscription.

`userId` is the value of the normalized **`x-ms-client-principal-name`** header (set by the auth
middleware for every path: EasyAuth, service-principal, and dev). The SSE endpoint reads it; the
downstream worker reads the same header and passes it to the bridge, so the two always agree.

## Publish API & modes
`RealtimeBridge.Publish(userId, jobId, mode, data, urlHref, urlLabel, status, message)` — non-blocking,
best-effort, never throws. Overloads let PowerShell call it with just the fields it needs.

| `mode` | Matrix effect | Delivery |
|---|---|---|
| `start` | upsert entry, store `data` as the current message | deliver `start` frame |
| `update` (default) | upsert entry, replace the current message | deliver `update` frame |
| `end` | deliver the final message, then **evict** the entry | deliver `end` frame, then remove |

`start` and `update` are both upserts to Craft; the mode is a passthrough label the frontend uses to
know whether to create, update, or finalize UI. Only `end` is special (eviction).

PowerShell:
```powershell
$jobId  = [guid]::NewGuid().ToString()
$userId = $Request.Headers.'x-ms-client-principal-name'
[Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "start",  @{ done = 0;   total = 300 })
[Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "update", @{ done = 142; total = 300 })
[Craft.Services.RealtimeBridge]::Publish($userId, $jobId, "end",    @{ done = 300; total = 300 }, "/tenants/contoso/report", "View report")
```

## Frame schema (delivered over SSE)
Required on publish: **`userId`, `jobId`** (jobId must be a GUID). Everything else is optional.
```jsonc
{
  "jobId":  "…GUID…",
  "mode":   "start | update | end",
  "seq":    123,            // monotonic; also the SSE id: for Last-Event-ID resume
  "ts":     1730000000000,  // unix ms
  "status": 413,            // optional; Craft sets 413 on oversize, downstream may set its own
  "message":"…",            // optional human text
  "url":    { "href": "/path", "label": "View report" },  // optional click-to-navigate
  "data":   { … }           // optional payload (size-capped)
}
```
`userId` is **not** echoed to the client (implicit — it is the caller's own stream).

## Size limit & oversize behavior
`data` is serialized and byte-checked against `App:Realtime:MaxMessageBytes` (default 16 KB). If it
exceeds the cap, Craft **drops the payload but still delivers a small frame** with `status: 413` and a
`message`, so the frontend gets a visible "too large" signal instead of nothing. The oversized data is
**not stored** as the current message (that is the point — protect memory); the previous good current
message is retained for reconnect replay. Only the variable `data` field is size-checked — `mode`,
`url`, `message` always ride along. Coarser memory bounds: `MaxActiveJobs` and `MaxConnections`.

## Endpoint: `GET /.craft/events`
`/.craft/*` is the reserved **host-endpoint prefix** (mirrors `/.auth/`): anything under it is Craft,
everything else is the client app. This is the only realtime HTTP surface — publish is the in-process
bridge, consume is this SSE stream.
- Authenticated → `userId` (401 if no principal). Role-gated to `http`/`frontend` nodes.
- On connect: **replay the current message** for each of the user's live entries so the UI resyncs
  immediately; then stream live frames; heartbeat comment every ~20s.
- Bypasses response buffering, sets `Cache-Control: no-cache` + `X-Accel-Buffering: no`. The connect is
  **rate-limited like any request** (DDoS defense): a long-lived stream consumes one fixed-window permit
  at connect — not per window — so it isn't penalized for staying open, but connection floods are capped
  per client. Concurrent streams are additionally bounded by `MaxConnections`.
- Closing the `EventSource` stops delivery to that browser; the entry lives until `end`/TTL so other
  tabs and reconnects still resync.

## Lifecycle & cleanup
- `end` evicts the entry immediately.
- A **TTL sweep** (`EntryTtlMinutes`) is the backstop for jobs that never send `end` (e.g., a crash).
- Disconnect removes the connection; entries persist until `end`/TTL, so brief reconnects resume.

## Resource safety (Craft-specific)
- **Pure C#, never occupies a PowerShell runspace** — publish enqueues and returns.
- **Bounded per-connection queue** (`PerConnectionQueue`) with drop-oldest coalescing (progress frames
  supersede stale ones).
- Heartbeats survive App Service (~230s) and proxy idle timeouts (Caddy fine; nginx needs the header).

## Downstream responsibilities (not Craft's)
- Generate the GUID `jobId` (frontend) and thread `jobId` + `userId` through its own code — including
  into background/orchestration payloads, since the originating HTTP request is gone by the time a
  fan-out task runs.
- Decide **when** to call `start` / `update` / `end` and know when work is complete or how N fan-out
  workers agree it is done. Correlate multiple workers to one jobId.

## Multi-tab frontend model
Server side is unchanged and tab-unaware: Craft delivers every frame to **all** of the user's SSE
connections. Two frontend models:

- **Model A — one `EventSource` per tab (recommended first).** Each tab connects; Craft fans to all.
  Coordinate on the client: **`localStorage` is the source of truth** for cached frames (keyed by
  jobId) and the dismissed set; **`BroadcastChannel` (or the `storage` event)** syncs dismissal across
  tabs instantly (dismiss once → clears everywhere); each tab renders from the shared cache with its own
  TTL. Reconnect resync is authoritative from Craft's stored current message. `seq` dedups/orders.
  Cost: N tabs = N connections — budget against `MaxConnections`.
- **Model B — one shared connection (optimize later).** A `SharedWorker` (or `BroadcastChannel`
  leader-election) holds a single `EventSource` and rebroadcasts to tabs. 1 connection per user; more
  frontend complexity. Pure frontend change — Craft is identical.

Because Craft delivers by `userId`, a job started in one tab is visible in all the user's tabs — good for
a global notification center; scope to the originating tab via a `localStorage` record if desired.

## Config (`App:Realtime`)
`Enabled`, `MaxMessageBytes`, `MaxActiveJobs`, `MaxConnections`, `PerConnectionQueue`,
`HeartbeatSeconds`, `EntryTtlMinutes`.

## M1 deliverables
- `Services/RealtimeService.cs` — matrix + connections, moded publish, GUID + size enforcement,
  reconnect replay, TTL sweep.
- `Services/RealtimeBridge.cs` — static PS/C# publish surface (mirrors `AppLifecycleBridge`).
- `Services/CraftSettings.cs` — `RealtimeSettings`.
- `Services/Program.cs` — DI + bridge init, `GET /.craft/events`, startup-gate passthrough (the endpoint
  stays under the rate limiter).
- `appsettings.json` — commented `Realtime` block.

## M2 (later)
`Publish-CraftEvent -UserId -JobId -Mode -Data -Url` PS module cmdlet; a C# consume hook if a host
service wants to observe; optional WebSocket transport. Backplane/multi-instance remain deferred.
