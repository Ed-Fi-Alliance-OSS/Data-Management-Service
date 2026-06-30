# OWASP Authentication Coverage Summary

This document summarizes the authentication security posture of the **Ed-Fi Data
Management Service (DMS)** and the **Ed-Fi DMS Configuration Service (CMS)**, with
particular focus on **JWT replay risk** and the compensating controls that apply
where replay prevention depends on the Identity Provider (IdP).

It is the reference for OWASP-aligned authentication concerns (OWASP ASVS V3
"Session Management" / token handling and the OWASP JWT Cheat Sheet) and maps each
stated control to the automated tests that exercise it.

## Trust paths

DMS and CMS accept JSON Web Tokens (JWTs) issued under three trust paths. The
replay posture differs by path because the responsibility for revocation differs.

| Path | Token issuer | Per-request validation owner |
|---|---|---|
| **DMS resource API** | Configured OIDC IdP (Keycloak or the CMS self-contained provider) | DMS, by stateless self-inspection against the IdP's published signing keys |
| **CMS self-contained provider** | CMS (OpenIddict-based, `AppSettings:IdentityProvider = self-contained`, the default) | CMS, with an additional per-request token-status check |
| **Keycloak / external IdP** | Keycloak (`AppSettings:IdentityProvider = keycloak`) | The IdP owns revocation; DMS/CMS validate signature and claims only |

## Token validation controls

All paths validate the following before a request is authorized:

- **Issuer** matches the configured authority.
- **Audience** matches the configured audience.
- **Signature** verifies against the issuer's signing key(s) (`RequireSignedTokens`).
- **Lifetime** is enforced (`ValidateLifetime`), with **expiration required**
  (`RequireExpirationTime`).
- A bounded **clock skew** is allowed (DMS: configurable `ClockSkewSeconds`; CMS:
  5 minutes).

A token failing any of these checks is rejected with `401 Unauthorized`.

## JWT replay posture and design decision

A signed JWT access token is a **bearer credential** (RFC 6750): anyone presenting
a valid, unexpired token is granted access. Bearer tokens are therefore inherently
**replayable until they expire or are revoked**. The platform's stance on replay is
deliberate and differs per trust path:

### DMS resource API — stateless bearer validation, no app-side replay cache

DMS performs **self-inspection** of the token rather than querying the OAuth
provider on each request (see `TokenIntrospection.feature`). It validates the
signature and standard claims and extracts `jti` into `ClientAuthorizations.TokenId`
for correlation/logging only.

DMS **does not** maintain a replay/nonce cache, enforce one-time use, or perform a
per-request revocation/introspection call. Within a token's validity window, the
same valid token may be presented any number of times and each request succeeds.
`jti` does **not** participate in the accept/reject decision.

**Decision:** DMS keeps standard stateless bearer semantics. A distributed
replay-prevention cache or per-request introspection is **explicitly out of scope**
— it would add coordination cost and latency without a corresponding threat in the
DMS deployment model. Replay exposure is bounded by the compensating controls
below (short token TTL, TLS, IdP-side revocation).

### CMS self-contained provider — per-request revocation-on-demand

For self-contained tokens, CMS issues a token with a GUID `jti`, persists it in the
`dmscs.OpenIddictToken` table, and on **every** authenticated request re-checks that
token's status by `jti` after standard validation. A request is authorized only when
the stored status is `valid`. CMS exposes:

- `POST /connect/revoke` (RFC 7009) — sets the token status to `revoked`.
- `POST /connect/introspect` (RFC 7662) — reports active/inactive status.

This is **not** one-time-use enforcement (a valid token remains reusable until it
expires or is revoked), but it does provide **immediate, server-side revocation**:
once a token is revoked, every subsequent use is rejected. This is the strongest
replay control available in the platform and applies only to the self-contained
provider scheme.

### Keycloak / external IdP — delegated to the IdP

When tokens are issued by Keycloak or another external IdP, DMS and CMS validate
the signature and claims against the IdP's published keys but **cannot locally
verify revocation**. Revocation, session management, and any `jti`/introspection
semantics are owned by the IdP. This is an **IdP-dependent gap** addressed by the
compensating controls below.

## `jti` handling matrix

How each path treats the `jti` (JWT ID) claim. "Accepted"/"Rejected" assume the
token is otherwise validly signed and unexpired.

| `jti` condition | DMS resource API | CMS self-contained |
|---|---|---|
| Present and status `valid` | Accepted (`jti` informational) | Accepted |
| Missing | Accepted — `TokenId` falls back to a derived value; `jti` not required | **Rejected** (no status to confirm) |
| Malformed (not a GUID) | Accepted — stored as an opaque string, never interpreted | **Rejected** (cannot resolve status) |
| Unknown (not in token store) | Accepted — DMS never looks it up | **Rejected** (status absent ≠ `valid`) |
| Revoked | Not detected app-side (IdP-dependent) | **Rejected** (status `revoked`) |

## Replay / lifecycle behavior matrix

| Scenario | DMS resource API | CMS self-contained |
|---|---|---|
| Same valid token reused before expiry | Accepted on every request | Accepted until revoked or expired |
| Expired token | Rejected (lifetime check) | Rejected (lifetime check) |
| Manipulated signature | Rejected | Rejected |
| Revoked token reused | Not detected app-side (see compensating controls) | Rejected |
| Externally-issued token, revocation unverifiable | Not detected app-side (see compensating controls) | Not detected app-side (Keycloak scheme) |

## Compensating controls for IdP-dependent gaps

Where app-side replay/revocation is not performed (DMS for all tokens; CMS for
externally-issued tokens), the following compensating controls bound the risk:

- **Short access-token lifetime (TTL).** A short TTL shrinks the window in which a
  captured token can be replayed. Configure the IdP / self-contained provider for
  the shortest TTL practical for the deployment.
- **Transport confidentiality (TLS).** All token transport must use TLS to prevent
  capture in transit. Bearer tokens must never traverse plaintext channels.
- **Bounded clock skew.** Lifetime validation allows only a small, fixed clock-skew
  tolerance, limiting acceptance of marginally-expired tokens.
- **IdP-side revocation and session management.** For externally-issued tokens,
  revocation is enforced at the IdP; operators should configure IdP revocation /
  short-lived tokens and rotate signing keys per IdP guidance.
- **Server-side revocation (CMS self-contained only).** The per-request `jti`
  status check plus `/connect/revoke` provide immediate revocation for
  self-contained tokens.
- **No internal-detail leakage on failure.** Authentication failures return a
  generic `401` (`application/problem+json` for DMS) with no failure-reason or
  stack-trace disclosure; specifics are logged server-side only. This avoids
  giving an attacker oracles that would aid token forgery or replay.

## Test coverage map

The behaviors above are exercised by automated tests:

**Unit tests**

- DMS — `EdFi.DataManagementService.Core.Tests.Unit/Security/JwtValidationServiceTests.cs`:
  valid token, expired token, invalid signature, missing claims, **valid token
  validated repeatedly (replay is accepted)**, and **`jti` is informational
  (malformed/opaque `jti` does not affect the decision)**.
- CMS — `EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictTokenManagerTests.cs`:
  `ValidateTokenAsync` accepts a token whose status is `valid` and **rejects**
  revoked / unknown-`jti` / missing-`jti` / malformed-`jti` tokens; `RevokeTokenAsync`
  delegates revocation for a valid `jti` and is a no-op for missing/malformed `jti`.

**End-to-end tests**

- DMS — `EdFi.DataManagementService.Tests.E2E/Features/Security/OwaspCriticalPaths.feature`:
  valid JWT replayed multiple times within its lifetime is accepted;
  manipulated-signature tokens are rejected; authentication-failure responses do
  not leak internal details. (The feature's "expired" scenario rewrites `exp`
  without re-signing, so it is rejected on signature validation before expiry is
  evaluated; true-expiry rejection via the lifetime check is covered by the DMS
  unit test above.)
- CMS — `EdFi.DmsConfigurationService.Tests.E2E/Features/OwaspCriticalPaths.feature`:
  a revoked self-contained token is rejected on reuse (token issued → revoked via
  `/connect/revoke` → reused → `401`).

## Dynamic scanning

OWASP ZAP can be run against the DMS and CMS OpenAPI specs using the convenience
script described in [`eng/zap/README.md`](../eng/zap/README.md).

## References

- RFC 6749 — The OAuth 2.0 Authorization Framework
- RFC 6750 — OAuth 2.0 Bearer Token Usage
- RFC 7009 — OAuth 2.0 Token Revocation
- RFC 7519 — JSON Web Token (JWT)
- RFC 7662 — OAuth 2.0 Token Introspection
- OWASP Application Security Verification Standard (ASVS), V3 Session Management
- OWASP JSON Web Token (JWT) Cheat Sheet
