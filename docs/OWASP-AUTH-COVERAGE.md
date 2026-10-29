# OWASP Authentication Coverage Summary

This document summarizes the authentication security posture of the **Ed-Fi Data
Management Service (DMS)** and the **Ed-Fi API Configuration Service**, with
particular focus on **JWT replay risk** and the compensating controls that apply
where replay prevention depends on the Identity Provider (IdP).

It is the reference for OWASP-aligned authentication concerns (OWASP ASVS V3
"Session Management" / token handling and the OWASP JWT Cheat Sheet) and maps each
stated control to the automated tests that exercise it.

## Trust paths

DMS and the Configuration Service accept JSON Web Tokens (JWTs) issued under three trust paths. The
replay posture differs by path because the responsibility for revocation differs.

| Path | Token issuer | Per-request validation owner |
|---|---|---|
| **DMS resource API** | Configured OIDC IdP (Keycloak or the Configuration Service self-contained provider) | DMS, by stateless self-inspection against the IdP's published signing keys |
| **Configuration Service self-contained provider** | the Configuration Service (OpenIddict-based, `AppSettings:IdentityProvider = self-contained`, the default) | the Configuration Service, with an additional per-request token-status check |
| **Keycloak / external IdP** | Keycloak (`AppSettings:IdentityProvider = keycloak`) | DMS and the Configuration Service validate signature and claims only; revocation state lives at the IdP, which Configuration Service `/connect/revoke` delegates to |

## Token validation controls

All paths validate the following before a request is authorized:

- **Issuer** matches the configured authority.
- **Audience** matches the configured audience.
- **Signature** verifies against the issuer's signing key(s) (`RequireSignedTokens`).
- **Lifetime** is enforced (`ValidateLifetime`), with **expiration required**
  (`RequireExpirationTime`).
- A bounded **clock skew** is allowed (DMS: configurable `ClockSkewSeconds`; the Configuration Service:
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

### Configuration Service self-contained provider — per-request revocation-on-demand

For self-contained tokens, the Configuration Service issues a token with a GUID `jti`, persists it in the
`dmscs.OpenIddictToken` table, and on **every** authenticated request re-checks that
token's status by `jti` after standard validation. A request is authorized only when
the stored status is `valid`. The Configuration Service exposes:

- `POST /connect/revoke` (RFC 7009) — sets the token status to `revoked`. The caller
  authenticates with **client credentials**, as RFC 7009 §2.1 requires: HTTP Basic, or
  `client_id`/`client_secret` in the form body, exactly as at `/connect/token`.
  Deliberately **not** a bearer access token — the token in the `token` field is the
  subject of the request, so letting it double as proof of identity would let anyone
  holding a token revoke it. Two `client_id`s are therefore in play and must not be
  conflated: the **caller's**, established by those credentials, and the **target
  token's**, read from its `client_id` claim. A caller may revoke only a token whose
  claim matches its own, so one client cannot revoke another's. Exactly one
  authentication mechanism is accepted: Basic combined with form credentials, and any
  duplicated parameter or `Authorization` header, is `400 invalid_request` before any
  credential is examined. Failed client authentication is `invalid_client` — `401` with a
  `WWW-Authenticate: Basic` challenge when the caller attempted HTTP Basic, `400`
  otherwise. Every error is in the RFC 6749 §5.2 OAuth error format — `application/json`
  carrying top-level `error` and a fixed `error_description` rather than this API's usual
  `application/problem+json`, since an OAuth client reads `error` off the root of the
  body. Authentication failure is reported rather than hidden because RFC 7009's
  uniform-`200` rule covers whether a *token* is valid or owned, not whether the *caller*
  authenticated. A database failure is `503 temporarily_unavailable` ("could not be
  confirmed"), never a `200` and never `invalid_client`.

  The caller's `client_id` is the client's stored canonical spelling, not the one it
  typed, which matters because tokens are minted from the canonical value too: a
  client that authenticates under a spelling an engine accepts but did not store
  (SQL Server's default collation resolves a mis-cased id) would otherwise fail the
  comparison against its own token. The target token's signature, issuer and audience
  are verified before its `client_id` is trusted; otherwise a forged token naming the
  caller could carry a victim's `jti`. The database `UPDATE` is itself constrained to the
  caller's application, so ownership is enforced in the mutation as well as by the claim
  comparison. A token that fails verification, carries no
  `client_id`, or belongs to another client is a **silent no-op that still returns
  `200 OK`** — per RFC 7009 the outcome is indistinguishable from revoking an
  unknown token, so nothing leaks about the token's existence or owner. A target
  token past its `exp` (plus the validator's clock skew) is likewise a no-op rather
  than being revoked by `jti`, since verification includes the lifetime check. The
  ownership details here are `self-contained` behaviour; see the Keycloak section below
  for how the endpoint revokes in `keycloak` mode.
- `POST /connect/introspect` (RFC 7662) — reports active/inactive status.
  The caller must authenticate with client credentials, and only the token's own client receives its metadata.
  Any other token is reported as `{"active": false}`.

This is **not** one-time-use enforcement (a valid token remains reusable until it
expires or is revoked), but it does provide **immediate, server-side revocation**:
once a token is revoked, every subsequent use is rejected. This is the strongest
replay control available in the platform and applies only to the self-contained
provider scheme.

#### A `200 OK` from `/connect/revoke` does not confirm revocation

Every token outcome of the revocation endpoint is an identical bodyless `200 OK` —
success, wrong owner, unverifiable token, expired token, unknown token. RFC 7009 requires
this, and it is what stops the endpoint from becoming an oracle for token existence and
ownership, but it also means the status code carries no confirmation. **Incident response
must not treat `200 OK` as proof that a leaked credential was contained** — confirm with
`POST /connect/introspect` (self-contained) authenticated as the token's owner, which reports `{"active": false}` once
revocation has taken effect (or when it could not decide the token because a key or token-status store failed, which it
logs as the Error `Token validation could not reach a decision`), or with Keycloak's own introspection endpoint in
`keycloak` mode (see the Keycloak section below). A `503` is not a "not revoked" answer
either: it means the outcome could not be confirmed, and the caller should retry (which is
safe) and confirm the same way.

The no-op conditions are all cases where the request was never entitled to revoke the
token — a token belonging to another client, an unverifiable token, an expired one. A
casing defect that previously produced a silent no-op for a *legitimate* client has been
fixed on both database engines by deriving both sides of the comparison from the stored
canonical `client_id` — the token's claim is minted from it, and the caller's id is the
value client authentication resolved rather than the spelling the caller sent (see
[ADR: Canonical `client_id` casing](../reference/adr-client-id-casing.md)).
The confirmation step below nonetheless remains the only positive evidence that a given
revocation took effect. For the full list of no-op conditions and the confirmation
procedure, see
[CS-AUTH.md § Confirming that a revocation actually took effect](../reference/design/configuration-service/CS-AUTH.md#confirming-that-a-revocation-actually-took-effect),
which is the canonical description.

Reading stored status directly is not a substitute either: an expired target token keeps
its stored status rather than being marked `revoked`, so a `valid` reading in
`dmscs.OpenIddictToken` or an admin status view does not mean the token is still usable.

#### Credential reset, client deletion and un-approval revoke outstanding tokens

Three client lifecycle operations mark every outstanding self-contained token of the affected
client `revoked`, in the same database transaction that makes the change (DMS-1493):

| Operation | Endpoints | The client's tokens are revoked when |
|---|---|---|
| Credential reset | `PUT /v3/apiClients/{id}/reset-credential`, `PUT /v3/applications/{id}/reset-credential` | the new secret is committed |
| Client deletion | `DELETE /v3/apiClients/{id}`, and `DELETE /v3/applications/{id}` for each client of the application | the client is deleted from the identity provider |
| Un-approval | `PUT /v3/apiClients/{id}` with `isApproved: false` | `isApproved = false` is committed |

Once the operation has answered success, the next Configuration Service request that presents one
of those tokens is rejected with `401`, because the per-request status check no longer finds it
`valid`. Tokens of other clients, including other clients of the same application, do not change.

- **A grant racing the operation is revoked or refused.** Every token grant for a client takes the
  same database row lock as these operations and, while holding it, re-checks that the client
  still exists, still has the secret the grant authenticated with, and is still approved. A grant
  that stored its token before the operation has that token revoked by it. A grant that reaches
  the store after the operation is answered `invalid_client`, exactly as a wrong secret is, and
  stores nothing.
- **Re-approval does not revive tokens.** Approving the client again lets it obtain new tokens;
  the tokens revoked by the un-approval stay revoked.
- **All or nothing.** If the revocation fails, the operation rolls back: the new secret, the
  deletion or the approval change is not persisted either, and the request fails. Retrying is
  safe.

**Residual DMS window.** DMS does not consult token status (see
[DMS resource API](#dms-resource-api--stateless-bearer-validation-no-app-side-replay-cache)), so it
keeps accepting a revoked token until the token's `exp` plus `JwtAuthentication:ClockSkewSeconds`
(default 30 seconds). With the default self-contained token lifetime
(`IdentitySettings:TokenExpirationMinutes`, 30 minutes), a token stays usable at DMS for up to 30
minutes 30 seconds after it was issued, however soon after issuance its client was reset, deleted
or un-approved. This is the same window that follows `/connect/revoke`; a shorter token lifetime
shortens it.

**Tokens that were never marked revoked.** Only an operation performed by a Configuration Service
that includes DMS-1493 marks tokens revoked. A token of a client that was reset, deleted or
un-approved before that version was deployed is still `valid` in the token store. The
Configuration Service accepts it until its `exp` plus the Configuration Service's fixed 5-minute
clock skew (up to 35 minutes after issuance with the default lifetime). DMS accepts it until `exp`
plus `ClockSkewSeconds`, as above. These are two separate bounds. Repeating the reset or the
un-approval revokes such tokens. A client already deleted from the Configuration Service cannot be
addressed through the API again, so its tokens are left to expire. Every such token expires within
one token lifetime, plus the applicable clock skew, of the deployment.

**Keycloak mode.** The same operations regenerate the client's secret, delete the client, or
disable it at Keycloak, so the old credentials stop obtaining new tokens. The Configuration Service
stores no Keycloak tokens, and neither it nor DMS asks Keycloak about a token on each request, so a
Keycloak token issued before the operation is accepted by the Configuration Service until `exp`
plus 5 minutes and by DMS until `exp` plus `ClockSkewSeconds`.

**Grants for one client are serialized.** Because every grant takes the client's row lock,
concurrent grants for the same client queue behind one another. This holds also when the per-client
token limit is disabled (`IdentitySettings:BearerTokenPerClientLimit` below 1) and for clients
exempt from the limit, which previously stored tokens without the lock. A grant that cannot obtain
the lock within 5 seconds is answered `409` and can be retried. Grants for different clients do
not wait for each other.

The transaction order of each operation, the logging and the confirmation procedure are in
[CS-AUTH.md § Lifecycle revocation](../reference/design/configuration-service/CS-AUTH.md#lifecycle-revocation).

### Keycloak / external IdP — delegated to the IdP

When tokens are issued by Keycloak or another external IdP, DMS and the Configuration Service validate
the signature and claims against the IdP's published keys but **cannot locally
verify revocation**, and neither app path calls the IdP per request. Revocation,
session management, and any `jti`/introspection semantics are owned by the IdP. The
practical constraint: an externally-issued token revoked at the IdP is still
**accepted by these paths until it expires**. This is an **IdP-dependent gap**
bounded (not closed) by the compensating controls below.

In Keycloak mode `POST /connect/revoke` is authenticated and revokes at Keycloak. The Configuration Service
applies the same request rules as in self-contained mode (exactly one authentication
mechanism, no duplicated parameters, a non-empty `token`, no request without credentials),
then:

1. reads the caller's client definition through the Keycloak Admin API with the Configuration Service
   service credentials, and continues only when it proves exactly one **confidential**,
   non-bearer-only client with that exact `client_id`. Keycloak authenticates a public
   client without checking any secret, so this gate is what stops a public client — or
   anyone who knows its id — from revoking. Public and bearer-only clients get
   `invalid_client`; a failed, refused or ambiguous read is `503`, never "not found";
2. forwards the revocation to Keycloak with the **caller's** credentials as form fields
   (never the Configuration Service service credentials), so Keycloak authenticates the caller and enforces
   ownership by the token's `azp`. Keycloak's answer for another client's token
   (`400 invalid_request` "Unmatching clients") is the only provider error normalized to
   `200 OK`; every other answer maps to a fixed Configuration Service response, and provider text is never
   forwarded or logged.

Revocation at Keycloak does not change the stateless posture above: Configuration Service and DMS validate
Keycloak tokens locally and keep accepting a revoked token until it expires (plus their
clock skew). Confirm a revocation through Keycloak's introspection endpoint with a
confidential client that is in the token's audience (from Keycloak 26.4.12, per Red Hat's
migration guide, and observed on 26.7.5, any other client is answered `active:false`) —
Configuration Service `/connect/introspect` cannot observe Keycloak tokens (it makes no credential checks in that mode and answers `{"active": false}`). Details: [CS-AUTH.md § Token revocation](../reference/design/configuration-service/CS-AUTH.md#token-revocation).

## `jti` handling matrix

How each path treats the `jti` (JWT ID) claim. "Accepted"/"Rejected" assume the
token is otherwise validly signed and unexpired.

| `jti` condition | DMS resource API | Configuration Service self-contained |
|---|---|---|
| Present and status `valid` | Accepted (`jti` informational) | Accepted |
| Missing | Accepted — `TokenId` falls back to a derived value; `jti` not required | **Rejected** (no status to confirm) |
| Malformed (not a GUID) | Accepted — stored as an opaque string, never interpreted | **Rejected** (cannot resolve status) |
| Unknown (not in token store) | Accepted — DMS never looks it up | **Rejected** (status absent ≠ `valid`) |
| Revoked | Not detected app-side (IdP-dependent) | **Rejected** (status `revoked`) |

## Replay / lifecycle behavior matrix

| Scenario | DMS resource API | Configuration Service self-contained |
|---|---|---|
| Same valid token reused before expiry | Accepted on every request | Accepted until revoked or expired |
| Expired token | Rejected (lifetime check) | Rejected (lifetime check) |
| Manipulated signature | Rejected | Rejected |
| Revoked token reused | Not detected app-side (see compensating controls) | Rejected |
| Externally-issued token, revocation unverifiable | Not detected app-side (see compensating controls) | Not detected app-side (Keycloak scheme) |
| Token of a client reset, deleted or un-approved after DMS-1493 was deployed | Accepted until `exp` + `ClockSkewSeconds` (residual DMS window) | **Rejected** (`401`) on the next request |
| Token of a client reset, deleted or un-approved before DMS-1493 was deployed | Accepted until `exp` + `ClockSkewSeconds` | Accepted until `exp` + 5 minutes (never marked revoked) |
| Token grant racing a reset, deletion or un-approval | Not applicable (DMS issues no tokens) | Stored before the operation and revoked by it, or refused with `invalid_client` |
| Earlier token after the client is re-approved | Accepted until `exp` + `ClockSkewSeconds` | **Rejected** (stays `revoked`) |

## Compensating controls for IdP-dependent gaps

Where app-side replay/revocation is not performed (DMS for all tokens; the Configuration Service for
externally-issued tokens), the following compensating controls bound the risk:

- **Short access-token lifetime (TTL).** A short TTL shrinks the window in which a
  captured token can be replayed. Configure the IdP / self-contained provider for
  the shortest TTL practical for the deployment.
- **Transport confidentiality (TLS).** All token transport must use TLS to prevent
  capture in transit. Bearer tokens must never traverse plaintext channels.
- **Bounded clock skew.** Lifetime validation allows only a small, fixed clock-skew
  tolerance, limiting acceptance of marginally-expired tokens.
- **Signing-key rotation (measurable app-side control).** Retiring a signing key
  makes every token issued under it fail signature validation once validators have
  successfully refreshed their keys, which cuts short the acceptance window for tokens
  already in circulation. Combined with the short TTL above, this is the measurable
  control on the stateless paths; rotate per IdP guidance. For the Configuration Service self-contained
  provider there are two separate caches:
  - **Configuration Service instances.** A retired key's tokens are rejected on the Configuration Service's own endpoints
    only after each instance reloads its signing-key snapshot: within a bounded delay
    on a healthy instance, and up to the maximum staleness while an instance cannot
    read the key store.
  - **DMS instances.** DMS validates against its own cached JWKS, which the Configuration Service
    bounds do not cover. A DMS instance stops accepting the retired key only after it
    successfully re-fetches JWKS or is restarted. A failed re-fetch keeps the old key
    set.

  See
  [Rotating and retiring a database signing key](../reference/design/configuration-service/CS-AUTH.md#rotating-and-retiring-a-database-signing-key).
- **IdP-side revocation (constraint, not an app-side control).** Revoking a token at
  the IdP does **not** retroactively reject it on the stateless app paths (DMS for
  all tokens; the Configuration Service for externally-issued tokens) — a revoked externally-issued token
  is accepted until it expires. IdP revocation and session management still prevent
  *new* tokens from being issued to a compromised client, but do not invalidate
  tokens already in circulation on these paths. Configuration Service `/connect/revoke` reaches Keycloak's
  revocation in `keycloak` mode as an authenticated, ownership-checked operation, which
  takes effect at Keycloak (its introspection and, for a refresh token, its session) but
  not on these paths.
- **Server-side revocation (Configuration Service self-contained only).** The per-request `jti`
  status check plus `/connect/revoke` provide immediate revocation for
  self-contained tokens. Revocation is itself an authenticated, ownership-checked
  operation: a client can revoke only its own tokens, so the control cannot be
  turned into a denial-of-service against other clients. Credential reset, client
  deletion and un-approval revoke every token of the client in the transaction that
  makes the change, so they also cut off a compromised client's outstanding tokens at
  the Configuration Service, though not at DMS (see
  [Credential reset, client deletion and un-approval revoke outstanding tokens](#credential-reset-client-deletion-and-un-approval-revoke-outstanding-tokens)).
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

  The DMS **issuer pin** is covered in the same file:
  - a metadata document whose issuer differs from the configured authority is rejected; the
    error log names both values, and a metadata refresh is requested;
  - repeated mismatches are logged as an error only once per episode, while every one still
    requests a metadata refresh;
  - a **legitimate token is rejected while the metadata issuer mismatches**, and accepted again
    once the metadata matches;
  - the comparison is exact: an issuer that differs only by a trailing slash, or only by letter
    case, is rejected;
  - a token whose `iss` differs from the configured authority is rejected.

  `EdFi.DataManagementService.Core.Tests.Unit/Security/HttpDocumentRetrieverTests.cs` covers the
  **signing-key origin pin**: a document address on another host, port or scheme than
  `MetadataAddress` is refused before any request is sent, and a metadata document that asserts
  the right issuer but names a foreign `jwks_uri` fails retrieval without contacting that host.
  The check compares parsed origins, so an address carrying the metadata origin as userinfo, or
  on a host that merely starts with the metadata host, is refused too. With an `https`
  `MetadataAddress`, an `http` address on another host is refused and logged by the same origin
  check, and repeated refusals are logged as an error only once per episode.

  `EdFi.DataManagementService.Core.Tests.Unit/Startup/AuthStartupTaskRegistrationTests.cs` covers
  the **registered configuration manager** enforcing that pin (a foreign `jwks_uri` fails
  retrieval without being contacted), and DMS refusing an `http` `MetadataAddress` when
  `RequireHttpsMetadata` is true.

  `EdFi.DataManagementService.Core.Tests.Unit/Startup/WarmUpOidcMetadataTaskTests.cs` covers
  **DMS startup failing** on a metadata issuer mismatch, with both values named and sanitized
  in the error. These fixtures cover DMS only; the Configuration Service's own issuer validation is not
  exercised by them.
- The Configuration Service — `EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictTokenManagerTests.cs`:
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
  already-revoked token. Three further fixtures assert the **failure-category** split:
  an expired token produces no `Warning` entry (Debug only); a bad-signature token
  produces a `Warning` naming signature/key id; and an unacceptable-audience token
  produces a `Warning` naming issuer/audience and pointing at configuration, *not* the
  signature message. Routine expiry and a misconfigured `Authority`/`Audience` therefore
  cannot bury genuine forgery signal. Two more cover
  **canonical `client_id` minting**: a token obtained with non-canonical casing
  carries the canonical `sub`, `client_id` and `azp`, and consequently a token
  obtained under one casing is revocable by a caller authenticated under another —
  the scenario that was previously a silent no-op. Revocation's client authentication
  closes the same loop from the caller's side: it uses the **stored** `client_id` for a
  mis-cased credential, falls back to the requested spelling only when the stored
  value is empty, and answers `InvalidClient` for an unknown client, a wrong secret, an
  unapproved application, or missing credentials (which it rejects without querying
  the repository at all), always before any signing key or token is read. DMS-1327
  fixtures in the same file separate operational failures from token and authentication
  outcomes: a throwing application lookup, secret hasher, signing-key load or `UPDATE`,
  a corrupt or truncated stored hash, a missing certificate (with no replacement
  certificate created), a corrupt key record and an empty key set are each
  `TemporarilyUnavailable`, while an unknown `kid` stays a token outcome; a
  mutation-then-lost-response is `TemporarilyUnavailable` with no claim about the
  token's state; a fault in the comparison logic propagates (500) rather than being
  relabelled an outage; and capturing loggers walk every field, scope and exception for
  planted token, secret and `kid` sentinels.
- The Configuration Service — `EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/IdentityModuleTests.cs`:
  `/connect/revoke` returns `invalid_client` to a caller presenting no client credentials,
  an unregistered `client_id`, a wrong `client_secret`, or credentials for an unapproved
  application — `401` with a `WWW-Authenticate: Basic` challenge only when HTTP Basic was
  attempted, `400` otherwise; `400 invalid_request` for a missing `token`, a non-form or
  malformed body, a duplicated parameter or `Authorization` header, and Basic mixed with
  form credentials (the manager never called); `503` for each manager outage result; and
  `200 OK` for an owned token (revoked), a token owned by another client (not revoked), a
  forged token (not revoked), and a malformed token (not revoked). Credentials are
  accepted both via HTTP Basic and in the form body. The strict Basic decoder is pinned
  case by case (`+` and `%20`, `%2B`, a `:` in the secret, incomplete and non-hex `%`
  escapes, invalid UTF-8 before and after percent decoding, whitespace, URL-safe
  alphabet, wrong padding, a tab after the scheme), and `/connect/token` keeps its lenient
  decoding. Ordering is pinned too: a request with neither credentials nor a `token`
  field gets `400 invalid_request`, since the request-shape check runs first. Every error
  is checked against the **OAuth error contract**, and pipeline fixtures show an
  unexpected fault is `500 server_error` in the same format while `/connect/token` keeps
  the Ed-Fi contract. A further fixture
  drives the **canonical-casing path end to end over HTTP**: the repository resolves a
  mis-cased `client_id` the way SQL Server's collation does, and the token minted under
  the canonical spelling is still revoked — the case that would otherwise be a silent
  `200 OK` no-op against the caller's own token. A final fixture asserts
  `/connect/register` and `/connect/token` are not answered `401`, and that `/connect/introspect` rejects a
  request without client credentials with `400 invalid_client`.
- The Configuration Service — `EdFi.DmsConfigurationService.Backend.Tests.Unit/KeycloakTokenRevocationManagerTests.cs`
  and `KeycloakClientFacadeTests.cs`: the delegated request carries the caller's
  credentials and never the service secret, forwards only recognised hints, and is sent
  exactly once; every client-type gate outcome (public, bearer-only, case variant,
  absent flag, ambiguous, refused, failed, timed out, caller cancelled) asserts whether a
  revoke request was sent, and only a proven confidential client gets one; each
  Keycloak answer recorded by the characterization maps as designed, with
  "Unmatching clients" the only normalized error; timeouts, transport failures,
  oversize, non-UTF-8 and unparseable bodies, and a lost response after the request was
  sent are `TemporarilyUnavailable`; provider bodies with planted sentinels never reach
  a log. The facade fixtures run the real Keycloak.Net package against Flurl's test
  transport and pin the bounded wait around its synchronous admin-token fetch.
- The Configuration Service — `EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Configuration/TokenRevocationStartupTests.cs`,
  `Middleware/ExceptionContentBoundaryMiddlewareTests.cs`, `Middleware/RequestLoggingMiddlewareTests.cs`
  and `Infrastructure/GlobalExceptionHandlerTests.cs`: every provider/engine shape boots
  with its shipped registration and refuses to start, with a fixed message naming only
  exception types, when the registration is removed or its factory throws, without
  contacting the provider; a `503` leaves the host serving; a failure raised on the route,
  including one where the framework's exception middleware logs, never puts the
  exception's message, inner exception or `Data` into any log record, while an aborted
  request stays a cancellation.
- The Configuration Service, lifecycle revocation (DMS-1493) —
  `EdFi.DmsConfigurationService.Backend.Tests.Unit`: `OpenIddictClientRepositoryTests.cs` pins the
  transaction order of credential reset (secret hashed before the transaction, secret written,
  tokens revoked, commit) and of deletion (application deleted, tokens revoked, commit, also when
  no application row was deleted); a reset of a missing client revokes nothing and rolls back, and
  a throwing revocation commits nothing. `OpenIddictUnapprovalTokenRevocationTests.cs` pins that
  the un-approval participant locks the application row and then revokes in the caller's
  transaction, revokes when the row is absent, and leaves commit and rollback to the caller, and
  that the Keycloak participant does nothing. `OpenIddictTokenManagerTests.cs` pins that a grant
  passes the stored secret it authenticated against to the store, and that a store refused on
  re-validation is answered exactly as a vanished client is. In
  `EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit`,
  `Configuration/TokenRevocationStartupTests.cs` boots each datastore and identity-provider
  combination and asserts that the API client repository receives that provider's participant,
  and that a missing participant registration fails resolution instead of skipping revocation.
- The Configuration Service — `EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration` and
  `Backend.Mssql.Tests.Integration` (`OpenIddictDataRepositoryTests.cs`): the revocation
  `UPDATE` changes only a row stored for the caller's application, and a second revocation
  changes nothing and keeps the original `RedemptionDate`, on both engines.

  For lifecycle revocation, the same files and `ApiClientTests.cs` cover, on both engines: the
  bulk revocation (scoped to one application, idempotent, undone with its transaction) and the
  application-row lock; grants that wait for the lock and refuse a changed secret, an un-approved
  client or a deleted client, with the token limit enforced and disabled; reset, deletion and
  un-approval through the real repositories (the client's tokens revoked, another client's
  untouched, an update that keeps the client approved and a repeated un-approval changing nothing,
  re-approval reviving nothing); concurrency fixtures in which a grant that commits while the
  operation waits for the lock has its token revoked, a grant with the old state after the
  operation is refused, and a grant blocked behind the open operation is refused once it commits;
  and rollback fixtures in which a failing revocation or a failing `ApiClient` update leaves the
  secret, the application rows, the approval and the tokens unchanged.

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
- The Configuration Service — `EdFi.DmsConfigurationService.Tests.E2E/Features/OwaspCriticalPaths.feature`:
  a revoked self-contained token is rejected on reuse (token issued → revoked via
  `/connect/revoke`, the caller authenticating with the same client's Basic
  credentials so it owns the target token → reused → `401`).
- The Configuration Service — `EdFi.DmsConfigurationService.Tests.E2E/Features/Revocation.feature`, on both
  identity providers: the OAuth error responses for a missing token, mixed mechanisms, no
  credentials and a wrong Basic secret; owner revocation with Basic and with form
  credentials proven active before and inactive after; a cross-client attempt answered
  `200` with the token still active. Under Keycloak, state is observed by a dedicated
  confidential observer client whose credentials are never submitted for revocation,
  and public clients (no secret, an arbitrary form secret, an arbitrary Basic secret) are
  refused with their token still active.
- The Configuration Service — `EdFi.DmsConfigurationService.Tests.E2E/Features/ClientLifecycleRevocation.feature`
  (self-contained only; scenarios 01–03 also run in the SQL Server lane): a client's token
  answers `403` on `GET /v3/vendors` while valid (authenticated but not authorized) and `401`
  once revoked. Client credential reset rejects both of the client's earlier tokens, leaves another
  client's token valid, accepts a token obtained with the new secret, and introspection with the
  new credentials reports the earlier token inactive. Client deletion also refuses the old
  credentials with `invalid_client`. Un-approval refuses new grants; after re-approval the earlier
  tokens stay rejected and a new token is accepted. Application credential reset rejects the
  application's earlier token, and deleting an application with several clients rejects every
  captured token of its clients while another application's token stays valid.
- The Configuration Service — `EdFi.DmsConfigurationService.Tests.E2E/Keycloak/KeycloakRevocationCharacterizationTests.cs`:
  Keycloak's own revocation endpoint characterized row by row (status, `error`,
  `error_description`, token state before and after), the evidence the Configuration Service mapping is
  keyed on.

## Dynamic scanning

OWASP ZAP can be run against the DMS and Configuration Service OpenAPI specs using the convenience
script described in [`eng/zap/README.md`](../eng/zap/README.md).

## References

- RFC 6749 — The OAuth 2.0 Authorization Framework
- RFC 6750 — OAuth 2.0 Bearer Token Usage
- RFC 7009 — OAuth 2.0 Token Revocation
- RFC 7519 — JSON Web Token (JWT)
- RFC 7662 — OAuth 2.0 Token Introspection
- OWASP Application Security Verification Standard (ASVS), V3 Session Management
- OWASP JSON Web Token (JWT) Cheat Sheet
