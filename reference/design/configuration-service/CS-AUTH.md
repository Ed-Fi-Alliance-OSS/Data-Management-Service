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
| `POST /connect/revoke` | Revoke a token (RFC 7009) | Client credentials (HTTP Basic or form body), same as `/connect/token` [^revoke] |

[^revoke]: RFC 7009 §2.1 requires the revocation caller to authenticate the same
way it authenticates to the token endpoint (RFC 6749 §2.3) — client credentials,
not a bearer access token. This is checked, and enforced, only in `self-contained`
mode; revocation itself only happens there too. In `keycloak` mode `/connect/revoke`
is an unauthenticated no-op that returns `200 OK` and revokes nothing. See
[Which provider modes actually perform the check](#which-provider-modes-actually-perform-the-check).

In `self-contained` mode, `/connect/token` also accepts credentials via HTTP
Basic authentication in addition to the form body.

### Revocation Is Authenticated and Ownership-Checked

`POST /connect/revoke` requires the caller to authenticate with client
credentials — `client_id`/`client_secret`, via HTTP Basic auth or form fields,
exactly as `/connect/token` accepts them. This is not the ownership check below;
it just establishes who is asking. It is deliberately **not** a bearer access
token: the token named in the `token` field is the one being revoked, so it
cannot double as proof of the caller's identity without letting a client revoke
itself using the very credential that gets invalidated by the call, and RFC 7009
does not permit it regardless. `ValidateClientCredentialsAsync` on
`ITokenRevocationManager` authenticates the pair by reusing the same
application lookup and secret-hash comparison `/connect/token` uses, so the two
call sites cannot drift on what counts as a valid secret. Missing or invalid
client credentials return `401 Unauthorized` with `invalid_client` — this is
reported, not masked as `200 OK`, because RFC 7009's "always 200" guarantee
covers whether a *token* is valid or owned, not whether the *caller*
authenticated.

Once authenticated, a caller may only revoke a token that belongs to it. The
token named in the `token` form field is first verified for signature, issuer,
audience and lifetime, and its `client_id` claim is then compared to the
authenticated caller's own `client_id`. Verification comes first on purpose:
trusting an unverified `client_id` would let a caller forge a token naming
itself while embedding another client's `jti`.

Two distinct credentials are in play on every revocation request, which is easy
to conflate. The **caller's** `client_id`/`client_secret` (Basic auth or form
fields) establishes *who is asking*; the **target** token is the one in the
`token` form field and is *what gets revoked*. A client typically revokes its
own currently-held access token, but the target can be any token that client
was issued:

```http
POST /connect/revoke HTTP/1.1
# Authorization identifies the caller by client credentials, not the token being revoked.
Authorization: Basic base64(client_id:client_secret)
Content-Type: application/x-www-form-urlencoded

# The token form field is what gets revoked; its client_id is the one compared.
token=eyJ...TARGET
```

A client revoking one of its *other* outstanding tokens (say, rotating a leaked
credential while keeping its current session alive) authenticates with its
client credentials and names a different target token minted for the same
`client_id`, and the revocation succeeds. A client supplying a target token
minted for a different `client_id` gets `200 OK` and no revocation.

When the caller has authenticated but the target token cannot be verified,
carries no `client_id`, or belongs to a different client, nothing is revoked and
the response is still `200 OK`. Per RFC 7009 this is indistinguishable from
revoking an unknown token, so the response reveals nothing about whether the
token exists or who owns it. Only a missing `token` form field returns
`400 Bad Request`. This request-shape check runs before client authentication
and before the provider-mode branch, so it applies even to an unauthenticated
caller or in `keycloak` mode: a caller with no credentials and no `token` field
gets `400`, not `401`.

Because verification includes the lifetime check, a target token already past its
`exp` (plus the validator's clock-skew allowance) is also a no-op. This is a
deliberate narrowing: revocation previously parsed the target token without
verifying it and would revoke an expired token by `jti`. An expired token is
already rejected everywhere else, so there is nothing left to revoke. A practical
consequence for anyone reading the `dmscs.OpenIddictToken` table or an admin status
view directly: an expired token keeps its stored status (typically `valid`) rather
than being flipped to `revoked` by a revocation attempt, so "not `revoked`" in the
table does not imply "still usable".

### Confirming that a revocation actually took effect

Every outcome of `POST /connect/revoke` is an identical bodyless `200 OK` — success,
wrong owner, unverifiable token, expired token, and (in `keycloak` mode) not
attempted at all. That is required by RFC 7009 and is deliberate, but it means the
`200` alone is **not** evidence that anything was revoked. Anyone who must be certain
— containing a leaked credential, for example — has to confirm out of band:

```http
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJ...TARGET
```

A revoked token reports `{"active": false}`. If it still reports `{"active": true}`,
the revocation did not take effect and the credential is still live. Treat
containment as incomplete until introspection confirms it.

### Client id casing

Tokens are minted with the **stored canonical** `client_id`, not the casing the caller
supplied at `/connect/token`. Every token issued to one registered client therefore
carries one identity, whatever casing that client used on a given call, so the
case-sensitive ownership comparison above always matches a client's own tokens.

This was previously a live defect: a token minted under one casing could not be
revoked by a caller authenticated under another, and the mismatch surfaced as the
silent `200 OK` no-op described above. Canonical minting closes it on **both**
database engines. See
[ADR: Canonical `client_id` casing in minted tokens](../../adr-client-id-casing.md)
for the decision record, including why the ownership comparison itself deliberately
remains case-sensitive.

Client **lookup** is a separate matter and is unchanged: it is case-sensitive on
PostgreSQL, which matches RFC 6749's rule that protocol parameter values are case
sensitive. SQL Server's default collation makes its lookup case-insensitive, so the
two engines currently differ and SQL Server departs from the RFC; that is a known
issue tracked separately and out of scope here. It is a conformance and consistency
concern only — because minting is canonical, a client that authenticates on SQL
Server with non-canonical casing still receives a canonical token and can revoke
normally.

One residue remains: tokens minted **before** this change still carry the requested
casing, so such a token may resist revocation by a canonically-cased caller until it
expires. Token lifetimes are short and the population drains on its own.

### Which provider modes actually perform the check

The `client_id` claim name is identical for self-contained and Keycloak-issued
tokens, so the comparison itself needs no per-provider branching. It does not
follow that both modes perform ownership-checked revocation:

- **`self-contained`** — the OpenIddict store registers an `ITokenRevocationManager`,
  so the endpoint authenticates the caller's client credentials, verifies the
  target token, compares `client_id`, and revokes by `jti` on a match.
- **`keycloak`** — no `ITokenRevocationManager` is registered (`KeycloakTokenManager`
  implements `ITokenManager` only), so there is no local way to authenticate the
  caller's client credentials, and the handler's revocation branch is never
  entered. The request returns `200 OK` and **nothing is revoked or checked** —
  not client authentication, not ownership comparison. Token revocation remains
  the IdP's responsibility in this mode. This is safe precisely because the mode
  is a no-op end to end: there is no data to protect behind the missing
  authentication check, since nothing is read, mutated, or revealed either way.

## Scopes

Authorization is scope-based. Three scopes are defined:

| Scope | Description |
|---|---|
| `edfi_admin_api/full_access` | Full access to all CMS API endpoints |
| `edfi_admin_api/readonly_access` | Read-only access to all CMS API endpoints |
| `edfi_admin_api/authMetadata_readonly_access` | Access to `/v3/authorizationMetadata` only |

## Endpoint Authorization Model

Scope requirements are applied per HTTP method via extension helpers:

| Helper | Allowed scopes | Used for |
|---|---|---|
| `MapSecuredGet` | `full_access` or `readonly_access` | All GET endpoints |
| `MapSecuredPost` | `full_access` only | All POST endpoints |
| `MapSecuredPut` | `full_access` only | All PUT endpoints |
| `MapSecuredDelete` | `full_access` only | All DELETE endpoints |
| `MapLimitedAccess` | Any of the three scopes | `/v3/authorizationMetadata` |
| `MapPublic` | Anonymous | Health, JWKS, discovery endpoints |

## Roles

Two roles are configurable via `IdentitySettings`:

- `ConfigServiceRole` — assigned to clients that manage the CMS (admin tooling).
- `ClientRole` — assigned to DMS clients that consume the CMS (e.g., to retrieve authorization metadata).
