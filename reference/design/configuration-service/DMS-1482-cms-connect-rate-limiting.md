# DMS-1482 — Configuration Service `/connect/*` rate limiting

**Status:** design approved in brainstorming 2026-10-22; pending written-spec review.
**Story:** [DMS-1482](https://edfi.atlassian.net/browse/DMS-1482) (parent DMS-1475), including comment 98735.
**Baseline:** `DMS-1482` at `6bb7d328a` (equal to `origin/main` on 2026-10-22).
**Supporting analysis (not requirements):** `.plans/DMS-1482-implementation-pre-spec.md`, `.plans/DMS-1482-implementation-pre-spec.nano.md`, `.plans/DMS-1482-implementation-pre-spec-review.md`. Their aliases (C.Identity, D.Limiter, R08, U1…) are used below without repeating their analysis.

## 1. Requirements

Original acceptance criteria (Jira, verified 2026-10-22; this closes review blocker B1):

| ID | Criterion |
|---|---|
| REQ-1 | Requests to `/connect/*` are rate limited per client and per source address. Comment 98735: the source-address partition must be explicit and tested, because enumeration varies `client_id` on every request. |
| REQ-2 | Exceeding the limit returns 429 with a body consistent with the Ed-Fi error contract (qualified by D1). |
| REQ-3 | Rate limiting occurs before client secret verification. |
| REQ-4 | Limits are configurable, with documented defaults. |
| REQ-5 | Tests cover the limit being enforced and the 429 response shape. |

## 2. Approved decisions

| ID | Decision | Rationale |
|---|---|---|
| D1 | **Approved deviation from REQ-2's wording:** token, register and introspect return an Ed-Fi 429. Revoke returns a 429 with an OAuth body. | Revoke's OAuth error contract is deliberate (DMS-1327 D-01/D-17, CS-AUTH). DMS-1491 (Open) will move the remaining `/connect/*` errors to OAuth. Reversing revoke for one status would be undone by DMS-1491. |
| D2 | Two independent budgets, and both must admit a request: **per source address** and **per `client_id` across all sources**. | Stops enumeration (source) and distributed guessing against one client (client). Accepted tradeoff: someone who knows a `client_id` can exhaust that client's window. The window bounds the impact, and real clients cache tokens. |
| D3 | **Backoff excluded.** The failed-attempt counter with exponential backoff that the story and comment say to "consider" is deferred, as a possible follow-up ticket. | It needs handler-to-limiter outcome feedback and new state, carries its own lockout-DoS tradeoff, and overlaps DMS-1490. |
| D4 | Enabled by default. Source 120, client 30, per 60-second fixed window. No queueing. Enforcement is per process. | Real per-client traffic is already capped by `BearerTokenPerClientLimit` (15 active tokens per 30-minute token lifetime). The source limit is sized for aggregate DMS-proxy traffic from a single IP. |
| D5 | Split admission: a source gate in middleware, plus a client gate inside the token and revoke handlers (approach A). | Reuses each route's existing credential parser. Avoids early form reads, so revoke's encoding validation (review R08) is preserved. Stays inside the route exception boundary (R14). |
| D6 | Native IPv6 sources are keyed on their /64 prefix. | A subscriber normally owns a whole /64, so per-address keys would allow free address rotation. |

Rejected approaches: (B) a single middleware that extracts `client_id` itself. It would pre-read the form, bypassing revoke's `UrlEncodedFormValidatingStream` because `FormFeature` caches the result, and it would duplicate two deliberately different parsers. (C) ASP.NET `AddRateLimiter`/`UseRateLimiter` endpoint policies. These have the same form-read problem, plus the undisposed endpoint-limiter timer documented in D.Limiter.

## 3. Scope

**In scope:** the CMS frontend (`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore`), one factory in the CMS data model, CMS unit tests, `appsettings.json`, compose and CI environment overrides, and documentation.

**Out of scope:**
- DMS-1490 enumeration code and timing fixes.
- DMS-1491 OAuth migration of other routes and status codes.
- DMS-1479 introspection authentication.
- DMS-1484 header and `Cache-Control` changes.
- Backoff (D3).
- Distributed or cross-replica enforcement.
- DMS limiter or proxy changes, including forwarding `Retry-After` through the DMS proxy.
- Backend, persistence, collation or `RelationalMappingVersion` changes.
- Unrelated cleanup.

## 4. Design

### 4.1 Components (all new, CMS frontend unless noted)

| Component | Responsibility |
|---|---|
| `Configuration/ConnectRateLimitSettings` + validator | Binds the top-level `ConnectRateLimit` section: `Enabled` (bool, default `true`), `SourcePermitLimit` (120), `ClientPermitLimit` (30), `WindowSeconds` (60). When enabled, every number must be a positive integer. Added to the `ReportInvalidConfiguration` options check in `Program.cs`, beside `IdentitySettings`. |
| `ConnectRateLimiter` (DI singleton, disposed by the host) | Holds two `PartitionedRateLimiter<string>` instances, each a `FixedWindowRateLimiter` with `QueueLimit = 0` and `AutoReplenishment = true`. `AttemptSource(HttpContext)` and `AttemptClient(string clientId)` return a `RateLimitLease` from the synchronous `AttemptAcquire`. When disabled, both return an acquired lease (`RateLimitPartition.GetNoLimiter` or equivalent). |
| `ConnectRateLimitMetadata` | Marker added with `.WithMetadata` to all four routes in `IdentityModule.MapEndpoints`. |
| `Middleware/ConnectSourceRateLimitMiddleware` | Passes the request through unless the matched endpoint carries the marker. Otherwise it calls `AttemptSource` and, if rejected, writes the 429 (§4.4) and stops the pipeline. |
| `FailureResponse.ForTooManyRequests(correlationId)` (data model) | Ed-Fi 429 body (§4.4). |
| `FailureResults.TooManyRequests(correlationId)` | The `IResult` form of the same body, for handlers. |

### 4.2 Pipeline and data flow

`Program.cs` order: `UseRouting → ExceptionContentBoundaryMiddleware → FrameworkErrorResponseMiddleware → UseCors → ConnectSourceRateLimitMiddleware → UseAuthentication → …`

This position means:
- the remote address has already been through trusted forwarding;
- security headers, request logging and both exception boundaries wrap the rejection;
- CORS headers allow Swagger UI to read the 429;
- no bearer authentication work runs for rejected requests.

```
request → source gate (all four connect routes; no body read)
  ├─ rejected → 429
  └─ admitted → handler parses exactly as today
        → client gate (token, revoke) immediately before the manager call
            ├─ rejected → 429; no manager, hasher, Keycloak call or revocation
            └─ admitted → unchanged manager flow
```

Handler gate positions:
- **`GetClientAccessToken`:** after `validator.GuardAsync` and the grant-type check, before `tokenManager.GetAccessTokenAsync`.
- **`RevokeToken`:** after the D-03 shape checks and credential parsing (Basic or form), before the revocation manager call.

Malformed requests keep their current answers and order. Only requests that would reach secret verification use the client budget. The budget is spent whether or not the client exists, so a 429 is not an existence oracle.

Route coverage:

| Route | Source gate | Client gate |
|---|---|---|
| `/connect/token` | yes | yes |
| `/connect/revoke` | yes | yes |
| `/connect/register` | yes | no: its `ClientId` names the client being created, not the caller |
| `/connect/introspect` | yes | no: it has no caller credentials at baseline (see §7, DMS-1479) |

Route-context suffixes, path base and path case are handled by endpoint routing, not string matching. Non-connect endpoints never carry the marker.

### 4.3 Partition keys

- **Source:** `Connection.RemoteIpAddress`, read after `UseForwardedHeaders` (trusted proxies and networks only; raw forwarded headers are never read).
  - IPv4-mapped IPv6 converts to IPv4.
  - Native IPv6 is masked to its /64 (D6).
  - A null address goes to one shared `"unknown"` partition, never exempt.
- **Client:** the `client_id` produced by the route's existing parser.
  - Compared with `StringComparer.OrdinalIgnoreCase`, so MSSQL's case-insensitive lookup can't be used to get fresh budgets with case variants (see `reference/adr-client-id-casing.md`). This matches `RegisterClient.IsUnique`.
  - Values longer than 256 characters are truncated for keying only. That only ever merges keys, which is the safe direction.
  - The source budget limits how many new client partitions one source can create per window, and the framework removes idle partitions.
- No database lookup or credential verification is used to build any key.

### 4.4 Rejection contracts

- **Format selection:** both gates choose the format from the endpoint's `OAuthErrorContractMetadata`, the same signal `GlobalExceptionHandler` uses.
- **Ed-Fi** (token, register, introspect): `application/problem+json`, status 429.
  ```json
  { "detail": "The number of allowed requests has been exceeded. Retry the request later.",
    "type": "urn:ed-fi:management-api:too-many-requests", "title": "Too Many Requests",
    "status": 429, "correlationId": "<TraceIdentifier>", "validationErrors": {}, "errors": [] }
  ```
  - The type mirrors DMS's established `urn:ed-fi:api:too-many-requests`, allowed under DMS-1218's platform-convention rule.
  - The middleware writes it through `FailureResponseWriter`; handlers return `FailureResults.TooManyRequests`.
  - It is distinct from, and leaves unchanged, `…:security:authentication:too-many-tokens`.
- **OAuth** (revoke): `OAuthErrorResponseWriter` with status 429, `error: "temporarily_unavailable"`, and the same fixed description. RFC 6749 and RFC 7009 define no throttling code; DMS-1491 may revisit this value.
- **`Retry-After`:** the lease's `MetadataName.RetryAfter` rounded up to whole seconds; omitted when absent (DMS precedent).
- **No new headers:** `Cache-Control` is not added; that belongs to DMS-1484.

### 4.5 Configuration and documentation surfaces

- **`appsettings.json`:** adds the `ConnectRateLimit` section with the D4 defaults.
- **Compose:** `eng/docker-compose/local-config.yml` and `published-config.yml` map `CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_{ENABLED,SOURCE_PERMIT_LIMIT,CLIENT_PERMIT_LIMIT,WINDOW_SECONDS}` with the same defaults. `.env.example` documents them.
- **CI environment files:** the environment files that the E2E and smoke workflows actually load get generous permit limits, so the limiter stays enabled but never binds test traffic. The plan identifies the exact files from workflow and script usage.
- **`docs/CONFIGURATION.md`:** a new `ConnectRateLimit` section covering defaults, the units, per-process and per-replica scope, NAT and DMS-proxy sharing of one source budget, IPv6 /64, `Enabled=false`, and the two response formats.
- **Other notes:** a short note in `reference/design/configuration-service/CS-AUTH.md`, and a changelog entry in the current unreleased changelog file.

## 5. Failure handling and regression safeguards

- **Invalid configuration:** reported through the existing `ReportInvalidConfiguration` path. The limiter is never used, because that middleware short-circuits before routing.
- **Logging:** one structured log per rejection, carrying the gate (`source`/`client`) and route template only. Client ID, IP, credentials and tokens are never logged. `RequestLoggingMiddleware` still logs the 429.
- **Known operator risk, no code change:** DMS's own system client (`ConfigurationServiceTokenHandler.FetchTokenAsync`) throws on any non-token response, a 429 included. Token caching keeps its rate far below the client limit. This is documented.
- **Preserved behavior:**
  - token's lenient Basic/form precedence and malformed-Basic fallback;
  - revoke's strict shape checks, D-03 first-error order, `%`/UTF-8 validation, ownership and indistinguishable outcomes;
  - registration's `AllowRegistration` gate;
  - the `too-many-tokens` 429 and DMS proxy parsing (`OAuthManager` maps the new 429 to DMS's generic 429; `urn:ed-fi:management-api:too-many-requests` is already a near-miss case in `OAuthManagerTests`);
  - both identity providers;
  - `/v3/*`, health, discovery and tenancy behavior.

## 6. AC → design → verification

Tests follow NUnit, FluentAssertions and FakeItEasy, with `Given_` fixtures that arrange and act in setup and `It_` assertions. They exercise the real HTTP pipeline in the CMS frontend unit test project. Each fixture uses tiny limits (for example 2) on an isolated host. There are no sleeps or timing races. `FixedWindowRateLimiterOptions` takes no `TimeProvider`, so window renewal is not tested; a renewal test would need a real-time wait, which AGENTS.md disallows.

| AC | Design | Verification (each must fail if the feature is reverted) |
|---|---|---|
| REQ-1 | §4.2 source and client gates; §4.3 keys; D2, D6 | Source: N admitted and N+1 → 429 when only `client_id` varies, and when Host, `Tenant` header, route-context suffix or path case vary. A different remote IP (via `ForwardedHeadersTests.RemoteIpStartupFilter`) is still admitted. Untrusted `X-Forwarded-For` doesn't change the bucket; a trusted proxy's does. IPv6 addresses in one /64 share a budget. Client: client A exhausted while client B from the same IP is admitted; case variants of A share a budget. All four routes are source-limited. `/v3/*` and look-alike paths (for example `/connectx/token`) are never limited. |
| REQ-2 | §4.4 | Exact Ed-Fi body, media type and status/body agreement on token, register and introspect, for both source and client rejection. Exact OAuth body on revoke. `Retry-After` present and rounded up. Security headers present. `FailureResponse.ForTooManyRequests` unit test. Existing too-many-tokens fixtures unchanged. |
| REQ-3 | §4.2 gate positions | With a fake `ITokenManager` and `ITokenRevocationManager`: an admitted known client reaches the manager (control); a client-rejected request makes zero manager calls. A source-rejected revoke with a malformed body gets 429 (the body was not read), while an admitted malformed `%GG` revoke still gets 400 with limiting enabled. |
| REQ-4 | §4.1 settings; §4.5 | Shipped `appsettings.json` values (enabled, 120/30/60) read as in `ClientSecretHashingIterationsSettingsTests`. Environment and in-memory overrides take effect. Invalid values (zero, negative) are reported at startup (pattern: `JobOptionsStartupTests`). `Enabled=false` means no 429 past the limits. Documentation updated. |
| REQ-5 | All of the above | The rows above, plus: `ConnectRateLimiter` is disposed with the host. |

Regression surfaces to leave unchanged: `IdentityModuleTests` (`TokenEndpointBasicAuthTests`, `OAuthEndpointErrorTests`, `RevocationOwnershipTests`, token-limit fixture), `FailureResponseWriterTests`, `OAuthManagerTests`, and the CMS E2E `Connect`, `Token` and `Revocation` features.

## 7. Dependencies, coordination and remaining items

- **DMS-1479 (In QA)** adds caller authentication to introspect. Whichever change merges second adds the client gate to introspect immediately before its token validation, keyed on the authenticated caller's `client_id`. That's one call and doesn't change this design.
- **DMS-1491 (Open)** owns any later change to the throttle response format or codes. Comment on DMS-1491 noting the new Ed-Fi 429 and revoke's `temporarily_unavailable` 429. Optional; no blocking dependency.
- **DMS-1490 (Open)** is independent. The client budget is spent regardless of whether the client exists, so this work adds no new oracle.
- **Follow-up candidate:** a per-source failed-attempt backoff ticket (D3), if the product owner wants it.
- **Remaining assumptions (not blockers):** the D4 numbers come from repository evidence (token-count cap, DMS-proxy topology), not measured production load. Operators can tune them, and the documentation says so. Proxy trust in each deployment remains an operator responsibility under the existing `ReverseProxy` settings.
- **Blockers:** none.
