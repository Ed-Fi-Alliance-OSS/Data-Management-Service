# DMS-1482 — Configuration Service `/connect/*` rate limiting

**Status:** approved 2026-10-22; implemented on branch `DMS-1482`.
**Story:** [DMS-1482](https://edfi.atlassian.net/browse/DMS-1482) (parent DMS-1475), including comment 98735.
**Original baseline:** `DMS-1482` at `6bb7d328a` (equal to `origin/main` on 2026-10-22).
**Integration baseline:** `main` at `0a544eb76`, including DMS-1479's authenticated introspection.

**Review remediation (2026-10-26):** D2 now uses a source-plus-client budget alongside the independent
source budget, as proposed by the story. The original global client budget let public failed attempts
exhaust DMS's private CMS refresh allowance (CAR-001). Trusted forwarding now isolates both budgets.
SHA-256 fingerprints of complete canonical client keys replace prefix truncation to keep distinct
long IDs independent with bounded retained state.
For CAR-002, the user chose to align every new rate-limit rejection with REQ-2's Ed-Fi contract,
including revoke, while preserving the existing OAuth errors for other statuses. D1 records this
decision; the acceptance criteria remain unchanged.

> **Superseded by [DMS-1491](https://edfi.atlassian.net/browse/DMS-1491):** the rate-limit `429` follows each route's own error contract.
> `/connect/token`, `/connect/revoke` and `/connect/introspect` answer the OAuth error `temporarily_unavailable` with the same text as `error_description`, and only `/connect/register` keeps the Ed-Fi problem details.
> An oversized client id at `/connect/token` is `invalid_client`, as at revoke and introspect, instead of an Ed-Fi validation `400`.
> See `CS-AUTH.md` (Token Endpoints).

## 1. Requirements

Original acceptance criteria (Jira, verified 2026-10-22):

| ID | Criterion |
|---|---|
| REQ-1 | Requests to `/connect/*` are rate limited per client and per source address. Comment 98735: the source-address partition must be explicit and tested, because enumeration varies `client_id` on every request. |
| REQ-2 | Exceeding the limit returns 429 with a body consistent with the Ed-Fi error contract. |
| REQ-3 | Rate limiting occurs before client secret verification. |
| REQ-4 | Limits are configurable, with documented defaults. |
| REQ-5 | Tests cover the limit being enforced and the 429 response shape. |

## 2. Design decisions

| ID | Decision | Rationale |
|---|---|---|
| D1 | All four routes return an Ed-Fi 429 for rate-limit rejection. Other errors retain their existing contracts. | Aligns with REQ-2 and the user's 2026-10-26 resolution of CAR-002. This supersedes the original design's OAuth 429 exception for revoke; DMS-1327's existing non-429 OAuth errors remain unchanged. DMS-1491 owns any future OAuth migration. |
| D2 | Two budgets, and both must admit a request: **per source address** and **per source address and `client_id` pair**. | The independent source budget stops enumeration that varies client IDs. The pair budget limits guessing against one client at that source without letting public failed attempts exhaust another source's private refresh allowance. Distributed sources have separate allowances. Callers behind one NAT or IPv6 /64 still share both budgets. |
| D3 | **Backoff excluded.** The failed-attempt counter with exponential backoff that the story and comment say to "consider" is deferred, as a possible follow-up ticket. | It needs handler-to-limiter outcome feedback and new state, carries its own lockout-DoS tradeoff, and overlaps DMS-1490. |
| D4 | Enabled by default. Source 120, client 30, per 60-second fixed window. No queueing. Enforcement is per process. | Real per-client traffic is already capped by `BearerTokenPerClientLimit` (15 active tokens per 30-minute token lifetime). Proxy deployments require trusted source attribution; aggregate capacity alone does not provide caller isolation. |
| D5 | Split admission: a source gate in middleware, plus a client gate inside token, revoke and authenticated introspect handlers. | Reuses each route's existing credential parser. Avoids early form reads, so revoke's and introspect's percent-encoding and UTF-8 validation is preserved. Client admission remains inside the route exception boundary. |
| D6 | Native IPv6 sources are keyed on their /64 prefix. | A subscriber normally owns a whole /64, so per-address keys would allow free address rotation. |
| D7 | DMS forwards its processed source address; paired Compose stacks configure an exactly trusted DMS peer. | Isolates public callers from each other and from unforwarded system refresh. The dedicated link makes the transport address deterministic without trusting the shared bridge. The readiness validator detects later overrides that break this trust. Standalone CMS retains its existing forwarding defaults. |
| D8 | Client counting conservatively merges common linguistic aliases across providers. | SQL Server can authenticate width, accent, case and padding aliases as the same client. Ordinal comparison would give those aliases fresh budgets. Distinct PostgreSQL or Keycloak clients can consequently share capacity; arbitrary SQL collations are not emulated. |
| D9 | Parsed client IDs longer than 1024 UTF-16 code units are rejected before client admission. Token retains its Ed-Fi 400 validation format; revoke and authenticated introspect return OAuth `invalid_client` (401 with the Basic challenge for a Basic attempt, 400 otherwise), as CS-AUTH specifies for invalid credentials. | Bounds linguistic processing and allocations. The limit leaves room above the stored MSSQL identifier size, applies to Basic and form credentials even when limiting is disabled, and does not truncate credentials. An oversized fallback partition would split long aliases from short IDs and reopen a bypass. |

Rejected approaches: a single middleware that extracts `client_id` itself would pre-read the form, bypassing revoke's `UrlEncodedFormValidatingStream` because `FormFeature` caches the result, and duplicate two deliberately different parsers. ASP.NET `AddRateLimiter`/`UseRateLimiter` endpoint policies can provide source admission, but cannot replace the client gate without extracting credentials before the handler. Keeping client admission after the existing parsers preserves validation order; the singleton owns and disposes both partitioned limiters.

## 3. Scope

**In scope:** the CMS frontend (`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore`), one factory in the CMS data model, CMS unit tests, `appsettings.json`, compose and CI environment overrides, and documentation.

**Out of scope:**
- DMS-1490 enumeration code and timing fixes.
- DMS-1491 OAuth migration of other routes and status codes.
- Changes to DMS-1479 introspection authentication beyond integrating client admission before its existing credential verification.
- DMS-1484 header and `Cache-Control` changes.
- Backoff (D3).
- Distributed or cross-replica enforcement.
- DMS limiter changes and forwarding `Retry-After` through the DMS proxy. Source-address forwarding and recognizing the new CMS throttle without an unrecognized-response warning are narrow integration changes.
- Backend, persistence, collation or `RelationalMappingVersion` changes.
- Unrelated cleanup.

## 4. Design

### 4.1 Components (all new, CMS frontend unless noted)

| Component | Responsibility |
|---|---|
| `Configuration/ConnectRateLimitSettings` + validator | Binds the top-level `ConnectRateLimit` section: `Enabled` (bool, default `true`), `SourcePermitLimit` (120), `ClientPermitLimit` (30), `WindowSeconds` (60). When enabled, every number must be a positive integer. Added to the `ReportInvalidConfiguration` options check in `Program.cs`, beside `IdentitySettings`. |
| `ConnectRateLimiter` (DI singleton, disposed by the host) | Holds two `PartitionedRateLimiter<string>` instances, each a `FixedWindowRateLimiter` with `QueueLimit = 0` and `AutoReplenishment = true`. `AttemptSource(HttpContext)` and `AttemptClient(HttpContext, string clientId)` return a `RateLimitLease` from the synchronous `AttemptAcquire`. When disabled, each budget uses one shared `RateLimitPartition.GetNoLimiter` partition, independent of caller-supplied keys. |
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
        → client gate (token, revoke, authenticated introspect) immediately before the manager call
            ├─ rejected → 429; no manager, hasher, Keycloak call or revocation
            └─ admitted → unchanged manager flow
```

Handler gate positions:
- **`GetClientAccessToken`:** after `validator.GuardAsync` and the grant-type check, before `tokenManager.GetAccessTokenAsync`.
- **`RevokeToken`:** after the D-03 shape checks and credential parsing (Basic or form), before the revocation manager call.
- **`IntrospectToken`:** when an introspection manager is registered (self-contained mode), after the same shape checks and credential parsing as revoke, before the introspection manager call. The no-manager Keycloak path keeps source admission only.

Existing malformed-request checks retain their order. Token validation and the shared revoke/introspect credential parser reject oversized client IDs before linguistic processing (D9). Only requests that would reach secret verification use the client budget. The budget is spent whether or not the client exists, so a 429 is not an existence oracle.

Route coverage:

| Route | Source gate | Client gate |
|---|---|---|
| `/connect/token` | yes | yes |
| `/connect/revoke` | yes | yes |
| `/connect/register` | yes | no: its `ClientId` names the client being created, not the caller |
| `/connect/introspect` | yes | yes in self-contained mode; no in Keycloak mode, which does not authenticate a caller |

Route-context suffixes, path base and path case are handled by endpoint routing, not string matching. Non-connect endpoints never carry the marker.

### 4.3 Partition keys

- **Source:** `Connection.RemoteIpAddress`, read after `UseForwardedHeaders` (trusted proxies and networks only; raw forwarded headers are never read).
  - IPv4-mapped IPv6 converts to IPv4.
  - Native IPv6 is masked to its /64 (D6).
  - A null address goes to one shared `"unknown"` partition, never exempt.
  - DMS's public token proxy forwards `0.0.0.0` when its connection address is null. CMS treats this as one shared public source, separate from DMS's unforwarded private refresh address. Trusted forwarding is still required.
- **Client:** the source key above paired with the `client_id` produced by the route's existing parser.
  - All three client-authenticated handlers enforce the 1024-code-unit input limit before key generation (D9).
  - An invariant linguistic sort key ignores case, accents, width and kana type. This merges common MSSQL lookup aliases, including full-width ASCII letters. It is deliberately conservative across providers and does not emulate every locale-specific SQL collation.
  - Trailing spaces and linguistically ignored suffix characters are removed before key generation, because SQL Server equality pads with spaces. The original credential is still passed unchanged to authentication. No Unicode normalization is performed, avoiding exceptions on malformed UTF-16.
  - The entire remaining ID is converted to a sort key, hashed with SHA-256 and encoded as base64, with no prefix truncation. The 44-character fingerprint bounds retained state; the validated input length bounds temporary comparison and allocation work. Distinct significant suffixes contribute to the fingerprint. The source and base64 key are joined with `|`, which neither component can contain.
  - The source budget limits how many new client partitions one source can create per window, and the framework removes idle partitions.
- No database lookup or credential verification is used to build any key.

### 4.4 Rejection contracts

> **Superseded by [DMS-1491](https://edfi.atlassian.net/browse/DMS-1491):** `ConnectRateLimitRejection.Create` now selects the format by the endpoint's `OAuthErrorContractMetadata`: the OAuth error `temporarily_unavailable` on token, revoke and introspect, and the Ed-Fi body below only on register.

- **Format:** both gates return Ed-Fi problem details on every route, independently of the endpoint's `OAuthErrorContractMetadata`. That metadata continues to select the contract for other errors.
- **Ed-Fi** (token, register, introspect, revoke): `application/problem+json`, status 429.
  ```json
  { "detail": "The number of allowed requests has been exceeded. Retry the request later.",
    "type": "urn:ed-fi:management-api:too-many-requests", "title": "Too Many Requests",
    "status": 429, "correlationId": "<TraceIdentifier>", "validationErrors": {}, "errors": [] }
  ```
  - The type mirrors DMS's established `urn:ed-fi:api:too-many-requests`, allowed under DMS-1218's platform-convention rule.
  - Both gates execute the same result from `ConnectRateLimitRejection.Create`, using `FailureResults.TooManyRequests`.
  - It is distinct from, and leaves unchanged, `…:security:authentication:too-many-tokens`.
- **`Retry-After`:** the lease's `MetadataName.RetryAfter` rounded up to whole seconds; omitted when absent (DMS precedent).
- **No new headers:** `Cache-Control` is not added; that belongs to DMS-1484.

### 4.5 Configuration and documentation surfaces

- **`appsettings.json`:** adds the `ConnectRateLimit` section with the D4 defaults.
- **Compose:** `eng/docker-compose/local-config.yml` and `published-config.yml` map `CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_{ENABLED,SOURCE_PERMIT_LIMIT,CLIENT_PERMIT_LIMIT,WINDOW_SECONDS}` with the same defaults. `.env.example` documents them.
- **Proxy readiness:** the DMS launchers apply `eng/docker-compose/dms-cms-proxy.yml` after both service files when CMS is included in the combined stack, consistently across infrastructure, DMS-only and teardown phases. The overlay reserves an internal `cms-proxy` subnet and peers via `DMS_CMS_PROXY_SUBNET`, `DMS_CMS_PROXY_IP` and `CMS_PROXY_IP`. DMS pins `ed-fi-api-config` to the CMS peer. The overlay explicitly enables CMS forwarding and adds the exact DMS peer independently of the standalone forwarding flag and optional ingress trust. It also sets `ConnectRateLimit:RequiredProxyAddress`; a cross-options validator rejects enabled limiting if forwarding is subsequently disabled or that exact IP is absent from `KnownProxies`. CMS-only deployments omit the overlay, retain the default `false` flag and empty required peer, and allocate no dedicated subnet. The isolated paired secret-resolution harness uses a distinct subnet.
- **CI environment files:** E2E profiles and the local `.env.smoke` / `.env.smoke.ds61` profiles use generous permit limits with limiting enabled. The scheduled smoke workflow additionally disables limiting for its stack startup because the pinned smoke utility requests a token per resource GET. Focused HTTP tests exercise enforcement with small limits.
- **`docs/CONFIGURATION.md`:** a new `ConnectRateLimit` section covering defaults, the units, per-process and per-replica scope, NAT and DMS-proxy sharing of one source budget, IPv6 /64, `Enabled=false`, and the Ed-Fi rejection format.
- **Other notes:** a short note in `reference/design/configuration-service/CS-AUTH.md`, and a changelog entry in the current unreleased changelog file.

## 5. Failure handling and regression safeguards

- **Invalid configuration:** reported through the existing `ReportInvalidConfiguration` path. The limiter is never used, because that middleware short-circuits before routing.
- **Logging:** a Debug-level rejection log carries the gate (`source`/`client`) and route template only. Client ID, IP, credentials and tokens are never logged. `RequestLoggingMiddleware` still records the 429. DMS recognizes the exact CMS throttle type/status and rebuilds its generic 429 without warning; unknown upstream responses retain their warning and no upstream text is relayed.
- **Availability boundary:** DMS's system client (`ConfigurationServiceTokenHandler.FetchTokenAsync`) throws on any non-token response, a 429 included. With trusted forwarding configured, public attempts and private refresh use separate source-plus-client budgets even when they name the same identity. No anonymous client ID is exempt. Callers sharing a source, including a NAT or IPv6 /64, still share capacity; deployments must preserve trusted source attribution. The CMS regression fixture exhausts public failed attempts and then sends the same unforwarded credential-and-scope POST as DMS refresh on the private path, while public attempts remain rejected. CMS tests do not reference DMS Core. Self-contained CMS warns at startup when limiting is enabled without a required proxy address and forwarding is disabled or has no explicit trust configuration; deployments must still verify that configured peers match their topology.
- **Preserved behavior:**
  - token's lenient Basic/form precedence and malformed-Basic fallback;
  - revoke's strict shape checks, D-03 first-error order, `%`/UTF-8 validation, ownership and indistinguishable outcomes;
  - registration's `AllowRegistration` gate;
  - the `too-many-tokens` 429 and DMS proxy parsing (`OAuthManager` maps the new 429 to DMS's generic 429; `urn:ed-fi:management-api:too-many-requests` is already a near-miss case in `OAuthManagerTests`);
  - both identity providers;
  - `/v3/*`, health, discovery and tenancy behavior.

## 6. AC → design → verification

Tests follow NUnit, FluentAssertions and FakeItEasy, with `Given_` fixtures that arrange and act in setup and `It_` assertions. They exercise the real HTTP pipeline in the CMS frontend unit test project. Each fixture uses tiny limits (for example 2) on an isolated host. There are no sleeps or timing races. `FixedWindowRateLimiterOptions` takes no `TimeProvider`, so window renewal is not covered by these deterministic fixtures; a real-time wait would introduce timing-dependent coverage.

| AC | Design | Verification (each must fail if the feature is reverted) |
|---|---|---|
| REQ-1 | §4.2 source and client gates; §4.3 keys; D2, D6 | Source: N admitted and N+1 → 429 when only `client_id` varies, and when Host, `Tenant` header, route-context suffix or path case vary. A different remote IP (via `RemoteIpStartupFilter`) is still admitted. Untrusted `X-Forwarded-For` doesn't change the bucket; a trusted proxy's does. IPv6 addresses in one /64 share both budgets. Client: client A exhausted while client B from the same IP is admitted; case and trailing-space variants of A share a budget across token, revoke and authenticated introspect at that source; a different source has its own allowance. A private POST matching DMS system refresh succeeds after public attempts against the same identity exhaust either gate, while public attempts remain rejected. Long IDs with significant suffix differences stay independent. All four routes are source-limited. `/v3/*` and look-alike paths (for example `/connectx/token`) are never limited. |
| REQ-2 | §4.4 | Exact Ed-Fi body, media type and status/body agreement on all four routes for source rejection, and on token, revoke and authenticated introspect for client rejection. `Retry-After` present and rounded up. Security headers present. A malformed revoke within the allowance retains its OAuth 400. `FailureResponse.ForTooManyRequests` unit test. Existing too-many-tokens fixtures unchanged. |
| REQ-3 | §4.2 gate positions | With fake `ITokenManager`, `ITokenRevocationManager` and `ITokenIntrospectionManager` implementations: an admitted known client reaches the manager (control); a client-rejected request makes zero manager calls. A source-rejected revoke with a malformed body gets 429 (the body was not read), while an admitted malformed `%GG` revoke still gets 400 with limiting enabled. |
| REQ-4 | §4.1 settings; §4.5 | Shipped `appsettings.json` values (enabled, 120/30/60) read as in `ClientSecretHashingIterationsSettingsTests`. Environment and in-memory overrides take effect. Invalid values (zero, negative) are reported at startup (pattern: `JobOptionsStartupTests`). `Enabled=false` means no 429 past the limits. Documentation updated. |
| REQ-5 | All of the above | The rows above, plus: `ConnectRateLimiter` is disposed with the host. |

Regression surfaces to leave unchanged: `IdentityModuleTests` (`TokenEndpointBasicAuthTests`, `OAuthEndpointErrorTests`, `RevocationOwnershipTests`, token-limit fixture), `FailureResponseWriterTests`, `OAuthManagerTests`, and the CMS E2E `Connect`, `Token` and `Revocation` features.

## 7. Dependencies, coordination and remaining items

- **DMS-1479 (integrated from `main` at `0113a0a40`)** adds caller authentication to introspect and shares its credential parser with revoke. The integrated handler retains the source marker, applies the client gate after parsing and before the introspection manager call, and preserves the client-ID bound on both authenticated routes. Coverage includes introspection rejection, skipped manager calls and a shared token/revoke/introspect budget. The submitted token's claims never supply a client partition key.
- **DMS-1491** owns any later change to the throttle response format or codes. The current Ed-Fi 429 on all four routes follows REQ-2; a future OAuth migration must update both rejection paths and their tests. No blocking dependency.
  > **Superseded by [DMS-1491](https://edfi.atlassian.net/browse/DMS-1491):** that migration is done; see the note at the top of this document.
- **DMS-1490 (Open)** is independent. The client budget is spent regardless of whether the client exists, so this work adds no new oracle.
- **Follow-up candidate:** a per-source failed-attempt backoff ticket (D3), if the product owner wants it.
- **Remaining assumptions (not blockers):** the D4 numbers come from repository evidence (token-count cap, DMS-proxy topology), not measured production load. Operators can tune them, and the documentation says so. Proxy trust in each deployment remains an operator responsibility under the existing `ReverseProxy` settings.
- **Proxy integration (D7):** other topologies must configure their actual trusted peers; a higher shared limit does not provide source isolation. Pipeline fixtures distinguish an exhausted caller, a second caller, an unforwarded system refresh and forged forwarding values. Compose fixtures cover standalone compatibility, paired routing/trust alignment and subnet overrides.
- **Credential bounds (D9):** handler fixtures cover Basic/form credentials on token/revoke/authenticated introspect, the accepted length boundary, oversized padding before and after exhaustion, unchanged short-alias counting, and rejected calls not reaching their managers.
