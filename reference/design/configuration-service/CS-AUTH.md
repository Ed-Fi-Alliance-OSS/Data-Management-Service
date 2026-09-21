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
| `POST /connect/revoke` | Revoke a token (RFC 7009) | Required — `Authorization: Bearer <token>` [^revoke] |

[^revoke]: Authentication is required in **both** provider modes, but revocation itself
only happens in `self-contained` mode. In `keycloak` mode `/connect/revoke` is a no-op
that returns `200 OK` and revokes nothing, however the caller authenticates. See
[Which provider modes actually perform the check](#which-provider-modes-actually-perform-the-check).

In `self-contained` mode, `/connect/token` also accepts credentials via HTTP
Basic authentication in addition to the form body.

### Revocation Is Authenticated and Ownership-Checked

`POST /connect/revoke` requires an authenticated caller. The requirement is a
bare "must be authenticated" check, not one of the scope or role policies below:
an ordinary client-credentials token issued by `/connect/token` does not carry
the `IdentitySettings:ConfigServiceRole` claim that `SecurityConstants.ServicePolicy`
demands, so requiring that policy would stop clients from revoking their own
tokens. A request with no `Authorization` header, or with an invalid or expired
bearer token, returns `401 Unauthorized`.

A caller may only revoke a token that belongs to it. The token named in the
`token` form field is first verified for signature, issuer, audience and lifetime,
and its `client_id` claim is then compared to the `client_id` claim of the caller's
own bearer token. Verification comes first on purpose: trusting an unverified
`client_id` would let a caller forge a token naming itself while embedding
another client's `jti`.

Two distinct tokens are in play on every revocation request, which is easy to
conflate. The **caller's** token is the one in the `Authorization` header and
establishes *who is asking*; the **target** token is the one in the `token` form
field and is *what gets revoked*. They are frequently the same token — a client
logging itself out — but need not be:

```http
POST /connect/revoke HTTP/1.1
# Authorization identifies the caller; its client_id must match the target's.
Authorization: Bearer eyJ...CALLER
Content-Type: application/x-www-form-urlencoded

# The token form field is what gets revoked; its client_id is the one compared.
token=eyJ...TARGET
```

A client revoking one of its *other* outstanding tokens (say, rotating a leaked
credential while keeping its current session alive) supplies two different tokens
that both carry the same `client_id`, and the revocation succeeds. A client
supplying a target token minted for a different `client_id` gets `200 OK` and no
revocation.

When the token cannot be verified, carries no `client_id`, or belongs to a
different client, nothing is revoked and the response is still `200 OK`. Per
RFC 7009 this is indistinguishable from revoking an unknown token, so the
response reveals nothing about whether the token exists or who owns it. Only a
missing `token` form field returns `400 Bad Request`.

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

This matters because silent no-ops are reachable in practice, not just in theory:
a target token whose `client_id` differs from the caller's only by letter case is
rejected by the case-sensitive ownership comparison even though both tokens belong
to the same registered client. Such a pair is possible because a token is minted
with the `client_id` casing the client supplied at `/connect/token` rather than the
stored canonical casing, while client lookup is case-insensitive under SQL Server's
default collation. An operator who assumed `200 OK` meant success would believe a
leaked credential was contained when it was not.

### Which provider modes actually perform the check

The `client_id` claim name is identical for self-contained and Keycloak-issued
tokens, so the comparison itself needs no per-provider branching. It does not
follow that both modes perform ownership-checked revocation:

- **`self-contained`** — the OpenIddict store registers an `ITokenRevocationManager`,
  so the endpoint authenticates the caller, verifies the target token, compares
  `client_id`, and revokes by `jti` on a match.
- **`keycloak`** — no `ITokenRevocationManager` is registered (`KeycloakTokenManager`
  implements `ITokenManager` only), so the handler's revocation branch is never
  entered. The request returns `200 OK` and **nothing is revoked**, ownership
  comparison included. Token revocation remains the IdP's responsibility in this
  mode. The one behavior that did change here is authentication: an unauthenticated
  caller now gets `401` instead of a `200 OK` no-op.

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
