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
| `POST /connect/revoke` | Revoke a token (RFC 7009) | Required — `Authorization: Bearer <token>` |

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
`token` form field is first verified for signature, issuer and audience, and its
`client_id` claim is then compared to the `client_id` claim of the caller's own
bearer token. Verification comes first on purpose: trusting an unverified
`client_id` would let a caller forge a token naming itself while embedding
another client's `jti`.

When the token cannot be verified, carries no `client_id`, or belongs to a
different client, nothing is revoked and the response is still `200 OK`. Per
RFC 7009 this is indistinguishable from revoking an unknown token, so the
response reveals nothing about whether the token exists or who owns it. Only a
missing `token` form field returns `400 Bad Request`.

The `client_id` claim name is the same for self-contained and Keycloak-issued
tokens, so this check needs no per-provider branching.

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
