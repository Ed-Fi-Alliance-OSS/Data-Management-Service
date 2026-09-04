---
jira: TBD
source_spike: DMS-1413
depends_on: 02
---

# Story: Add Identity Endpoints and Feature Toggle

## Description

Add the frontend endpoint module for `/identity/v2` and the `AppSettings:EnableIdentityManagement` toggle.
The endpoints call the Core facade and pass `HttpContext.RequestAborted`.

## Acceptance Criteria

- `AppSettings:EnableIdentityManagement` exists and defaults to `false`.
- The five identity routes are mapped only when the feature is enabled.
- When disabled, all five routes answer routing `404`.
- The route prefix supports the same tenant and route-qualifier prefixing used by other fixed-service endpoints.
- `GET /identity/v2/identities/results` routes to get-by-id with id `results`.
- `GET /identity/v2/identities/results/{token}` routes to results polling.
- POST handlers request body parsing through the existing frontend body extraction path.
- GET handlers do not parse a body and ignore `Content-Type`.
- Compose and environment files carry `AppSettings__EnableIdentityManagement`, following the existing `AppSettings__EnableManagementEndpoints` entries in `eng/docker-compose/local-dms.yml`, `eng/docker-compose/published-dms.yml`, and `eng/azure-vm/compose/docker-compose.yml`, so a Docker stack can enable the feature.
- No plugin maps identity HTTP routes.

## Tasks

1. Add frontend configuration binding for the feature toggle.
2. Add the endpoint module and route mappings.
3. Add disabled-route absence tests.
4. Add route-collision tests for `results` and `results/{token}`.
5. Add cancellation propagation tests from `HttpContext.RequestAborted`.
6. Add compose and environment entries for the feature toggle.
