# Roles and Scopes for DMS and Configuration Service Applications

This document outlines the roles and scopes used in the **DMS** and **the Configuration Service**
applications, focusing on client registration, token generation, and access
control mechanisms.

## Configuration Service Application

### Client Registration

When a new client is registered using the `/connect/register` endpoint in the Configuration Service
application, the following defaults are automatically assigned:

**Default Role:** cms-client

**Default Scope:** edfi_admin_api/full_access

### Supported Scopes

| Scope Name                                      | Description                                                                 |
|------------------------------------------------|-----------------------------------------------------------------------------|
| `edfi_admin_api/full_access`                   | Provides **full access** to all API endpoints (read and write operations). |
| `edfi_admin_api/readonly_access`               | Grants **read-only** access to all `GET` endpoints.                        |
| `edfi_admin_api/authMetadata_readonly_access`  | Allows read-only access to `GET /v3/authorizationMetadata`, `/v3/claimSets`, `/v3/apiClients`, `/v3/apiClients/{id}`, `/v3/dataStores`, `/v3/dataStores/{id}`, `/v3/dataStoreContexts`, `/v3/dataStoreContexts/{id}`, `/v3/dataStoreDerivatives`, `/v3/dataStoreDerivatives/{id}`, `/v3/applications/{id}`, `/v3/profiles`, and `/v3/profiles/{id}`. |

> [!NOTE]
> Scopes define the level of access granted to a client. Assign
> appropriate scopes based on the client’s needs.

> [!IMPORTANT]
> `edfi_admin_api/authMetadata_readonly_access` covers every Configuration
> Service read a single-tenant DMS makes. With multi-tenancy enabled, DMS also
> reads `GET /v3/tenants`, which this scope does not grant, so a multi-tenant
> DMS needs `edfi_admin_api/readonly_access`.

### How the Configuration Service Evaluates Token Scopes

A token's `scope` may be one space-delimited string or an array. An array reaches the
Configuration Service as one `scope` claim per element. The Configuration Service
considers every claim whose type is exactly `scope`, splits each value on ASCII
space, and drops empty entries. It does not split on commas or tabs, so the order of
scopes in the token never changes the decision.

An endpoint's scope policy succeeds when **any** scope the token holds equals **any**
scope that endpoint allows. The comparison is exact and case-sensitive. A missing,
empty, or nonmatching `scope` grants nothing. The scope policy is independent of the
service-role policy: a matching scope alone does not open a route that also requires
the configured role, and the role alone does not open a route without an allowed
scope.

### Self-Contained Token Issuance

In `self-contained` mode, a new access token carries its granted scopes separated by
single spaces. This applies to both issuance paths: the requested scopes the client
holds, and the client's registered scopes when no scope is requested. A request that
names none of the client's scopes is rejected with `invalid_scope` and issues no token.
The token response `scope` and the signed JWT `scope` describe the same granted values.

A token *request* may list the requested scopes separated by commas or spaces.
Issued tokens retain their scope values and delimiters until they expire;
requesting a new token is necessary to obtain the current issuance format.

### Claim-Set Names Must Not Contain Commas

A claim-set name must not contain white space or a comma. One shared rule applies
wherever a name is introduced or replaced: application insert and update, claim-set
insert, update, copy, and import, fragment registration, and the XML conversion tool.
The validation message is `Claim set name must not contain white spaces or commas.`
The API, import, and application paths reject an invalid name; fragment registration
skips registering it and continues with the other claim sets; the XML conversion tool
throws its established `InvalidOperationException`. A comma is never removed from a
name. Existing names that already contain a comma are not
changed automatically; see
[Deployment preflight](#deployment-preflight-for-claim-set-names-and-scope-shapes).

### Algorithm Policy of the Configuration Service Token Helper

The Configuration Service helper that verifies self-contained tokens
(`JwtTokenValidator`) accepts RS256 only, matching the algorithm the service signs
with. A token that is correctly signed with another algorithm is untrusted, even when
the trusted RSA key could verify it and its claims are valid. This is a restriction on
that helper and the paths that call it (self-contained Bearer validation,
introspection, and revocation). It is not a platform-wide algorithm policy for
Keycloak or for DMS.

### Configuration Service Role Usage

**cms-client:** This role identifies a standard Configuration Service client. Currently, it serves
as an identity tag and may be expanded in the future to support additional
authorization logic.

### Configuration Service Scopes Are Not Tenant-Scoped

Configuration Service scopes control **what operations** a client may perform. They do not control
**which tenant** those operations apply to.

In a multi-tenant deployment, the tenant a request operates on is selected by the
`Tenant` request header. Configuration Service access tokens carry no tenant claim, and the header is
not validated against the caller's identity. A client holding
`edfi_admin_api/full_access` therefore has full administrative access to **every**
tenant in the deployment, and a client holding `edfi_admin_api/readonly_access` or
`edfi_admin_api/authMetadata_readonly_access` can read every tenant's configuration
within that scope's endpoints.

This is deliberate: the tenant partition is a data routing mechanism, not a security
boundary. Issue Configuration Service credentials accordingly — treat every one as platform-wide.
Where a hard boundary between tenants is required, deploy a separate CMS instance per
boundary. See
[Getting Started with Multi-Tenancy](./MULTI-TENANCY-GETTING-STARTED.md#tenant-header)
for details.

## DMS Application

### Token Generation

Clients can obtain an access token via the `/oauth/token` endpoint.

The `client key and secret` used for token generation are generated by the Configuration Service
application when a vendor application is created.

Stored in Keycloak as a client with the default role: `dms-client`.

### Claim-Set Selection from Token Scopes

DMS selects the claim set that governs a request from the token's `scope` values. A
token may carry several scopes, either as one space-separated string or as an array,
and DMS supports both shapes. DMS considers every claim whose type is exactly
`scope`, splits each value on ASCII space, and removes empty entries. It does not split
on commas, lowercase the values, or treat tabs as separators. A value such as
`Alpha,Beta` is one literal scope, not two.

DMS then matches those values against the claim-set names in the requesting tenant's
claim-set metadata, using the existing case-insensitive comparison
(`InvariantCultureIgnoreCase`). Exactly **one recognized claim set** applies:

- Matching counts claim-set records, not scope occurrences. A repeated scope, or case
  variants of one name (`Alpha alpha`), match one record and select it.
- Scopes that match no claim set grant nothing. Other scopes in the token, such as
  `profile`, are ignored and never appear in the selected name.
- If the scopes recognize two or more different claim sets, DMS authorizes with none
  of them and never combines their permissions. The order of scopes does not matter.
- When one record matches and a single spelling occurs in the token, that spelling is
  kept. If several case variants of the same record occur, DMS uses the name as stored
  in the metadata, so the result does not depend on scope order.

| Token scope values | Tenant metadata contains | Result |
| --- | --- | --- |
| `Alpha profile`, or `["profile", "Alpha"]` | `Alpha` only | `Alpha` is selected |
| `["profile Alpha", "Alpha"]` | `Alpha` only | `Alpha` is selected |
| `Alpha alpha` | One `Alpha` record | That one record is selected |
| `Alpha Beta`, in either shape or order | `Alpha` and `Beta` | Multiple claim sets: rejected |
| Missing, empty, or only unknown values | No matching record | No claim set |
| `Alpha,Beta` | `Alpha` and `Beta` | One literal scope that matches nothing |
| `Alpha` | Two records whose names match `Alpha` | Security configuration error |

The outcomes map to responses as follows. A failure that occurs earlier in a
pipeline, such as authentication or tenant binding, keeps its precedence.

| Outcome | Resource requests | Identity-service requests | `/oauth/token_info` |
| --- | --- | --- | --- |
| One claim set recognized | Authorized with that claim set | Authorized with that claim set | Reports that claim set |
| No claim set recognized | `500` security configuration (unchanged) | `403` (unchanged) | `401` (unchanged) |
| Multiple claim sets recognized | `403`, `The token identifies multiple claim sets.` | `403`, same message | `403`, same message |
| Duplicate matching names in metadata | `500` security configuration | `500` security configuration | `500` security configuration |

The `403` and `500` responses do not reflect scope values, claim-set names, or tokens.
Failing to retrieve metadata is never treated as "no match".

#### What is cached

DMS caches the validated principal and the unresolved scope values from the token. It
does not cache the selected claim-set name, because the answer depends on the
requesting tenant's metadata. The claim set is resolved on every request against the
claim-set metadata that request sees, outside the validated-token cache. The same
signed token can therefore resolve differently after a claim-set change or in another
tenant, without the token being validated again.

That metadata is subject to the existing claim-set cache lifetime and reload behavior,
so a change is eventually consistent. It is not an immediate revocation. State is
local to each replica. See
[How long a change takes to apply](./IDENTITY-MANAGEMENT.md#how-long-a-change-takes-to-apply)
and [Reloading the claim-set cache](./IDENTITY-MANAGEMENT.md#reloading-the-claim-set-cache).

#### Token info

`/oauth/token_info` reports the selected claim-set name as `claim_set.name`. It is not
a direct copy of the token's `scope`. See
[Token Info Endpoint](../reference/design/token_info-endpoint.md).

### DMS Role Usage

**dms-client:** This role is assigned to all vendor applications by default.
Currently, the role is used only as an identity tag and does not enforce any
specific access control logic.

## Deployment Preflight for Claim-Set Names and Scope Shapes

Before rolling out the scope and claim-set-name behavior above, operators must inventory
and, where needed, correct existing data. This is a **deployment prerequisite**, not a
code migration: the product does not rename, rewrite, or repair anything automatically.
The procedure is in
[Operations](./OPERATIONS.md#deployment-preflight-for-claim-set-names-and-scope-shapes).
