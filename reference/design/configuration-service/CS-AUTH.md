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
| --- | --- | --- |
| `POST /connect/token` | Issue an access token (client credentials grant only) | Anonymous (client credentials in the request) |
| `POST /connect/introspect` | Introspect a token (RFC 7662) | Client credentials (HTTP Basic or form body, exactly one of them) [^introspect] |
| `POST /connect/revoke` | Revoke a token (RFC 7009) | Client credentials (HTTP Basic or form body, exactly one of them) [^revoke] |

[^revoke]: RFC 7009 §2.1 requires the revocation caller to authenticate the same
way it authenticates to the token endpoint (RFC 6749 §2.3), with client credentials
and not a bearer access token. Both identity provider modes enforce it and both revoke:
`self-contained` in the CMS token store, `keycloak` by delegating to Keycloak. See
[Token revocation](#token-revocation).

[^introspect]: RFC 7662 §2.1 requires some form of authorization to call the introspection endpoint, such as client
authentication or a bearer access token, so that it cannot be used to scan for valid tokens.
The Configuration Service uses client credentials in both identity provider modes, as at `/connect/revoke`; a bearer
access token is not accepted. In `keycloak` mode Keycloak checks them (see [Token introspection](#token-introspection)).
RFC 7662 §2.3 answers invalid client credentials with `401`, "as described in Section 5.2" of RFC 6749.
The endpoint sends that `401`, with a `WWW-Authenticate: Basic` challenge, when HTTP Basic was attempted.
Failed form credentials get `400 invalid_client`, as at `/connect/revoke`: RFC 6749 §5.2 requires the `401` only for
`Authorization` header authentication, and a challenge would invite a scheme the caller did not use.
See [Token introspection](#token-introspection).

All four `/connect/*` endpoints are rate limited per source address. `/connect/token`, `/connect/revoke`
and authenticated `/connect/introspect` are also limited per source address and `client_id` pair,
under either identity provider, immediately before the client secret is verified (DMS-1482). Different sources cannot
spend one another's client allowance.
A refusal is `429` with `Retry-After`, in the route's own error contract.
`/connect/token`, `/connect/revoke` and `/connect/introspect` answer the OAuth error `temporarily_unavailable` with the `error_description` `The number of allowed requests has been exceeded. Retry the request later.`
`/connect/register` answers Ed-Fi problem details of type `urn:ed-fi:management-api:too-many-requests`.
Limits, defaults and sizing are in
[CONFIGURATION.md](../../../docs/CONFIGURATION.md#connectratelimit); the design is
[DMS-1482](./DMS-1482-cms-connect-rate-limiting.md).

`/connect/token` accepts client credentials as HTTP Basic or as form fields in both identity provider modes, with Basic taking precedence.
Each value a parsed Basic header supplies wins, and a form field only fills a value Basic left empty, such as the empty secret in `Basic base64("id:")`.
A malformed Basic value falls back to the form fields.
A request that carries both Basic and form credentials is not rejected at `/connect/token`, unlike `/connect/revoke`.

An omitted or empty `scope` is treated as absent, as RFC 6749 section 3.2 requires, so CMS does not forward a `scope` parameter.
Keycloak then issues the client's default client scopes.
The `self-contained` provider issues the scopes registered for the client.

In `self-contained` mode, any other `scope` value is split on spaces and commas, and each value is compared with the client's registered scopes, ignoring case.
A `scope` that names none of them answers `400` with the RFC 6749 section 5.2 error body, served as `application/json`, and no token is issued or stored:

```json
{"error":"invalid_scope","error_description":"The requested scope is invalid, unknown, malformed, or exceeds the scope granted to the client."}
```

This includes a value made only of spaces or commas, which is supplied and therefore not absent, and any `scope` sent by a client with no registered scopes.
The client credentials are checked first, so a wrong secret still answers `invalid_client` whatever the `scope`.
The answer carries no `correlationId` and no `WWW-Authenticate` header.
It used to succeed with every scope registered for the client.

A `scope` that names some of the client's registered scopes succeeds with only those, as RFC 6749 section 3.3 permits.
Requested values the client does not hold are dropped, and so are registered scopes it did not request.
The granted scopes keep the spelling and order of the request, and duplicates are not removed.

A successful token response carries a `scope` member that lists the granted scopes separated by spaces, as RFC 6749 section 3.3 defines, for example `"scope":"edfi_admin_api/full_access edfi_admin_api/readonly_access"`.
In `self-contained` mode the member is present whenever at least one scope is granted; it is omitted for a client with no registered scopes that omitted `scope`, because RFC 6749 section 3.3 defines no empty scope value.
Under Keycloak it is the `scope` Keycloak returns, passed through unchanged, and it is omitted when Keycloak returns none.
The `scope` claim in a `self-contained` access token lists the same granted scopes, also separated by spaces (see below).

Under Keycloak, a requested scope that Keycloak rejects answers `400 invalid_scope` in the OAuth error format, for example with the `error_description` `Invalid scopes: edfi_admin_api/readonly_access`.
It used to answer `502`, and then `400` in problem details.
Other Keycloak failures keep their status: a failed connection, a 5xx or any other `400` error code answers `502`, now as `server_error`, and a name-resolution failure or a timeout answers `500`.
A `client_secret` or `scope` longer than Keycloak's form attribute limit (128 KiB by default) also answers `502` `server_error`: CMS accepts form values up to 4 MiB, and Keycloak rejects the request with `413`.
Each token request to Keycloak closes its connection after the answer, as revocation and introspection requests do (see [Responses](#responses)), so such a request does not stall the next one.

The `self-contained` provider joins the scopes it grants with single spaces, in both cases that issue a token: the requested scopes the client holds, and the client's registered scopes when no scope is requested.
The token response `scope` and the signed JWT `scope` describe the same granted values.
The token request parser accepts a requested scope list separated by commas or spaces. Issued tokens retain their scope values and delimiters until they expire; clients must request a new token to obtain the current issuance format.

Both `/openapi/v1.json` and `/metadata/specifications` declare an `oauth2_client_credentials` scheme whose `tokenUrl` is the document's own server URL (request scheme, host and path base) followed by `/connect/token`.
The token URL follows the address the document was requested on, as the server URL and the DMS OpenAPI token URL do; the discovery document instead builds its endpoints from `IdentitySettings:Authority`, because services fetch it to validate tokens.
The scheme lists the scopes `edfi_admin_api/full_access`, `edfi_admin_api/readonly_access` and `edfi_admin_api/authMetadata_readonly_access`.
Operations that need no token (`/connect/register`, `/connect/token`, `GET /`, `GET /tenancy`, `GET /health` and discovery) declare an empty `security` array.
`/connect/revoke` and `/connect/introspect` instead require a `client_secret_basic` HTTP Basic scheme, whose description notes that the credentials may be sent as form fields instead.
A stock Swagger UI can therefore authorize against the Configuration Service in both modes.

### `/connect/token` failures

`/connect/token` answers every failure in the OAuth error format of RFC 6749 section 5.2.
The body is `application/json` with exactly the root members `error` and `error_description`, written by the same writer as `/connect/revoke` and `/connect/introspect`.
It covers every error the handler produces and every exception that escapes it.
Responses produced before the endpoint is selected, such as the `405` for another HTTP method, keep their framework form.

| Condition | Status | `error` |
| --- | --- | --- |
| Client authentication failed, client id or secret missing or blank, or client id longer than 1024 UTF-16 code units, HTTP Basic attempted | `401` with `WWW-Authenticate: Basic realm="EdFi.DmsConfigurationService"` | `invalid_client` |
| The same, no HTTP Basic attempt | `400` | `invalid_client` |
| Body is not a form | `400` | `invalid_request` |
| A parameter or the `Authorization` header sent more than once | `400` | `invalid_request` |
| `grant_type` missing or empty | `400` | `invalid_request` |
| Unsupported `grant_type` | `400` | `unsupported_grant_type` |
| `self-contained`: a `scope` naming none of the client's registered scopes (see [Token Endpoints](#token-endpoints)) | `400` | `invalid_scope` |
| Keycloak rejected the requested scope | `400` | `invalid_scope` |
| Source or source-plus-client rate limit exceeded | `429` with `Retry-After` | `temporarily_unavailable`, with the fixed rate-limit description (see [Token Endpoints](#token-endpoints)) |
| Token limit reached | `429` | `temporarily_unavailable`, with the canonical limit sentence as `error_description` |
| Concurrent token requests for the same client (lock timeout) | `409` | `temporarily_unavailable` |
| Identity provider failure that is not the caller's to fix, a provider `403` included | `502` | `server_error` |
| Unexpected fault in CMS | `500` | `server_error` |

The `invalid_client` house rule is the one `/connect/revoke` and `/connect/introspect` follow.
A caller that attempted HTTP Basic gets `401` with the `Basic` challenge that RFC 6749 section 5.2 requires.
Any other caller, including one that sent form credentials, one that sent no credentials at all and one whose `Authorization` header is not `Basic`, gets `400` with no challenge, because a challenge would invite a scheme the caller did not choose.
Before this rule a failed form credential answered `401` and a missing one answered a validation `400`.

The handler checks the request without throwing, in this order: a body that is not a form, then a repeated `grant_type`, `client_id`, `client_secret` or `scope` or a repeated `Authorization` header, then `grant_type` missing, then `grant_type` unsupported, then client id or secret missing or only whitespace (after the lenient Basic parsing and the form fallback), then a client id longer than 1024 UTF-16 code units, then the source-plus-client rate limit, and only then the token manager.
A repeated parameter is rejected even when one copy is valid or empty, as at `/connect/revoke` and `/connect/introspect`.
A request that is wrong in the request shape is therefore told so before its credentials are examined.

The token limit and the lock timeout keep the statuses they always had, `429` and `409`, and take the OAuth body.
The `409` keeps its status rather than the `503` RFC 6749 suggests for `temporarily_unavailable`, because the DMS `/oauth/token` proxy recognizes the condition by status.
The `429` `error_description` is `Too many access tokens have been requested (limit is N). Access tokens should be reused until they expire.`, which the DMS proxy parses to rebuild its own answer.
The rate-limit `429` shares the `temporarily_unavailable` code, and the DMS proxy tells the two apart by their descriptions.

A failed client authentication is `invalid_client` whatever code the self-contained token manager or Keycloak reports.
RFC 6749 section 5.2 defines no other code for it, and passing on a second code, such as the `unauthorized_client` Keycloak reports for a wrong secret, would tell the caller whether the client id it sent exists.
For the same reason a Keycloak rejection always carries the fixed description `Invalid client or Invalid client credentials`: Keycloak tells a public client presenting any secret that it is a public client.
The description a service-owned failure carries is used as it is.

Keycloak's `invalid_scope` description, which names the rejected scopes, is the only provider text that reaches the caller.
It is used only when the provider sent a complete structured body (an `error` string and an `error_description` string) whose `error` is one of the six codes RFC 6749 section 5.2 defines for a token endpoint: `invalid_request`, `invalid_client`, `invalid_grant`, `unauthorized_client`, `unsupported_grant_type` and `invalid_scope`.
The comparison is ordinal, so `Invalid_Scope` is not recognized.
Because Keycloak echoes the requested scope, the description is also used only when every character is one section 5.2 allows in `error_description`: printable ASCII other than `"` and `\`.
Otherwise the fixed, service-owned description applies.

A provider failure that is not the caller's to fix answers `502 server_error` with a fixed description and no provider text.
That includes a Keycloak `403`, whose known cause is the provider's own configuration, such as a realm that requires HTTPS, so a `403` would wrongly blame the caller.
The `502` is logged at Warning with the failure kind and, from a structured body with one of the six codes, the provider's `error` and `error_description`, sanitized; the provider body itself is never logged.

`/connect/register` keeps the Ed-Fi problem-details contract of [DMS-1218](./DMS-1218-cms-error-response-compliance.md).
It is a CMS bootstrap endpoint and not an RFC 6749 or RFC 7591 endpoint: it takes PascalCase form fields, answers with a `{title, status}` success body and reports field-level `validationErrors`.
Its `403` when registration is disabled is fixed by the bootstrap rules above, and RFC 6749 section 5.2 defines no codes for it.

As at `/connect/revoke` and `/connect/introspect`, an OAuth answer carries no `correlationId` member and no correlation header.
Only an exception that the exception handler answers carries the `TraceId` response header.

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
   before any database or identity provider contact. Parsed `client_id` values longer than
   1024 UTF-16 code units (including padding) also fail here with `invalid_client`: `401`
   with the Basic challenge for an HTTP Basic attempt, `400` otherwise. This bound applies
   even when rate limiting is disabled and precedes client-budget processing.

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

`/connect/token` keeps its lenient parser in both identity provider modes; it uses
`Uri.UnescapeDataString` and falls back to form fields when the Basic value cannot be decoded.
For a secret sent without encoding, the two parsers agree or differ as follows:

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

`200 OK` with an empty body is the only success. Every error status, the rate-limit `429` described
above included, is an RFC 6749 §5.2 JSON object served as
`application/json`, with a **fixed** CMS `error_description` that
never echoes provider text:

```json
{ "error": "invalid_client", "error_description": "Invalid client or Invalid client credentials" }
```

| Condition | Response |
| --- | --- |
| Authenticated owner, supported live token | `200`, empty; token revoked |
| Authenticated caller; unknown, invalid, expired, already revoked, or another client's token | `200`, empty; nothing changes |
| Source or source-plus-client rate limit exceeded | `429 temporarily_unavailable` with `Retry-After` |
| Not a form body, malformed form, duplicated parameter or `Authorization` header, mixed mechanisms, missing or empty `token` | `400 invalid_request` |
| Missing, incomplete or invalid credentials **without** an HTTP Basic attempt | `400 invalid_client` |
| Malformed or invalid HTTP Basic credentials | `401 invalid_client` with `WWW-Authenticate: Basic realm="EdFi.DmsConfigurationService"` |
| A Keycloak public or bearer-only client | `invalid_client` by the same rule: `401` with the challenge when it attempted HTTP Basic, `400` otherwise |
| A token type the identity provider does not revoke (Keycloak: an ID token) | `400 unsupported_token_type`; reveals nothing about the token's owner |
| Keycloak rejected the request shape for a reason CMS could not anticipate, or rejected a value over its form attribute limit (see below) | `400 invalid_request` ("The identity provider rejected the revocation request.") |
| Database or identity provider failure, timeout, or an unusable provider answer | `503 temporarily_unavailable` |
| An unexpected fault in CMS | `500 server_error` |

A request without credentials and without `token` gets `400 invalid_request`, not an
authentication error, because request shape is checked first. A public client is answered
with the same `invalid_client` description as a wrong secret, so that answer does not
reveal the client's type. The exception is an oversized `client_secret` or `token` in
`keycloak` mode (below): the client-type gate answers an unknown, public or bearer-only
client `invalid_client`, while a confidential client's request goes on to Keycloak and is
answered `400 invalid_request`, so the two answers tell a confidential client from the
others without a correct secret.

In `keycloak` mode, a `client_id` of more than 255 Unicode code points is answered `invalid_client` without calling Keycloak.
Keycloak keeps client ids in a 255-character column, and PostgreSQL measures it in code points, so 255 characters outside the Basic Multilingual Plane are a valid client id.

In `keycloak` mode, the client-type gate comes first: an unknown, public or bearer-only `client_id` is answered `invalid_client` there, whatever the size of `client_secret` or `token`, and nothing is sent to Keycloak's revocation endpoint.
Only a request that passes the gate is delegated to Keycloak.
CMS accepts form values up to 4 MiB, so a delegated request whose `client_secret` or `token` is longer than Keycloak's form attribute limit (128 KiB by default) reaches Keycloak, which rejects it with `413` before it authenticates the caller; that answer is `400 invalid_request` and logged at Information.
It is therefore not `invalid_client`, and it is not `503`, because Keycloak is not failing.
`/connect/introspect` maps Keycloak's `413` the same way. Introspection has no client-type gate; requests that pass its other validation and admission checks are delegated to Keycloak (see [Token introspection](#token-introspection)).
Each request to Keycloak's token, revocation and introspection endpoints closes its connection after the answer: Keycloak 26.1.4 answers a form value over its limit with `413` without reading the rest of the request and keeps the connection open, so a reused connection would leave the next caller, possibly another client, without an answer until the timeout.

This endpoint, `/connect/introspect` and `/connect/token` are the three CMS endpoints that do not answer in the
`application/problem+json` contract of [DMS-1218](./DMS-1218-cms-error-response-compliance.md), and the departure is
deliberate: all three are OAuth endpoints, and an OAuth client reads `error` off the
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
| --- | --- |
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
| --- | --- | --- |
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

- **`self-contained`**: `POST /connect/introspect` with `token=<target>`, authenticated with
  the token owner's client credentials, reports `{"active": false}` once the token is revoked.
  Any other caller always receives `{"active": false}`, so only the owner's credentials can confirm anything.
  A token the service could not decide, because its signing-key or token-status store failed, also reports
  `{"active": false}` and logs the Error `Token validation could not reach a decision`, so check for that line
  before treating the answer as confirmation.
  A protected CMS request with the token
  answers `401`.
- **`keycloak`**: CMS `/connect/introspect` with `token=<target>`, authenticated with the token owner's client credentials, passes them to Keycloak's introspection and reports `{"active": false}` once an access token is revoked (see [Token introspection](#token-introspection)).
  It cannot observe a refresh token, which it never describes, or a public client's token, because Keycloak refuses a public client at introspection.
  From Keycloak 26.6.2 it confirms only a token whose `aud` contains the owner's client id: Keycloak answers `active:false` to an introspecting client outside the token's `aud`, live token or not.
  Every client CMS creates is in its own tokens' audience, but a token issued before its client gained that audience is not, so decode the token and check its `aud` before treating the answer as confirmation.
  Otherwise use Keycloak's own endpoint,
  `POST {Keycloak base URL}/realms/{realm}/protocol/openid-connect/token/introspect`,
  authenticated as a **confidential** client:
  - Access tokens. From Keycloak 26.6.2 (the Red Hat build of Keycloak 26.4.12; observed on
    26.7.5, not on 26.1.4), the introspecting client must be in the token's `aud`, or the answer is
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
| --- | --- | --- |
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
  described above. Source and client rate limits now bound admission before those admin calls.
  Admin-token caching remains deferred availability work; it would also have to handle concurrent
  cache misses and refresh failures.
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

## Token introspection

`POST /connect/introspect` reports the state of a token to the client that owns it.

- **The caller authenticates with client credentials.**
  Exactly one of HTTP Basic or the form fields `client_id` and `client_secret` is accepted.
  A bearer `Authorization` header is not authentication and is ignored.
- **The request rules and their order are those of `/connect/revoke`.**
  See [Request rules](#request-rules).
  The client's credentials are checked after them.
  Errors, the rate-limit `429` described in [Token endpoints](#token-endpoints) included, are RFC 6749 JSON (`{"error", "error_description"}`).
  A missing `token` is `400 invalid_request`.
  This is an exception to the problem-details format DMS-1218 gave that response, as it is for revoke.
  Failed credentials answer `invalid_client`: `401` with a `WWW-Authenticate: Basic` challenge when HTTP Basic was attempted, `400` otherwise.
- **Only the token's own client receives metadata.**
  For the owner's valid token the answer is `200` with `active` true, `client_id`, `scope`, `exp`, `iat`, `sub`, `aud`, `iss` and `token_type` `Bearer`, or `DPoP` for a Keycloak DPoP-bound access token (RFC 9449 §6.2).
  `scope` is space-delimited, and empty for a token without scopes.
  `exp` and `iat` are JSON integers.
  `aud` is a string for one audience and an array for several, as RFC 7662 §2.2 allows.
- **Every other outcome is exactly `{"active": false}`.**
  That covers another client's token and an unknown, malformed, unverifiable, expired or revoked token.
  In `keycloak` mode the one exception is a token Keycloak fails while checking, described below.
- **A host refuses to start without an introspection implementation.**
  Without one, the endpoint could not authenticate its callers.
  As for revocation, the startup error names only the exception types.
- **`self-contained` mode.**
  Ownership compares the token's `client_id` claim with the caller's stored client id, ordinally, and the token's status
  is read only from a row stored for the calling application, as revocation's update is.
  A token the service could not decide because the signing-key store or the token-status store failed is `{"active": false}`, and logs the Error in [Log signals](#log-signals).
  A client-store failure while authenticating the caller is `503 temporarily_unavailable`: the request was not judged, so an outage cannot read as rejected credentials.
  Retrying is safe.
- **`keycloak` mode.**
  The Configuration Service forwards the caller's credentials, as form fields, and the token to
  `POST {Keycloak base URL}/realms/{realm}/protocol/openid-connect/token/introspect`, so Keycloak authenticates the caller and decides the token's state.
  `token_type_hint` is not forwarded: the token is always looked up as an access token, and Keycloak's other token types (refresh, ID) are never described.
  Keycloak does not check ownership: from 26.6.2 it describes a token to any confidential client in the token's `aud`, and before 26.6.2 to any confidential client it authenticates.
  The Configuration Service therefore describes a token only when Keycloak reports it active and its `azp` equals the caller's client id, ordinally, and copies only the members listed above, never Keycloak's other claims.
  Keycloak leaves `scope` out of its answer for a token without scopes, as RFC 7662 §2.2 allows; the owner then receives an empty `scope`.
  Credentials Keycloak rejects, and a public client (which Keycloak refuses at introspection), answer `invalid_client` by the same rules as `self-contained` mode.
  Keycloak 26.1.4 also refuses a bearer-only client that sends its secret (`invalid_client`), while 26.7.5 authenticates it; a bearer-only client obtains no tokens, so it is never told about one.
  Keycloak 26.7.5 answers an unexpected failure while it authenticates the caller as 26.1.4 answers wrong credentials (`401 invalid_request` "Authentication failed."), so that failure is answered `invalid_client` too, not `503`.
  A `client_id` of more than 255 Unicode code points, the most Keycloak's client id column holds, is answered `invalid_client` without calling Keycloak.
  A `client_secret` or `token` longer than Keycloak's form attribute limit (128 KiB by default) is answered `400 invalid_request` ("The identity provider rejected the introspection request.") and logged at Information, as a delegated request is at `/connect/revoke`, where an unknown, public or bearer-only client id is answered `invalid_client` before any delegation.
  The Configuration Service accepts form values up to 4 MiB, and Keycloak rejects such a request with `413` before it authenticates the caller, so the answer is neither `invalid_client` nor `503`.
  Once it has authenticated the caller, Keycloak answers any failure while it checks the token or builds its answer with `400 invalid_request` "Failed to introspect token.": a JWT whose header has no `alg` as much as a failure of its own stores or mappers.
  The two cannot be told apart, so that answer is `503 temporarily_unavailable`, not `{"active": false}`, which could tell an owner its live token is not active; it is logged at Warning, not Error, because any caller can cause it with a malformed token.
  Any other Keycloak answer, a timeout or an unreachable Keycloak is `503 temporarily_unavailable`.
  Keycloak's answer is read whole, because its size does not follow the token's: Keycloak rebuilds an answer's claims from the client's protocol mappers as they are when it answers.
  A short token issued before its application gained 14,000 education organizations is answered with all of them, in more characters than any token Keycloak's form attribute limit lets through.
  No caller can enlarge an answer: only an active token is answered with claims, and they come from the token and from what administrators configured for its client; `/connect/token` reads Keycloak's answer whole too.
  The timeout is one `AppSettings:TokenRequestTimeoutSeconds` budget for Keycloak's response headers and another for reading its body, so a request can wait up to twice that setting.
  Unlike an undecided token in `self-contained` mode, a `503` here is not `{"active": false}`, because the same Keycloak call authenticates the caller: without a usable answer the caller is not authenticated either.
  Retrying is safe.
- **Each Keycloak client the Configuration Service creates is in its own tokens' audience.**
  From Keycloak 26.6.2 (the Red Hat build of Keycloak 26.4.12; see [Migrating to 26.6.2](https://www.keycloak.org/docs/latest/upgrading/index.html#migrating-to-26-6-2); observed on 26.7.5, not on 26.1.4) Keycloak answers `active:false` to an introspecting client that is not in the token's `aud`; `azp` alone does not count.
  The Configuration Service therefore adds an audience mapper (`oidc-audience-mapper`, with the client's own client id as its included client audience) to every Keycloak client it creates.
  `eng/docker-compose/setup-keycloak.ps1` adds the same mapper to the clients it creates, such as `DmsConfigurationService`.
  Run again for a client that already exists, it adds the mapper to that client only when no audience mapper counting by the rules below gives the client its own audience, and changes nothing else: credentials, roles, scopes and other mappers stay as they are, and a second run adds nothing.
  A conflicting `Client's own audience` mapper (below) is left as it is, and the script warns that the client was not repaired and needs an operator.
  It reaches only the client it is run for, not the other clients the Configuration Service manages.
  The client's access tokens carry `aud` `["<client id>", "account"]`, and its owner receives its own token's metadata on Keycloak 26.1.x and on 26.6.2 or later.
  DMS and the Configuration Service validate `aud` against their configured audience (`account` by default), which the array still contains.
  A client created by an earlier version gains the mapper from the next of these updates, even with unchanged values: `PUT /v3/apiClients/{id}` for that API client, and `PUT /v3/vendors/{id}` for every API client of the vendor's applications.
  `PUT /v3/applications/{id}` updates only the clients its change affects and those already pending, so an unchanged one reaches no client when none is already pending; it only retries the pending ones.
  A credential reset (`PUT /v3/apiClients/{id}/reset-credential` or `PUT /v3/applications/{id}/reset-credential`) changes only the secret and does not add the mapper.
  An audience mapper counts only when it is an OpenID Connect mapper that names the client (as its included client audience, or, without one, its included custom audience) and has *Add to access token* on, because Keycloak runs only the mappers of the client's protocol and checks the access token's `aud`, which every token of these clients has; the mapper is added beside one that is left out of access tokens, such as one added only to ID tokens, or one of another protocol, which stays as it is.
  The exception is a client that already has an OpenID Connect mapper named `Client's own audience` that does not put the client into its access tokens, for example one edited in Keycloak.
  Keycloak requires mapper names to be unique only within a protocol, so a mapper of another protocol under that name, such as a SAML one, is no exception.
  The update leaves that mapper as it is, so as not to remove an audience something else may rely on, still succeeds, and logs the Warning in [Log signals](#log-signals).
  On Keycloak 26.6.2 or later, an earlier client that is never updated that way, or that kept such a mapper, is answered `{"active": false}` for its own live token until an operator adds the same mapper in Keycloak: in the client's `<client id>-dedicated` scope, an *Audience* mapper with *Included Client Audience* set to the client itself and *Add to access token* and *Add to token introspection* on.
  When `Client's own audience` is taken, the operator gives the new mapper another name; any OpenID Connect audience mapper of the client itself (its dedicated scope) that names the client with *Add to access token* on satisfies later updates, which then add no other; one in a shared client scope does not.
  Only tokens issued after the mapper is added carry the audience, whichever way it is added: on Keycloak 26.6.2 or later, a live token issued before then is answered `{"active": false}` to its owner until it expires, so the owner requests a new token to be described.
  Keycloak also has opt-outs, the server option `allow-token-introspection-without-audience-check` and the client setting *Allow token introspection without audience check*, but its migration guide deprecates both, and they are not recommended.
  The compose files pin Keycloak 26.1.4; the Azure VM reference deployment runs 26.7.
- **Discovery.**
  `introspection_endpoint_auth_methods_supported` and `revocation_endpoint_auth_methods_supported` are both `["client_secret_basic", "client_secret_post"]`.

## Signing keys in self-contained mode

This section applies to `self-contained` mode only. In `keycloak` mode none of these
components is registered, and token validation is unchanged. The settings named here
are described, with their defaults and accepted ranges, in
[Signing-key settings](../../../docs/CONFIGURATION.md#signing-key-settings-configuration-service-self-contained-only).
The design and its test evidence are in
[DMS-1556](./DMS-1556-cms-signing-key-resolution-under-load.md).

### Accepted signing algorithm

The helper that verifies a self-contained token's signature, `JwtTokenValidator`,
accepts **RS256 only**, matching the algorithm token issuance signs with. A token that
is correctly signed with another algorithm (for example RS512 or PS256) fails
verification even when the trusted RSA key could verify it and its issuer, audience and
lifetime are valid. It is classified as untrusted, like an unsigned token or one with an
unknown key id, and its failure is sanitized as other verification failures are. The
same verification backs the self-contained `Bearer` schemes, introspection (the token
is reported `{"active": false}`), and revocation (nothing is changed, and the response
is the existing idempotent answer for a token that cannot be verified).

This is a restriction of that helper only. It is not a platform-wide algorithm policy,
and it does not change `keycloak` mode or token validation in DMS.

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
- **Introspection** checks the token supplied in the request form.
  The request's `Authorization` header is used only for HTTP Basic client authentication,
  and a bearer header is ignored.

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
| Usable snapshot, valid token, token status valid | Request proceeds | `200` with keys | `{"active": true}` for the token's owner | Revokes; `200` |
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
- **Introspection answers an undecided token as inactive.** It keeps its protocol answer
  (`{"active": false}`) when a dependency fails while deciding the token, and logs an Error naming the category.
  A failure of the client store while authenticating the caller is the exception: it answers `503 temporarily_unavailable`.
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
       timeout. New-key tokens are therefore accepted from at most load timeout +
       cooldown + load timeout after the insert: **50 s** at the defaults. This assumes a
       new-key request reaches the instance once the cooldown has ended. Without one,
       `T_prop` applies.
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
| Introspection could not authenticate the caller (answers `503`) | Error | `Introspection could not be completed: {Boundary} failed ({ExceptionTypes})`, followed by the Warning `Introspection for client {ClientId} could not be completed ({Reason}); answering 503` |
| Introspection answered inactive for another client's token | Debug | `Introspection answered inactive: the supplied token does not belong to the calling client {CallerClientId}` |
| Keycloak introspection could not be completed (`keycloak`, answers `503`) | Error | `Introspection for client {ClientId} could not be completed: Keycloak answered HTTP {StatusCode} …` or `…: the Keycloak introspection request failed ({Reason}, {ExceptionTypes})`, followed by the same Warning as above |
| Keycloak failed while checking the token (`keycloak`, answers `503`) | Warning | `Introspection for client {ClientId} could not be completed: Keycloak failed while checking the token (HTTP 400), which a malformed token or a Keycloak failure causes alike (undecided-token)`, followed by the same Warning as above |
| Keycloak refused the caller's credentials (`keycloak`) | Information | `Keycloak refused the introspection request for client {ClientId} (HTTP {StatusCode}, {ProviderError})` |
| Keycloak rejected an introspection form value over its form attribute limit (`keycloak`, answers `400 invalid_request`) | Information | `Keycloak rejected an introspection request as too large (HTTP 413) before authenticating the caller` |
| Keycloak rejected a revocation form value over its form attribute limit (`keycloak`, answers `400 invalid_request`) | Information | `Keycloak rejected a revocation request as too large (HTTP 413) before authenticating the caller` |
| Introspection client id longer than any Keycloak client id (`keycloak`, answers `invalid_client`) | Information | `Introspection refused: the client id is longer than any Keycloak client id` |
| Revocation client id longer than any Keycloak client id (`keycloak`, answers `invalid_client`) | Information | `Revocation refused: the client id is longer than any Keycloak client id` |
| Keycloak reported another client's token (`keycloak`, answers `{"active": false}`) | Debug | `Introspection answered inactive: the supplied token was not issued to the calling client {ClientId}` |
| A client update kept an OpenID Connect `Client's own audience` mapper that does not put the client into its access tokens (`keycloak`, the update succeeds without the client's own audience) | Warning | `Client {ClientId} already has an OpenID Connect protocol mapper named {MapperName} that does not put the client into its own access tokens' audience; it is left unchanged, …` |
| Introspection Basic credentials malformed | Information | `Introspection Basic credentials were malformed at the {Stage} stage` |

A snapshot publication labelled `Request` is a normal background reload started by a
request that found the snapshot overdue. It is not a fault. The message
`Failed to fetch public keys for JWKS`, logged before DMS-1556, is no longer emitted.

**For alerting,** match on message text that covers every source of an event:

- **Unknown key ids:** `was not in the signing-key snapshot` matches both unknown-key
  lines. The bearer line alone misses introspection.
- **Undecided requests:** `is unavailable` matches the dependency 503, the JWKS 503, and
  the introspection line. Introspection keeps its protocol answer on the wire when it cannot decide a token, so that log
  is the only signal of those failures. Revocation answers `503` and logs
  `Revocation could not be completed`.

### Connection capacity

The snapshot takes key reads off the steady-state request path; the exceptions are listed
under [The key snapshot](#the-key-snapshot). Bearer authentication still opens a database
connection for the uncached status check of every token that passes verification with a valid
`jti`, and introspection for the status of the caller's own verified token.
Token, introspection and revocation requests also read the calling client and verify its secret
(PBKDF2, `IdentitySettings:ClientSecretHashingIterations`), and most endpoints read their own data.
Under heavy concurrency the database's connection limit can still be reached. See the
connection-capacity note in
[Signing-key settings](../../../docs/CONFIGURATION.md#signing-key-settings-configuration-service-self-contained-only).

## Scopes

Authorization is scope-based. Three scopes are defined:

| Scope | Description |
| --- | --- |
| `edfi_admin_api/full_access` | Full access to all CMS API endpoints |
| `edfi_admin_api/readonly_access` | Read-only access to all CMS API endpoints |
| `edfi_admin_api/authMetadata_readonly_access` | Read access to the `MapLimitedAccess` endpoints below: every Configuration Service read a single-tenant DMS makes. A multi-tenant DMS also reads `GET /v3/tenants/`, which is `MapSecuredGet`, so it needs `readonly_access` |

### How a token's scopes are evaluated

A token's `scope` may be one space-delimited string or an array. An array arrives as one
`scope` claim per element, so the scope policy considers **every** claim whose type is
exactly `scope`. Each value is split on ASCII space and empty entries are removed. Commas
and tabs do not separate scopes, and the position of a scope in the token never changes
the decision.

The policy succeeds when any scope the token holds is any scope the endpoint allows.
The comparison is exact and case-sensitive: wrong-case and prefix-only values do not match.
A missing, empty or nonmatching `scope` grants nothing. The service-role policy is
evaluated independently and still composes with the scope policy (see
[Endpoint Authorization Model](#endpoint-authorization-model)), so a matching scope does
not open a route without the required role.

### Claim-set names

A claim-set name must not contain white space or a comma
(`Claim set name must not contain white spaces or commas.`). The rule is the shared
`ValidationConstants` rule, applied to application insert and update, claim-set insert,
update, copy and import, fragment registration (an invalid name is skipped and logged
while the other claim sets compose), and XML conversion (which fails with its existing
validation exception). A comma is rejected, never removed. The reason is that the claim
set's name is the scope in a token, and DMS separates scopes on spaces only. Existing
comma-containing names are not changed automatically; see the
[deployment preflight](../../../docs/OPERATIONS.md#deployment-preflight-for-claim-set-names-and-scope-shapes).

## Endpoint Authorization Model

Scope requirements are applied per HTTP method via extension helpers:

| Helper | Allowed scopes | Used for |
| --- | --- | --- |
| `MapSecuredGet` | `full_access` or `readonly_access` | All other GET endpoints |
| `MapSecuredPost` | `full_access` only | All POST endpoints |
| `MapSecuredPut` | `full_access` only | All PUT endpoints |
| `MapSecuredDelete` | `full_access` only | All DELETE endpoints |
| `MapLimitedAccess` | Any of the three scopes | Exactly these GET routes: `/v3/authorizationMetadata`, `/v3/claimSets/`, `/v3/apiClients/`, `/v3/apiClients/{id}`, `/v3/dataStores/`, `/v3/dataStores/{id}`, `/v3/dataStoreContexts/`, `/v3/dataStoreContexts/{id}`, `/v3/dataStoreDerivatives/`, `/v3/dataStoreDerivatives/{id}`, `/v3/applications/{id}`, `/v3/profiles/`, `/v3/profiles/{id}` |
| `MapPublic` | Anonymous | Tenancy |

## Roles

Two roles are configurable via `IdentitySettings`:

- `ConfigServiceRole` — assigned to clients that manage the CMS (admin tooling).
- `ClientRole` — assigned to DMS clients that consume the CMS (e.g., to retrieve authorization metadata).

Each role is recognized under the claim type configured for the Configuration Service
(`IdentitySettings:RoleClaimType`), independently of the scope policy. A role alone does
not open a route without an allowed scope, and a scope alone does not open a route without
its role. The DMS-side role middleware, which trusts only its own configured
`JwtAuthentication:RoleClaimType`, is described in
[Roles and Scopes](../../../docs/ROLES-SCOPES.md#role-claim-type).
