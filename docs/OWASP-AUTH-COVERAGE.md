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
DMS deployment model. Because DMS self-inspects and never calls the IdP per request,
a token revoked at the IdP is still accepted by DMS until it expires — IdP-side
revocation is **not** enforced for already-issued bearer tokens on this path. Replay
exposure is therefore bounded by the *measurable* compensating controls below (short
token TTL and signing-key rotation, plus TLS in transit), not by IdP revocation.

### CMS self-contained provider — per-request revocation-on-demand

For self-contained tokens, CMS issues a token with a GUID `jti`, persists it in the
`dmscs.OpenIddictToken` table, and on **every** authenticated request re-checks that
token's status by `jti` after standard validation. A request is authorized only when
the stored status is `valid`. CMS exposes:

- `POST /connect/revoke` (RFC 7009) — sets the token status to `revoked`. The caller
  must be authenticated (`401` without a valid bearer token) and may only revoke a
  token whose `client_id` claim matches the caller's own, so one client cannot
  revoke another's tokens. The target token's signature, issuer and audience are
  verified before its `client_id` is trusted; otherwise a forged token naming the
  caller could carry a victim's `jti`. A token that fails verification, carries no
  `client_id`, or belongs to another client is a **silent no-op that still returns
  `200 OK`** — per RFC 7009 the outcome is indistinguishable from revoking an
  unknown token, so nothing leaks about the token's existence or owner. A target
  token past its `exp` (plus the validator's clock skew) is likewise a no-op rather
  than being revoked by `jti`, since verification includes the lifetime check.
  Because the `client_id` claim name is the same for self-contained and
  Keycloak-issued tokens, the comparison needs no per-mode branching — but see the
  Keycloak note below for where it actually runs.
- `POST /connect/introspect` (RFC 7662) — reports active/inactive status.

This is **not** one-time-use enforcement (a valid token remains reusable until it
expires or is revoked), but it does provide **immediate, server-side revocation**:
once a token is revoked, every subsequent use is rejected. This is the strongest
replay control available in the platform and applies only to the self-contained
provider scheme.

#### A `200 OK` from `/connect/revoke` does not confirm revocation

Every outcome of the revocation endpoint is an identical bodyless `200 OK` — success,
wrong owner, unverifiable token, expired token, and (in Keycloak mode) not attempted at
all. RFC 7009 requires this, and it is what stops the endpoint from becoming an oracle
for token existence and ownership, but it also means the status code carries no
confirmation. **Incident response must not treat `200 OK` as proof that a leaked
credential was contained** — confirm with `POST /connect/introspect`, which reports
`{"active": false}` only once revocation has actually taken effect.

This is a live operational concern rather than a theoretical one, because at least one
known defect produces a silent no-op for a legitimate client. For the full list of no-op
conditions, the confirmation procedure, and that defect, see
[CS-AUTH.md § Confirming that a revocation actually took effect](../reference/design/configuration-service/CS-AUTH.md#confirming-that-a-revocation-actually-took-effect),
which is the canonical description.

Reading stored status directly is not a substitute either: an expired target token keeps
its stored status rather than being marked `revoked`, so a `valid` reading in
`dmscs.OpenIddictToken` or an admin status view does not mean the token is still usable.

### Keycloak / external IdP — delegated to the IdP

When tokens are issued by Keycloak or another external IdP, DMS and CMS validate
the signature and claims against the IdP's published keys but **cannot locally
verify revocation**, and neither app path calls the IdP per request. Revocation,
session management, and any `jti`/introspection semantics are owned by the IdP. The
practical constraint: an externally-issued token revoked at the IdP is still
**accepted by these paths until it expires**. This is an **IdP-dependent gap**
bounded (not closed) by the compensating controls below.

In Keycloak mode CMS registers no `ITokenRevocationManager` — only the MSSQL and
Postgres OpenIddict extensions do, and `KeycloakTokenManager` implements
`ITokenManager` alone — so `POST /connect/revoke` does not reach the
ownership-checked revocation path at all: it falls through to a bare `200 OK` and
**revokes nothing**. The ownership comparison described above therefore only
executes in `self-contained` mode. What *did* change for Keycloak mode is the
authentication requirement: `/connect/revoke` now answers `401` to an
unauthenticated caller, where it previously returned a `200 OK` no-op.

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
- **Signing-key rotation (measurable app-side control).** Retiring a signing key
  makes every token issued under it fail signature validation, which cuts short the
  acceptance window for tokens already in circulation. Combined with the short TTL
  above, this is the measurable control on the stateless paths; rotate per IdP
  guidance.
- **IdP-side revocation (constraint, not an app-side control).** Revoking a token at
  the IdP does **not** retroactively reject it on the stateless app paths (DMS for
  all tokens; CMS for externally-issued tokens) — a revoked externally-issued token
  is accepted until it expires. IdP revocation and session management still prevent
  *new* tokens from being issued to a compromised client, but do not invalidate
  tokens already in circulation on these paths.
- **Server-side revocation (CMS self-contained only).** The per-request `jti`
  status check plus `/connect/revoke` provide immediate revocation for
  self-contained tokens. Revocation is itself an authenticated, ownership-checked
  operation: a client can revoke only its own tokens, so the control cannot be
  turned into a denial-of-service against other clients.
- **No sensitive-detail leakage on failure.** DMS authentication failures return
  a fixed `application/problem+json` `401` body — `type`
  `urn:ed-fi:api:security:authentication`, `title` `Authentication Failed`,
  `detail` `The caller could not be authenticated.` — matching the design-doc and
  ODS/API contract. The `errors` array carries only a **coarse, approved
  classification** of the failure (a missing, unknown-scheme, empty, or malformed
  `Authorization` header, an absent client-authorization context, or `Invalid token`);
  it never discloses a stack trace,
  cryptographic detail, or the specific reason a token failed validation —
  expiry, bad signature, and bad claims all collapse to `Invalid token`. Full
  specifics are logged server-side only. This preserves the ODS/API-compatible
  contract while avoiding oracles that would aid token forgery or replay.

## Test coverage map

The behaviors above are exercised by automated tests:

**Unit tests**

- DMS — `EdFi.DataManagementService.Core.Tests.Unit/Security/JwtValidationServiceTests.cs`:
  valid token, expired token, invalid signature, missing claims, **valid token
  validated repeatedly (replay is accepted)**, and **`jti` is informational
  (malformed/opaque `jti` does not affect the decision)**.
- CMS — `EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictTokenManagerTests.cs`:
  `ValidateTokenAsync` accepts a token whose status is `valid` on repeated
  presentation (reusable while valid) and **rejects** expired (lifetime check, before
  the status lookup), revoked, unknown-`jti`, missing-`jti`, and malformed-`jti`
  tokens; `RevokeTokenAsync` delegates revocation for a valid `jti` owned by the
  calling client and is a no-op — repository never called — for every other case:
  a missing `jti`, a malformed `jti`, a token owned by another client, a token
  carrying no `client_id`, a token carrying an empty `client_id`, an absent caller
  `client_id`, a token whose `client_id` differs from the caller's **only by letter
  case** (pinning the comparison as case-sensitive), a token from **another issuer**,
  a token for **another audience**, a token **forged** to name the caller while
  embedding another `jti`, and an **expired** token owned by the caller. The
  issuer and audience fixtures sign with the service's own registered key, so they
  cannot pass on signature verification alone. The owned-token case also asserts the
  stored status is never queried, pinning revocation as idempotent for an
  already-revoked token. Two further fixtures assert the **log severity** split:
  an expired token produces no `Warning` entry (Debug only), while an untrusted
  token does, so routine expiry cannot bury forgery signal.
- CMS — `EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/IdentityModuleTests.cs`:
  `/connect/revoke` returns `401` to an unauthenticated caller and to one presenting
  an invalid bearer token, `400` when the `token` form field is missing, and `200 OK`
  for an owned token (revoked), a token owned by another client (not revoked), a
  forged token (not revoked), and a malformed token (not revoked). A caller holding
  only an ordinary client-credentials principal — `client_id` and a non-admin scope,
  no service-role claim — still succeeds, pinning the route's authorization as a bare
  authenticated check rather than a named policy; a companion fixture arms that one by
  driving the same role-less principal at a genuinely `ServicePolicy`-gated route and
  observing `403`. A final fixture asserts `/connect/register`, `/connect/token` and
  `/connect/introspect` stayed anonymous under the same authentication harness.

**End-to-end tests**

- DMS — `EdFi.DataManagementService.Tests.E2E/Features/Security/OwaspCriticalPaths.feature`:
  valid JWT replayed multiple times within its lifetime is accepted;
  manipulated-signature tokens are rejected; authentication failures return the
  fixed problem-details contract (`urn:ed-fi:api:security:authentication`) with
  only a coarse failure classification and no internal-detail leak. (The
  feature's "expired" scenario rewrites `exp`
  without re-signing, so it is rejected on signature validation before expiry is
  evaluated; true-expiry rejection via the lifetime check is covered by the DMS
  unit test above.)
- CMS — `EdFi.DmsConfigurationService.Tests.E2E/Features/OwaspCriticalPaths.feature`:
  a revoked self-contained token is rejected on reuse (token issued → revoked via
  `/connect/revoke`, authenticated with that same token so the caller owns it →
  reused → `401`).

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
