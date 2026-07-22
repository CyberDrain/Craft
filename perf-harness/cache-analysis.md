# CRAFT response cache + auth-header middleware — profile (frontend+http mode)

**What:** where time goes on a cacheable `/API/List*` request in frontend+http mode — the inbound
auth-header transform and the disk-backed response cache. Measured with `run-cache.ps1` (opt-in
`CRAFT_CACHE_TIMING` profiler, `Services/CacheProfiler.cs`), 2 vCPU, 20 VUs, SWA principal header on every
request. Segments (µs/request): auth (the `x-ms-client-principal` middleware) · roleHash (`GetUserRoleHash`)
· keyBuild (`BuildCacheKey`) · get (whole `CacheService.Get`), of which **disk** = `File.Exists` +
`File.ReadAllTextAsync` of the body · set (miss-path disk write).

## The path

```
Kestrel → auth middleware (decode/transform x-ms-client-principal) → /API/{List*} handler
   ├─ roleHash   GetUserRoleHash: base64 decode + JSON parse + SHA256 of the roles
   ├─ keyBuild   BuildCacheKey: endpoint + sorted query + role hash
   ├─ get        CacheService.Get: in-memory index lookup → File.Exists → File.ReadAllTextAsync(body)
   │               HIT  → write cached body
   │               MISS → PS invoke (Invoke-ListPerf) → cache.Set (File.WriteAllTextAsync)
```

## Measurements

**Cache HIT — dominated by the per-hit disk read of the body, and it scales with body size:**

| segment (µs/hit) | N=50 (~small) | N=2000 (~150 KB) |
|---|--:|--:|
| **get** | **162** | **~5900** |
| &nbsp;&nbsp;⤷ **disk read** | **154 (95%)** | **~5650 (96%)** |
| roleHash | 32 | ~200\* |
| keyBuild | 7 | ~260\* |
| auth | 20 | ~130\* |
| set | ~12 | ~30 |

\* the non-disk segments inflate at N=2000 only because the dominating disk read slows the whole system under
load (wall-clock contention); at N=50 they are the clean values (roleHash 32, keyBuild 7, auth 20).

**Cache MISS (unique query → PS invoke + Set):**

| segment (µs) | N=50 | N=2000 |
|---|--:|--:|
| set (disk write of body) | ~760 | ~1990 |
| get (empty) | ~3 | ~3 |
| roleHash / keyBuild / auth | ~25 / 6 / 27 | ~90 / 56 / 26 |

**End-to-end throughput (N=2000, large body, k6):**

| | req/s | latency avg | p95 |
|---|--:|--:|--:|
| **HIT** | 1219 | 16 ms | 56 ms |
| **MISS** | 127 | 157 ms | 242 ms |

## Findings

1. **A cache HIT re-reads and re-decodes the entire body from disk every time.** The "disk-backed in-memory
   cache" keeps only the **index** in memory; the bodies are disk-only. `File.ReadAllTextAsync` is **~95% of
   the hit cost** and scales with body size — a 150 KB `List*` response costs **~5.7 ms per hit** just to read
   the file and decode it to a string. (OS page cache makes the physical read cheap; the cost is the async
   file I/O + UTF-8 decode + large-string allocation, repeated on every hit.)
2. **The cache still wins big vs a miss** — 1219 vs 127 req/s for large bodies (a miss rebuilds + serializes
   2000 items in PowerShell, ~150 ms). So the cache is doing its job; the disk read is an *avoidable* tax on
   top of an already-worthwhile hit.
3. **auth + roleHash + keyBuild are minor** (~60 µs combined, small body). `roleHash` (base64 + JSON + SHA256)
   is the largest of the three at ~32 µs and is computed **twice** per miss (once for Get, once for Set). Not
   a priority next to the disk read.

## The optimization: an in-memory body tier (make it actually "in-memory")

Keep hot cache bodies in memory (a size-bounded LRU of the body strings) so a HIT returns from RAM instead of
re-reading + re-decoding the file. The disk stays as the durable/overflow backing. Expected: the hit path
drops from ~160 µs (small) / ~5.9 ms (large) to ~roleHash+keyBuild+memcopy (~40–70 µs) regardless of body
size — i.e. **~2.5× faster small hits and ~10×+ faster large hits**, lifting hit throughput well past the
current 1219 req/s (which is bottlenecked by the disk read). Trade-off: bounded extra RAM for the hot set.
Secondary: drop the redundant `File.Exists` stat before the read (the read already handles a missing file),
and reuse the computed `roleHash`/`cacheKey` across the Get and Set paths in the handler.

## Applied optimizations & results

Two changes (both shipped):
1. **In-memory body tier** — `CacheEntry.Body` holds the body string in RAM (bounded LRU, budget
   `App:Cache:MaxMemoryBytes`, default 64 MiB; 0 = disk-only). A HIT returns from memory; the disk stays as
   durable/overflow backing. **Subtlety found via profiling:** the stale-while-revalidate refresh republishes
   the entry on *every* hit, so the body must be attached to the entry **before** it's published to the index
   — publishing a body-less entry first made ~99.75% of Gets fall through to disk (only 5/2000 mem hits).
   After fixing the publish order: 100% mem hits at 1 VU, ~90% at 20 VU.
2. **Secondary:** dropped the redundant `File.Exists` stat before the read; the handler computes
   `roleHash`/`cacheKey` once and reuses it for the write-back (was recomputed on the miss path).

A/B (frontend+http, 2 vCPU, 20 VUs, `-DiskOnly` = before):

| scenario | disk-only (before) | mem-tier (after) | change |
|---|--:|--:|--:|
| **N=2000 (~150 KB) req/s** | 1208 | **3110** | **+157 %** |
| N=2000 latency avg | 16.4 ms | 6.3 ms | −62 % |
| N=2000 cache `get` | 4527 µs (disk 4415) | 825 µs (disk 735) | −82 %, ~90 % mem hits |
| **N=50 (~2 KB) req/s** | 5544 | **8010** | **+44 %** |
| N=50 latency avg | 3.5 ms | 2.4 ms | −31 % |
| N=50 cache `get` | 136 µs (disk 127) | **2.6 µs** (disk 0) | −98 %, 100 % mem hits |

The gain scales with body size (the disk read it removes is proportional to body size): +44 % throughput for
small `List*` responses, **+157 % for large ones**, with latency down 31–62 %. The residual ~10 % disk reads
at 20 VU on large bodies come from the constant refresh churn racing the promote — diminishing returns to
chase further. Trade-off: bounded extra RAM for the hot set (default 64 MiB).

## Reproduce

```powershell
pwsh scripts\run-cache.ps1 -Mode hit  -N 50     # small-body hit breakdown
pwsh scripts\run-cache.ps1 -Mode hit  -N 2000   # large-body hit — disk read dominates
pwsh scripts\run-cache.ps1 -Mode miss -N 2000   # miss = PS invoke + Set (disk write)
```
