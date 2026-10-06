# Ed-Fi Instance Management E2E Tests

This project contains end-to-end tests for the Data Management Service instance-management and
route-segment functionality, focusing on data segregation among instances. Like the
[standard DMS E2E suite](../EdFi.DataManagementService.Tests.E2E/README.md), it runs against a
locally-built `dms-local` Docker stack and is engine-aware (PostgreSQL or SQL Server).

## Purpose

These tests verify that:

- Multiple instances can be configured with route qualifiers (e.g., `districtId`, `schoolYear`)
- Data is properly segregated between instances
- Route segments correctly isolate data access
- Instance context is maintained throughout request processing
- Invalid route qualifiers are handled correctly
- The education organization projection serves each tenant's data stores only to its own
  projection credential, and the Configuration Service's production reader reads it end to end
  (see [Configuration Service projection reader project](#configuration-service-projection-reader-project))

## Prerequisites

- Docker running (Docker Engine with the Compose plugin, or Docker Desktop);
  `docker ps` and `docker compose version` must succeed in the test session
- PowerShell Core (`pwsh`) 7.0 or higher
- .NET 10.0 SDK

## Running the tests

**Always run from the repository root through the public `build-dms.ps1` `InstanceE2ETest`
target.** The target handles the full lifecycle described below; ordinary `dotnet test` is
**not** sufficient (see [Why not `dotnet test` directly?](#why-not-dotnet-test-directly)).

```powershell
# Full suite, default engine (PostgreSQL), self-contained identity:
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release

# Full suite, SQL Server:
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release -DatabaseEngine mssql
```

The suite is engine-aware. `-DatabaseEngine` defaults to `postgresql`; pass `mssql` to run the
same public entry point against SQL Server (which composes the `eng/docker-compose/.env.mssql`
overlay and swaps in `mssql.yml`; the SQL Server stack is relational-only).

`-EnvironmentFile` is optional: when omitted, `InstanceE2ETest` resolves the tracked
[`eng/docker-compose/.env.routeContext.e2e`](../../../../eng/docker-compose/.env.routeContext.e2e)
regardless of the current working directory, so the repo-root command above works as-is. Pass an
explicit `-EnvironmentFile` only to use a custom route-context environment file.

### Shard runs (both engines)

The suite uses two CI shard categories, `@instance-management-ci-shard-1` and
`@instance-management-ci-shard-2`. Reuse the already-built images with `-SkipDockerBuild`:

```powershell
# PostgreSQL, shard 1 and shard 2:
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release -SkipDockerBuild -TestFilter 'Category=@instance-management-ci-shard-1'
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release -SkipDockerBuild -TestFilter 'Category=@instance-management-ci-shard-2'

# SQL Server, shard 1 and shard 2:
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release -SkipDockerBuild -DatabaseEngine mssql -TestFilter 'Category=@instance-management-ci-shard-1'
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release -SkipDockerBuild -DatabaseEngine mssql -TestFilter 'Category=@instance-management-ci-shard-2'
```

Each shard writes `TestResults/EdFi.InstanceManagement.Tests.E2E.instance-shard-<N>.trx`. A full
run or a shard 1 run also writes
`TestResults/EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.<engine>.trx` (see
[Configuration Service projection reader project](#configuration-service-projection-reader-project)).

### CI lanes

On every DMS-relevant pull request,
[`.github/workflows/on-dms-pullrequest.yml`](../../../../.github/workflows/on-dms-pullrequest.yml)
runs both shards on each engine:

| Engine     | Required PR lane                              |
| ---------- | --------------------------------------------- |
| PostgreSQL | `run-instance-management-e2e-tests`           |
| SQL Server | `run-instance-management-e2e-tests-mssql`     |

Both feed the single required `dms-ci-gate` status check. See the standard suite's
[local and CI support matrix](../EdFi.DataManagementService.Tests.E2E/README.md#local-and-ci-support-matrix)
for the complete standard-plus-instance matrix.

## Suite-owned fixture lifecycle

`build-dms.ps1 InstanceE2ETest` (via `setup-local-dms.ps1`) prepares a deterministic,
suite-owned fixture before any scenario runs:

1. Start infra + Configuration Service, then provision the **three** route-context databases
   (`eng/docker-compose/provision-e2e-database.ps1`), generating the relational DMS schema and
   verifying the `dms."EffectiveSchema"` singleton and expected tables in each.
2. Start DMS, wait for `/health`, then register the fixture in the Configuration Service: the two
   canonical tenants `Tenant_255901` and `Tenant_255902`; one distinct vendor per tenant
   (2 vendors); three data stores (255901/2024 and 255901/2025 under `Tenant_255901`, 255902/2024
   under `Tenant_255902`); two route-context records per data store — a `districtId` and a
   `schoolYear` context, 6 total; and one application per tenant (2 applications) — all using
   engine-correct **opaque** connection strings (secrets are never logged).
3. Restart DMS **exactly once** after registration, wait for `/health`, then run the routed
   scenarios, which hydrate their tenant/route state from the fixture rather than creating it.

The route-context suite deliberately matches the local image's DS 5.2 core + TPDM schema surface
so the provisioned databases and the DMS runtime compute an identical `EffectiveSchemaHash`
(otherwise every routed data request returns 503). Schema-extension coverage (Sample, Homograph)
belongs to the standard DMS E2E suite, not this one.

## Configuration Service projection reader project

`InstanceE2ETest` also runs a Configuration Service test project,
[`EdFi.DmsConfigurationService.Tests.DmsProjectionE2E`](../../../config/tests/EdFi.DmsConfigurationService.Tests.DmsProjectionE2E/),
which drives the production
[education organization projection](../../../../docs/EDUCATION-ORGANIZATION-PROJECTION.md) reader
(`AddDmsEducationOrganizationProjectionReader`) against this stack: Discovery, real tokens, a
multi-page read of a seeded hierarchy, an empty data store, unknown, unavailable and unroutable
data stores, and injected page failures, restarts and caller cancellation.

**When it runs.** On a full run (no `-TestFilter`), and on a run whose filter names
`instance-management-ci-shard-1` under `Category` or `TestCategory`, as a whole word (so
`instance-management-ci-shard-10` does not match). Each engine's CI lane therefore runs it once, with
shard 1. A shard 2 run does not run it.

**Order.** It runs **before** the Reqnroll suite, in the same test-process context. The Reqnroll
suite's `AfterTestRun` hook deletes the suite-owned fixture (applications, data stores and vendors)
that the reader project provisions against, so the project cannot run after it. A reader project
failure fails the target, and the Reqnroll suite does not run.

**Build.** Its `dotnet test` builds and restores the project, even with `-UsePrebuiltOutput`. The
project belongs to the Configuration Service solution, which the shared CI build artifact does not
contain, so building it rebuilds nothing the artifact provides. This is the one exemption from the
CI-budget rule that every `dotnet test` in `build-dms.ps1` reuses the artifact with
`--no-build --no-restore`: [`eng/ci/tests/DmsPullRequestCiBudget.Tests.ps1`](../../../../eng/ci/tests/DmsPullRequestCiBudget.Tests.ps1)
pins it to `RunDmsProjectionReaderE2E`, to that function's single `dotnet test`, and to this one
project, and fails if any other function omits the flags.

**Result guard.** After the run, `Assert-DmsProjectionReaderTrxResults` reads
`TestResults/EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.<engine>.trx` and fails the target
when:

1. the file is missing or has no counters;
2. fewer than **8** results are from live tests, or any live result is not `Passed` (failed,
   skipped or not run). Live tests are the classes in the
   `EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live` namespace, the only ones that reach
   DMS; each result is mapped to its class through the TRX test definitions;
3. any test of the project, live or not, did not pass.

The project also holds harness tests (the `Harness` namespace) that need no stack, so the live
results are counted on their own: a run that discovered or selected no live test cannot pass on
harness tests alone, and the overall check still rejects a failed harness test. The minimum of 8
detects that live execution is missing; it is not a coverage inventory. The project has 20 live
tests, and a run in which at least 8 live tests ran and every test passed is accepted.

In CI the guard, through the target's exit code, is what fails a lane. The two lanes report the
TRX differently:

- **PostgreSQL** (`run-instance-management-e2e-tests`): the test reporter's `**/*.trx` wildcard
  picks up the reader TRX beside the Reqnroll one.
- **SQL Server** (`run-instance-management-e2e-tests-mssql`): the sanitizer covers every file in
  `TestResults`, but the reporter and the result upload select only the Reqnroll shard TRX. The
  reader's results appear in the sanitized setup log (`build-dms-setup.log`), which includes the
  guard's `DMS projection reader E2E: <n> of <m> live tests passed; ...` line and is shown and
  uploaded when the lane fails.

**Inputs.** The target exports `INSTANCE_E2E_DMS_BASE_URL`, `INSTANCE_E2E_CONFIG_SERVICE_URL`,
`INSTANCE_E2E_CONFIG_ADMIN_CLIENT_ID` and `INSTANCE_E2E_CONFIG_ADMIN_CLIENT_SECRET` (the DMS and
Configuration Service URLs and the system-administrator credential the Reqnroll `SetupHooks` use).
The project also reads `INSTANCE_E2E_DATABASE_ENGINE`, `INSTANCE_E2E_DATABASE_2_CONNECTION_STRING`,
`INSTANCE_E2E_ROUTE_MANIFEST` and both fixture tenants' `INSTANCE_E2E_FIXTURE_TENANT_<N>_NAME` and
`_VENDOR_ID`. A missing or malformed input fails every live test with
`DMS projection reader E2E setup failed: ...`; no test is skipped.

**What it creates.** Per tenant: a claim set granting only `Read` on the projection claim, exported
back to confirm the grant, and an application with no data stores or education organizations. In
`Tenant_255901`, `255901/2025`: a seven-organization hierarchy written through the DMS API by an
`EdFiSandbox` application. Two more data stores, one naming a database that does not exist and one
without a `schoolYear` context. Each record is registered for cleanup as soon as its id is known,
before any later request or check, and everything is deleted newest first in `OneTimeTearDown`; a
failed deletion fails the run, reported apart from any setup failure. The harness tests run
without a stack:

```powershell
dotnet test src/config/tests/EdFi.DmsConfigurationService.Tests.DmsProjectionE2E --filter "FullyQualifiedName~.Harness."
```

### Why not `dotnet test` directly?

Ordinary `dotnet test` only runs the scenarios; it does not provision the three databases,
register the CMS fixture, or perform the single post-registration DMS restart. Without that
lifecycle and the test-process environment contract it establishes, the `@InstanceFixture`
scenarios fail fast (missing/invalid fixture state), so run the tests only through the build
target unless that exact lifecycle and contract already exist in your session.

## Route-context databases and environment (names, not values)

The three district/school-year route databases default to the fixed routes and can be
overridden in the environment file (**names only; never print credential values**):

| Variable                     | Default database                              |
| ---------------------------- | --------------------------------------------- |
| `INSTANCE_E2E_DATABASE_1_NAME` | `edfi_datamanagementservice_d255901_sy2024` |
| `INSTANCE_E2E_DATABASE_2_NAME` | `edfi_datamanagementservice_d255901_sy2025` |
| `INSTANCE_E2E_DATABASE_3_NAME` | `edfi_datamanagementservice_d255902_sy2024` |

The three names must be non-empty and distinct and are validated as dedicated E2E database names
before any provisioning. The engine-specific database, credential, and port variable **names**
(never their values) are:

| Scope       | PostgreSQL                                                    | SQL Server                      |
| ----------- | ------------------------------------------------------------ | ------------------------------- |
| Database    | `POSTGRES_DB_NAME`                                           | `MSSQL_DB_NAME`                 |
| Credentials | `POSTGRES_PASSWORD` (user `POSTGRES_USER`, default `postgres`) | `MSSQL_SA_PASSWORD` (user `sa`) |
| Host port   | `POSTGRES_PORT` (default `5435`)                            | `MSSQL_PORT` (default `1435`)   |

Shared, engine-neutral names: `ROUTE_QUALIFIER_SEGMENTS` (enables route-based instance
resolution; `districtId,schoolYear`), `DMS_DATASTORE` (engine selector), and
`DATABASE_CONNECTION_STRING_ADMIN` (admin connection in the selected engine's format); see the
[standard suite's engine variable names](../EdFi.DataManagementService.Tests.E2E/README.md#environment-files-and-engine-variable-names-names-not-values).
If you override any route database name, keep the provisioning and registration values aligned to
the same databases.

URL pattern: `http://localhost:8080/{districtId}/{schoolYear}/data/ed-fi/{resource}` — for
example `http://localhost:8080/255901/2024/data/ed-fi/contentClassDescriptors`.

## Diagnostics and logging

- The **SQL Server** Instance CI lane captures the full `build-dms.ps1` setup/provisioning/test
  output to a per-job diagnostic file (never streamed to the console), snapshots each container's
  logs to files, and produces the `.trx` result plus timing artifacts. It runs
  [`eng/ci/sanitize-e2e-artifacts.ps1`](../../../../eng/ci/sanitize-e2e-artifacts.ps1)
  (redacting connection-string passwords, tokens, client keys/secrets, and Authorization headers)
  and gates every reporter, artifact upload, and console display on the sanitizer succeeding.
- The existing **PostgreSQL** Instance lane uploads its captured container logs and reports its
  `.trx`/timing artifacts directly; it does **not** run that sanitizer step.
- **Regardless of engine, raw local diagnostics may contain connection strings, tokens, or
  client secrets — sanitize them before sharing.** Inspect a running stack with
  `docker logs <container>`: `ed-fi-api` (DMS), `ed-fi-api-config-service` (Configuration
  Service), and `dms-postgresql` or `dms-mssql` (the datastore). Never echo raw credentials.

## Teardown and cleanup

Each run reprovisions the `dms-local` stack for its engine. To tear it down explicitly, use the
engine-aware wrapper with the **same engine** you started it with. Its `-EnvironmentFile`
defaults to `.env.routeContext.e2e` (resolved against `eng/docker-compose`); pass the same custom
file if you used one:

```powershell
# PostgreSQL:
pwsh ./src/dms/tests/EdFi.InstanceManagement.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine postgresql -EnvironmentFile '.env.routeContext.e2e'

# SQL Server:
pwsh ./src/dms/tests/EdFi.InstanceManagement.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine mssql -EnvironmentFile '.env.routeContext.e2e'
```

The wrapper delegates to the shared project-scoped teardown primitives (`start-local-dms.ps1 -d
-v` and `start-published-dms.ps1 -d -v`): those Compose projects are the sole authority for
removal (plus the two known locally-built images by exact name). This suite always runs
locally-built images, so the `dms-published` down is an expected no-op here. It never removes
unrelated containers, volumes, or databases.

In addition, scenarios tagged `@InstanceCleanup` clean up their own scenario-owned applications,
instances (including route contexts), and vendors after each scenario, while the suite-owned
`@InstanceFixture` state is preserved and torn down with the stack.

> [!TIP]
> Switching branches or changing DMS debugging code invalidates the running stack. Tear down and
> let the next `InstanceE2ETest` run reprovision fresh databases and image.

## Test structure

Tests use Reqnroll (SpecFlow successor) with Gherkin feature files under
`Features/InstanceManagement/` — including `InstanceSetup`, `RouteQualifierSegregation`,
`RouteQualifierErrors`, `RouteQualifierDiscovery`, `TenantAwareDiscovery`, `TenantSegregation`,
`ChangeQueriesInstanceIsolation`, `OwaspTenantIsolation`, and `ManagementClaimsetEndpoints` —
and under `Features/EducationOrganizationProjection/` (shard 1: Discovery templates, the
credential denial matrix, target resolution, a paged walk with exact parents, and the projection
credential's denial on the resource API).
Supporting code lives in `StepDefinitions/`, `Management/` (Configuration Service / DMS API
clients, fixture state, and hydration), `Models/`, and `Hooks/` (`SetupHooks.cs`,
`InstanceFixtureHooks.cs`, `InstanceManagementCleanupHooks.cs`).

## Troubleshooting

- **Tests fail with 404 for all requests.** Route qualifiers are not configured — rerun the
  build target, which sets `ROUTE_QUALIFIER_SEGMENTS`.
- **`@InstanceFixture` scenarios fail with missing/invalid fixture state.** The suite-owned
  fixture was not established — run the tests through `build-dms.ps1 InstanceE2ETest`, not
  `dotnet test`.
- **All routed requests return 503 / `EffectiveSchemaHash` mismatch.** A provisioned route
  database schema differs from the DMS runtime schema. Tear down and rerun so the databases are
  reprovisioned to match the image. Setup tries to catch this before the suite runs: it removes
  `USE_API_SCHEMA_PATH`, `API_SCHEMA_PATH`, and `SCHEMA_PACKAGES` from the process around its Docker
  phases so the selected `-EnvironmentFile` is authoritative, then verifies the started `ed-fi-api`
  container's schema settings against that file and fails with `DMS E2E setup mismatch: ...` before
  any scenario runs. Select a different `-EnvironmentFile` to change the package surface; ambient
  overrides of those three names are not a supported channel. See the standard suite's
  [Troubleshooting](../EdFi.DataManagementService.Tests.E2E/README.md#troubleshooting) section for
  the full failure mode.
- **SQL Server stack fails to start or is unhealthy.** Treat an unavailable or unhealthy
  `dms-mssql` container as an infrastructure failure, not a test result — inspect
  `docker logs dms-mssql` and confirm the `1435` host port is free.
- **`NODE_OPTIONS` errors.** Setting `NODE_OPTIONS` is not supported for these runs; clear it
  before invoking the build target.
- **Inspect logs.** Configuration Service: `docker logs ed-fi-api-config-service`; DMS:
  `docker logs ed-fi-api`; datastore: `docker logs dms-postgresql` or `docker logs dms-mssql`.
- **Custom route database names.** If you override `INSTANCE_E2E_DATABASE_1_NAME` /
  `_2_NAME` / `_3_NAME`, ensure the three remain distinct and dedicated; a mismatch fails the
  up-front name validation before provisioning.

## CDC support

Legacy document-store streaming tests have been removed. This Instance Management
route-context setup has no `-EnableKafkaCdc` integration or CDC message scenarios.
The separate [DMS E2E CDC opt-in](../../../../reference/cdc-documentation/operations-runbook.md#dms-e2e-setup)
supports its own managed single-target setup under the
[local bootstrap contract](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci);
it does not qualify this route-context harness. For local/published relational CDC,
start with the [CDC operator reference](../../../../reference/cdc-documentation/README.md).
API-driven message coverage remains owned by
[DMS-1325](../../../../reference/design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md).
