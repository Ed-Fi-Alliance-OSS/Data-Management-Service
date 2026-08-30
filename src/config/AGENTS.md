This directory contains the **Ed-Fi DMS Configuration Service (CMS)**, a functional
implementation of the Ed-Fi Management API specification. It is the administrative
control plane for the platform: vendors, applications, API clients, claim sets,
profiles, ownership tokens, tenants, and data store connection strings all live here.

See the repository-root `AGENTS.md` for code style, formatting, and test conventions
that apply to the whole repository. This file covers what is specific to CMS.

## Project Layout

| Path | Contents |
|---|---|
| `frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore` | Minimal API host: endpoint modules, middleware, authorization policies |
| `backend/EdFi.DmsConfigurationService.Backend` | Datastore-agnostic services and repository interfaces |
| `backend/EdFi.DmsConfigurationService.Backend.Postgresql` | PostgreSQL repositories (the default datastore) |
| `backend/EdFi.DmsConfigurationService.Backend.Mssql` | SQL Server repositories |
| `backend/EdFi.DmsConfigurationService.Backend.OpenIddict` | Self-contained identity provider (token issuance, signing keys, client secret hashing) |
| `backend/EdFi.DmsConfigurationService.Backend.Keycloak` | Keycloak identity provider integration |
| `datamodel/EdFi.DmsConfigurationService.DataModel` | Request/response models and FluentValidation validators |

## Authorization Model

Endpoints are secured by composing two policies, applied together by the
`MapSecured*` helpers in `Infrastructure/Authorization/EndpointBuilderExtensions.cs`.
Prefer those helpers over calling `MapGet`/`MapPost`/`RequireAuthorization` directly,
so that a new endpoint cannot silently ship without authorization:

| Helper | Policies applied |
|---|---|
| `MapSecuredGet` | `ServicePolicy` + read-only **or** admin scope |
| `MapSecuredPost` / `MapSecuredPut` / `MapSecuredDelete` | `ServicePolicy` + admin scope |
| `MapLimitedAccess` | `ServicePolicy` + admin, read-only, **or** auth-metadata read-only scope |
| `MapPublic` | Anonymous — use only for genuinely public documents |

`ServicePolicy` requires the configured `ConfigServiceRole` claim. Scope requirements
are evaluated by `ScopePolicyHandler`. The scopes themselves are documented in
`docs/ROLES-SCOPES.md`.

The OAuth endpoints under `/connect` (`Modules/IdentityModule.cs`) are intentionally
anonymous, because they are the endpoints a client uses to *obtain* credentials.

## Tenancy Is a Routing Concept, Not a Security Boundary

**This is a deliberate design decision. Do not "fix" it as though it were a defect.**

When `AppSettings:MultiTenancy` is enabled, `Middleware/TenantResolutionMiddleware.cs`
reads the `Tenant` request header, resolves it to a tenant row, and stores it in the
scoped `ITenantContextProvider`. Repositories then scope their SQL to that tenant via
`TenantContext.TenantWhereClause()`.

The header selects **which tenant's data the request operates on**. It does not, and
is not intended to, constrain *which* tenants a caller may reach:

- CMS access tokens carry **no tenant claim** (`JwtTokenGenerator.cs`), so there is
  nothing to bind the header to.
- `TenantResolutionMiddleware` runs **before** `UseAuthentication()`/`UseAuthorization()`
  in `Program.cs`, so no authenticated principal exists at the point tenancy is resolved.
- Any CMS client holding the appropriate scope may set any valid `Tenant` value and
  administer that tenant.

**Every CMS credential is effectively a platform-wide administrative credential.**
The tenant partition exists so that configuration data for different tenants can share
one CMS deployment and one database while routing to separate DMS data stores. It is a
data-partitioning and routing mechanism only.

Consequences to keep in mind when working in this codebase:

- CMS credentials must be issued and protected as platform-wide administrative
  credentials. Do not hand a CMS client key/secret to a party who should only see one
  tenant. There is no per-tenant CMS credential.
- Adding a tenant claim, or validating the header against the caller's identity, is a
  **product decision**, not a bug fix. If that changes, it must change deliberately —
  token issuance, middleware ordering, and this document all move together.
- When writing or reviewing a repository method, still scope queries with
  `TenantContext.TenantWhereClause()`. Tenant scoping is what keeps a request from
  *accidentally* reading across the partition and returning incoherent data. It is
  correctness, not access control.
- Do not describe the `Tenant` header as providing "isolation", "separation", or any
  other security property in code comments, API documentation, or user-facing docs.
  Say "routing" or "partitioning".

Tenant isolation as a *security* boundary, where it is required, is achieved by
running separate CMS deployments with separate databases and separate credentials.

## Datastore Selection

`AppSettings:Datastore` (`postgresql` or `mssql`) selects the repository
implementations, the deployment/migration path, and the data store connection string
validator. `Infrastructure/WebApplicationBuilderExtensions.ConfigureDatastore` is the
single place that switch is made; keep new repository registrations there so both
engines stay in step.

## Identity Provider Selection

`AppSettings:IdentityProvider` selects between `self-contained` (OpenIddict, the
default) and `keycloak`. Both paths configure JWT bearer authentication and register
the same authorization policies; only token issuance and revocation differ. See
`docs/OWASP-AUTH-COVERAGE.md` for the per-path replay and revocation posture.

## Configuration Values That Gate Endpoints

Several endpoints are registered or short-circuited based on configuration. When
adding a flag of this kind, make it observable in behavior — a flag that no code reads
is worse than no flag, because operators will believe it is protecting them.

| Setting | Effect |
|---|---|
| `IdentitySettings:AllowRegistration` | `/connect/register` returns an authorization failure when `false` (the default) |
| `AppSettings:EnableApplicationResetEndpoint` | `/v3/applications/{id}/reset-credential` is not mapped at all when `false` |
| `ClaimsOptions:DangerouslyEnableUnrestrictedClaimsLoading` | The `/management/*` claims endpoints return 404 when `false` |
