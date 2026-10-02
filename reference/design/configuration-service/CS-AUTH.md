# Authorization in the Configuration Service

## Identity Providers

The CMS supports two identity provider modes, configured via `AppSettings:IdentityProvider`:

- **`keycloak`** — tokens are issued by an external Keycloak instance. The CMS validates incoming tokens against it.
- **`self-contained`** — the CMS acts as its own OAuth 2.0 server using OpenIddict. No external provider is required.

## Bootstrap / Client Registration

A `POST /connect/register` endpoint handles initial client registration. It is
gated by `IdentitySettings:AllowRegistration`:

- When `true`, any caller can register a new client (intended for first-time setup).
- When `false`, the endpoint returns `403 Forbidden`.

Registered clients are assigned the scope `edfi_admin_api/full_access` and the
role defined in `IdentitySettings:ConfigServiceRole`.

## Token Endpoints

The CMS exposes standard OAuth 2.0 endpoints:

| Endpoint | Purpose | Caller authentication |
|---|---|---|
| `POST /connect/token` | Issue an access token (client credentials grant only) | Anonymous (client credentials in the request) |
| `POST /connect/introspect` | Introspect a token (RFC 7662) | Anonymous |
| `POST /connect/revoke` | Revoke a token (RFC 7009) | Client credentials (HTTP Basic or form body, exactly one of them) [^revoke] |

[^revoke]: RFC 7009 §2.1 requires the revocation caller to authenticate the same
way it authenticates to the token endpoint (RFC 6749 §2.3), with client credentials
and not a bearer access token. Both identity provider modes enforce it and both revoke:
`self-contained` in the CMS token store, `keycloak` by delegating to Keycloak. See
[Token revocation](#token-revocation).

In `self-contained` mode, `/connect/token` also accepts credentials via HTTP
Basic authentication in addition to the form body.

## Token revocation

`POST /connect/revoke` authenticates the caller, then revokes the named token only if
it belongs to that caller. The design, its evidence and the per-step verification are in
[DMS-1327](./DMS-1327-cms-token-revocation.md); this section is the operator and client
reference.

### Two credentials per request

Two distinct credentials are in play on every revocation request, and they are easy to
conflate. The **caller's** `client_id`/`client_secret` (HTTP Basic or form fields)
establishes *who is asking*. The **target** token, in the `token` form field, is *what
gets revoked*. The caller is deliberately **not** authenticated by a bearer access token:
the target token cannot double as proof of identity without letting anyone who holds a
token revoke it, and RFC 7009 does not permit it regardless. A `Bearer` (or any other
non-Basic) `Authorization` header grants nothing on this endpoint and is ignored.

```http
POST /connect/revoke HTTP/1.1
# Authorization identifies the caller by client credentials, not the token being revoked.
Authorization: Basic base64(form-encoded client_id ":" form-encoded client_secret)
Content-Type: application/x-www-form-urlencoded

# The token form field is what gets revoked; its owner is compared with the caller.
token=eyJ...TARGET&token_type_hint=access_token
```

A client revoking one of its *other* outstanding tokens (rotating a leaked credential
while keeping its current session alive, for example) names that token and succeeds. A
client naming a token issued to a different client gets `200 OK` and nothing is revoked.

### Request rules

The endpoint checks the request in a fixed order and the first failure answers, so a
request-shape problem is never hidden behind an authentication result, and an
authentication failure is never hidden behind a token outcome:

1. The body must be `application/x-www-form-urlencoded` and parse as a form.
2. `token`, `token_type_hint`, `client_id` and `client_secret` may each appear at most
   once, and the `Authorization` header at most once.
3. **Exactly one** client authentication mechanism. A request that attempts HTTP Basic
   and also carries a `client_id` or `client_secret` form key (even an empty one) is
   refused before the Basic value is parsed. Keycloak would let a form `client_id`
   override the Basic header, so CMS never forwards a choice of identities.
4. `token` must be present and non-empty.
5. Client authentication: a malformed Basic value, or incomplete form credentials, fails
   before any database or identity provider contact.

**HTTP Basic decoding differs from `/connect/token`.** On `/connect/revoke` the Basic value
is decoded per RFC 6749 §2.3.1, which has the client form-encode `client_id` and
`client_secret`, join them with the first `:`, and base64-encode the result. CMS decodes
each part as a form value:

- `+` decodes to a space and `%XX` to the byte it names, so `%2B` is a literal `+` and `%25`
  a literal `%`. Every other character, raw non-ASCII UTF-8 included, is taken as it is.
- The attempt is **malformed** (`401 invalid_client`, never repaired and never a fallback to
  form credentials) when the base64 is not the standard alphabet with correct padding and no
  embedded whitespace, when the decoded bytes or the bytes after percent decoding are not
  valid UTF-8, when a `%` is not followed by two hex digits, when there is no `:`, when the
  id or the secret is empty, or when anything other than one or more spaces separates
  `Basic` from the value.

`/connect/token` keeps its lenient parser, which uses `Uri.UnescapeDataString` (a `+` stays a
`+`) and falls back to form fields when the Basic value cannot be decoded. The difference
matters for secrets that contain `+` or `%`. Sent unencoded, a `+` becomes a space and a valid
`%XX` sequence becomes another character, so revocation compares a different secret and answers
`401 invalid_client`, and a `%` without two hex digits is malformed, while `/connect/token`
accepts the same header. Clients should form-encode both values before building the header,
which every conforming client does and which is safe for raw non-ASCII characters too.

`token_type_hint` accepts `access_token` and `refresh_token`. Any other value, including an
empty one, is treated as no hint. A hint never blocks lookup and never changes
authorization: `self-contained` ignores it, and `keycloak` forwards only the two recognised
values (Keycloak's endpoint does not read it either).

### Responses

`200 OK` with an empty body is the only success. Every other status is an RFC 6749 §5.2
JSON object served as `application/json`, with a **fixed** CMS `error_description` that
never echoes provider text:

```json
{ "error": "invalid_client", "error_description": "Invalid client or Invalid client credentials" }
```

| Condition | Response |
|---|---|
| Authenticated owner, supported live token | `200`, empty; token revoked |
| Authenticated caller; unknown, invalid, expired, already revoked, or another client's token | `200`, empty; nothing changes |
| Not a form body, malformed form, duplicated parameter or `Authorization` header, mixed mechanisms, missing or empty `token` | `400 invalid_request` |
| Missing, incomplete or invalid credentials **without** an HTTP Basic attempt | `400 invalid_client` |
| Malformed or invalid HTTP Basic credentials | `401 invalid_client` with `WWW-Authenticate: Basic realm="EdFi.DmsConfigurationService"` |
| A Keycloak public or bearer-only client | `invalid_client` by the same rule: `401` with the challenge when it attempted HTTP Basic, `400` otherwise |
| A token type the identity provider does not revoke (Keycloak: an ID token) | `400 unsupported_token_type`; reveals nothing about the token's owner |
| Keycloak rejected the request shape for a reason CMS could not anticipate | `400 invalid_request` ("The identity provider rejected the revocation request.") |
| Database or identity provider failure, timeout, or an unusable provider answer | `503 temporarily_unavailable` |
| An unexpected fault in CMS | `500 server_error` |

A request without credentials and without `token` gets `400 invalid_request`, not an
authentication error, because request shape is checked first. A public client is answered
with the same `invalid_client` description as a wrong secret, so the response does not
reveal the client's type.

This is the one CMS endpoint that does not answer in the `application/problem+json`
contract of [DMS-1218](./DMS-1218-cms-error-response-compliance.md), and the departure is
deliberate: `/connect/revoke` is an OAuth endpoint, and an OAuth client reads `error` off the
root of the body. It covers every error the endpoint produces, including the missing-token
`400` (which DMS-1218 had converted to problem details) and the `500` for an unexpected
fault. Responses produced before the endpoint is selected (Kestrel request-line and header
limits, TLS failures) are transport failures and keep their framework form.

The failure to authenticate is reported rather than masked as `200 OK` because RFC 7009's
"always 200" rule covers whether a *token* is valid or owned, not whether the *caller*
authenticated.

### Ownership

**`self-contained`.** The caller is authenticated with the same application lookup and
secret-hash comparison `/connect/token` uses (through a path that reports a hashing or
configuration failure as `503` instead of a wrong secret). The target token is then
verified for signature, issuer, audience and lifetime, and only then is its `client_id`
claim compared, ordinally, with the caller's **stored canonical** `client_id`. Verification
comes first on purpose: trusting an unverified `client_id` would let a caller forge a token
naming itself while embedding another client's `jti`. The mutation itself is constrained by
ownership as well:

```sql
UPDATE ... SET Status = 'revoked', RedemptionDate = <now>
 WHERE Id = @jti AND ApplicationId = @callerApplicationId AND Status <> 'revoked'
```

so a token stored for another application is never changed even if the claim comparison
were bypassed, and a second revocation keeps the original `RedemptionDate`.

Because verification includes the lifetime check, a target token already past its `exp`
(plus the validator's 5-minute clock skew) is a no-op. An expired token is already rejected
everywhere else. A practical consequence for anyone reading the `dmscs.OpenIddictToken`
table or an admin status view directly: an expired token keeps its stored status (typically
`valid`), so "not `revoked`" in the table does not imply "still usable".

**`keycloak`.** CMS first establishes, with its own service credentials, that the caller is
exactly one **confidential**, non-bearer-only client in the realm (see
[Keycloak client-type check](#keycloak-client-type-check)), then forwards the revocation to
`{Keycloak base URL}/realms/{realm}/protocol/openid-connect/revoke` with the **caller's**
`client_id` and `client_secret` as form fields, the token, and the recognised hint. The CMS
service credentials are never sent with the revocation. Keycloak authenticates the caller
and enforces ownership itself (the token's `azp` must be the caller). Its answer for another
client's token, `400 invalid_request` "Unmatching clients", is the **only** provider error CMS
normalizes to `200`; it is matched on both members exactly. Keycloak client ids are
case-sensitive, so a case variant of a real client id is an unknown client.

### Keycloak client-type check

Keycloak authenticates a **public** client without a secret and ignores any secret such a
client supplies, so delegation alone would let a public client holding a user-flow token
revoke it, and an arbitrary secret would defeat the "no secret, no revocation" rule. Before
any request that can change provider state, CMS reads the caller's client definition through
the Keycloak Admin REST API (`GET /admin/realms/{realm}/clients?clientId=...`) and delegates
only on affirmative evidence:

| Admin read outcome | Result |
|---|---|
| Exactly one client whose `clientId` matches ordinally, `publicClient` explicitly `false`, `bearerOnly` absent or `false` | delegate |
| No ordinal match | `invalid_client` |
| The match is public, or bearer-only | `invalid_client` |
| `publicClient` absent, an incomplete record, or more than one match | `503` |
| Admin authentication fails, or the read is refused (401/403) | `503`, logged with "the Configuration Service client lacks permission to read clients in the realm" |
| Any other failure, or the read times out | `503` |

"No such client" is answered `invalid_client` only after a successful read; a failed,
refused or ambiguous read is never taken as "not found". The CMS service account
(`IdentitySettings:ClientId`) therefore needs the `realm-management` client role
`view-clients`. The `realm-admin` role that `setup-keycloak.ps1` assigns to the
Configuration Service client includes it. Without it every revocation is answered `503`.
See [KEYCLOAK-SETUP.md](../../../eng/docker-compose/KEYCLOAK-SETUP.md#token-revocation-through-the-configuration-service).

### Supported token types and their effects

| Token | `self-contained` | `keycloak` |
|---|---|---|
| Access token (JWT) | Supported; the only type CMS issues. Located by its verified `jti` | Supported. Verified end to end on Keycloak 26.1.4 |
| Refresh token | Not issued | Supported by Keycloak's endpoint. Issued for `client_credentials` only when the client attribute `client_credentials.use_refresh_token` is `true`. Characterized directly against Keycloak (26.1.4 and 26.7.5), not through a CMS end-to-end scenario |
| Offline token | Not issued | Revocable at Keycloak per its source. **Untested**: needs `offline_access` and a user flow, outside this ticket's verification |
| ID token | Not issued | `400 unsupported_token_type`. Keycloak's type check runs before its ownership check, so the answer reveals nothing about the owner |

Related-token and session effects:

- **`self-contained`**: only the named token changes. There are no sessions and no refresh
  tokens.
- **`keycloak`, access token**: Keycloak enters the token's `jti` in its revoked-token store
  for the token's remaining lifetime. The session is untouched: a paired refresh token
  still obtains a new access token (observed).
- **`keycloak`, refresh token**: Keycloak detaches the client session, which also
  invalidates the paired access token (observed).
- **`keycloak`, offline token**: per Keycloak's source, the same session detachment for the
  offline session. Not observed.

### Confirming that a revocation actually took effect

Every token outcome of `POST /connect/revoke` is an identical bodyless `200 OK`: revoked,
wrong owner, unverifiable, expired, unknown. RFC 7009 requires this, and it is what stops
the endpoint from becoming an oracle for token existence and ownership, but it means the
`200` alone is **not** evidence that anything was revoked. Anyone who must be certain
(containing a leaked credential, for example) confirms through the provider's validation
path:

- **`self-contained`**: `POST /connect/introspect` with `token=<target>` reports
  `{"active": false}` once the token is revoked. A protected CMS request with the token
  answers `401`.
- **`keycloak`**: CMS `/connect/introspect` cannot observe Keycloak tokens (it always
  answers `{"active": false}` in this mode). Use Keycloak's own endpoint,
  `POST {Keycloak base URL}/realms/{realm}/protocol/openid-connect/token/introspect`,
  authenticated as a **confidential** client:
  - Access tokens. From Keycloak 26.4.12 (Red Hat's statement; observed on 26.7.5, not on
    26.1.4), the introspecting client must be in the token's `aud`, or the answer is
    `active:false` for a live token, which proves nothing. Put the introspecting client into
    the audience of the tokens you observe: an `Audience` mapper whose included client
    audience is the introspecting client, added to the clients that **receive** those tokens
    (in their dedicated scope, or in a client scope assigned to them as a default scope), not
    to the introspecting client. Only tokens issued after the change carry the audience; a
    token issued before it keeps its `aud` and still answers `active:false` to that client.
    The CMS end-to-end observer is set up this way. See
    [KEYCLOAK-SETUP.md](../../../eng/docker-compose/KEYCLOAK-SETUP.md#confirming-a-revocation-at-keycloak).
  - Refresh tokens. Only the client the token was issued to can introspect it, with
    `token_type_hint=refresh_token`.
  - Public clients. Keycloak refuses them at introspection (`403`), so their tokens must
    be observed by another, confidential client.

If the token still reports active, the revocation did not take effect and the credential
is still live. Treat containment as incomplete until the validation path confirms it.

### Propagation and clock skew

Revocation changes the provider's state immediately, but not every consumer consults that
state:

| Consumer | `self-contained` token | `keycloak` token |
|---|---|---|
| CMS protected endpoints | Rejected on the next request (per-request status check by `jti`) | **Accepted until `exp` + 5 minutes** (default JWT bearer clock skew): CMS validates Keycloak tokens locally and does not introspect |
| DMS resource API | **Accepted until `exp` + `JwtAuthentication:ClockSkewSeconds`** (default 30 s): DMS self-inspects and does not consult token status | Same as the `self-contained` column |
| Provider introspection | `active:false` immediately | `active:false` immediately (subject to the `aud` rule above) |

Per-request introspection is deliberately not added (see
[OWASP-AUTH-COVERAGE.md](../../../docs/OWASP-AUTH-COVERAGE.md)); short token lifetimes bound
the window.

### `503 temporarily_unavailable`: retry and confirm

A `503` means CMS could not **confirm** the outcome. It does not mean the token was not
revoked: the database `UPDATE` may have committed before the connection dropped, and
Keycloak may have recorded the revocation before its response was lost. The description
says exactly that ("Token revocation could not be confirmed. Retry the request and confirm
the token's state through the provider's validation path.").

- **Retrying is safe.** `self-contained` re-revocation is a no-op on an already revoked row,
  and Keycloak answers `200` for an already revoked token.
- **Confirm** through the validation path above when certainty matters.
- **CMS keeps serving.** An outage is a per-request `503`, never a stopped host. CMS does not
  check provider availability at startup. It refuses to start only when no token revocation
  implementation can be constructed for the configured `AppSettings:IdentityProvider` (a
  custom registration that removes or breaks the shipped one); that startup error names only
  the exception types.

Causes include: a database outage during application lookup, secret verification, signing
key retrieval or the `UPDATE`; a missing or unreadable signing certificate, or no usable
active signing key; Keycloak unreachable, timing out, or answering with a status or body CMS
does not recognise (for example `403` "HTTPS required" or `404` for a wrong realm, which are
operator misconfigurations); the client-type read failing as above. The log entry names the
category, the HTTP status where there is one, and exception type names only.

### Transport security

The caller's client secret and the token travel to CMS in the request, and in `keycloak`
mode CMS forwards both to Keycloak. Both hops must use TLS outside local development:

- Clients must call `/connect/revoke` over HTTPS.
- CMS sends the delegated revocation and the admin read to the base URL derived from
  `IdentitySettings:Authority` (everything before `/realms/`). It must be an `https` URL in
  any deployment that is not local development. The local Docker stack's
  `http://dms-keycloak:8080` is the documented exception: it stays on the Docker network, and
  the realm's `sslRequired=external` admits plain HTTP from private addresses. A realm set to
  `sslRequired=all` answers every plain-HTTP delegated call `403` "HTTPS required", which CMS
  answers `503`.

### Logging

Revocation logs the sanitized caller `client_id`, the outcome category, the provider's
HTTP status code, the provider `error` through a fixed allowlist (anything else is logged as
`unrecognized`), and exception **type names**. It never logs the token, a secret, the
`Authorization` header, a provider body or `error_description`, token verification detail,
or an exception object. A failure on the route (including one raised while the framework
constructs the revocation implementation for the request) is logged as `HttpRequestFailed`
with `ExceptionTypes` and no exception attached; the exception's message, inner exceptions
and `Data` are withheld from every logger, the framework's own included.

### Plugin compatibility requirement for replacement secret hashers

A replacement `IClientSecretHasher` registered through the plugin contract is called by
revocation through `VerifySecretAsync`. For revocation to keep operational failures apart
from authentication failures, its `VerifySecretAsync` must:

1. return `false` for a normal credential rejection;
2. **throw** for an operational failure that prevents verification (configuration, a
   dependency, an unreadable or incomplete stored value);
3. log neither the secret nor dependency exception content.

A conforming plugin's exception is answered `503`. A plugin that suppresses operational
failures and returns `false` cannot satisfy the guarantee: such failures are answered
`invalid_client`. Nothing is mutated, but the caller is told its credentials are wrong
during an outage, so the plugin is non-conforming. The built-in hasher's own
`VerifySecretAsync` predates this requirement and suppresses failures, which is why
revocation calls its failure-preserving path instead. `/connect/token` is unchanged and keeps
using `VerifySecretAsync`.

### Known limitations

- **Replacement hasher compatibility.** The guarantee above holds only for a conforming
  replacement hasher; the plugin contract is not changed to enforce it.
- **Shared token validator suppresses failures.** Revocation verifies the target token with the
  same validator as bearer authentication, which reports every verification failure as a token
  outcome. If a key that imports successfully cannot be used at the time of the request (the
  stored key changed after the token was issued, or a cryptographic provider refuses the
  key), the token is reported untrusted and revocation answers `200` without revoking.
  Ordinary signature failures are token outcomes by design. Confirm through introspection.
- **Synchronous Keycloak admin token.** The Keycloak admin client library fetches its admin
  access token synchronously and without cancellation before each admin call. CMS runs the
  read on the thread pool and stops waiting after the timeout, but the abandoned token fetch
  cannot be aborted: it holds a pool thread until it ends (bounded by the library's own
  100-second default timeout) and its result is discarded.
- **Two admin requests per revocation.** Each Keycloak revocation costs an admin token request
  and a client read before the revoke itself. This is acceptable for a low-volume endpoint.
- **Separate timeout windows.** The admin read, the revoke request (until its response
  headers) and the reading of an error body each get their own window of
  `AppSettings:TokenRequestTimeoutSeconds`. A Keycloak revocation can therefore take up to
  three times that setting (two when Keycloak answers `200`) before CMS answers `503`.
- **Basic decoding differs from `/connect/token`.** See [Request rules](#request-rules).
- **Propagation.** Revoked Keycloak tokens stay usable at CMS and DMS until they expire, and
  revoked `self-contained` tokens stay usable at DMS until they expire (see
  [Propagation and clock skew](#propagation-and-clock-skew)).
- **Untested token types.** Offline tokens and Keycloak refresh-token revocation through CMS
  end to end are not exercised by the CMS test suites (see
  [Supported token types](#supported-token-types-and-their-effects)).
- **Keycloak versions.** Verification runs against the pinned Keycloak 26.1 image of the E2E
  stack (26.1.4). A direct characterization of Keycloak's revocation endpoint on 26.7.5 found
  revocation behaviour identical to 26.1.4 (only introspection differs); CMS itself was not run
  end to end against 26.7. This is a compatibility record, not a support policy.

### Client id casing

Both sides of the `self-contained` ownership comparison are derived from the **stored
canonical** `client_id`, never from the casing a caller happened to type:

- Tokens are minted with the stored value, not the casing supplied at
  `/connect/token`, so every token issued to one registered client carries one
  identity whatever casing that client used on a given call.
- The caller's id is the stored value that client authentication resolved, not the one it
  sent in its credentials.

Either half alone leaves the defect open. Where an engine authenticates a mis-cased
`client_id` — SQL Server's default collation does — passing the caller's own spelling
into the case-sensitive comparison would fail it against the client's own canonically
minted token: a silent `200 OK` no-op on a legitimate revocation.

This was previously a live defect: a token minted under one casing could not be
revoked by a caller authenticated under another, and the mismatch surfaced as the
silent `200 OK` no-op described above. It is closed on **both** database engines. See
[ADR: Canonical `client_id` casing in minted tokens](../../adr-client-id-casing.md)
for the decision record, including why the ownership comparison itself deliberately
remains case-sensitive.

Client **lookup** is a separate matter and is unchanged: it is case-sensitive on
PostgreSQL, which matches RFC 6749's rule that protocol parameter values are case
sensitive. SQL Server's default collation makes its lookup case-insensitive, so the
two engines currently differ and SQL Server departs from the RFC; that is a known
issue tracked separately and out of scope here. It is a conformance and consistency
concern only — because both the minted claim and the authenticated caller's id are
canonical, a client that authenticates on SQL Server with non-canonical casing still
receives a canonical token and can revoke normally.

One residue remains: tokens minted **before** this change still carry the requested
casing, so such a token may resist revocation by a canonically-cased caller until it
expires. Token lifetimes are short and the population drains on its own.

Under `keycloak`, client ids are case-sensitive at Keycloak, and the client-type check matches
ordinally, so a case variant is an unknown client (`invalid_client`).

## Scopes

Authorization is scope-based. Three scopes are defined:

| Scope | Description |
|---|---|
| `edfi_admin_api/full_access` | Full access to all CMS API endpoints |
| `edfi_admin_api/readonly_access` | Read-only access to all CMS API endpoints |
| `edfi_admin_api/authMetadata_readonly_access` | Read access to the `MapLimitedAccess` endpoints below: every Configuration Service read a single-tenant DMS makes. A multi-tenant DMS also reads `GET /v3/tenants/`, which is `MapSecuredGet`, so it needs `readonly_access` |

## Endpoint Authorization Model

Scope requirements are applied per HTTP method via extension helpers:

| Helper | Allowed scopes | Used for |
|---|---|---|
| `MapSecuredGet` | `full_access` or `readonly_access` | All other GET endpoints |
| `MapSecuredPost` | `full_access` only | All POST endpoints |
| `MapSecuredPut` | `full_access` only | All PUT endpoints |
| `MapSecuredDelete` | `full_access` only | All DELETE endpoints |
| `MapLimitedAccess` | Any of the three scopes | Exactly these GET routes: `/v3/authorizationMetadata`, `/v3/claimSets/`, `/v3/apiClients/`, `/v3/apiClients/{id}`, `/v3/dataStores/`, `/v3/dataStores/{id}`, `/v3/dataStoreContexts/`, `/v3/dataStoreContexts/{id}`, `/v3/dataStoreDerivatives/`, `/v3/dataStoreDerivatives/{id}`, `/v3/applications/{id}`, `/v3/profiles/`, `/v3/profiles/{id}` |
| `MapPublic` | Anonymous | Health, JWKS, discovery endpoints |

## Roles

Two roles are configurable via `IdentitySettings`:

- `ConfigServiceRole` — assigned to clients that manage the CMS (admin tooling).
- `ClientRole` — assigned to DMS clients that consume the CMS (e.g., to retrieve authorization metadata).
