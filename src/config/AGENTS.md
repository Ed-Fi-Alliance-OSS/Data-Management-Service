## Authorization Model

Use the `MapSecured*` helpers in
`Infrastructure/Authorization/EndpointBuilderExtensions.cs` for endpoints. They
compose the service-role and scope policies; do not map protected endpoints directly
with `MapGet`/`MapPost` and authorization calls.

`MapPublic` is anonymous and should be limited to public documents. The OAuth `/connect`
endpoints are intentionally anonymous so clients can obtain credentials.

## Tenancy Is Not Authorization

Tenant selection is routing and data partitioning, not a security boundary. Do not treat
the `Tenant` header as restricting a caller to a tenant, or document tenant isolation as
a security property.

When `AppSettings:MultiTenancy` is enabled, `TenantResolutionMiddleware` resolves the
header before authentication; CMS tokens carry no tenant claim. Any appropriately
scoped CMS caller can therefore select any valid tenant. Keep repository queries
tenant-scoped with `TenantContext.TenantWhereClause()` for partition correctness, not
access control. Tenant-level security requires separate CMS deployments, databases,
and credentials.

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

- `IdentitySettings:AllowRegistration` controls `/connect/register`.
- `AppSettings:EnableApplicationResetEndpoint` controls mapping of the application
  credential-reset endpoint.
- `ClaimsOptions:DangerouslyEnableUnrestrictedClaimsLoading` controls the
  `/management/*` claims endpoints.
