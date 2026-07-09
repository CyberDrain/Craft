# Craft — Risk Register Code Evidence & Mitigation Analysis

**Companion to:** *Project Craft — Engineering Execution Plan & Security Risk Assessment*
**Purpose:** For every risk in the register (R1–R14) and every "positive finding", this document points at the **actual code** as it exists in the repository today, quotes the relevant lines, and states plainly whether the risk is *mitigated*, *not a concern*, *partially mitigated*, or *open* — and if open, how it can be mitigated. This is the **"before" baseline**: each entry is written so a follow-up change can be shown as a before/after diff against the exact file and line cited.

**Grounding rule:** Every claim below is tied to a file and line range that was read directly. Where the parent assessment overstates or misstates the current state, that is called out explicitly in the **Errata** section (§3) — nothing here is inferred or assumed.

**Repository state:** branch `dev`. File/line references are accurate as of this review; re-verify after any material refactor of `Services/`.

> **Change set applied (§1a).** After the initial review, several items were actioned in code: **R8** (fail-closed storage), **R7** (Kestrel limits + rate limiter on by default), **R5 + R14** (native OIDC login/session subsystem removed — including the now-dead `ValidateIdToken`/OIDC-discovery code and its NuGet packages; token validation is owned by the EasyAuth platform), and **R10** (secure-by-default CSP). Rows marked *Closed (this change)* / *Hardened (this change)* / *Mitigated (this change)* reflect the post-change state; the pre-change evidence is preserved so the before/after is legible. Build verified clean (`dotnet build`, 0 warnings / 0 errors). Out of scope by decision: **R4** (accepted — relies on the EasyAuth platform header guarantee; see below), **R11** (owned elsewhere), **R3** (owner verifying Azure dir-permissions), **R6** (no action — SSH off by default).

---

## 1. Summary verdict per risk

| ID | Register status | Code-verified state (post-review) | Notes |
|----|-----------------|---------------------|-------|
| R1 | Open — GA gate | **Feature, not a gap — needs guardrail** | Exclusive worker checkout + per-invocation reset exist; shared caches are process-wide (name-keyed, not tenant-keyed) *by design* (worker warmup). Residual = usage discipline: never cache tenant data un-keyed. |
| R2 | Partially mitigated | **Low / accepted** | **Both** images default to Production; dev mode only via local compose override. Not a deployed-image risk. |
| R3 | Open — GA gate | **Open (confirmed)** | No `USER` directive; container runs as root. Feasible once SSH (root login) leaves the image (R6). |
| R4 | Open | **Accepted — mitigated by the platform** | Trust of `x-ms-client-principal` relies on the documented EasyAuth guarantee that App Service strips inbound principal headers ("External requests aren't allowed to set these headers"). The SWA-format pass-through is intentionally retained; direct exposure without EasyAuth is unsupported. |
| R5 | Accepted (beta) | **Closed (this change)** | Native OIDC session/cookie subsystem removed — no session key derived from the client secret remains. |
| R6 | Partially mitigated | **Low (off by default)** | SSH installed but `CRAFT_SSH_ENABLED=false`; `Docker!` fallback only if SSH is explicitly enabled without a password. Planned: drop SSH from the dev image. |
| R7 | Open | **Hardened (this change)** | Kestrel limits now unconditional; 10-min default timeout; `MaxRequestBodySize=100 MB`; connection cap 200; rate limiter **on by default** (300 req / 10 s per client). |
| R8 | Open | **Closed (this change)** | `UseDevelopmentStorage=true` fallback removed from all 3 sites; fails startup in production unless explicitly opted in. |
| R9 | Partially mitigated | **Open for self-host (mostly downstream)** | Deployment template owned by CIPP-NG; Craft-layer defaults (cookie `Secure`) remain Craft's. |
| R10 | Open | **Mitigated (this change)** | Secure-by-default CSP now shipped as the `ContentSecurityPolicy` default (overridable); other security headers intentionally deferred to the edge/hosted app. |
| R11 | Open | **Open — owned elsewhere** | No CodeQL / Trivy / SBOM / Dependabot on this repo; ownership assigned outside this workstream. |
| R12 | Open | **Compensated by platform** | No Docker `HEALTHCHECK`; App Service health-probe + restart-on-failure + `ContainerHealthMonitor` cover it. |
| R13 | Open | **Largely mitigated** | Outbound HTTP logging scrubs OAuth/credential keys; only arbitrary app-message redaction (CIPP-NG) is residual. |
| R14 | Accepted | **Closed (this change)** | In-memory session store removed with the OIDC subsystem. |

---

## 1a. Change set applied (before → after)

These changes were made after the review discussion. Each is a self-contained before/after against the exact files cited in §2.

**R8 — fail-closed storage (`StorageSettings.ResolveConnection`).**
- *Before:* three services fell back to `?? "UseDevelopmentStorage=true"` when no connection string was configured ([AuthService.cs:133](Services/AuthService.cs), [OrchestratorTableStore.cs:35](Services/OrchestratorTableStore.cs), [SetupService.cs:765](Services/SetupService.cs)).
- *After:* a single resolver [`StorageSettings.ResolveConnection`](Services/CraftSettings.cs) is used by all three. It resolves *explicit setting → `AzureWebJobsStorage`*, and **throws at startup** if neither is set unless the dev-emulator is explicitly allowed (`App:Storage:AllowDevelopmentStorage=true`, `CRAFT_ALLOW_DEV_STORAGE=true`, or `ASPNETCORE_ENVIRONMENT=Development`). A production misconfiguration now fails loudly instead of pointing RBAC/state at a non-existent emulator.

**R7 — Kestrel limits (`KestrelLimitsSettings`, `RateLimitSettings`).**
- *Before:* the entire Kestrel limits block was skipped when no timeout was configured; `MaxRequestBodySize` unset (~28 MB default); `MaxConcurrentConnections = null` (unlimited); no rate limiter.
- *After* ([Program.cs](Services/Program.cs) Kestrel block): limits applied **unconditionally**; timeout defaults to **600 s (10 min)**; `MaxRequestBodySize` = **100 MB** (configurable, 0 = unlimited); `MaxConcurrentConnections` = **200** (configurable, ≤0 = unlimited); slow-loris data-rate limits retained. A per-client fixed-window rate limiter is **enabled by default** at **300 requests / 10 s**, partitioned by `x-ms-client-principal-name` and falling back to the first `X-Forwarded-For` hop (so anonymous callers behind the App Service load balancer aren't collapsed into one partition). Disable via `App:RateLimit:Enabled=false`.

**R5 + R14 — native OIDC login/session removed.**
- *Before:* [AuthService.cs](Services/AuthService.cs) contained a full OIDC login flow (`GetLoginUrl`/`HandleCallback`/`ExchangeCodeForTokens`), an in-memory `_sessions` store (R14), and an AES session cookie whose key was derived from the Entra client secret (R5); [Program.cs](Services/Program.cs) mapped `/login`, `/.auth/login/aad`, `/.auth/callback`, `/logout`, `/.auth/logout` and a session-based `/.auth/me`.
- *After:* all of the above removed. Authentication is owned by the upstream Azure App Service EasyAuth layer; Craft only transforms the injected `x-ms-client-principal` header and enforces authorization via the `allowedUsers` table. R5 and R14 are thereby eliminated rather than merely accepted. The `ValidateIdToken` / OIDC-discovery code was **also removed** in a follow-up: it had no callers (its only caller was the deleted `HandleCallback`), and token signature/issuer/audience validation is performed by the EasyAuth platform before it injects the header — Craft never receives a raw JWT to validate. The two `System.IdentityModel.*` / `Microsoft.IdentityModel.*` NuGet packages were dropped with it.

**R4 — accepted (no code change).** An in-app strip of non-App-Service-format inbound headers was considered and then **reverted by decision**: trust of `x-ms-client-principal` rests on the documented Azure guarantee that App Service EasyAuth strips inbound principal headers ("External requests aren't allowed to set these headers, so they're present only if App Service sets them" — [MS Learn](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-user-identities#access-user-claims-in-app-code)). The middleware keeps the translate-then-convert-to-SWA path unchanged, including the SWA-format pass-through. Residual exposure exists only if the container is deployed **without** EasyAuth in front (direct `:8080`), which is an unsupported configuration. See the R4 detailed entry.

**R10 — secure-by-default CSP.**
- *Before:* the CSP middleware only emitted a header when `App:Frontend:ContentSecurityPolicy` was configured, and the default was null (commented out in `appsettings.json`) — so no CSP shipped out of the box.
- *After* ([CraftSettings.cs](Services/CraftSettings.cs) `FrontendSettings.ContentSecurityPolicy`): the property now **defaults** to the CIPP-compatible policy `default-src https: blob: 'unsafe-eval' 'unsafe-inline'; object-src 'self' blob:; img-src 'self' blob: data: *`, so a CSP is emitted even when unconfigured. The hosted app (CIPP-NG) or a deployment can override it via config, or set it to `""` to disable. Other response headers (HSTS, `X-Content-Type-Options`, `X-Frame-Options`) were intentionally **not** added as middleware — deferred to the edge/hosted app per review decision.

---

## 2. Per-risk breakdown

### R1 — Cross-request / cross-tenant state bleed in shared runspaces  *(Critical)*

**What the code actually does — three separate controls:**

**(a) Workers are checked out exclusively — one request at a time.** The pool is a `BlockingCollection`; a checkout *removes* the worker from the collection until it is reclaimed, so two requests can never share one runspace concurrently.

`Services/PowerShellWorkerPool.cs` (checkout removes from pool):
```csharp
public PowerShellWorker? CheckoutHttp(TimeSpan timeout)
{
    if (!_httpReady.IsSet) _httpReady.Wait(timeout);
    if (_httpPool.TryTake(out var w, timeout))   // worker leaves the collection here
    { ... return w; }
    return null;
}
```
The worker is only returned by `Reclaim(...)`, which re-adds it: `if (isHttp) _httpPool.Add(worker); else _bgPool.Add(worker);` (`PowerShellWorkerPool.cs:545`). **→ Concurrent in-runspace bleed is not possible; the risk is strictly *sequential* reuse.**

**(b) Every invocation runs a reset before the worker returns to the pool.** This mirrors Azure Functions' `ResetRunspace`. `Cleanup()` runs in the `finally` of `InvokeAsync` (`PowerShellWorker.cs:256`):
```csharp
private void Cleanup()
{
    _pwsh.Commands.Clear();
    _pwsh.Streams.ClearStreams();
    CleanupGlobalVariables();   // remove any global var not present at init
    CleanupJobs();              // Remove-Job -Force for anything left running
}
```
`CleanupGlobalVariables()` (`PowerShellWorker.cs:308–329`) removes every global-scope `PSVariable` that is not in a baseline snapshot captured once at worker init (`s_builtinGlobalVars`, `PowerShellWorker.cs:51–58`). **→ Global-scope leftovers from one request do not survive into the next.**

**(c) The residual, *by-design* sharing surface — this is the actual R1 exposure.** Two mechanisms are deliberately process-wide and are **not** reset between requests:

1. Named shared caches (`Services/PowerShellRunnerService.cs:24, 45`):
   ```csharp
   private static readonly ConcurrentDictionary<string, Hashtable> SharedCaches = new(StringComparer.OrdinalIgnoreCase);
   public static Hashtable GetSharedCache(string name) =>
       SharedCaches.GetOrAdd(name, _ => Hashtable.Synchronized(new Hashtable()));
   ```
   Caches are keyed **by name only** — there is no tenant/customer dimension in the key. They are injected into a module's `$script:` scope on each worker (`PowerShellWorker.cs:94–111`), and `CleanupGlobalVariables()` deliberately skips module-scoped variables (`if (v.Module != null) continue;`, `PowerShellWorker.cs:319`).
2. Process env vars set from PowerShell (`PowerShellRunnerService.cs:53–57`, `SetProcessEnvVar` → `Environment.SetEnvironmentVariable`) are visible to every worker.

**Assessment:** The *platform* provides sequential isolation for command/stream/global-variable state (controls a + b). It **cannot** isolate anything the hosted code chooses to cache in a named shared cache or module scope (control c) — that is a deliberate cross-runspace sharing feature. Whether this becomes real cross-tenant bleed depends on whether the hosted CIPP-NG code caches tenant-specific tokens/credentials in these caches *without a tenant-keyed cache key*. That determination is application-layer (CIPP-NG), which the parent assessment scopes out — but Craft is what makes the shared, name-keyed cache available, so the platform owns the *guardrail*.

**How to close it (before → after):**
- Add an isolation regression test asserting a `$global:`/`$script:` variable set in request 1 is absent in request 2 on the same worker (none exists today — no test project is present in the repo).
- Document and lint against un-keyed tenant caching: any `GetSharedCache` used for tenant data must include the tenant id in the cache key.
- Consider tagging module-injected caches for reset, or namespacing shared caches by tenant at the platform layer.

---

### R2 — Development-mode auth bypass reaching production  *(Critical)*

**What the code does:** In `Development`, unauthenticated requests are handed a fabricated principal with configured roles.

`Services/Program.cs:937–953`:
```csharp
else if (app.Environment.IsDevelopment())
{
    logger.LogDebug("[Auth] Dev auth bypass: injecting dev principal for {Path}", path);
    var devPrincipal = new { identityProvider = "aad", userId = ..., userRoles = CraftSettings.Auth.DevRoles.ToArray() };
    ...
    context.Request.Headers["x-ms-client-principal"] = devBase64;
}
```
The bypass lives only in the auth middleware; Craft no longer maps `/.auth/me` (App Service EasyAuth serves it at the platform edge). A startup warning is emitted (`Program.cs:419–423`).

**Current mitigations that exist:**
- The entire bypass is gated on `app.Environment.IsDevelopment()`, which is false unless `ASPNETCORE_ENVIRONMENT=Development` is set.
- The **release image does not set that variable** — `build/Dockerfile.release` contains no `ASPNETCORE_ENVIRONMENT`, so it defaults to `Production`. (The only place it is set to `Development` is `build/.env.example:11`, used for the local dev compose image, not the release image.)

**Assessment:** Partially mitigated. The default posture of the production image is safe, and the bypass is loud. What is **missing** is a hard fail-closed: nothing refuses to boot if a release build somehow has `ASPNETCORE_ENVIRONMENT=Development`.

**How to close it:** Add a startup guard that, when the build is the release/production image (e.g. keyed off a compile constant or an image-baked env flag), either refuses to start or force-disables the dev principal even if `IsDevelopment()` is true. This is a small, testable change and is a natural before/after.

---

### R3 — Container runs as root  *(Critical)*

**What the code does:** `build/Dockerfile.release` has **no `USER` directive**. The final `runtime` stage installs packages, copies the app, and sets `ENTRYPOINT ["/entrypoint.sh"]` — all as root. The optional SSH server is also configured for root login (`docker/sshd_config`, root password set in `entrypoint.sh:18`).

**Assessment:** Open, confirmed. Kestrel binds `:8080` (non-privileged) so a non-root user is feasible; the blocker is verifying PowerShell runspaces, the writable log/cache directories, and `/home/craft` restart tracker all work under a fixed UID.

**How to close it:** Add a non-root `USER` after the file copy, `chown` the writable paths (log dir, cache dir, `/run/sshd` if SSH used), and verify pwsh + Kestrel start. Before/after = Dockerfile diff plus a `docker run ... id` showing non-zero UID.

---

### R4 — `x-ms-client-principal` header spoofing on direct exposure  *(High)* — **ACCEPTED (mitigated by the platform)**

**What the code does:** the auth middleware translates the App Service EasyAuth (`claims`) format — re-deriving roles from the `allowedUsers` table ([Program.cs:924](Services/Program.cs:924)) — and passes an already-SWA-format `x-ms-client-principal` (carrying `userRoles`) through unchanged. It does **not** independently verify that the header came from the front end; it trusts the platform.

**Why this is accepted rather than stripped in-app.** Azure App Service Authentication guarantees the trust boundary at the edge — from the [Microsoft docs](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-user-identities#access-user-claims-in-app-code): *"App Service makes the claims available to your code by injecting them into request headers. **External requests aren't allowed to set these headers, so they're present only if App Service sets them.**"* So in the supported deployment (EasyAuth in front of the container) a client cannot inject or forge `x-ms-client-principal*`; any header we receive was set by the platform after it validated the token's signature/issuer/audience. An in-app strip of non-App-Service-format headers was implemented and then **reverted by decision** — it duplicated the platform guarantee and added no value inside the supported boundary, while the translate-then-convert-to-SWA path is what CIPP-NG actually needs (see the header-format discussion / §2 of the parent notes).

**Residual exposure:** only if the container is deployed **without** EasyAuth in front (direct `:8080`), where the platform stripping no longer applies and a spoofed SWA-format header would be trusted. That is an unsupported configuration (ties to R9). If direct exposure ever becomes a supported mode, revisit with an explicit "EasyAuth-mode" gate + inbound header strip.

**Assessment:** Accepted. The header-shape vector is closed *by the platform* in every supported deployment; no in-app code is required or retained for it.

---

### R5 — Session key derived from the Entra client secret  *(High, accepted for beta)* — **CLOSED (this change)**

**Before:** `Services/AuthService.cs` derived the session-cookie AES-256 key from the Entra client secret — `_cookieKey = SHA256.HashData(Encoding.UTF8.GetBytes(ClientSecret + "_craft_session_key"))` — and encrypted the session id with it (`EncryptSessionId`, AES-256-CBC, per-message IV). This coupled session integrity to client-secret rotation and reused one secret for both token exchange and session confidentiality.

**After:** the entire session-cookie subsystem was removed (`CookieKey`, `EncryptSessionId`/`DecryptSessionId`, `SetSessionCookie`, the `_sessions` store, and the OIDC login flow that created sessions — see §1a). There is no longer a Craft-derived session key of any kind: authentication and session handling are owned by the upstream Azure App Service EasyAuth layer. The client-secret→session-key coupling that defined R5 no longer exists.

**On `ValidateIdToken`:** initially kept, then removed once the analysis confirmed it was dead code — its only caller was the deleted `HandleCallback`, and in the EasyAuth-fronted model the platform validates the token signature/issuer/audience before injecting the claims-principal header (Craft never sees a raw JWT). Token validation is therefore owned by EasyAuth, not by Craft. The OIDC-discovery helper and the two `System.IdentityModel.*` / `Microsoft.IdentityModel.*` packages were dropped with it.

**Verification:** `dotnet build` clean; grep confirms no `CookieKey` / `EncryptSessionId` / `ValidateIdToken` references remain in `Services/`.

---

### R6 — SSH sidecar with a conventional default password  *(High)* — **better than the register states**

**What the code does:** SSH is **disabled by default** and only starts when explicitly enabled.

`build/Dockerfile.release:61–62`:
```dockerfile
# SSH is opt-in. Set CRAFT_SSH_ENABLED=true at runtime to start sshd on port 2222.
ENV CRAFT_SSH_ENABLED="false"
```
`docker/entrypoint.sh:9–28` only launches `sshd` when `CRAFT_SSH_ENABLED` is truthy, and only then falls back to the Azure-convention password if the operator set none:
```sh
ssh_enabled="${CRAFT_SSH_ENABLED:-false}"
case "$(... "$ssh_enabled" ...)" in
  1|true|yes|on)
      ssh_password="${CRAFT_SSH_PASSWORD:-Docker!}"   # fallback only when enabled + unset
      echo "root:${ssh_password}" | chpasswd
      /usr/sbin/sshd ...
```

**Assessment:** The register's treatment ("default password only when `CRAFT_SSH_PASSWORD` unset; disable SSH by default") is **already implemented**. The `Docker!` fallback is only reachable when an operator has *opted into* SSH without setting a password. The daemon is installed but inert. Two residual notes: (1) the fallback is a hardcoded default credential in the image (relevant to the "no hardcoded secrets" positive finding — see §3); (2) when enabled it is root login. Inside the App Service SCM boundary this matches the platform convention and is acceptable.

**How to further harden:** When SSH is enabled but `CRAFT_SSH_PASSWORD` is unset, log a loud warning (or refuse) rather than silently using `Docker!`; document that self-host templates leave `CRAFT_SSH_ENABLED=false`.

---

### R7 — No rate limiting or request-body-size limits at Kestrel  *(High)* — **HARDENED (this change)**

**Before:** the Kestrel limits block ran **only when a timeout was set** (`if (kestrelTimeout > 0)`), so a deployment with no configured timeout got *no* limits at all. Even when it ran: `MaxRequestBodySize` was never set (ASP.NET Core ~28 MB default), `MaxConcurrentConnections = null` (unlimited), and there was no rate limiter anywhere in the codebase. Slow-loris `MinRequestBodyDataRate`/`MinResponseDataRate` and header timeouts were the only protections, and only conditionally.

**After** (`Services/Program.cs` Kestrel block + `KestrelLimitsSettings`/`RateLimitSettings` in `CraftSettings.cs`):
- Limits are applied **unconditionally** (the `if (kestrelTimeout > 0)` gate is gone).
- Request timeout **defaults to 600 s (10 min)** when not otherwise configured.
- `MaxRequestBodySize` = **100 MB**, configurable via `App:Limits:MaxRequestBodyMB` (0 = unlimited).
- `MaxConcurrentConnections` = **200**, configurable via `App:Limits:MaxConcurrentConnections` (≤0 = unlimited).
- Slow-loris data-rate limits and HTTP/2 caps retained.
- An **opt-in** per-client fixed-window rate limiter is wired (`builder.Services.AddRateLimiter` + `app.UseRateLimiter`), partitioned by `x-ms-client-principal-name` (fallback: remote IP), **disabled by default** (`App:RateLimit:Enabled` / `CRAFT_RATELIMIT_ENABLED`) pending tuning against the HTTP worker-pool size.

**Assessment:** Hardened. The body-size cap, connection cap, and always-on slow-loris protection are live; the rate limiter is staged behind a flag so it can be enabled after we agree on the window/pool ratio (the HTTP pool defaults to 2 workers, so the limiter is the higher-value control — noted for the follow-up discussion).

**Verification:** `dotnet build` clean. Enabling the limiter (`CRAFT_RATELIMIT_ENABLED=true`) and issuing a burst is the before/after (429 responses past the window).

---

### R8 — Development-storage fallback when storage config is missing  *(High)* — **CLOSED (this change)**

**Before:** three storage-touching services independently fell back to `?? "UseDevelopmentStorage=true"` when neither an explicit setting nor `AzureWebJobsStorage` was configured — `AuthService.cs:133` (`allowedUsers` RBAC table), `OrchestratorTableStore.cs:35` (orchestrator state), `SetupService.cs:765` (first-user seeding). In production a missing connection string silently pointed authorization and orchestrator state at a non-existent local emulator.

**After:** a single resolver `StorageSettings.ResolveConnection(explicitConnection, purpose)` in `Services/CraftSettings.cs` is used by all three sites:
```csharp
public string ResolveConnection(string? explicitConnection, string purpose)
{
    if (!string.IsNullOrWhiteSpace(explicitConnection)) return explicitConnection;
    var env = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
    if (!string.IsNullOrWhiteSpace(env)) return env;
    if (DevStorageAllowed) return "UseDevelopmentStorage=true";
    throw new InvalidOperationException($"No Azure Storage connection is configured for {purpose}. ...");
}
```
The dev-emulator fallback is only returned when explicitly opted in — `App:Storage:AllowDevelopmentStorage=true`, `CRAFT_ALLOW_DEV_STORAGE=true`, or `ASPNETCORE_ENVIRONMENT=Development`. Otherwise it **throws**, so the service fails to construct and the host fails to start (mirroring the existing fail-closed pattern for `AUTH_SECRET` at `AuthService.cs:94`).

**Assessment:** Closed. A production misconfiguration now fails loudly at startup instead of resolving RBAC/state to an empty emulator. The connection is a first-class config surface (explicit setting → `AzureWebJobsStorage`), and the emulator is a deliberate, logged dev-only opt-in.

**Verification:** `dotnet build` clean; grep confirms no `UseDevelopmentStorage=true` literal remains in `Services/`.

---

### R9 — Self-hosted misconfiguration (TLS, public container, weak app reg)  *(High)*

**What exists:**
- A first-run setup wizard (`Services/SetupService.cs`) that creates the EasyAuth app registration (`CreateAuthAppRegistration`, ~`:167`), configures App Service `authsettingsV2` (`ConfigureAppServiceAuth`, ~`:454`) with a secure-ish auth default `unauthenticatedClientAction = RedirectToLoginPage` and `allowedApplications`/`allowedAudiences` restrictions, and can store the secret in Key Vault when `Setup.KeyVaultName` is set (`:481–496`).
- Self-host documentation: `docs/configuration.md` and `docs/deployment-modes-plan.md` (session model, Key Vault secret references, sticky-session caveat for multi-instance).
- Session cookies are `HttpOnly`, `SameSite=Lax`, and `Secure = context.Request.IsHttps` (`AuthService.cs:509–516`).

**What is missing / the exposure:**
- **No ARM/Bicep template exists in the repo** (no `*.bicep`, no `azuredeploy*.json`, no `deploy/`/`infra/` directory, no "Deploy to Azure" button). The register's treatment ("ship hardened ARM/Bicep template as the only documented path") is **not yet implemented**.
- Setup configures application-layer auth but **does not enforce infrastructure TLS** — no `httpsOnly`, `minTlsVersion`, FTPS, or public-network settings are touched (grep of `SetupService.cs` returns none).
- The cookie `Secure` flag is conditional on `context.Request.IsHttps`. Behind a TLS-terminating front end that forwards over plain HTTP to `:8080`, `IsHttps` can be false unless forwarded-proto handling is in place — worth verifying so session cookies are always marked `Secure` in production.
- No startup validation warns on insecure config (the only fail-fast is "no deployment roles enabled", `Program.cs:37–67`).

**Assessment:** Open for the self-host threat model — which the residual-risk statement already makes a GA gate. The hosted-by-CyberDrain model is compensated operationally; self-host is not.

**How to close it:** Publish a hardened ARM/Bicep template (httpsOnly=true, minTlsVersion=1.2, FTPS disabled, SSH env left false) as the sole documented path; add a startup configuration-validation page/log that flags missing TLS enforcement and plaintext secrets. Before/after = template diff + a validation screen showing pass/fail.

---

### R10 — No security headers (CSP, HSTS, X-Content-Type-Options)  *(Med)* — **MITIGATED (this change)**

**What the code does:** A CSP middleware exists, but only fires if a CSP string is configured:

`Services/Program.cs:642–659`:
```csharp
if (!string.IsNullOrEmpty(CraftSettings.Frontend.ContentSecurityPolicy))
{
    var csp = CraftSettings.Frontend.ContentSecurityPolicy;
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            if (!headers.ContainsKey("Content-Security-Policy"))
                headers["Content-Security-Policy"] = csp;
            return Task.CompletedTask;
        });
        await next();
    });
}
```
**Before:** the setting defaulted to null (the `appsettings.json` example was commented out), so the CSP middleware — though present — emitted nothing out of the box.

**After** ([CraftSettings.cs](Services/CraftSettings.cs) `FrontendSettings.ContentSecurityPolicy`): the property now **defaults** to the CIPP-compatible policy:
```
default-src https: blob: 'unsafe-eval' 'unsafe-inline'; object-src 'self' blob:; img-src 'self' blob: data: *
```
A CSP is therefore emitted secure-by-default. The hosted app (CIPP-NG) or a deployment overrides it via `App:Frontend:ContentSecurityPolicy`; `""` disables it. (`'unsafe-eval'`/`'unsafe-inline'` are required by the CIPP frontend; a deployment can tighten the policy via config.)

**Assessment:** Mitigated. Per the review decision, `Strict-Transport-Security` / `X-Content-Type-Options` / `X-Frame-Options` were **not** added as host middleware — those are left to the edge (Azure front end) / hosted app rather than duplicated here.

**Verification:** `dotnet build` clean. Before/after = `curl -I` now shows a `Content-Security-Policy` header on responses with no configuration applied.

---

### R11 — Release pipeline has no image or code scanning  *(Med)*

**What the code does:** `.github/workflows/` contains exactly two files — `dev-container.yml` and `release-container.yml` — and both only build and push the image (`docker/build-push-action@v6`). There is:
- **No CodeQL** (no `codeql.yml`, no `github/codeql-action`).
- **No PSScriptAnalyzer** workflow.
- **No Trivy / Grype image scan.**
- **No SBOM (SPDX/syft) generation.**
- **No `.github/dependabot.yml`** and **no `dependency-review-action`.**

`release-container.yml` build step (only security-relevant action is the build itself):
```yaml
- name: Build and push
  uses: docker/build-push-action@v6
  with:
    context: .
    file: build/Dockerfile.release
    push: true
    ...
```

**Assessment:** Open, confirmed. **This directly contradicts §6 (Code analysis plan) and §7 (Dependency review) of the parent assessment, which describe these workflows as if they already exist.** They do not — see Errata §3. `SECURITY.md` provides a vulnerability-disclosure process (inherited from CIPP), which is a genuine positive but is policy, not pipeline tooling. **Ownership:** by review decision this item is owned outside this workstream (not actioned here). The §3 scope note still applies — the C# and container-image surfaces belong on *this* repo and cannot be inherited from CIPP-NG, whoever picks it up.

**How to close it:** Add CodeQL (`csharp`), PSScriptAnalyzer (SARIF), Trivy image scan pre-push, SPDX SBOM export, and `.github/dependabot.yml` (nuget, github-actions; npm when the CIPP-NG monorepo becomes the frontend source). Before/after = the workflow files existing and green.

---

### R12 — No `HEALTHCHECK`; failed workers may serve degraded state  *(Med)* — **open but compensated**

**What the code does:** `build/Dockerfile.release` has **no `HEALTHCHECK` instruction**. However, an application-level crash-loop guard exists: `Services/ContainerHealthMonitor.cs` records restart attempts to `/home/craft/restart-tracker.json` and, if the same instance crashes more than `MaxRestarts` times within the window, sets `ShouldBlockStartup = true` so Kestrel never binds and Azure reallocates the worker (`ContainerHealthMonitor.cs:117–127`).

**Assessment:** Open with respect to a *readiness* health check (nothing detects a live-but-degraded worker pool and reports unhealthy), but the crash-loop path is covered by app logic rather than a Docker `HEALTHCHECK`. Note Azure App Service uses its own health-check-path probe, not the Docker `HEALTHCHECK` directive, so the remediation should target both.

**How to close it:** Add a `HEALTHCHECK` (and/or document an Azure health-check path) wired to a readiness endpoint that reflects worker-pool state (`ContainerHealthMonitor` + pool-ready signals). Before/after = `docker inspect` showing health status transitions.

---

### R13 — Filesystem logs may capture sensitive request data  *(Med)* — **largely mitigated**

**What the code does — the highest-risk vector is already handled.** The outbound HTTP diagnostic logger scrubs credential-bearing form keys before anything reaches disk. `Services/HttpDiagnosticListener.cs:211–236`:
```csharp
private static readonly HashSet<string> s_sensitiveFormKeys = new(StringComparer.OrdinalIgnoreCase)
{ "refresh_token", "client_secret", "assertion", "client_assertion", "code", "password", };
...
// scrub BEFORE truncating so a sensitive key past the 300-char cutoff is still redacted
var scrubbed = ScrubSensitiveFormValues(body);
```
It also only logs *slow* requests and truncates bodies to 300 chars. So the concrete high-risk path — logging OAuth token-exchange / partner-API request bodies (which carry `client_secret`, `refresh_token`, auth `code`) — is redacted.

**Residual:** `Services/FileLoggerProvider.cs` (the general file logger, `RotatingFileLogger.Log`, `:230–258`) writes log *messages* verbatim — it has no redaction. So if hosted code logs a secret inside a plain message string (`LogInformation($"...{token}")`), it lands raw. That is application-message discipline (CIPP-NG), not a framework gap. Rotation + retention cap (`_maxFileCount`, `:162–194`) bound the window.

**Assessment:** Largely mitigated for the framework's own outbound-request logging; the residual is arbitrary app-message redaction, which belongs with the hosted application.

**How to further close it:** Optionally add a redaction pass (bearer-token / `AUTH_SECRET` regex) in `FileLoggerProvider`'s write path for defence-in-depth. Before/after = a planted token in a general log message showing `***`.

---

### R14 — In-memory sessions: no revocation propagation, restart logs users out  *(Low, accepted)* — **CLOSED (this change)**

**Before:** `Services/AuthService.cs` held an in-memory session store `private readonly ConcurrentDictionary<string, SessionData> _sessions = new();`, populated by the native OIDC login flow. A restart cleared all sessions; there was no cross-instance revocation.

**After:** the `_sessions` store and the entire native OIDC login/session subsystem were removed (see §1a). Craft no longer maintains sessions at all — authentication and session lifetime are owned by the upstream Azure App Service EasyAuth layer. The risk is eliminated rather than accepted: there is no Craft-side session state to revoke, expire, or lose on restart.

**Verification:** `dotnet build` clean after removal; no remaining reference to `SessionData` / `_sessions` in `Services/` (grep). Any future need for Craft-owned sessions at multi-instance scale would re-open this against a distributed store, not the in-memory dictionary.

---

## 3. Errata — where the parent assessment is inaccurate against the code

These are stated so the compliance record is defensible; each is a place where the "before" reality differs from the assessment text.

1. **§6 / §7 describe SAST/DAST/SCA tooling as implemented; it is not.** There is no `codeql.yml`, no `psscriptanalyzer.yml`, no Trivy, no SBOM step, no `.github/dependabot.yml`, and no `dependency-review-action` in the repository. Only `dev-container.yml` and `release-container.yml` exist, and they build/push only. These sections should be reframed as *planned*, not *live*. (Ties to R11.) **Scope note:** the argument that scanning is "covered downstream in CIPP-NG" holds only for the PowerShell surface — CIPP-NG neither compiles this repo's ~8,000 lines of C# nor builds this container image, so **CodeQL (`csharp`), Trivy, SBOM, and Dependabot (`nuget`, `github-actions`) belong on *this* repo** and cannot be inherited. R11 stays open specifically for the .NET + image layers.

2. **Positive finding "No dynamic code execution — startup AST validation of all PowerShell scripts" is overstated.** `Services/ScriptRepository.cs:144` does call `Parser.ParseInput` — but for **function discovery** (enumerating `FunctionDefinitionAst` to build the route table), *not* to reject `Invoke-Expression` or enforce a language mode. There is no `ConstrainedLanguage`/`LanguageMode` enforcement and no `Invoke-Expression` ban anywhere in `Services/`. The *real* control that holds is structural: HTTP requests are dispatched to **pre-loaded, repository-sourced named functions** via a route table (`ScriptRepository.GetByRoute`), so request input selects a function and parameters — it is not concatenated into an executed script. That is a legitimate "no dynamic execution of untrusted input" story, but it should be described accurately rather than as AST-based prohibition.

3. **Positive finding "No hardcoded secrets" has one caveat:** the SSH fallback password `Docker!` is a hardcoded default credential baked into the image (`docker/entrypoint.sh:17`). It is only reachable when SSH is explicitly enabled without a password (see R6), but a strict secret-scan reading should acknowledge it.

4. **R6 is more mitigated than "Partially mitigated" implies.** SSH is off by default (`CRAFT_SSH_ENABLED="false"`, `Dockerfile.release:62`) and the entrypoint only ever touches the password when SSH is turned on.

5. **R10 is more mitigated than "Open" implies.** A CSP middleware exists (`Program.cs:642–659`); it is simply inert by default because no CSP value is configured.

6. **R8 affects three services, not one** *(now closed — see R8 / §1a).* The register cited only `AuthService.cs`; the same fallback was also in `OrchestratorTableStore.cs` and `SetupService.cs`. All three now route through the fail-closed resolver.

7. **"allusers mode" is the `AllowAllTenantUsers` setting, and authorization is active even under EasyAuth.** The parent discussion's "allusers mode" maps to `Auth.AllowAllTenantUsers` (`AuthService.GetUserRoles`): when true, any EasyAuth-authenticated user who is not in the `allowedUsers` table is admitted with default roles `["anonymous","authenticated"]` and authorization is delegated downstream to CIPP-NG; when false, the table is the gate (unknown user → 401). This matters for the "OIDC is dead code" framing: the *login/session* subsystem was dormant (and is now removed), but the **header-transform + `allowedUsers` authorization path is active** and enforces access in the EasyAuth-fronted deployment.

---

## 4. Verified positive findings

- **id_token signature validation — owned by the platform.** Token signature/issuer/audience/lifetime validation is performed by Azure App Service EasyAuth *before* it injects the `x-ms-client-principal` header (the docs confirm external requests can't set that header). Craft receives a validated claims-principal, not a raw JWT, so it performs — and needs — no in-app JWT validation. The former `AuthService.ValidateIdToken` was removed as dead code (see R5).
- **Session payload encryption (AES-256)** — *removed with the OIDC/session subsystem (see R5/R14).* Previously AES-256-CBC with a per-cookie IV; no longer present because Craft no longer issues sessions. Cookie hardening (`HttpOnly`/`SameSite=Lax`) is likewise moot now that no session cookie is set.
- **Secrets via environment / config, not code** — auth credentials come from Azure App Service env vars (`WEBSITE_AUTH_CLIENT_ID`, `AUTH_SECRET`, `WEBSITE_AUTH_AAD_ALLOWED_TENANTS`), not source. The former in-code accessors (`ClientId`/`ClientSecret`/`TenantId`) were removed with the OIDC code; only `IsConfigured` (reads `WEBSITE_AUTH_CLIENT_ID`) remains. Note the `Docker!` SSH caveat above.
- **Minimal base images** — `mcr`/GHCR-mirrored `dotnet/aspnet:8.0-bookworm-slim` (`Dockerfile.release:24`).
- **Vulnerability-disclosure process** — `SECURITY.md` (reporting channel + advisory process).

---

*Prepared as the code-evidence baseline for the Craft risk register. Every citation was read from source; re-verify line numbers after refactors to `Services/`. Intended to be paired with follow-up commits so each open item can be shown as a before/after diff against the exact file and line above.*
