# EdFi.DataManagementService.Tests.Integration

API-level integration tests that exercise the real DMS HTTP pipeline against
real provisioned PostgreSQL and SQL Server databases.

> [!NOTE]
> These are **API-level, in-process** integration tests. For **Docker-stack relational
> E2E**, see [`eng/docker-compose/README.md`](../../../../eng/docker-compose/README.md) and
> the [Relational Backend Developer Guide](../../../../docs/RELATIONAL-BACKEND.md). The
> relational backend has **no hot reload** — the effective schema is fixed at provisioning,
> so a schema change requires reprovisioning a fresh database and restarting the service.
> These tests already follow that model: every test starts from a freshly provisioned
> schema-only database (see [Invariants](#invariants)).

## What this is

These tests boot the production DMS host via
`WebApplicationFactory<Program>` and drive it through HTTP against an isolated
per-test database leased from a per-fixture cached baseline. The pieces that
exist purely to talk to external systems are faked in `Doubles/`: JWT
validation, the CMS claim-set provider, the application context, the DMS
instance directory. Everything else is the real production code path -
ApiSchema loading, profile XML parsing, write/read middleware, the relational
backend, and the DDL pipeline that provisions each baseline.

## What this is not

- Not a docker-stack E2E. See `EdFi.DataManagementService.Tests.E2E` for tests
  that run against a fully composed stack with DMS, CMS, and an identity
  provider.
- Not an exhaustive matrix. Deep edge cases for backend DDL generation,
  relational write merging, and apiSchema shape coverage live in
  `EdFi.DataManagementService.Backend.Postgresql.Tests.Integration` and
  `EdFi.DataManagementService.Backend.Mssql.Tests.Integration`.
- Not an authorization end-to-end. JWT and claim-set lookups are faked, so
  these tests prove the public DMS pipeline is correctly wired to the
  relational backend - not that production auth integrates correctly.

## Prerequisites

Tests skip cleanly via `Assert.Ignore` when their dialect's connection string
is unconfigured, so it is fine to run with only one dialect available.

### PostgreSQL

Start PostgreSQL via the repo docker compose:

```powershell
cd eng/docker-compose
pwsh ./start-postgresql.ps1
```

Then set the admin connection string:

```powershell
$env:ConnectionStrings__DatabaseConnection = "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_dms_backend_integration;pooling=true;minimum pool size=10;maximum pool size=50;Application Name=EdFi.DataManagementService;NoResetOnClose=true;"
```

### SQL Server

Start SQL Server in a container. The example maps SQL Server to host port
`14333` to avoid collisions with a developer SQL Server on `1433`:

```powershell
docker run --name dms-mssql-integration-2025 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='EdFi_Dms1!' -p 14333:1433 -d mcr.microsoft.com/mssql/server:2025-latest
```

Then set the admin connection string:

```powershell
$env:ConnectionStrings__MssqlAdmin = "Server=localhost,14333;User Id=sa;Password=EdFi_Dms1!;TrustServerCertificate=true"
```

If the container already exists, use `docker start dms-mssql-integration-2025`.
The version-suffixed name keeps SQL Server 2025 separate from any `dms-mssql-integration` container created from the earlier SQL Server 2022 instructions.
Do not reuse that legacy container: it still runs SQL Server 2022, so the MSSQL tests would run against an unsupported runtime and tests gated on SQL Server 2025 (such as the native-json evaluation fixture) would silently skip.
Remove it with `docker rm -f dms-mssql-integration` once anything you need from it is saved.
If `14333` is busy, map another host port and use that port in
`ConnectionStrings__MssqlAdmin`.

### appsettings.Test.json alternative

Either connection string can also be supplied via an `appsettings.Test.json`
file in the project directory, under the standard `ConnectionStrings` section.

## Running

All dialects:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration
```

PostgreSQL only:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "Category=PostgresqlIntegration"
```

SQL Server only:

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "Category=MssqlIntegration"
```

Via the repo build script (runs all `*.Tests.Integration` projects):

```powershell
./build-dms.ps1 IntegrationTest -Configuration Debug
```

## Fixture map

Every test binds to a `FixtureKey`, which resolves to a source repo directory
holding the fixture's ApiSchema and `fixture.json` manifest. The harness
consumes that directory both to provision the DDL baseline and to drive the
DMS host's ApiSchema loader, so the host and the DDL pipeline see the same
effective schema.

| FixtureKey | Source repo path | Purpose |
| --- | --- | --- |
| `FocusedStableKeyUpdateSemantics` | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics/` | Stable-key update semantics for a single resource shape. |
| `ProfileRootOnlyMerge` | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-root-only-merge/` | Profile-aware merges that stay on the root resource table. |
| `DescriptorRuntime` | `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime/` | Descriptor runtime create/update, no-op PUT, identity-immutable PUT, query/paging, and required-descriptor reference resolution coverage with the full shared descriptor query contract. |
| `ProfileSeparateTableMerge` | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-separate-table-merge/` | Profile-aware merges that span a separate child table. |
| `ProfileNestedAndRootExtensionChildren` | `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-nested-and-root-extension-children/` | Nested children plus root-level extension children under a profile. |
| `ProfileCollectionAlignedExtension` | `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension/` | Extension scope aligned to a profile-visible collection. |
| `ProfileCollectionAlignedExtensionHiddenDescendant` | `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension-hidden-descendant/` | Same shape with a hidden descendant; loaded only by scenarios that need it. |
| `AuthorizationQuery` | `src/dms/backend/Fixtures/synthetic/authorization-query/` | Synthetic EdOrg relationship authorization resources used for API-level relationship ProblemDetails response coverage. |
| `AuthoritativeDs52` | `src/dms/backend/Fixtures/authoritative/ds-5.2/` | Full Ed-Fi Data Standard 5.2 schema. Loaded only by scenarios that need production resource shapes the focused fixtures do not model (e.g. School/ClassPeriod/BellSchedule identity-propagation regressions). Baseline provisioning is heavier than focused fixtures. |
| `AuthoritativeDs52Tpdm` | `src/dms/backend/Fixtures/authoritative/ds-5.2-tpdm/` | Full Ed-Fi Data Standard 5.2 schema plus the checked-in TPDM ApiSchema. Loaded only by SurveyResponse/TPDM production-shape regressions that need the responder-choice reference model. Baseline provisioning is heavier than focused fixtures. |

The harness's `FixtureKey` enum lists only fixtures whose effective schema the
strict production DMS runtime mapping compiler accepts. The backend-only
`small/referential-identity` fixture (used by
`Backend.{Postgresql,Mssql}.Tests.Integration` in non-strict mode) is omitted
here because the runtime compiler is hardcoded strict and rejects fixtures
authored without post-key-unification columns.

## Runtime-compatibility materialization

Before the DMS host and the DDL baseline pipeline are given the source
fixture, the harness performs a one-pass augmentation step that adds neutral
defaults - empty arrays, empty objects, empty strings - for fields the DMS
HTTP middleware requires but the DDL-tier fixtures do not declare. The
materialized fixture is written once to a temp directory and is the single
source both the host and the DDL pipeline read, so effective-schema hashes
match.

Materialization only fills in empty/neutral values. Scenarios that need
non-empty query metadata, authorization securable definitions, or other
non-default values must supply that content explicitly in the source fixture
or in a scenario-owned overlay - they will not be invented by the harness.

## Profile XML

Profile XML for each fixture lives at
`Fixtures/Profiles/<FixtureKey>/*.xml` within this project. Each fixture
that uses a profile in any of its scenarios gets its own directory. The
fake CMS profile catalog walks that directory at harness startup.

`Fixtures/Profiles/ProfileRootOnlyMerge/` holds the first profile XML
files used by the suite (`profilerootonlymergeitem-visible.xml` and
`profilerootonlymergeitem-readonly.xml`). They back the profiled HTTP
scenarios in `Scenarios/ProfileRootOnlyMergeProfileScenario.cs`, which
cover profiled create/read, hidden-field preservation through a
profiled PUT, and the read-only-profile-used-for-write 405 path. The
unprofiled CRUD scenarios bound to the same fixture continue to run
in no-profile mode regardless of what profile XML is present.

Use lowercase `.xml` extensions. Linux CI is case-sensitive about file
extensions, and the catalog walker matches the lowercase pattern.

## Invariants

- Every test starts from a schema-only database plus whatever explicit seed
  the scenario inserts. There is no inter-test state.
- The DMS host (and its in-memory ApiSchema cache) is disposed before the
  leased database is released back to the baseline.
- The host and the DDL pipeline read the same materialized ApiSchema; their
  effective-schema hashes match.
- Failures in DMS host startup surface as test exceptions rather than
  silently terminating the NUnit process, because
  `Doubles/NonExitingStartupProcessExit.cs` replaces the production
  `IStartupProcessExit`.

## Adding a new scenario

1. **Pick a `FixtureKey`.** Reuse one of the existing fixtures whose shape
   matches what you need to assert. Add a new key (and a new repository path
   entry in `FixtureRepositoryPaths`) only when no existing fixture
   exercises the shape.
2. **Add profile XML** under `Fixtures/Profiles/<FixtureKey>/` if the
   scenario is profile-constrained. Skip this for no-profile scenarios.
3. **Write the scenario** as a `static` method on a class in `Scenarios/`,
   taking the `ApiIntegrationHarness` as its single argument. Use
   `harness.HttpClient` for HTTP and `harness.DbConnection` for persisted-
   state assertions.
4. **Add per-dialect wrappers** in `Tests/Postgresql/` and `Tests/Mssql/`.
   Each wrapper extends the matching dialect base, overrides `Fixture` to
   bind to the chosen `FixtureKey`, and exposes one `[Test]` method per
   scenario entry point.

## Cross-engine API parity convention

Required cross-engine API behaviors are tracked in the machine-readable parity
catalog (`ParityScenarioCatalog.Api.cs` in
`EdFi.DataManagementService.Backend.Tests.Common`) and enforced by the
`Given_The_Api_Parity_Catalog_Resolution` reflection meta-test in this project,
which resolves every Api-layer catalog row's declared PostgreSQL and SQL Server
locations to real `[Test]` methods without needing a database connection.

When you add an API behavior that must hold on both engines:

1. **Implement the scenario logic once** as a `static` method in `Scenarios/`
   (see [Adding a new scenario](#adding-a-new-scenario)).
2. **Add independently named PostgreSQL and SQL Server `[Test]` wrappers** in
   `Tests/Postgresql/` and `Tests/Mssql/`. The wrapper class names differ by
   dialect; both expose the **same stable method entry point** (the `[Test]`
   method name recorded in the catalog).
3. **Record the exact locations** — file, fixture class, and method — for both
   engines in the matching `ParityScenarioCatalog.Api.cs` row.
4. **Run the API parity meta-test**
   (`dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration --filter "FullyQualifiedName~Parity"`);
   it fails with an actionable `scenario [engine] File::Fixture::Method` message
   when a declared covered location does not resolve.

This convention is **catalog-driven**: it enforces only the wrappers the catalog
declares. It does **not** ban legitimate single-engine regression tests, and it
does **not** require global PostgreSQL/SQL Server wrapper-name or file-name
symmetry — only that each catalog-declared covered location resolves to exactly
one `[Test]` method.

## Debugging

When a runtime failure produces little useful output in the test console,
read the Serilog rolling file at
`bin/Debug/net10.0/logs/YYYYMMDD.log`. The DMS host writes full exception
detail there, including ApiSchema-loading and startup-validation failures
that the test console may swallow.

For MSSQL setup failures, first distinguish "unconfigured" from "configured
but unreachable." If `ConnectionStrings__MssqlAdmin` is absent, the MSSQL tests
skip with `Assert.Ignore`. If it is present but points at a stopped container,
the fixture fails while creating the leased database, before any API request is
issued.

Useful checks:

```powershell
Get-ChildItem Env:ConnectionStrings__MssqlAdmin -ErrorAction SilentlyContinue
Test-NetConnection -ComputerName localhost -Port 14333
docker ps --filter name=dms-mssql-integration-2025
docker logs dms-mssql-integration-2025 --tail 80
```
