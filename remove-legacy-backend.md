# Remove DMS Legacy Backend Plan

## Goal

Remove the legacy DMS PostgreSQL document backend from `src/dms/backend/EdFi.DataManagementService.Old.Postgresql` and remove its integration test project at `src/dms/backend/EdFi.DataManagementService.Old.Postgresql.Tests.Integration`.

After this work, the relational backend is the only DMS runtime backend path. There should be no runtime feature flag, E2E lane category, Docker environment switch, CI job, or startup branch that selects between legacy and relational backend behavior.

## Assumptions

- PostgreSQL and MSSQL remain supported datastore engines for the relational backend.
- `AppSettings:Datastore` will still be needed to select the database engine (`postgresql` or `mssql`).
- `AppSettings:UseRelationalBackend`, `USE_RELATIONAL_BACKEND`, `AppSettings__UseRelationalBackend`, and the `relational-backend` E2E category/filter lane exist only because the relational backend was optional. These should be removed everywhere.
- DMS `DeployDatabaseOnStartup` exists only for legacy DMS schema provisioning and should be removed entirely, not redefined as relational startup provisioning. CMS `DeployDatabaseOnStartup` remains in scope for the Configuration Service only.
- Keep DMS provisioning-script responsibilities separate: `eng/docker-compose/provision-dms-schema.ps1` remains the CMS-selected target provisioning helper, while the renamed `eng/docker-compose/provision-e2e-database.ps1` owns explicit E2E database reset/provisioning.
- Do not add compatibility shims, type forwarders, old namespace aliases, or runtime switches to preserve the deleted backend surface. Update callers to the remaining relational backend APIs directly.
- MSSQL remains a supported relational runtime datastore. MSSQL relational provisioning is handled by `SchemaTools ddl provision --dialect mssql` for manual/integration use; do not add new MSSQL Docker/E2E automation as part of this cleanup.
- There are no migration concerns.

## Phase 1: Inventory And Guardrails

1. Capture the current dependency surface before editing:

   ```bash
   rg -n "Old\.Postgresql|UseRelationalBackend|USE_RELATIONAL_BACKEND|AppSettings__UseRelationalBackend|relational-backend|relational-ci-shard|NEED_DATABASE_SETUP|DeployDatabaseOnStartup" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   rg -n "MappingSet is not null|MappingSet is null|new QueryRequest|new GetRequest|ITokenInfoEducationOrganizationLookup|GetService<IChangeQueryRepository>" src/dms
   rg -n "InstanceManagement|routeContext|dms\.document|Backend\.Installer|Installer/EdFi\.DataManagementService\.Backend\.Installer\.dll|to_debezium" src/dms/tests/EdFi.InstanceManagement.Tests.E2E eng/docker-compose build-dms.ps1
   ```

2. Treat these as removal targets in product/runtime code:

   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql`
   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql.Tests.Integration`
   - solution entries in `src/dms/EdFi.DataManagementService.sln` and `src/dms/EdFi.DataManagementService-Docker.sln`
   - project references to `EdFi.DataManagementService.Old.Postgresql`
   - CI job "Run Old PostgreSQL Integration Tests"
   - Docker image copies/publishes of old backend or installer artifacts that only exist for legacy schema setup

3. Historical design notes should be left untouched. Do not edit historical/archive/design-note documents as part of this work; runtime code, tests, CI, Docker, active docs, and developer instructions should not keep active legacy-backend guidance.

## Phase 2: Move Required PostgreSQL Infrastructure Out Of Old.Postgresql

The new PostgreSQL relational backend still consumes a few useful classes from the old project namespace. Move only the reusable infrastructure before deleting the old project.

1. Move `NpgsqlDataSourceCache` and `NpgsqlDataSourceProvider` into `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`, not the common backend project. These are PostgreSQL-specific infrastructure types.

2. Recreate `PostgresqlServiceExtensions.AddPostgresqlDatastore(IConfiguration)` in `EdFi.DataManagementService.Backend.Postgresql` with the same public method name and signature, but with only relational-safe PostgreSQL infrastructure registrations. Do not leave a forwarding extension in `EdFi.DataManagementService.Old.Postgresql`.

   - keep mapping-pack option binding
   - keep `MappingSetCompiler`
   - keep `NoOpMappingPackStore`
   - keep PostgreSQL `RuntimeMappingSetCompiler` with `SqlDialect.Pgsql` and `PgsqlDialectRules`
   - keep `IMappingSetProvider`
   - keep `NpgsqlDataSourceCache`
   - keep scoped `NpgsqlDataSourceProvider`
   - do not register `RelationalDocumentStoreRepository` or `IQueryHandler`; those belong in the frontend composition root

3. Do not move these legacy registrations:

   - `PostgresqlDocumentStoreRepository`
   - `PostgresqlAuthorizationRepository`
   - `IGetDocumentById`, `IQueryDocument`, `IUpdateDocumentById`, `IUpsertDocument`, `IDeleteDocumentById`
   - `ISqlAction`
   - old document hydrator and operation/model classes
   - old startup validator/cache/initializer classes under `Old.Postgresql/Startup`
   - embedded SQL scripts under `Old.Postgresql/Deploy/Scripts`

4. Delete `AuthorizationRepositoryTokenInfoEducationOrganizationLookupAdapter`. The relational path should use the relational token-info lookup registrations:

   - PostgreSQL: `AddPostgresqlRelationalTokenInfoEducationOrganizationLookup`
   - MSSQL: `AddMssqlRelationalTokenInfoEducationOrganizationLookup`

5. Update namespaces and tests that currently import `EdFi.DataManagementService.Old.Postgresql` for moved infrastructure. The old namespace should disappear from active code instead of being preserved through aliases or wrappers.

## Phase 3: Make Relational Runtime Wiring Unconditional

Update `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`.

1. Remove `using EdFi.DataManagementService.Old.Postgresql`.

2. Remove the `UseRelationalBackend` read and all conditional branches based on it.

3. For PostgreSQL datastore selection:

   - call the new `Backend.Postgresql.AddPostgresqlDatastore`
   - always call `AddPostgresqlReferenceResolver`
   - always register `RelationalDocumentStoreRepository` as `IDocumentStoreRepository`
   - always register `RelationalDocumentStoreRepository` as `IQueryHandler`
   - always register `RelationalBackendMappingInitializer`
   - always register `AddPostgresqlRelationalTokenInfoEducationOrganizationLookup`
   - keep PostgreSQL fingerprint and resource-key readers

4. For MSSQL datastore selection:

   - always call `AddMssqlRelationalRuntimeServices`
   - always register `RelationalDocumentStoreRepository` as `IDocumentStoreRepository`
   - always register `RelationalDocumentStoreRepository` as `IQueryHandler`
   - always register `RelationalBackendMappingInitializer`
   - always register `AddMssqlRelationalTokenInfoEducationOrganizationLookup`
   - keep MSSQL fingerprint and resource-key readers

5. Delete `ConfigureQueryHandler` legacy query-handler selection and `AddPostgresqlQueryHandler`.

6. Remove helper methods that only exist to replace old registrations. In the composition root, register `RelationalDocumentStoreRepository` directly as both `IDocumentStoreRepository` and `IQueryHandler`.

## Phase 4: Remove The Legacy Database Setup Path

The old PostgreSQL deployer creates the legacy `dms.document` schema. It must not remain wired after deleting the old backend.

1. Remove PostgreSQL use of `Old.Postgresql.Deploy.DatabaseDeploy` from frontend startup, and remove the DMS startup provisioning path instead of replacing it with a renamed relational runtime provisioner.

2. Delete `Backend.Installer`; do not replace it with another runtime-packaged installer/provisioner:

   - delete `src/dms/backend/EdFi.DataManagementService.Backend.Installer`
   - delete DMS `IDatabaseDeploy`, `DatabaseDeployResult`, PostgreSQL/MSSQL `Deploy/DatabaseDeploy`, and their unit tests; update any remaining DMS callers so no non-CMS deployer path remains
   - remove `Backend.Installer` project entries from DMS solution files
   - remove all `/app/Installer` Docker, package, build, CI, and script plumbing
   - keep `eng/docker-compose/provision-dms-schema.ps1` for CMS-selected target provisioning; do not force E2E reset/provision callers through it
   - rename `eng/docker-compose/provision-relational-e2e-database.ps1` to `eng/docker-compose/provision-e2e-database.ps1`
   - update DMS E2E, build-script, workflow, template, unit-test, and docs callers to invoke `provision-e2e-database.ps1`
   - make `provision-e2e-database.ps1` own explicit E2E database reset/provision mode and read `E2E_DATABASE_NAME`
   - use `SchemaTools ddl provision --dialect mssql` for MSSQL manual/integration provisioning instead of adding a new runtime installer

3. Remove DMS database setup-on-startup behavior entirely:

   - `NEED_DATABASE_SETUP`
   - `DMS_DEPLOY_DATABASE_ON_STARTUP`
   - `AppSettings:DeployDatabaseOnStartup`
   - `AppSettings__DeployDatabaseOnStartup`
   - DMS frontend `DeployDatabaseOnStartup` app-setting properties, validators, startup phases, and `InitializeDatabase`/`IDatabaseDeploy` runtime wiring

   Scope this cleanup to DMS provisioning only. The Configuration Service also has an `AppSettings:DeployDatabaseOnStartup` setting for CMS database deployment; do not remove that CMS setting or its documentation as part of removing the DMS legacy backend.

4. Update `src/dms/run.sh` to stop invoking `/app/Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql`.

5. Update `src/dms/Dockerfile`:

   - remove old backend project copy
   - remove old backend source copy
   - remove backend installer restore/publish/copy

6. Update build and package plumbing that publishes or packages the legacy installer:

   - remove `PublishBackendInstaller` from `build-dms.ps1`
   - remove any `PublishBackendInstaller` invocation from `build-dms.ps1`
   - remove installer output from `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.nuspec`
   - remove CI, Docker, or release-package expectations for `/app/Installer`

7. Update scripts and tests that currently rely on the legacy installer:

   - `eng/docker-compose/start-local-dms.ps1`
   - `eng/docker-compose/start-published-dms.ps1`
   - `eng/docker-compose/bootstrap-*.psm1`
   - `eng/docker-compose/env-utility.psm1`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1`
   - Pester tests under `eng/docker-compose/tests`

8. For route-context or instance-management E2E setup, replace `dms.document` schema checks and dumps with generated relational DDL provisioning checks, especially `dms."EffectiveSchema"` and generated resource tables. Reuse `provision-e2e-database.ps1` or its shared explicit reset/provision helper path for E2E route-context databases; keep `provision-dms-schema.ps1` focused on CMS-selected bootstrap provisioning.

## Phase 4A: Remove Legacy Document-Store CDC And Kafka Plumbing

The current CDC/Debezium/Kafka path is tied to the legacy document-store schema (`dms.document`, partitioned `dms.document_*` tables, `dms.educationorganizationhierarchytermslookup`, `to_debezium`, and the `edfi.dms.document` topic). Relational-backend CDC is a separate future implementation, so do not preserve or pretend to migrate the legacy connector path in this work. Do not delete generic Kafka or Kafka UI infrastructure unless it only exists for the legacy document-store CDC path.

1. Remove active legacy document-store Debezium connector configuration:

   - `eng/docker-compose/postgresql_connector.json`
   - `eng/docker-compose/data_store_connector_template.json`
   - default connector registration in `eng/docker-compose/setup-connectors.ps1`
   - connector setup branches and comments in `eng/docker-compose/start-local-dms.ps1`, `eng/docker-compose/start-published-dms.ps1`, and `eng/docker-compose/start-all-services.ps1`

2. Delete Kafka E2E scenarios and helpers that assert the legacy document topic or payload shape until relational CDC is implemented. If a helper has shared non-CDC responsibilities, keep only the shared portion and remove all legacy document-topic behavior. Do not preserve hidden switches or compatibility shims for the legacy connector path:

   - `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/General/KafkaMessaging.feature`
   - `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Management/KafkaMessageCollector.cs`
   - `src/dms/tests/EdFi.DataManagementService.Tests.E2E/StepDefinitions/KafkaStepDefinitions.cs`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Features/InstanceManagement/KafkaTopicPerInstance.feature`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/InstanceKafkaMessageCollector.cs`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/InstanceKafkaTestConfiguration.cs`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/KafkaTopicHelper.cs`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/StepDefinitions/InstanceKafkaStepDefinitions.cs`

3. Delete instance-management Debezium helpers that hard-code legacy table lists and publication names unless a file contains shared non-CDC code that is still actively used. Any retained code must not register or configure the legacy connector path:

   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Infrastructure/DebeziumConnectorClient.cs`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Infrastructure/PostgresReplicationCleanup.cs`

4. Remove documentation and RestClient examples that describe `dms.document`, `to_debezium`, or `edfi.dms.document` as the active DMS streaming contract. Replace them with a short note that relational CDC/Kafka support is pending a separate implementation.

## Phase 5: Remove The UseRelationalBackend App Setting

1. Delete `UseRelationalBackend` from:

   - `src/dms/core/EdFi.DataManagementService.Core/Configuration/AppSettings.cs`
   - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Configuration/AppSettings.cs` if present in frontend settings
   - `src/dms/tests/EdFi.DataManagementService.Tests.E2E/AppSettings.cs`
   - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings*.json`
   - integration-test and unit-test configuration overrides

2. Make core middleware relational behavior unconditional:

   - `ValidateDatabaseFingerprintMiddleware`
   - `ValidateResourceKeySeedMiddleware`
   - `ResolveMappingSetMiddleware`
   - `ExtractDocumentInfoMiddleware`
   - `ProfileWriteValidationMiddleware`
   - `ProfileWritePipelineMiddleware`

3. Remove non-flag legacy runtime branches that are still selected by nullable `MappingSet` values or optional service lookups:

   - make `QueryRequestHandler` create relational query requests unconditionally and remove legacy `new QueryRequest(...)` paths
   - make `GetByIdHandler` create relational get requests unconditionally and remove legacy `new GetRequest(...)` paths
   - after mapping-set resolution is expected, handlers must require a resolved mapping set and fail fast as a pipeline/configuration error if it is missing
   - make write/delete request contracts relational-only; `MappingSet` should be non-nullable on relational request contracts and helper methods that require it. `RequestInfo.MappingSet` may remain nullable in this cleanup pass while middleware ordering still resolves it progressively.
   - remove `GetTokenInfoHandler` fallback behavior that depends on `ITokenInfoEducationOrganizationLookup`; delete `ITokenInfoEducationOrganizationLookup` and require the relational token-info lookup registration instead
   - delete the legacy read-profile filtering path from `ProfileFilteringMiddleware`; keep only behavior still needed by the relational projection/content-type flow
   - keep explicit unsupported behavior for datastores/features that still lack change-query support; do not treat missing `IChangeQueryRepository` as a legacy-backend selector

4. Make startup validation unconditional:

   - `ValidateDatabaseFingerprintReaderRegistrationTask`
   - `ValidateResourceKeyRowReaderRegistrationTask`
   - `ValidateStartupInstancesTask`

5. Update `ApiService` to stop passing `_appSettings.Value.UseRelationalBackend` into `ResourceActionAuthorizationMiddleware`. If that middleware only uses the flag to choose old authorization behavior, remove the constructor parameter and keep the relational behavior.

6. Remove "disabled flag" tests and update remaining tests to assert the always-relational behavior. Examples found during inventory:

   - `ValidateDatabaseFingerprintMiddlewareFeatureFlagTests`
   - `ResolveMappingSetMiddlewareTests` cases for `Given_UseRelationalBackend_Is_False`
   - `ValidateStartupInstancesTaskTests` cases for disabled relational backend
   - `ExtractDocumentInfoMiddlewareTests` toggle cases
   - `ResourceActionAuthorizationMiddlewareTests` legacy-mode helpers
   - app-settings tests expecting the flag default

7. Update error messages that mention "UseRelationalBackend is enabled" so they describe missing relational backend registrations directly.

## Phase 6: Remove E2E Lane Tags And Build Script Lane Logic

All DMS E2E tests now run against the relational backend because it is the only backend. There must be no positive or negative E2E filter that selects, excludes, validates, or documents a relational backend lane.

1. Remove `@relational-backend` from all E2E `.feature` files.

2. Remove bare `relational-backend` NUnit category references from filters, guardrail tests, scripts, workflows, and docs. Reqnroll feature tags use `@relational-backend`, but NUnit filters and build-script normalization often use `Category=relational-backend` without the `@`.

3. Delete all backend-lane filters instead of preserving them in another form:

   - remove `Category=@relational-backend`
   - remove `Category=relational-backend`
   - remove `Category!=@relational-backend`
   - remove `Category!=relational-backend`

   If a command also has a shard or focused non-backend filter, keep only the non-backend portion.

4. Replace every relational shard tag/filter with the generic E2E shard name:

   - `@relational-ci-shard-1` becomes `@e2e-ci-shard-1`
   - `@relational-ci-shard-2` becomes `@e2e-ci-shard-2`
   - `@relational-ci-shard-3` becomes `@e2e-ci-shard-3`
   - `@relational-ci-shard-4` becomes `@e2e-ci-shard-4`

   Apply the same rename to NUnit filters, workflow matrix commands, guardrail tests, docs, and generated Reqnroll category references. Update `.feature` files but not the generated `.feature.cs` artifacts.

5. Rename feature files whose names encode the old lane if they are no longer relational-only variants:

   - `RelationshipsWithEdOrgsOnlyRelational.feature` becomes `RelationshipsWithEdOrgsOnlyAdditional.feature`
   - `RelationshipsWithPeopleRelational.feature` becomes `RelationshipsWithPeople.feature`

6. Update `build-dms.ps1`:

   - remove `ConvertTo-Boolean` usage for `USE_RELATIONAL_BACKEND`
   - remove `Test-FilterIncludesRelationalCategory`
   - remove `Test-FilterExcludesRelationalCategory`
   - remove `Assert-E2ETestLaneMatchesFilter`
   - rename `Initialize-RelationalE2EDatabase` to `Initialize-E2EDatabase`
   - update `Initialize-E2EDatabase` to call `eng/docker-compose/provision-e2e-database.ps1`
   - always reset/provision the E2E relational database before DMS E2E tests
   - always set `AppSettings__DataStoreDatabaseName` for the test process
   - stop setting `AppSettings__UseRelationalBackend`
   - update test-result suffixes from `relational` to `e2e` and from `relational-shard-N` to `e2e-shard-N`
   - replace filters such as `Category=@relational-backend&Category=@relational-ci-shard-3` with only the neutral shard filter, such as `Category=@e2e-ci-shard-3`
   - replace filters such as `Category!=@relational-backend` with no backend filter at all

7. Consolidate env files:

   - perform one explicit replacement operation: make final `eng/docker-compose/.env.e2e` contain the current relational E2E content, with legacy backend switches removed and names neutralized
   - remove `USE_RELATIONAL_BACKEND=true` from the final `.env.e2e`
   - rename `RELATIONAL_E2E_DATABASE_NAME` to `E2E_DATABASE_NAME` in the final `.env.e2e`
   - delete `eng/docker-compose/.env.e2e.relational` after its content is moved into `eng/docker-compose/.env.e2e`
   - rename `eng/docker-compose/.env.smoke.relational` to `eng/docker-compose/.env.smoke`
   - rename `eng/docker-compose/.env.template.relational` to `eng/docker-compose/.env.template`
   - remove obsolete legacy neutral files only after their relational replacements are in place and all references are updated

8. Update E2E helpers:

   - remove `AppSettings.UseRelationalBackend`
   - remove `LegacyDataStoreDatabaseName`
   - make `SearchContainerSetup` always use the renamed `provision-e2e-database.ps1` reset/provision behavior
   - remove direct legacy reset paths and legacy database-name assumptions

9. Migrate Instance Management E2E setup to the relational backend:

   - update `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1` so it no longer relies on the legacy backend by omission; the DMS container must start with the same always-relational runtime path as the rest of DMS E2E
   - remove the one-shot `Installer/EdFi.DataManagementService.Backend.Installer.dll` call and all `dms.document` verification, schema dump, and schema replay logic
   - replace the per-instance database setup with the renamed E2E reset/provision helper path, run once for each route-context database, verifying `dms."EffectiveSchema"` and expected generated resource tables instead of `dms.document`
   - keep `.env.routeContext.e2e` as the neutral route-context E2E env file; it must not depend on `USE_RELATIONAL_BACKEND`, `NEED_DATABASE_SETUP`, or `DMS_DEPLOY_DATABASE_ON_STARTUP`
   - update `build-dms.ps1 InstanceE2ETest` to call the relational route-context provisioning flow and restart DMS when needed to clear cached database state
   - update `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md` so it describes relational setup and clearly excludes legacy installer/database setup
   - delete Instance Management Kafka topic-per-instance scenarios and legacy Kafka helpers until relational CDC/Kafka support exists, keeping only shared non-CDC code that remains actively used

## Phase 7: Remove Old Project References And Files

1. Delete:

   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql`
   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql.Tests.Integration`

2. Remove project references from:

   - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/EdFi.DataManagementService.Backend.Postgresql.csproj`
   - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj`
   - test projects that reference old PostgreSQL types

3. Remove old-project and old-test-project `InternalsVisibleTo` entries from all remaining projects, including:

   - `src/dms/backend/EdFi.DataManagementService.Backend/EdFi.DataManagementService.Backend.csproj`
   - `src/dms/core/EdFi.DataManagementService.Core/EdFi.DataManagementService.Core.csproj`

4. Remove old project entries and configuration blocks from:

   - `src/dms/EdFi.DataManagementService.sln`
   - `src/dms/EdFi.DataManagementService-Docker.sln`

5. Use `dotnet sln remove` for solution edits. Hand-edit solution files only for stale GUID/configuration blocks that `dotnet sln remove` cannot clean up.

## Phase 8: Update CI And Templates

1. Remove the old PostgreSQL integration test job from `.github/workflows/on-dms-pullrequest.yml`.

2. Update E2E workflow names and filters so they run against the single relational backend path without any `relational-backend` category filter:

   - `.github/workflows/on-dms-pullrequest.yml`
   - `.github/workflows/nightly-keycloak-e2e.yml`
   - `.github/workflows/scheduled-build.yml`
   - `.github/workflows/scheduled-pre-image-test.yml`
   - `.github/workflows/scheduled-smoke-test.yml`

3. Remove `use_relational_backend` inputs and conditional branches from template workflows:

   - `.github/workflows/build-populated-template.yml`
   - `.github/workflows/EdFi.Api.Populated.Template.PostgreSQL.yml`
   - `.github/workflows/EdFi.Api.Minimal.Template.PostgreSQL.yml`

4. Update log export paths and job summaries from "Relational E2E" to neutral "DMS E2E" naming.

5. Update package and release workflow templates so they no longer publish, upload, download, or validate `Backend.Installer` artifacts.

## Phase 9: Update Documentation

1. Update active developer docs and setup instructions:

   - `AGENTS.md`
   - `README.md`
   - `GETTING_STARTED.md`
   - `docs/DOCKER.md`
   - `docs/MULTI-TENANCY-GETTING-STARTED.md`
   - `docs/RELATIONAL-BACKEND.md`
   - `docs/CONFIGURATION.md`
   - `eng/docker-compose/README.md`

   Do not edit historical/archive/design-note documents as part of documentation cleanup.

2. Remove instructions that tell developers to choose a legacy or relational E2E lane. E2E documentation should state that all DMS E2E tests run against the relational backend because it is the only DMS backend.

3. Replace examples using:

   - `.env.e2e.relational`
   - `USE_RELATIONAL_BACKEND=true`
   - `AppSettings__UseRelationalBackend=true`
   - `Category=@relational-backend`
   - `Category!=@relational-backend`
   - `Category=relational-backend`
   - `Category!=relational-backend`
   - `NEED_DATABASE_SETUP`
   - `DMS_DEPLOY_DATABASE_ON_STARTUP`
   - `AppSettings:DeployDatabaseOnStartup`
   - `AppSettings__DeployDatabaseOnStartup`
   - `RELATIONAL_E2E_DATABASE_NAME`
   - `provision-relational-e2e-database.ps1`
   - `.env.smoke.relational`
   - `.env.template.relational`

4. Document the new default E2E command, for example:

   ```powershell
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e'
   ```

5. Document neutral shard filters:

   ```powershell
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-3'
   ```

## Phase 10: Validation

1. Format changed C# files:

   ```bash
   dotnet csharpier format src/dms
   ```

2. Run static searches. Runtime code, tests, CI, Docker, and active docs should have no active references to removed legacy plumbing:

   ```bash
   rg -n "Old\.Postgresql|EdFi\.DataManagementService\.Old\.Postgresql" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   rg -n "UseRelationalBackend|USE_RELATIONAL_BACKEND|AppSettings__UseRelationalBackend|relational-backend|relational-ci-shard" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   rg -n "NEED_DATABASE_SETUP|DMS_DEPLOY_DATABASE_ON_STARTUP|DeployDatabaseOnStartup|Backend\.Installer|dms\.document|edfi\.dms\.document|to_debezium" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   rg -n "RELATIONAL_E2E_DATABASE_NAME|provision-relational-e2e-database|\.env\.e2e\.relational|\.env\.smoke\.relational|\.env\.template\.relational" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   rg -n "MappingSet is not null|MappingSet is null|new QueryRequest|new GetRequest|ITokenInfoEducationOrganizationLookup|GetService<IChangeQueryRepository>" src/dms
   rg -n "Test-DmsDocumentTablePresent|Installer/EdFi\.DataManagementService\.Backend\.Installer\.dll|dms\.document|to_debezium" src/dms/tests/EdFi.InstanceManagement.Tests.E2E eng/docker-compose build-dms.ps1
   ```

   Do not edit historical documents to satisfy these searches. CMS `DeployDatabaseOnStartup` matches are expected and must remain. Remaining active DMS matches should be renamed, removed, or reworked.

3. Build:

   ```bash
   dotnet build src/dms/EdFi.DataManagementService.sln -c Release
   ```

4. Run focused unit tests:

   ```bash
   dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj -c Release
   dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj -c Release
   dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj -c Release
   ```

5. Run backend integration tests for PostgreSQL and MSSQL where local dependencies are available:

   ```bash
   dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj -c Release
   dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj -c Release --filter "Category=MssqlIntegration"
   ```

6. Run API-level DMS integration tests:

   ```bash
   dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj -c Release
   ```

7. Run DMS E2E through the repo-root build script after teardown/setup:

   ```powershell
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e'
   ```

8. E2E sharding:

   ```powershell
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-1'
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-2'
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-3'
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-4'
   ```

9. Run Instance Management E2E through the repo-root build script:

   ```powershell
   ./build-dms.ps1 InstanceE2ETest -Configuration Release -SkipDockerBuild
   ```

   Preserve existing Instance Management shard filters after the relational route-context setup is in place, and run each `instance-management-ci-shard-*` filter during full validation.

10. Build and inspect the Docker image:

   - confirm `/app/Installer` is absent
   - confirm no `AppSettings__UseRelationalBackend` environment variable is present in the compose files
   - confirm startup logs show relational mapping initialization and no legacy backend query-handler or legacy schema installation

## Completion Criteria

- `EdFi.DataManagementService.Old.Postgresql` and `EdFi.DataManagementService.Old.Postgresql.Tests.Integration` are deleted.
- No project or solution references old PostgreSQL projects.
- No runtime code can select the legacy backend.
- Core handlers and middleware no longer fall back to legacy request types, legacy token-info lookups, or nullable-`MappingSet` backend selection. Change-query endpoints may keep explicit unsupported behavior for datastores/features that lack support.
- No Docker or build script flag can enable or disable the relational backend.
- DMS `DeployDatabaseOnStartup` behavior and settings are removed entirely; CMS database deployment settings remain untouched.
- `Backend.Installer` is deleted, has no project or solution entries, and no deleted legacy installer artifact is published, copied into Docker images, or packaged in the frontend NuGet output.
- E2E tests, build filters, workflows, and docs no longer use `@relational-backend` or bare `relational-backend`; DMS E2E shard tags are renamed to backend-neutral tags.
- Relational E2E environment and script names are neutralized now: `RELATIONAL_E2E_DATABASE_NAME` is renamed to `E2E_DATABASE_NAME`, `provision-relational-e2e-database.ps1` is renamed to `provision-e2e-database.ps1`, relational env-file names are renamed to neutral names, and any neutral-file collision is resolved by replacing it with the relational version.
- Instance Management E2E tests provision route-context databases through the renamed E2E reset/provision helper path and no longer use `Backend.Installer`, `dms.document`, or legacy CDC/Kafka assumptions.
- CI no longer runs old PostgreSQL integration tests.
- DMS startup always wires relational document store, relational query handler, mapping initialization, fingerprint validation, resource-key validation, and relational authorization lookup.
- Legacy document-store CDC/Debezium/Kafka configuration is removed from active compose/test execution; legacy-only Kafka scenarios and helpers are deleted; docs state that relational CDC/Kafka support is pending a separate implementation.
- Documentation describes a single DMS backend path and the required `SchemaTools ddl provision` workflow.
