# DMS-1480 Management Endpoints Design

## Status

Approved on 2026-09-14.

## Sources

- Story: `.plans/DMS-1480.md`
- Supporting analysis: `.plans/DMS-1480-implementation-pre-spec.md`
- Supporting analysis: `.plans/DMS-1480-implementation-pre-spec.nano.md`
- Current branch evidence: `DMS-1480` at `2b97a118`
- Relevant sibling precedent: DMS-1476 / PR #1246, merged at `5d3f7216`

The story and approved design are authoritative. The pre-specs are supporting analysis only.

## Scope

DMS-1480 resolves the false-confidence `EnableManagementEndpoints` configuration by making
`AppSettings:EnableManagementEndpoints` control registration of the DMS claimset management
endpoints.

The accepted `.env` audit boundary is limited to the DMS management surface:

- `DMS_ENABLE_MANAGEMENT_ENDPOINTS`
- `DMS_ENABLE_CLAIMSET_RELOAD`
- `DMS_MANAGEMENT_REQUIRED_ROLE`

This work must verify that each of those in-scope keys has a real consuming path:

- `DMS_ENABLE_MANAGEMENT_ENDPOINTS` maps to `AppSettings:EnableManagementEndpoints` and controls
  management endpoint registration.
- `DMS_ENABLE_CLAIMSET_RELOAD` maps to `AppSettings:EnableClaimsetReload` and remains the inner
  claimset operation gate in `ApiService`.
- `DMS_MANAGEMENT_REQUIRED_ROLE` maps to `AppSettings:ManagementEndpoints:RequiredRole` and remains
  part of the existing mapping-time authorization gate.

## Exclusions

This design does not include:

- `JwtAuthentication:Authority` issuer pinning or removal.
- Appsettings-wide dead-configuration enforcement.
- Repository-wide `.env` audits.
- Generic endpoint-module discovery redesign.
- JWT/authentication redesign.
- Unrelated configuration cleanup.

If broader configuration liveness enforcement is desired, it should be handled by a separate ticket.

## Chosen Design

Enforce `EnableManagementEndpoints` in `ManagementEndpointModule`, at the existing
management-specific endpoint registration boundary.

`ManagementEndpointModule` should consume the already-bound core options type
`EdFi.DataManagementService.Core.Configuration.AppSettings`. Do not duplicate
`EnableManagementEndpoints` into the frontend `AppSettings` type. The core `AppSettings` type
already owns this flag, is bound from the `AppSettings` configuration section, and is resolved during
startup validation.

Endpoint registration follows this order:

1. If `AppSettings:EnableManagementEndpoints` is `false`, map no claimset management routes.
2. If `AppSettings:EnableManagementEndpoints` is `true`, run the existing DMS-1476 mapping-time
   safety gate for `AppSettings:ManagementEndpoints:RequiredRole` and
   `JwtAuthentication:RoleClaimType`.
3. If the role gate is usable, map the existing single-tenant or multi-tenant route forms unchanged.
4. Inside mapped handlers, preserve existing bearer/role authorization before tenant validation and
   before any claimset work.
5. Keep `AppSettings:EnableClaimsetReload` as the inner operation gate used by `ApiService`; it must
   not decide whether routes are registered.

This is the simplest responsible solution because it reuses DMS-1476's fail-closed mapping model and
keeps generic endpoint discovery unchanged.

## Rationale

The story allows either enforcement or removal of `EnableManagementEndpoints`. Enforcement is chosen
because DMS-1476 retained `DMS_ENABLE_MANAGEMENT_ENDPOINTS` in deployment surfaces while adding
`ManagementEndpoints:RequiredRole`, establishing management endpoints as an opt-in, layered surface.

Removal would be legal under the story wording but would discard a useful deployment-level kill
switch. Making generic endpoint discovery configuration-aware would add unnecessary infrastructure
for a management-specific requirement.

## Affected Contracts and Components

### Configuration

`AppSettings:EnableManagementEndpoints` becomes behaviorally effective. Deployments that set it to
`false` should not have claimset management endpoints registered.

The existing hierarchy becomes:

1. `EnableManagementEndpoints`: exposes or hides the management endpoint surface.
2. `ManagementEndpoints:RequiredRole` plus `JwtAuthentication:RoleClaimType`: determines whether the
   surface can be safely mapped and authorized.
3. `EnableClaimsetReload`: determines whether the claimset reload/view operation succeeds after a
   request reaches the handler.

### Endpoint Registration

`ManagementEndpointModule.MapEndpoints` is the implementation seam. The generic
`MapRouteEndpoints` infrastructure remains unchanged.

When management endpoints are disabled, requests fall through the existing unmatched-route behavior.
No special disabled-management response body is added.

### Authorization and Tenant Flow

When routes are mapped, preserve these existing DMS-1476 invariants:

- Missing or invalid bearer authentication returns `401`.
- Authenticated callers without the required role return `403`.
- Denied requests do not call claimset services.
- Multi-tenant handlers authorize before tenant validation so unauthorized callers cannot probe tenant
  existence.

### Documentation

Update management endpoint documentation in `docs/CONFIGURATION.md` and `docs/CACHING-STRATEGY.md`
to describe the full control hierarchy:

- `EnableManagementEndpoints=true` exposes the management route surface.
- A valid `ManagementEndpoints:RequiredRole` and `JwtAuthentication:RoleClaimType` map it safely.
- `EnableClaimsetReload=true` enables the claimset operations after authorization.

## Failure Handling and Regression Safeguards

When `EnableManagementEndpoints=false`, route registration short-circuits before role usability
validation. This avoids misleading warnings about missing role configuration for a surface the
operator explicitly disabled.

When `EnableManagementEndpoints=true`, retain the current warning behavior for unusable
`ManagementEndpoints:RequiredRole` or blank `JwtAuthentication:RoleClaimType`.

Do not introduce startup failure for disabled or unusable management endpoint configuration. DMS-1476
intentionally made unusable management role configuration fail closed by leaving endpoints unmapped.

Default compose values of `DMS_ENABLE_MANAGEMENT_ENDPOINTS=false` become effective. That is an
externally observable compatibility change, but it is the intended security correction for a
previously no-op configuration key.

## AC-to-Design-to-Verification Mapping

| Acceptance criterion | Design behavior | Verification approach |
| --- | --- | --- |
| Either the setting demonstrably controls endpoint registration, or it is removed consistently. | Enforce the existing setting at `ManagementEndpointModule.MapEndpoints`; disabled means claimset management routes are absent. | Extend `ManagementEndpointModuleTests` to inspect `EndpointDataSource` and prove no single-tenant or tenant-scoped claimset management routes are registered when `EnableManagementEndpoints=false`, even with valid role configuration. |
| Existing management behavior is preserved when the setting is enabled. | With `EnableManagementEndpoints=true`, route mapping and handlers follow existing DMS-1476 behavior. | Keep or extend existing registration tests for single-tenant and multi-tenant routes, unusable role non-registration, 401/403 behavior, no claimset work on denied calls, and authorization before tenant validation. |
| `EnableClaimsetReload` remains independent. | `EnableClaimsetReload=false` does not unmap routes when global management is enabled; it only affects `ApiService` claimset operations. | Preserve the existing test that claimset routes map even when claimset reload is disabled, making the global management flag explicitly enabled in that fixture. |
| No in-scope documented management env key remains without a consuming path. | The accepted audit boundary is the management trio only. Each key maps to compose configuration and a behavioral consumer. | Review env and compose bindings for the trio in `eng/docker-compose` and `eng/azure-vm`; verify tests cover `DMS_ENABLE_MANAGEMENT_ENDPOINTS` behavior and existing code/tests cover the other two consumers. |

## Dependencies

- Existing core `AppSettings` binding from the `AppSettings` section.
- Existing `ManagementEndpointsOptions` role validation helper.
- Existing `ManagementEndpointModuleTests` route-registration and authorization test fixture.
- Existing DMS-1476 authorization services and route behavior.

## Approved Deviations

The story comment suggested widening the dead-config concern from `.env` files to `appsettings.json`
while also saying there is no scope change. The approved DMS-1480 design does not widen scope to
`appsettings.json` or `JwtAuthentication:Authority`. That discrepancy is intentionally left for a
separate ticket.

The `.env` audit is intentionally limited to the DMS management surface. A repository-wide env audit
would include CMS, database, compose, script, infrastructure, Kafka, and Swagger variables and is not
part of this story.

## Remaining Blockers

None for implementation planning.

## Handoff to Writing Plans

`superpowers:writing-plans` should create an implementation plan that:

- Updates `ManagementEndpointModule` to consume core `AppSettings` and short-circuit route mapping
  when `EnableManagementEndpoints=false`.
- Updates `ManagementEndpointModuleTests.CreateFactory` so tests can set
  `AppSettings:EnableManagementEndpoints`, with existing positive route-mapping tests explicitly
  enabling it.
- Adds route absence tests for disabled management endpoints in single-tenant and multi-tenant modes.
- Preserves the DMS-1476 role gate, authorization behavior, tenant validation ordering, and
  `EnableClaimsetReload` layering.
- Updates docs for the management endpoint control hierarchy.
- Performs the bounded management env trio audit without expanding into unrelated configuration
  cleanup.
