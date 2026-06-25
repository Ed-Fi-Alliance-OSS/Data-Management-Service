# Remove DMS Legacy Backend Plan

## Goal

Remove the legacy DMS PostgreSQL document backend from `src/dms/backend/EdFi.DataManagementService.Old.Postgresql` and remove its integration test project at `src/dms/backend/EdFi.DataManagementService.Old.Postgresql.Tests.Integration`.

After this work, the relational backend is the only DMS runtime backend path. There should be no runtime feature flag, E2E lane category, Docker environment switch, CI job, or startup branch that selects between legacy and relational backend behavior.

## Assumptions

- PostgreSQL and MSSQL remain supported datastore engines for the relational backend.
- `AppSettings:Datastore` will still be needed to select the database engine (`postgresql` or `mssql`).
- `AppSettings:UseRelationalBackend`, `USE_RELATIONAL_BACKEND`, `AppSettings__UseRelationalBackend`, and the `relational-backend` E2E category/filter lane exist only because the relational backend was optional. These should be removed everywhere.
- There are no migration concerns.

## Phase 1: Inventory And Guardrails

1. Capture the current dependency surface before editing:

   ```bash
   rg -n "Old\.Postgresql|UseRelationalBackend|USE_RELATIONAL_BACKEND|AppSettings__UseRelationalBackend|relational-backend|relational-ci-shard|NEED_DATABASE_SETUP|DeployDatabaseOnStartup" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   ```

2. Treat these as removal targets in product/runtime code:

   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql`
   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql.Tests.Integration`
   - solution entries in `src/dms/EdFi.DataManagementService.sln` and `src/dms/EdFi.DataManagementService-Docker.sln`
   - project references to `EdFi.DataManagementService.Old.Postgresql`
   - CI job "Run Old PostgreSQL Integration Tests"
   - Docker image copies/publishes of old backend or installer artifacts that only exist for legacy schema setup

3. Historical design notes should be left untouched but runtime code, tests, CI, Docker, docs, and developer instructions should not keep active legacy-backend guidance.

## Phase 2: Move Required PostgreSQL Infrastructure Out Of Old.Postgresql

The new PostgreSQL relational backend still consumes a few useful classes from the old project namespace. Move only the reusable infrastructure before deleting the old project.

1. Move `NpgsqlDataSourceCache` and `NpgsqlDataSourceProvider` into `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`.

2. Move or recreate the non-legacy portion of `PostgresqlServiceExtensions.AddPostgresqlDatastore` in `EdFi.DataManagementService.Backend.Postgresql`:

   - keep mapping-pack option binding
   - keep `MappingSetCompiler`
   - keep `NoOpMappingPackStore`
   - keep PostgreSQL `RuntimeMappingSetCompiler` with `SqlDialect.Pgsql` and `PgsqlDialectRules`
   - keep `IMappingSetProvider`
   - keep `NpgsqlDataSourceCache`
   - keep scoped `NpgsqlDataSourceProvider`

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

5. Update namespaces and tests that currently import `EdFi.DataManagementService.Old.Postgresql` for moved infrastructure.

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

6. Simplify helper method names if useful. For example, `ReplaceWithRelationalDocumentStoreRepository` can become `ConfigureDocumentStoreRepository` because there is no longer anything to replace.

## Phase 4: Remove The Legacy Database Setup Path

The old PostgreSQL deployer creates the legacy `dms.document` schema. It must not remain wired after deleting the old backend.

1. Remove PostgreSQL use of `Old.Postgresql.Deploy.DatabaseDeploy` from frontend startup.

2. Remove or redefine `Backend.Installer`:

   - Preferred cleanup: delete `src/dms/backend/EdFi.DataManagementService.Backend.Installer` if it only exists to invoke legacy scripts.
   - If a one-shot installer is still required, reimplement it as a generated relational DDL provisioner that references `Backend.Ddl`, `Backend.RelationalModel`, and the effective ApiSchema inputs. It must not reference `Old.Postgresql`.

3. Remove DMS legacy direct-start database setup switches unless a generated relational provisioner replaces them:

   - `NEED_DATABASE_SETUP`
   - `DMS_DEPLOY_DATABASE_ON_STARTUP`
   - `AppSettings:DeployDatabaseOnStartup`
   - `AppSettings__DeployDatabaseOnStartup`

   Scope this cleanup to DMS legacy provisioning. The Configuration Service also has an `AppSettings:DeployDatabaseOnStartup` setting for CMS database deployment; do not remove that CMS setting or its documentation as part of removing the DMS legacy backend.

4. Update `src/dms/run.sh` to stop invoking `/app/Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql`.

5. Update `src/dms/Dockerfile`:

   - remove old backend project copy
   - remove old backend source copy
   - remove backend installer restore/publish/copy if the installer is deleted

6. Update scripts and tests that currently rely on the legacy installer:

   - `eng/docker-compose/start-local-dms.ps1`
   - `eng/docker-compose/start-published-dms.ps1`
   - `eng/docker-compose/bootstrap-*.psm1`
   - `eng/docker-compose/env-utility.psm1`
   - `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1`
   - Pester tests under `eng/docker-compose/tests`

7. For route-context or instance-management E2E setup, replace `dms.document` schema checks and dumps with relational provisioning checks, especially `dms."EffectiveSchema"` and generated resource tables.

## Phase 4A: Remove Legacy Document-Store CDC And Kafka Plumbing

The current CDC/Debezium/Kafka path is tied to the legacy document-store schema (`dms.document`, partitioned `dms.document_*` tables, `dms.educationorganizationhierarchytermslookup`, `to_debezium`, and the `edfi.dms.document` topic). Relational-backend CDC is a separate future implementation, so do not preserve or pretend to migrate the legacy connector path in this work.

1. Remove or disable active legacy document-store connector configuration:

   - `eng/docker-compose/postgresql_connector.json`
   - `eng/docker-compose/data_store_connector_template.json`
   - default connector registration in `eng/docker-compose/setup-connectors.ps1`
   - connector setup branches and comments in `eng/docker-compose/start-local-dms.ps1`, `eng/docker-compose/start-published-dms.ps1`, and `eng/docker-compose/start-all-services.ps1`

2. Remove or quarantine Kafka E2E scenarios and helpers that assert the legacy document topic or payload shape until relational CDC is implemented:

   - `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/General/KafkaMessaging.feature`
   - `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Management/KafkaMessageCollector.cs`
   - `src/dms/tests/EdFi.DataManagementService.Tests.E2E/StepDefinitions/KafkaStepDefinitions.cs`

3. Update instance-management Debezium helpers that hard-code legacy table lists and publication names:

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

3. Make startup validation unconditional:

   - `ValidateDatabaseFingerprintReaderRegistrationTask`
   - `ValidateResourceKeyRowReaderRegistrationTask`
   - `ValidateStartupInstancesTask`

4. Update `ApiService` to stop passing `_appSettings.Value.UseRelationalBackend` into `ResourceActionAuthorizationMiddleware`. If that middleware only uses the flag to choose old authorization behavior, remove the constructor parameter and keep the relational behavior.

5. Update or remove "disabled flag" tests. Examples found during inventory:

   - `ValidateDatabaseFingerprintMiddlewareFeatureFlagTests`
   - `ResolveMappingSetMiddlewareTests` cases for `Given_UseRelationalBackend_Is_False`
   - `ValidateStartupInstancesTaskTests` cases for disabled relational backend
   - `ExtractDocumentInfoMiddlewareTests` toggle cases
   - `ResourceActionAuthorizationMiddlewareTests` legacy-mode helpers
   - app-settings tests expecting the flag default

6. Update error messages that mention "UseRelationalBackend is enabled" so they describe missing relational backend registrations directly.

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

   Apply the same rename to NUnit filters, workflow matrix commands, guardrail tests, docs, and any generated Reqnroll category references.

5. Rename feature files whose names encode the old lane if they are no longer relational-only variants:

   - `RelationshipsWithEdOrgsOnlyRelational.feature` becomes `RelationshipsWithEdOrgsOnly.feature`
   - `RelationshipsWithPeopleRelational.feature` becomes `RelationshipsWithPeople.feature`

6. Update `build-dms.ps1`:

   - remove `ConvertTo-Boolean` usage for `USE_RELATIONAL_BACKEND`
   - remove `Test-FilterIncludesRelationalCategory`
   - remove `Test-FilterExcludesRelationalCategory`
   - remove `Assert-E2ETestLaneMatchesFilter`
   - rename `Initialize-RelationalE2EDatabase` to `Initialize-E2EDatabase`
   - always provision the E2E relational database before DMS E2E tests
   - always set `AppSettings__DataStoreDatabaseName` for the test process
   - stop setting `AppSettings__UseRelationalBackend`
   - update test-result suffixes from `relational` to neutral names like `e2e` or `e2e-shard-N`
   - replace filters such as `Category=@relational-backend&Category=@relational-ci-shard-3` with only the neutral shard filter, such as `Category=@e2e-ci-shard-3`
   - replace filters such as `Category!=@relational-backend` with no backend filter at all

7. Consolidate env files:

   - make `eng/docker-compose/.env.e2e` the default relational E2E environment
   - remove `USE_RELATIONAL_BACKEND=true`
   - keep or rename `RELATIONAL_E2E_DATABASE_NAME` to a neutral name such as `E2E_DATABASE_NAME`
   - remove or rename `.env.e2e.relational`, `.env.smoke.relational`, and `.env.template.relational` after workflow references are updated

8. Update E2E helpers:

   - remove `AppSettings.UseRelationalBackend`
   - remove `LegacyDataStoreDatabaseName`
   - make `SearchContainerSetup` always use relational reset behavior
   - remove direct legacy reset paths and legacy database-name assumptions

## Phase 7: Remove Old Project References And Files

1. Delete:

   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql`
   - `src/dms/backend/EdFi.DataManagementService.Old.Postgresql.Tests.Integration`

2. Remove project references from:

   - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/EdFi.DataManagementService.Backend.Postgresql.csproj`
   - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj`, if the installer remains
   - test projects that reference old PostgreSQL types

3. Remove old-project and old-test-project `InternalsVisibleTo` entries from all remaining projects, including:

   - `src/dms/backend/EdFi.DataManagementService.Backend/EdFi.DataManagementService.Backend.csproj`
   - `src/dms/core/EdFi.DataManagementService.Core/EdFi.DataManagementService.Core.csproj`

4. Remove old project entries and configuration blocks from:

   - `src/dms/EdFi.DataManagementService.sln`
   - `src/dms/EdFi.DataManagementService-Docker.sln`

5. Run `dotnet sln` commands where possible instead of hand-editing solution GUID blocks.

## Phase 8: Update CI And Templates

1. Remove the old PostgreSQL integration test job from `.github/workflows/on-dms-pullrequest.yml`.

2. Update E2E workflow names and filters so they run against the single relational backend path without any `relational-backend` category filter:

   - `.github/workflows/on-dms-pullrequest.yml`
   - `.github/workflows/scheduled-build.yml`
   - `.github/workflows/scheduled-pre-image-test.yml`
   - `.github/workflows/scheduled-smoke-test.yml`

3. Remove `use_relational_backend` inputs and conditional branches from template workflows:

   - `.github/workflows/build-populated-template.yml`
   - `.github/workflows/EdFi.Dms.Populated.Template.PostgreSQL.yml`
   - `.github/workflows/EdFi.Dms.Minimal.Template.PostgreSQL.yml`

4. Update log export paths and job summaries from "Relational E2E" to neutral "DMS E2E" naming.

## Phase 9: Update Documentation

1. Update developer docs and setup instructions:

   - `AGENTS.md`
   - `README.md`
   - `GETTING_STARTED.md`
   - `docs/RELATIONAL-BACKEND.md`
   - `docs/CONFIGURATION.md`
   - `eng/docker-compose/README.md`

2. Remove instructions that tell developers to choose a legacy or relational E2E lane. E2E documentation should state that all DMS E2E tests run against the relational backend because it is the only DMS backend.

3. Replace examples using:

   - `.env.e2e.relational`
   - `USE_RELATIONAL_BACKEND=true`
   - `AppSettings__UseRelationalBackend=true`
   - `Category=@relational-backend`
   - `Category!=@relational-backend`
   - `Category=relational-backend`
   - `Category!=relational-backend`

4. Document the new default E2E command, for example:

   ```powershell
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e'
   ```

5. If sharding remains, document neutral shard filters:

   ```powershell
   ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-3'
   ```

## Phase 10: Validation

1. Format changed C# files:

   ```bash
   dotnet csharpier format src/dms
   ```

2. Run static searches. Runtime code, tests, CI, Docker, and docs should have no active references to removed legacy plumbing:

   ```bash
   rg -n "Old\.Postgresql|EdFi\.DataManagementService\.Old\.Postgresql" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   rg -n "UseRelationalBackend|USE_RELATIONAL_BACKEND|AppSettings__UseRelationalBackend|relational-backend|relational-ci-shard" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   rg -n "NEED_DATABASE_SETUP|DMS_DEPLOY_DATABASE_ON_STARTUP|Backend\.Installer|dms\.document|edfi\.dms\.document|to_debezium" src/dms eng .github build-dms.ps1 docs README.md GETTING_STARTED.md AGENTS.md
   ```

   Any remaining matches should be either intentionally historical or renamed/reworked.

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

9. Build and inspect the Docker image:

   - confirm `/app/Installer` is absent if the installer was deleted
   - confirm no `AppSettings__UseRelationalBackend` environment variable is present in the compose files
   - confirm startup logs show relational mapping initialization and no legacy backend query-handler or legacy schema installation

## Completion Criteria

- `EdFi.DataManagementService.Old.Postgresql` and `EdFi.DataManagementService.Old.Postgresql.Tests.Integration` are deleted.
- No project or solution references old PostgreSQL projects.
- No runtime code can select the legacy backend.
- No Docker or build script flag can enable or disable the relational backend.
- E2E tests, build filters, workflows, and docs no longer use `@relational-backend` or bare `relational-backend`; shard tags are either removed or renamed to backend-neutral tags.
- CI no longer runs old PostgreSQL integration tests.
- DMS startup always wires relational document store, relational query handler, mapping initialization, fingerprint validation, resource-key validation, and relational authorization lookup.
- Legacy document-store CDC/Debezium/Kafka configuration is removed or explicitly disabled; docs state that relational CDC/Kafka support is pending a separate implementation.
- Documentation describes a single DMS backend path and the required generated relational DDL provisioning workflow.
