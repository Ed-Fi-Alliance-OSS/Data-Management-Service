## Security Metadata Tools Versioning

`EdFi.Api.SecurityMetadataTools` takes major/minor from the existing
`Get-VersionNumber` / MinVer app release source and patch from the tool project's
committed `SecurityMetadataToolsPatchVersion`, through
`Get-SecurityMetadataToolsVersion` in the repository-root `package-helpers.psm1`.
Start a new major/minor line at patch `0`; commit an increment for each subsequent
stable tool publication, including normal app releases after independent tool
patches. Prerelease calculations do not consume a patch. Follow the single
[versioning policy](clis/EdFi.DmsConfigurationService.SecurityMetadataTools/VERSIONING.md)
for selected refs, dry runs, duplicate-version refusal, exact-version promotion,
and promotion recovery. Maintain the implemented build and release integration
against that policy and keep its entry-point descriptions current.

## Authorization Model

Configuration Service has no fallback authorization policy: any plain `MapGet`/`MapPost` endpoint is
anonymous. Map protected endpoints with the `MapSecured*` or `MapLimitedAccess` helpers
in `Infrastructure/Authorization/EndpointBuilderExtensions.cs`; they compose the
service-role and scope policies. Use `MapLimitedAccess` only for reads DMS performs.

Anonymous endpoints (`/health`, `/`, `/tenancy`, metadata, `/.well-known/*`, and the
OAuth `/connect/*` endpoints other than introspect and revoke) are intentional. Prefer `MapPublic` for new ones.

`/connect/introspect` and `/connect/revoke` authenticate the caller with client credentials inside the handler.
In `keycloak` mode both delegate that check to Keycloak. Neither is mapped with `MapSecured*`.

## Tenancy Is Not Authorization

Tenant selection is routing and data partitioning, not a security boundary. Do not treat
the `Tenant` header as restricting a caller to a tenant, or document tenant isolation as
a security property.

When `AppSettings:MultiTenancy` is enabled, `TenantResolutionMiddleware` resolves the
header before authentication; Configuration Service tokens carry no tenant claim. Any
appropriately scoped caller can therefore select any valid tenant. Keep repository queries
tenant-scoped with `TenantContext.TenantWhereClause()` for partition correctness, not
access control. Tenant-level security requires separate Configuration Service
deployments, databases, and credentials.

## Datastore Selection

`AppSettings:Datastore` selects PostgreSQL or SQL Server. Keep datastore-specific
registrations in `Infrastructure/WebApplicationBuilderExtensions.ConfigureDatastore`
so both implementations remain aligned.

## Identity Provider Selection

`AppSettings:IdentityProvider` selects `self-contained` (OpenIddict) or `keycloak`.
Both configure the same JWT authorization policies; token issuance and revocation
differ. Preserve behavior across both paths when changing authentication.

## Endpoint Feature Gates

These settings affect whether endpoints are available. Verify the behavior is enforced
when changing them; do not add configuration flags that are not read by runtime code.

- `AppSettings:IdentityProvider`: JWKS and `openid-configuration` are mapped only in
  `self-contained` mode.
- `AppSettings:MultiTenancy`: `TenantModule` (`/v3/tenants`) is mapped only when enabled.
- `IdentitySettings:AllowRegistration`: `/connect/register` is always mapped and
  returns 403 when disabled.
- `ClaimsOptions:DangerouslyEnableUnrestrictedClaimsLoading`: the `/management/*`
  claims endpoints are always mapped and return 404 when disabled.
