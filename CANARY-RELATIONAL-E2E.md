# Relational Canary E2E Plan

## Goal

Run exactly one existing DMS E2E scenario from `src/dms/tests/EdFi.DataManagementService.Tests.E2E` against the new relational backend without disturbing the current legacy-backed lane.

Phase 1 is intentionally narrow:

- Move one tagged scenario only.
- Include the auth mismatch fix in the E2E harness.
- Include both local execution and GitHub Actions execution for the canary lane.
- Reset only `RELATIONAL_E2E_DATABASE_NAME` between relational runs.
- Accept the temporary workaround that DMS still starts with one legacy startup instance present.

The first canary scenario remains:

- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Resources/UpdateResourcesValidation.feature`
- Scenario `01 Put an existing document (Resource)`

## Why This Scenario

- It stays on the relational surface that already exists: non-descriptor PUT and GET-by-id.
- It avoids query, delete, profile, and extension-heavy paths.
- Its final assertion already uses the strongest current E2E readback check: `And the record can be retrieved with a GET request`, which performs semantic JSON comparison instead of a status-only check.

## Phase 1 Decisions

- Add a separate relational lane instead of repurposing the existing E2E environment.
- Do not change `POSTGRES_DB_NAME`, `DATABASE_CONNECTION_STRING`, `DATABASE_CONNECTION_STRING_ADMIN`, or `DMS_CONFIG_DATABASE_CONNECTION_STRING` for the relational lane.
- Add a dedicated relational database name, `RELATIONAL_E2E_DATABASE_NAME`, and provision it separately.
- Drive relational mode with `AppSettings__UseRelationalBackend=true` from a new `eng/docker-compose/.env.e2e.relational`.
- Keep `NEED_DATABASE_SETUP=true` and `DMS_DEPLOY_DATABASE_ON_STARTUP=false` in the relational lane.
- Provision the relational database from the host repo with `SchemaTools ddl provision --create-database`, using the same `SCHEMA_PACKAGES` resolution path that the stack already uses for schema download.
- Leave the default startup instance pointed at the existing legacy bootstrap database in phase 1 so DMS stays up.
- Reset the relational lane by dropping and recreating only `RELATIONAL_E2E_DATABASE_NAME`; do not use the current legacy-table cleanup path for relational runs.
- Include the GitHub Actions changes needed so every current DMS workflow that invokes `E2ETest` handles the relational canary correctly.

## Current Constraints

### Harness and environment

- `eng/docker-compose/local-dms.yml` does not currently pass `AppSettings__UseRelationalBackend`.
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1` is hard-wired to `./.env.e2e`.
- The current wrapper creates a default startup DMS instance through `eng/docker-compose/start-local-dms.ps1`, and that behavior is useful for phase 1 because DMS currently fails when CMS has zero startup instances.

### Cleanup

- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Management/ContainerSetupBase.cs` deletes legacy tables such as `dms.Reference`, `dms.Alias`, and `dms.Document`.
- That cleanup path should be treated as incompatible with the relational canary lane.

### Provisioning

- `src/dms/run.sh` still runs the legacy installer when `NEED_DATABASE_SETUP=true`.
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs` and `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs` still wire `DeployDatabaseOnStartup` through the legacy deploy path even when relational mode is enabled.
- The relational database therefore needs its own host-side provision step and should not reuse legacy deployment hooks.

### Routing and startup behavior

- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ResolveDmsInstanceMiddleware.cs` can reload CMS instances on cache miss, so the relational DMS instance can be created after DMS starts.
- `src/dms/core/EdFi.DataManagementService.Core/Startup/ValidateStartupInstancesTask.cs` logs per-instance relational validation failures without aborting startup, which makes the phase 1 legacy-startup-instance workaround acceptable.

### Auth

- The helper-created CMS DMS instances are hardcoded to `edfi_datamanagementservice` in `AuthorizationDataProvider.cs` and `ProfileAwareAuthorizationProvider.cs`.
- `AuthorizationDataProvider.cs` still defaults its claim set name to `SIS-Vendor`, while the currently loaded claim set metadata in this stack uses `SISVendor`.
- The chosen canary scenario currently flows through `E2E-NoFurtherAuthRequiredClaimSet` via `StepDefinitions.cs`, so the auth name mismatch is not the only path in play, but phase 1 should still normalize the helper default so manual smoke runs and direct helper callers do not drift from the stack.

## Implementation Plan

### 1. Add a relational E2E environment lane

- Update `eng/docker-compose/local-dms.yml` to pass `AppSettings__UseRelationalBackend` from an environment variable such as `USE_RELATIONAL_BACKEND`.
- Add `eng/docker-compose/.env.e2e.relational`.
- Keep the main bootstrap variables in `.env.e2e.relational` pointed at the existing legacy bootstrap database:
  - `POSTGRES_DB_NAME`
  - `DATABASE_CONNECTION_STRING`
  - `DATABASE_CONNECTION_STRING_ADMIN`
  - `DMS_CONFIG_DATABASE_CONNECTION_STRING`
- Add relational-only variables in `.env.e2e.relational`:
  - `USE_RELATIONAL_BACKEND=true`
  - `RELATIONAL_E2E_DATABASE_NAME=edfi_datamanagementservice_relational`
- Parameterize `src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1` so it can boot either `./.env.e2e` or `./.env.e2e.relational` without duplicating the wrapper logic.
- Preserve current behavior as the default so legacy local runs do not change.

### 2. Add host-side relational database provision and reset

- Add a small host-side step or helper script that:
  - drops `RELATIONAL_E2E_DATABASE_NAME` if it exists
  - recreates it
  - resolves schema files from `SCHEMA_PACKAGES`
  - provisions the database with `EdFi.DataManagementService.SchemaTools ddl provision --create-database`
- Reuse the schema-resolution approach already present in `eng/preflight-dms-schema-compile.ps1` so the relational canary uses the same schema package set that runtime download uses.
- Run this helper from the repo host, not from inside the DMS container, because the current DMS image does not publish `SchemaTools`.
- Keep this reset path isolated to the relational lane. Do not mix it with the existing legacy cleanup logic.

### 3. Parameterize E2E-created DMS instances for the relational lane

- Update `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Authorization/AuthorizationDataProvider.cs` so the created CMS DMS instance connection string is not hardcoded to `edfi_datamanagementservice`.
- Make the same change in `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Authorization/ProfileAwareAuthorizationProvider.cs`.
- Read the target DMS instance database name from a shared E2E setting or constant so legacy and relational runs can point at different databases without forking the auth logic.
- For the relational canary lane, point those created CMS DMS instances at `RELATIONAL_E2E_DATABASE_NAME`.

### 4. Fix the auth name mismatch in the harness

- Normalize the helper default claim set naming so the stack uses the claim set names actually exposed by the local CMS metadata.
- For phase 1, that means changing the helper default away from `SIS-Vendor` and aligning it with `SISVendor`, ideally through one shared constant or configuration value instead of another hardcoded string.
- Keep explicit claim-set scenarios free to request other names when they need them.
- Leave the chosen canary scenario on its current `E2E-NoFurtherAuthRequiredClaimSet` path unless there is a specific reason to make the canary depend on a system claim set.

### 5. Isolate relational cleanup from legacy cleanup

- Teach the E2E harness to skip `ContainerSetupBase.ResetDatabase()` when the relational lane is active.
- The relational lane should rely on the dedicated drop/recreate-plus-provision step instead of legacy table deletes.
- Keep the existing legacy reset path untouched for non-relational runs.

### 6. Move one scenario by tag

- Add `@relational-backend` to `Scenario: 01 Put an existing document (Resource)` in `UpdateResourcesValidation.feature`.
- Do not tag additional scenarios in phase 1.
- Keep all other scenarios on the legacy lane.

### 7. Define local execution flow

Legacy lane:

- Start with the existing setup path.
- Run the legacy lane through the standard harness entry point: `pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -EnvironmentFile './.env.e2e' -TestFilter 'Category!=@relational-backend' -IdentityProvider self-contained`.

Relational lane:

- Start the stack with the relational environment file.
- Allow the normal startup path to create one legacy startup instance so DMS stays alive in phase 1.
- Drop, recreate, and provision `RELATIONAL_E2E_DATABASE_NAME`.
- Run only the canary through the same entry point: `pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -EnvironmentFile './.env.e2e.relational' -TestFilter 'Category=@relational-backend' -IdentityProvider self-contained`.
- Do not rely on `dotnet test --list-tests` for lane verification; the current VSTest discovery path in this project does not honor the category filter reliably.
- On failures, inspect DMS and related container logs before changing the scenario.

### 8. Prepare GitHub Actions for the canary lane

The current DMS E2E workflows run the full E2E project once per identity provider against the default `./.env.e2e` path by calling `./build-dms.ps1 E2ETest`. That is not enough once one scenario moves to `@relational-backend`, because the legacy lane would still try to execute the tagged scenario against the legacy environment.

Phase 1 CI wiring should therefore split DMS E2E execution into two lanes:

- Legacy lane:
  - environment file `./.env.e2e`
  - test filter `Category!=@relational-backend`
- Relational lane:
  - environment file `./.env.e2e.relational`
  - test filter `Category=@relational-backend`
  - host-side drop/recreate/provision of `RELATIONAL_E2E_DATABASE_NAME` before test execution

To support that cleanly:

- Extend `build-dms.ps1` so `E2ETest` can accept a test filter and environment file override, or bypass `build-dms.ps1` in workflow steps and call `dotnet test` directly for the split runs.
- Ensure the relational setup path used in CI can call the same host-side relational provision/reset helper as local runs.
- Update the DMS PR workflow first, since that is the main validation path for this suite.
- Update the scheduled DMS E2E workflows that currently invoke `E2ETest` so they do not run `@relational-backend` against the legacy environment.
- Give the relational lane separate test-result and log artifact names so failures are easy to isolate.

## Expected Code Changes

The first implementation should stay close to this set of files:

- `eng/docker-compose/local-dms.yml`
- `eng/docker-compose/.env.e2e.relational`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1`
- one new relational provision/reset helper on the host side
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Authorization/AuthorizationDataProvider.cs`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Authorization/ProfileAwareAuthorizationProvider.cs`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Management/ContainerSetupBase.cs`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Resources/UpdateResourcesValidation.feature`
- `build-dms.ps1`
- `.github/workflows/on-dms-pullrequest.yml`
- `.github/workflows/scheduled-build.yml`
- `.github/workflows/scheduled-pre-image-test.yml`

## Acceptance Criteria

- The selected canary scenario passes against the relational backend.
- The relational run uses a dedicated CMS DMS instance that points at `RELATIONAL_E2E_DATABASE_NAME`.
- The relational database is provisioned from the same `SCHEMA_PACKAGES` set used by runtime schema download.
- The relational lane does not repurpose `POSTGRES_DB_NAME` or the existing bootstrap connection strings.
- The relational lane does not execute the legacy table-delete cleanup path.
- The auth helpers no longer default to the mismatched `SIS-Vendor` name.
- Legacy local runs remain available with the current environment and can exclude the relational canary by tag.
- The DMS PR workflow can run both legacy and relational E2E lanes without executing the tagged relational canary in the legacy environment.

## Out of Scope for Phase 1

- Moving more than one E2E scenario
- Query-path relational coverage
- Delete-path relational coverage
- Replacing the temporary startup dependency on one legacy instance
- General relational cleanup support for every current E2E feature

Phase 1 includes the GitHub Actions changes needed for every current DMS workflow that invokes `E2ETest` to handle the relational canary correctly. Broader CI expansion outside those DMS E2E workflows remains optional follow-up work.

## Follow-up After the Canary Is Green

- Remove the startup dependency on a legacy instance.
- Expand the relational tag set to additional PUT, POST, and descriptor scenarios that stay off query.
- Decide whether relational reset should remain an external helper or become a first-class E2E test hook.
- Expand CI coverage beyond the canary lane only after the relational lane is stable and repeatable.
