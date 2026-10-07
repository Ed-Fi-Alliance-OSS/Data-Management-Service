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

1. The body must be `application/x-www-form-urlencoded` and parse as a form. Its encoding must
   be well formed: every `%` starts a two-hex-digit escape (a literal `%` is sent as `%25`), and
   every name and value decodes to valid UTF-8. A `charset` parameter, if present, must name
   UTF-8; any other charset is refused with `400 invalid_request`.
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

`/connect/token` keeps its lenient parser, which uses `Uri.UnescapeDataString` and falls back to
form fields when the Basic value cannot be decoded. For a secret sent without encoding, the two
parsers agree or differ as follows:

- **A literal `+`.** `/connect/token` keeps it; revocation decodes it to a space, compares a
  different secret and answers `401 invalid_client`.
- **A valid `%XX` sequence.** Both decode it to the character it names, so both compare the decoded
  secret. A secret that really contains such a sequence must send its `%` as `%25` at either
  endpoint.
- **A `%` not followed by two hex digits.** `/connect/token` keeps it as it is; revocation treats
  the attempt as malformed (`401 invalid_client`).

Percent-encoding both values (`%2B` for `+`, `%25` for `%`, `%20` for a space) gives a header
that decodes to the same credentials at both endpoints; raw non-ASCII characters need no encoding
at either.

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
case-sensitive, so a case variant of a real client id is an unknown client. CMS does not follow
a redirect from the revoke endpoint: any `3xx` is answered `503`, and the form carrying the
caller's secret and the token is never resent to the redirect target.

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
  read on the thread pool and stops waiting after the timeout, but timing out the wait does not
  stop the fetch: an in-flight token fetch cannot be aborted, holds a pool thread until it ends
  (bounded by the library's own 100-second default timeout) and its result is discarded.
- **Admin requests before the caller is authenticated.** Each Keycloak revocation request that
  supplies a client id and a secret costs an admin token request and a client read before
  Keycloak authenticates the caller, so the credentials need not be valid to trigger them. A
  request without credentials is refused before any admin call, and an unknown client id never
  reaches the revoke request. The endpoint's request volume, not its authenticated volume,
  therefore sets the admin load, and a slow Keycloak lets such requests hold pool threads as
  described above. Bounding this (admin-token caching, rate limiting) is deferred availability
  work; token caching would also have to handle concurrent cache misses and refresh failures.
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
  end to end against 26.7. This is a compatibility record, not a support policy. Changing the
  Keycloak image requires rerunning `KeycloakRevocationCharacterizationTests` (CMS E2E) and
  confirming that the ownership-mismatch answer CMS normalizes (`400 invalid_request`
  "Unmatching clients", matched exactly) is unchanged. If Keycloak changes that text, revoking
  another client's token is answered `400` instead of `200`.

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

## Signing keys in self-contained mode

This section applies to `self-contained` mode only. In `keycloak` mode none of these
components is registered, and token validation is unchanged. The settings named here
are described, with their defaults and accepted ranges, in
[Signing-key settings](../../../docs/CONFIGURATION.md#signing-key-settings-configuration-service-self-contained-only).
The design and its test evidence are in
[DMS-1556](./DMS-1556-cms-signing-key-resolution-under-load.md).

### The key snapshot

Each Configuration Service instance holds the public keys it validates tokens with in one
in-memory **snapshot**. The snapshot comes from one of two sources:

- **Database keys** (the default): every row of `dmscs.OpenIddictKey` with
  `IsActive = true`. Token issuance signs with the newest active row.
- **Certificates** (`IdentitySettings:UseCertificates`): the one key of the configured or
  development certificate, with the certificate thumbprint as the key id.

The default `Bearer` scheme, `DmsJwtBearer`, the JWKS endpoint and introspection all use
the same snapshot. Revocation does not: it reads the keys itself on each request, never
creates a development certificate, and answers `503 temporarily_unavailable` when a key
cannot be used (see [Token revocation](#token-revocation)). In steady state, validating a
token does not read the key table. The table is read only by a load, and a load starts in one of these ways:

- the startup load;
- a scheduled reload;
- a request that finds the snapshot missing, expired, or overdue;
- a token whose key id the snapshot does not hold.

The last two run on a request's behalf:

- **Missing or expired snapshot.** The request waits for the load, bounded by the load
  timeout. It is refused at once during a retry backoff, and while a load that outlived
  its deadline is still finishing.
- **Unknown key id.** The request also waits when its reload is allowed, or when it joins
  a load already running.
- **Overdue snapshot.** The request is served from the snapshot and does not wait.

The per-token status check (revoked or not) is a separate database read, never cached. Two
paths make it, and both make it only after the token's verification succeeds and its
`jti` claim is present and parses as a GUID:

- **Bearer authentication** (`Bearer`, `DmsJwtBearer`) checks the token from the
  `Authorization` header. On this path, a request without a bearer token reads no status.
  Neither does a request whose token fails verification or has no usable `jti`.
- **Introspection** checks the token supplied in the request form, whatever the request's
  own `Authorization` header.

Revocation verifies the token but does not read its status. See [Cached keys do not keep the service available through a
database outage](#cached-keys-do-not-keep-the-service-available-through-a-database-outage).

### Refresh, backoff, cooldown and staleness

- **One load at a time.** An instance runs at most one load. Requests that need keys while
  a load is running wait for that load rather than starting their own. A load that runs
  longer than `SigningKeyLoadTimeoutSeconds` (default 10 s) is canceled and counted as
  failed. If the database call itself does not end at that deadline, no new load starts
  until it does, and its late result is discarded.
- **Scheduled reload.** After a successful load the next reload is due once the snapshot's
  age reaches `SigningKeyRefreshIntervalSeconds` ±10 % (default 300 s, so 270–330 s). The
  ±10 % is drawn once per snapshot. The age is measured as described under
  [How time is measured](#how-time-is-measured).
- **Fresh and overdue.** A snapshot is *fresh* until the refresh interval has passed and
  *overdue* after that. An overdue snapshot is still used, and the first request that finds
  it overdue starts a reload in the background. Whichever comes first, the scheduled reload
  or such a request, does the load.
- **A failed load never replaces the snapshot.** The previous keys stay in use, and no
  failure is ever published as an empty key set. The next load is allowed only after a
  backoff of 5, 10, 20, 40 and then 60 s, each ±20 %, after consecutive failures (at most
  72 s). During a backoff no request starts a load. The background service wakes at the
  end of the backoff, so an instance retries without any traffic.
- **Recovery.** These bounds are conditional (design §4.4, *Bounds*). When the key store
  recovers at *R* and no load is in flight, the next load starts within the remaining
  backoff, by *R* + 72 s, and completes within `SigningKeyLoadTimeoutSeconds` after that:
  *R* + 82 s at the default settings. A load already in flight at *R* started against the
  failing store and may still fail; its backoff then counts from its own failure, so add
  one load timeout (*R* + 92 s). A database call that outlives its deadline delays the
  next load until it actually ends, so neither bound holds while such a call is
  outstanding. The bounds count elapsed time, not real time across a host suspension; see
  [How time is measured](#how-time-is-measured).
- **Maximum staleness.** A snapshot can be used until its age reaches
  `SigningKeyMaxStalenessSeconds` after its last successful load (default 3600 s). After that it
  has *expired*, and it is not used while its age stays past the limit; a successful load
  replaces it. Setting the system clock back does not extend this; see
  [How time is measured](#how-time-is-measured).
- **Unknown key id.** A token whose `kid` is not in the snapshot triggers one reload. The
  reload happens only when no backoff is running and the last completed load, of any kind,
  ended at least `SigningKeyUnknownKeyRefreshCooldownSeconds` ago (default 30 s). The
  cooldown applies to the whole instance, so tokens carrying made-up key ids cause at most
  one load per cooldown. If the key is still absent after the reload, or the reload was
  not allowed, the token is rejected with 401.

  The cooldown protects the key store's database capacity: without it, requests with
  made-up key ids could drive a store read each.

  **Bootstrap exception, once per instance.** When the instance's first successful load
  finds no key, one unknown-key reload may start inside the cooldown while the snapshot is
  still empty. This covers a fresh store whose setup inserts the first key after CMS
  starts: the first request carrying that key is accepted without waiting out the
  cooldown. The allowance is spent when that reload starts, even if it finds no key or
  fails (a failure backs off as usual). No later load restores it, and an instance whose
  first load found a key never gets it. The allowance helps this instance accept the
  first key's tokens. It does nothing for DMS or other consumers; see
  [Adding the first key](#adding-the-first-key).

  After the allowance is spent, the normal cooldown applies, including while the store
  stays empty. A key inserted after an earlier unknown-key reload has spent the allowance
  is accepted only after the cooldown, as on any store.
- **Empty key set.** A store that holds no active key is a *successful* load of zero keys.
  It is published with a Warning
  (`Signing-key snapshot {n} is empty: … so every token will be rejected`). JWKS then
  answers `200 {"keys":[]}`, and every token is rejected with 401, until a later load of
  that instance finds a key.
  - **A database with no key when the service starts** behaves this way until a key is
    added. After that, the first token signed with the new key triggers the unknown-key
    reload, subject to the cooldown, or the next scheduled reload picks the key up.
  - **JWKS lags the insert.** Issuance uses the new key at once, so JWKS can still answer
    `200 {"keys":[]}` after the insert until the instance's next successful load. A
    consumer that fetches JWKS in that window caches the empty set. See
    [Adding the first key](#adding-the-first-key).

### How time is measured

The intervals above count **elapsed time**. Two measures are used, and a change of the system
clock affects them differently:

- **Backoff and the unknown-key cooldown** use the process's monotonic clock, so changing the
  system clock neither stretches nor shrinks them. A wall-clock step, forward or back, neither
  opens them early nor keeps them closed.
- **A snapshot's age** (fresh, overdue, expired, and when its scheduled reload is due) is the
  larger of two measures: the wall-clock time since the load completed, and the monotonic time
  elapsed since then.
  - **Never below elapsed time.** The age never falls below the monotonic elapsed time, whatever
    the wall clock does. Setting the clock back therefore cannot keep a snapshot fresh, cannot
    postpone its scheduled reload beyond its interval of elapsed time, and cannot extend trust in
    a retired key past `SigningKeyMaxStalenessSeconds` of elapsed time.
  - **A clock ahead of elapsed time ages the snapshot early.** A wall clock ahead of that
    monotonic baseline, for example after a forward step, makes the snapshot older. A reload can
    then happen early. On a healthy key store that costs one extra load: the reloaded snapshot
    starts from the new wall time, so the two measures agree again. With a failing key store, the
    snapshot can expire early and protected requests answer 503 sooner.
  - **Expiry is not latched.** If such a forward step is later reversed, the age returns to the
    monotonic elapsed time. A snapshot that read as expired during the step can then be usable
    again. That stays within the bound, because its elapsed age has not passed the maximum
    staleness.
- **A clock change wakes nothing.** It takes effect at the next request, the next load outcome,
  or the next timer of the background service, whichever comes first.
- **Waits are whole milliseconds.** The background service rounds each wait up to whole
  milliseconds, at least 1 ms. It starts a load only when a fresh check finds nothing left to
  wait. A scheduled load therefore never starts early. Rounding can make it start up to 1 ms
  late, and a busy host can delay it further.
- **Host suspension (Linux).** On Linux, .NET's monotonic clock does not advance while the host or
  VM is suspended. A backoff or cooldown interrupted by a suspension still has its remaining
  elapsed time to run after the resume. The recovery bounds above therefore hold in elapsed time,
  and in real time they lengthen by any suspension that overlaps them. The wall-clock measure
  does count a suspension, so a snapshot still ages across one. This document makes no claim
  about other platforms.
- **Residual case.** Trust in a snapshot can exceed `SigningKeyMaxStalenessSeconds` of real time
  only when both measures under-count the same interval. An example is a suspended Linux host
  whose wall clock is also set back.
- **Token lifetimes are unchanged.** A token's `exp` and `nbf`, with the 5-minute validation clock
  skew, are still checked against the wall clock.

### Dependency 503s, ordinary 401s, and an empty JWKS

Each response says whether the token was judged, or whether the service could not reach a
decision:

| Situation | Protected endpoints | `GET /.well-known/jwks.json` | Introspection | Revocation |
| --- | --- | --- | --- | --- |
| Usable snapshot, valid token, token status valid | Request proceeds | `200` with keys | `{"active": true}` | Revokes; `200` |
| Token rejected: bad signature, wrong issuer or audience, expired, missing or unknown `kid`, revoked, malformed | **401**, `WWW-Authenticate: Bearer`, no `Retry-After` | unaffected | `{"active": false}` | No-op `200` |
| No usable snapshot (never loaded, or past maximum staleness) | **503**, category `SigningKeyStore` | **503** | `{"active": false}` and an Error log | not affected (revocation reads its keys itself; a failed read is `503 temporarily_unavailable`) |
| Token-status read fails (usable snapshot) | **503**, category `TokenStatusStore` | unaffected | `{"active": false}` and an Error log | not affected (revocation does not read the status) |
| The instance's last successful load found no active key (empty snapshot) | **401** for every token | `200 {"keys":[]}` | `{"active": false}` | not affected (no active key at request time is `503 temporarily_unavailable`) |

- **A dependency 503** carries `Retry-After: 30`, has no `WWW-Authenticate`, and has a
  generic `application/problem+json` body (`title` `Service Unavailable`, the request's
  `correlationId`). The body never names the failing store. The category appears only in
  the Error log line:
  `Authentication could not reach a decision: the {SigningKeyStore|TokenStatusStore} is unavailable (trace …)`,
  and for JWKS `The JWKS could not be served: the SigningKeyStore is unavailable (trace …)`.
  Clients should treat it as transient and retry after the indicated delay, not discard
  their token.
- **An ordinary 401** means the token was judged against the keys the instance holds, and
  rejected. While the snapshot is usable, a key-store failure does not turn a request into
  a 503. A token whose `kid` is unknown, and whose reload fails or is not allowed, is
  therefore judged against the usable keys and gets 401. The same token gets 503 when no
  usable snapshot exists.
- **An empty JWKS (`200 {"keys":[]}`)** reports what that instance's **last successful
  load** found: no active key. It is not a live read of the table, so a key inserted
  since that load is not yet listed. It is never the result of a failed load. A failed
  load never replaces the snapshot, so JWKS answers 503 when no usable snapshot exists,
  and otherwise serves the last keys it loaded.
  - **Before DMS-1556,** JWKS also answered `200 []` when the key read *failed*, and
    protected requests answered 401 when key or status reads failed. Both are now the 503
    above. This is a deliberate contract change.
  - **For a JWKS consumer,** a failed fetch is now visible as a failure, not as an empty
    key set it might adopt.
  - **Fresh keys still need verifying.** A successful `200` still shows only that
    instance's snapshot. Before relying on a key change, verify it on every instance; see
    [Verifying a key change on every instance](#verifying-a-key-change-on-every-instance).
- **Introspection is unchanged on the wire.** It keeps its protocol answer
  (`{"active": false}`) when a dependency fails, and logs an Error naming the category.
  Revocation answers `503 temporarily_unavailable` instead (see
  [Token revocation](#token-revocation)).
- Failures after authentication, in the endpoint's own data access, are not part of this
  classification and keep their existing responses (for example 500).

### Cached keys do not keep the service available through a database outage

The snapshot makes authentication independent of the **key table**, not of the
**database**. The token-status check is deliberately uncached, so a revocation takes
effect on the next request. That check reads the database for every token that passes
validation. When the whole database is unavailable, no token is accepted, and where a
request stops depends on the snapshot:

- **No usable snapshot:** 503 `SigningKeyStore`, before the token is judged.
- **Usable snapshot, token rejected on its own merits** (bad signature, expired, unknown
  `kid`, and so on): 401, before any status read.
- **Usable snapshot, token passes validation:** the status read fails, giving 503
  `TokenStatusStore`.

Maximum staleness keeps valid tokens accepted only when key reads fail while token-status
reads still work, for example while the key table is locked.

### Rotating and retiring a database signing key

This is how the system behaves, not an extra mechanism. A key change passes through **two
independent caching layers**, and each one must pick it up:

- **Each Configuration Service instance's snapshot.** It is used for validation and JWKS
  on that instance only.
- **Each consumer's cached key set.** DMS, and any other service that validates
  Configuration Service tokens from JWKS, keep their own.

The Configuration Service's own timing bounds say nothing about how long a consumer keeps
an old or empty key set.

#### What each layer does

- **Issuance.** Token issuance reads the newest active row directly. An inserted key
  therefore signs the next token issued, on every instance, at once.
- **Configuration Service validation and JWKS.** These use the instance's snapshot. Write
  `T_prop` for the time a healthy instance takes to use a key-table change, with no
  traffic:

  `T_prop` = load timeout + refresh interval + 10 % + load timeout = 10 + 300 + 30 + 10 =
  **350 s** at the defaults.

  A key-table change committed at time *t* is expected to be in use on a healthy instance
  by *t* + `T_prop`. "Healthy" means that every load succeeds within its deadline and the
  background reload runs as designed. This is an expectation under those conditions, not
  a guarantee to sleep on. The verification below is what tells you the change arrived.

  The first load-timeout term is the load that may already be running at *t*. That load
  can read the rows from before the change and publish them after it, as late as
  *t* + 10 s. The next refresh interval, and the cooldown, count from that publication. For
  example, a load that publishes the old rows at *t* + 9 s, followed by a scheduled load
  that starts 330 s later and takes 9 s, puts the change in use at *t* + 348 s. Any load
  that starts after *t* sees the change. An instance whose key store is failing picks up
  the change only with its first successful load after the store recovers.
- **DMS.**
  - **When it fetches.** DMS fetches the discovery document and JWKS when it starts. It
    logs `OIDC metadata cache warmed up successfully. Issuer: …, SigningKeys: {n}`.
    Afterwards it re-fetches on its automatic refresh schedule,
    `JwtAuthentication:AutomaticRefreshIntervalHours` (default 24). It requests an early
    re-fetch only when the discovered issuer does not match its configured authority.
  - **Unknown keys.** A token signed with a key DMS has not cached is rejected with 401.
    It does not trigger a re-fetch.
  - **Failed fetches.** A re-fetch that fails keeps the cached set: IdentityModel logs
    `IDX20806 … Returning the current configuration`. The refresh schedule is therefore
    not a bound on how long DMS trusts a retired key. A retired key leaves a DMS instance
    only after a **successful** re-fetch, or a restart that **successfully** loads the
    corrected set.
  - **At startup.** A DMS instance that cannot fetch JWKS at startup, for example because
    the Configuration Service answers 503, does not start.
- **Revocation.** `POST /connect/revoke` takes effect on the Configuration Service's own
  endpoints at the next request, because their status check is not cached. DMS validates
  tokens itself and never consults that status. A revoked token is accepted by DMS until
  it expires.

#### Verifying a key change on every instance

Before you restart or otherwise refresh any consumer, confirm the change on **every
Configuration Service instance that serves traffic**, addressing each one individually.
Use the container or pod address, or a port-forward per instance. Repeated requests
through a load balancer do not show that every instance has the change.

```powershell
foreach ($instance in 'http://cms-0:8081', 'http://cms-1:8081') {   # each instance, with its path base
    (Invoke-RestMethod "$instance/.well-known/jwks.json").keys.kid
}
```

- **The key id to look for** is the row's `KeyId`. `Generate-OpenIddictKey-Insert.ps1`
  sets it with `-KeyId` (default `key-<random>`) and prints it in the `INSERT`. The active
  ids are also returned by `SELECT "KeyId" FROM dmscs."OpenIddictKey" WHERE "IsActive"`.
  In certificate mode it is the certificate thumbprint.
- **After an insert,** every instance must list the new key id. **After a retirement,** no
  instance may list the retired one.
- **`503`:** that instance has no usable snapshot. Stop until it serves `200`.
- **A connection failure:** the instance is unavailable. Stop until it serves and passes
  the check, or until it is deliberately out of service and receives no traffic.
- **`200 {"keys":[]}`:** reports what that instance's last successful load found. It is
  not a live read of the table.
- **Shortening the wait.** Restarting an instance makes it load the table at startup. That
  can shorten the wait, but it does not replace the check, because the startup load can
  fail.

#### Refreshing DMS and other consumers

Waiting for the Configuration Service does not clear a consumer's cache.

1. Once the verification above passes, restart each DMS instance. A rolling restart is
   fine.
2. On **each** instance, confirm the outcome with a token whose header `kid` is the key in
   question. The `kid` is in the token's first segment (base64url-encoded JSON). The
   `SigningKeys: {n}` count in the startup log line is a diagnostic only. It does not
   identify the keys.
3. A restart also discards the token DMS caches for its own Configuration Service calls
   (`CacheSettings:TokenCacheExpirationSeconds`, default 1500 s, held in memory).

#### Adding the first key

There are two supported orders:

- **Key inserted before the Configuration Service starts** (`start-local-dms.ps1` runs
  `setup-openiddict.ps1 -InitDb` first). The startup load finds the key. Verify it on every
  instance, then start DMS.
- **Key inserted after the Configuration Service starts** (`start-local-config.ps1`, which
  the Configuration Service E2E setup uses).
  - Each running instance first publishes an empty snapshot, with the Warning
    `Signing-key snapshot {n} is empty`.
  - After the insert, tokens are issued with the new key at once. Each instance accepts
    them through the bootstrap exception described under
    [Refresh, backoff, cooldown and staleness](#refresh-backoff-cooldown-and-staleness),
    then subject to the cooldown, or at its next reload.
  - Its JWKS lists the key only after that instance's next successful load.
  - Verify the key on every instance before starting DMS.
  - Restart any DMS instance that started in the meantime, and confirm acceptance on it.
    Depending on its startup order, it may have cached an empty key set.

#### Adding a key (rotation)

1. **Insert the new key** as an active row, at a quiet time. On PostgreSQL,
   [`Generate-OpenIddictKey-Insert.ps1`](../../../eng/docker-compose/Generate-OpenIddictKey-Insert.ps1)
   prints a suitable `INSERT`. **Signing switches at the next token issued, on every
   instance.** Validation does not switch at that moment:
   - **Configuration Service instances** accept the new key's tokens as soon as their
     snapshot holds the key.
     - The first new-key token an instance sees triggers the unknown-key reload when the
       cooldown and backoff allow.
     - Otherwise that token is rejected with **401**, and the key arrives at the next
       allowed unknown-key reload or by `T_prop`, whichever comes first.
     - A request may also join a load that is already running, but a load that started
       before the insert returns without the key.
     - *Healthy key store, with traffic.* The last load that read the old rows completes
       within one load timeout of the insert. Its cooldown ends one cooldown later. The
       first new-key request after that starts a reload, which completes within one load
       timeout. New-key tokens are therefore accepted from at most load timeout + cooldown
       + load timeout after the insert: **50 s** at the defaults. This assumes a new-key
       request reaches the instance once the cooldown has ended. Without one, `T_prop`
       applies.
     - *Key store failing.* The key arrives with the first successful load after the store
       recovers, within the conditional recovery bounds above (*R* + 82 s at the defaults
       with no load in flight at recovery, *R* + 92 s with one).
     - Instances are independent: one may accept a new-key token while another still
       answers 401 until its cooldown ends or its next reload.
   - **Each DMS instance** rejects new-key tokens with 401 until a successful re-fetch, or
     a restart, loads the new key. A client can present the same token again once that
     has happened.
2. **Verify the new key on every instance** ([above](#verifying-a-key-change-on-every-instance)).
   Where you need no 401s from the Configuration Service itself, do this before new-key
   tokens reach it.
3. **Restart each DMS instance**, and confirm a new-key token is accepted on each
   ([above](#refreshing-dms-and-other-consumers)). Do this while the old key is still
   active. The restarted instance then holds both keys, and tokens minted before the
   switch keep validating.
4. **Keep the old key active** for at least `TokenExpirationMinutes` plus the 5-minute
   validation clock skew after the insert (35 min at the defaults). Tokens minted before
   the switch then stay valid until they expire.

#### Retiring a key

1. **Retire the old key**, after the retention window of step 4 above, by setting
   `IsActive = false`.
   - **Configuration Service instances** reject its tokens within `T_prop` on healthy
     instances.
   - **An instance that cannot read the key store** keeps trusting the retired key until
     its snapshot expires: at most `SigningKeyMaxStalenessSeconds` (3600 s) after its last
     successful load. Its JWKS keeps listing the key meanwhile. That is the configurable
     trade-off between availability and how long a retired key can be accepted.
2. **Verify on every instance** that the retired key id is no longer listed.
3. **Restart each DMS instance**, and confirm on each that a token signed with the retired
   key is rejected. Until a DMS instance successfully re-fetches or restarts, it keeps
   accepting such tokens. Retiring the key in the database does not, by itself, stop any
   consumer from trusting it.

#### Retiring a key early, for example after a compromise

1. Set `IsActive = false` on the key, and make sure another active key exists.
2. Restart every Configuration Service instance, so that each loads the table at startup.
   An instance whose startup load fails answers 503 rather than trusting the old snapshot.
3. Verify on every instance that the key id is no longer listed.
4. Restart every DMS instance. On each, confirm two things:
   - a token signed with the retired key, and not yet expired, is rejected;
   - a token signed with the remaining key is accepted.
5. Revoke known tokens through `POST /connect/revoke`. That stops them on the Configuration
   Service's own endpoints only.

#### Replacing the certificate (certificate mode)

The snapshot holds the one certificate key, so there is no overlap window.

- **Issuance** reads the certificate file at every token, so replacing the file switches
  signing at once.
- **Validation and JWKS** switch at the instance's next load. Until then, tokens signed with
  the new certificate face the acceptance delay of [Adding a key](#adding-a-key-rotation),
  step 1. After the load, tokens signed with the replaced certificate are rejected. Each
  DMS instance keeps the old certificate's key until it successfully re-fetches or
  restarts.

To replace the certificate:

1. Work at a quiet time.
2. Replace the file on every instance.
3. Restart each Configuration Service instance.
4. Verify the new thumbprint on every instance.
5. Restart each DMS instance, and confirm a new-certificate token is accepted on each.

Clients holding tokens signed with the old certificate must obtain new ones.

### Log signals

| Event | Level | Message starts with |
| --- | --- | --- |
| Snapshot published | Information | `Signing-key snapshot {n} published from {Database\|Certificate} ({Trigger}): {k} keys, …` |
| Empty snapshot published | Warning | `Signing-key snapshot {n} is empty: …` |
| Load failed | Error | `Signing-key load failed ({Trigger}): category SigningKeyStore, kind {Retrieval\|Processing}, consecutive failures {n}, next attempt in {s} s. …` |
| Load finished after its deadline | Warning | `A signing-key load that outlived its deadline has finished …; its result was discarded` |
| Unknown key id (bearer schemes) | Warning | `Bearer token key id {kid} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}` |
| Unknown key id (introspection) | Warning | `Token key id {KeyId} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}` |
| Dependency 503 | Error | `Authentication could not reach a decision: the {Category} is unavailable (trace …)` |
| JWKS 503 | Error | `The JWKS could not be served: the SigningKeyStore is unavailable (trace …)` |
| Introspection could not decide (answers `{"active": false}`) | Error | `Token validation could not reach a decision: the {Category} is unavailable` |
| Revocation could not decide (answers `503`) | Error | `Revocation could not be completed: …` |

A snapshot publication labelled `Request` is a normal background reload started by a
request that found the snapshot overdue. It is not a fault. The message
`Failed to fetch public keys for JWKS`, logged before DMS-1556, is no longer emitted.

**For alerting,** match on message text that covers every source of an event:

- **Unknown key ids:** `was not in the signing-key snapshot` matches both unknown-key
  lines. The bearer line alone misses introspection.
- **Undecided requests:** `is unavailable` matches the dependency 503, the JWKS 503, and
  the introspection line. Introspection keeps its protocol answer on the wire, so that log
  is the only signal of its failures. Revocation answers `503` and logs
  `Revocation could not be completed`.

### Connection capacity

The snapshot takes key reads off the steady-state request path; the exceptions are listed
under [The key snapshot](#the-key-snapshot). Bearer authentication and introspection still
open a database connection for the uncached status check of every token that passes
verification with a valid `jti`, and most endpoints read their own data.
Under heavy concurrency the database's connection limit can still be reached. See the
connection-capacity note in
[Signing-key settings](../../../docs/CONFIGURATION.md#signing-key-settings-configuration-service-self-contained-only).

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
