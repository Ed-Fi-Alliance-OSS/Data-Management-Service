# Data Management Service End to End Tests

This is a suite of end-to-end tests that cover the Ed-Fi Resources, Descriptors, and
Discovery API surface. They run against a locally-built DMS container stack that must be
rebuilt to stay in sync with the codebase, or against a locally-debugged API instance.

> [!NOTE]
> **No hot reload.** The effective schema is fixed at provisioning. After any ApiSchema
> change, reprovision a fresh database and restart DMS — see the
> [Relational Backend Developer Guide](../../../../docs/RELATIONAL-BACKEND.md). The full
> stack setup is documented in [`eng/docker-compose/README.md`](../../../../eng/docker-compose/README.md).

## Prerequisites

- Docker Desktop running
- PowerShell Core (`pwsh`) 7.0 or higher
- .NET 10.0 SDK

## Running the tests

All commands below are run **from the repository root** through the public `build-dms.ps1`
`E2ETest` target. That target starts the `dms-local` Docker stack, resets and provisions the
relational E2E database for the selected engine, restarts DMS, runs the tests, and writes a
`.trx` result to `./TestResults`. `-SkipDockerBuild` reuses the already-built local images
(`ed-fi-api-local`, `ed-fi-api-config-local`); omit it only when you want the target to
rebuild those images first.

The suite is engine-aware. `-DatabaseEngine` defaults to `postgresql`; pass `mssql` to run the
same public entry point against SQL Server. The `-DatabaseEngine mssql` value composes the
`eng/docker-compose/.env.mssql` overlay onto `-EnvironmentFile` and swaps `mssql.yml` for
`postgresql.yml`; the SQL Server stack is relational-only (no Kafka / OpenSearch).

### PostgreSQL (default engine)

```pwsh
# Default engine (PostgreSQL), self-contained identity, full DS 5.2 suite. The exclusion filter drops
# the unsharded @StandardVersion-6_1 scenarios, whose assertions require a DS 6.1 stack and would fail
# against the default DS 5.2 provisioning (run those with the DS 6.1 commands below):
pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category!=@StandardVersion-6_1'

# Explicit engine is equivalent to the default:
pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -DatabaseEngine postgresql -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category!=@StandardVersion-6_1'
```

### SQL Server (MSSQL)

```pwsh
# SQL Server, self-contained identity, full DS 5.2 suite. As above, exclude the DS 6.1-only scenarios
# that require a DS 6.1 stack:
pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -DatabaseEngine mssql -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category!=@StandardVersion-6_1'

# SQL Server, self-contained identity, bounded representative cross-section (the PR-gated signal):
pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -DatabaseEngine mssql -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@MssqlRepresentative'

# SQL Server, Keycloak identity, same representative set:
pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -DatabaseEngine mssql -IdentityProvider keycloak -EnvironmentFile './.env.e2e' -TestFilter 'Category=@MssqlRepresentative'
```

### Filtered and version-coupled runs

Any run can be narrowed with `-TestFilter 'Category=@<tag>'`. Common tags:

| Filter                              | Selects                                                                 |
| ----------------------------------- | ----------------------------------------------------------------------- |
| `Category!=@StandardVersion-6_1`    | The full DS 5.2 suite. A bare no-filter run also selects the unsharded DS 6.1 scenarios, which require a DS 6.1 stack and fail on the default DS 5.2 provisioning, so exclude them as shown. |
| `Category=@e2e-ci-shard-1` … `-4`   | One of the four DS 5.2 CI shards.                                        |
| `Category=@MssqlRepresentative`     | The bounded SQL Server representative cross-section (a subset of DS 5.2).|
| `Category=@StandardVersion-6_1`     | The DS 6.1 version-coupled scenarios (XSD metadata, Discovery, and a datastore round-trip smoke that proves a public data-plane request reaches the DS 6.1 datastore); run only against a DS 6.1 stack (`-DataStandardVersion 6.1`). |

```pwsh
# Data Standard 6.1 focused run (add -DataStandardVersion 6.1). The DS 6.1 PostgreSQL lane
# also needs a Kafka host entry (see the CI workflow); the MSSQL DS 6.1 stack is relational-only
# and needs none.

# PostgreSQL, DS 6.1:
pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -DataStandardVersion 6.1 -TestFilter 'Category=@StandardVersion-6_1'

# SQL Server, DS 6.1:
pwsh ./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -DatabaseEngine mssql -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -DataStandardVersion 6.1 -TestFilter 'Category=@StandardVersion-6_1'
```

The `@MssqlRepresentative` and `@StandardVersion-6_1` filters carry no shard, so the run writes
`TestResults/EdFi.DataManagementService.Tests.E2E.filtered.trx`; a shard filter writes
`...E2E.e2e-shard-<N>.trx`.

> [!IMPORTANT]
> A filtered run only matches scenarios whose tags are compiled into the current Release test
> assembly. After editing feature-file tags, run `build-dms.ps1 Build` (or drop `-SkipDockerBuild`)
> before a filtered run, otherwise `Category=@MssqlRepresentative` can match zero tests against a
> stale assembly.

### Debugging against a locally-run API

To debug the API while running the tests, change `ApiUrl` in `SearchContainerSetup.cs` to
`http://localhost:5198/` and run `EdFi.DataManagementService.Frontend.AspNetCore` in debug mode.

> [!WARNING]
> Your database tables are truncated after each feature file runs. Double-check your
> `DatabaseConnection` in `appsettings.json` and be aware of this before you run the tests.

## Local and CI support matrix

The `build-dms.ps1` public entry points can run any combination below locally. The **Required
PR lane** column names the job in
[`.github/workflows/on-dms-pullrequest.yml`](../../../../.github/workflows/on-dms-pullrequest.yml)
that runs that combination on every DMS-relevant pull request (a subset of what is runnable
locally); combinations without a named lane are locally-runnable only. The Instance Management
suite rows are documented in its own
[README](../EdFi.InstanceManagement.Tests.E2E/README.md).

| Suite    | Engine     | Identity        | Data Standard / filter                 | Required PR lane                             |
| -------- | ---------- | --------------- | -------------------------------------- | -------------------------------------------- |
| Standard | PostgreSQL | self-contained  | DS 5.2 full (shards `@e2e-ci-shard-1..4`) | `run-e2e-tests`                              |
| Standard | PostgreSQL | self-contained  | DS 6.1 `@StandardVersion-6_1`          | `run-e2e-tests-ds61`                         |
| Standard | SQL Server | self-contained  | `@MssqlRepresentative`                 | `run-e2e-tests-mssql`                        |
| Standard | SQL Server | keycloak        | `@MssqlRepresentative`                 | `run-e2e-tests-mssql`                        |
| Standard | SQL Server | self-contained  | DS 6.1 `@StandardVersion-6_1`          | `run-e2e-tests-mssql-ds61`                   |
| Standard | PostgreSQL | keycloak        | any                                    | _(locally runnable; not a required PR lane)_ |
| Standard | SQL Server | self-contained  | DS 5.2 full                            | _(locally runnable; not a required PR lane)_ |
| Instance | PostgreSQL / SQL Server | self-contained | `@instance-management-ci-shard-1/2` | `run-instance-management-e2e-tests` / `run-instance-management-e2e-tests-mssql` |

`e2e-summary` aggregates the four standard DMS E2E lanes and, with the other required jobs,
feeds the single required `dms-ci-gate` status check. The scheduled full DS 6.1 and smoke
workflows are owned elsewhere and are not part of this suite. Template restore / API-SDK smoke
coverage is owned by separate DMS-1289 workflows and is not part of these lanes.

## Direct `setup` and direct `dotnet test`

`build-dms.ps1 E2ETest` is the supported path. For interactive debugging you can prepare only
the stack and then run `dotnet test` yourself, but the test process configuration must align
with the running, provisioned stack.

The setup wrapper starts infra + Configuration Service, configures the E2E data store,
provisions the E2E database, and starts DMS:

```pwsh
pwsh src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1 -DatabaseEngine <postgresql|mssql> -EnvironmentFile './.env.e2e'
```

A direct `dotnet test` then reads its configuration from `appsettings.json` plus environment
variables. The engine, database name, and both connection strings must match the stack the
setup created. The relevant configuration keys (settable as `AppSettings__<Name>` environment
variables — **names only; never commit or echo credential values**) are:

- `AppSettings__DatabaseEngine` — `postgresql` (default) or `mssql`.
- `AppSettings__DataStoreDatabaseName` — the provisioned E2E database (default `edfi_datamanagementservice_e2e`).
- `AppSettings__DataStoreAdminConnectionString` — host-side admin connection used to reset the database between feature files.
- `AppSettings__DataStoreConnectionString` — the data-store connection the registered application uses.

If the engine or database name does not match the provisioned stack, the run fails fast at
setup (an engine/schema mismatch surfaces as an `EffectiveSchemaHash` mismatch and all
data-plane requests return 503).

## Environment files and engine variable names (names, not values)

The Docker-compose environment file supplies the containerized stack's settings. The default is
[`eng/docker-compose/.env.e2e`](../../../../eng/docker-compose/.env.e2e); `-DatabaseEngine mssql`
composes [`eng/docker-compose/.env.mssql`](../../../../eng/docker-compose/.env.mssql) on top of
it. The engine-specific database, credential, and port variable **names** you may need to align
for a custom stack (never their values) are:

| Scope       | PostgreSQL                                                    | SQL Server                      |
| ----------- | ------------------------------------------------------------ | ------------------------------- |
| Database    | `POSTGRES_DB_NAME`                                           | `MSSQL_DB_NAME`                 |
| Credentials | `POSTGRES_PASSWORD` (user `POSTGRES_USER`, default `postgres`) | `MSSQL_SA_PASSWORD` (user `sa`) |
| Host port   | `POSTGRES_PORT` (default `5435`)                            | `MSSQL_PORT` (default `1435`)   |

Shared, engine-neutral names: `DMS_DATASTORE` (engine selector, `postgresql` / `mssql`),
`E2E_DATABASE_NAME` (the E2E database the reset/provision step targets), `SCHEMA_PACKAGES` (the
ApiSchema packages baked into the provisioned database), and `DATABASE_CONNECTION_STRING_ADMIN`
(admin connection used for provisioning, in the selected engine's connection-string format).

> [!WARNING]
> The tracked `.env.e2e` / `.env.mssql` files contain **local test credentials**. Never copy
> their secret values into documentation, logs, or shared artifacts.

## Diagnostics and logging

- Test logs are written to the console and to the file system per `appsettings.json`. The API
  container logs are appended to the same file-system log at the end of the run before the
  container is destroyed; search for `API stdout logs`.
- The **SQL Server** CI lanes capture the full `build-dms.ps1` setup/provisioning/test output to
  a per-job diagnostic file (never streamed to the console), snapshot each container's logs to
  files, and produce the `.trx` result plus timing artifacts. Those lanes run
  [`eng/ci/sanitize-e2e-artifacts.ps1`](../../../../eng/ci/sanitize-e2e-artifacts.ps1) — redacting
  connection-string passwords, tokens, client keys/secrets, and Authorization headers — and gate
  every reporter, artifact upload, and console display on the sanitizer step succeeding.
- The existing **PostgreSQL** standard and DS 6.1 lanes upload their captured container logs and
  report their `.trx`/timing artifacts directly; they do **not** run that sanitizer step.
- **Regardless of engine, raw local diagnostics (container logs, TRX, setup output) may contain
  connection strings, tokens, or client secrets — sanitize them before sharing.** Inspect a
  running stack with `docker logs <container>`; the containers are `ed-fi-api` (DMS),
  `ed-fi-api-config-service` (Configuration Service), and `dms-postgresql` or `dms-mssql` (the
  datastore). Never echo raw credentials from these logs.

## Teardown and cleanup

Each `build-dms.ps1 E2ETest` run resets and provisions the `dms-local` stack for its engine, so
back-to-back runs are self-cleaning. To tear the stack down explicitly, use the engine-aware
wrapper with the **same engine** you started it with. Its `-EnvironmentFile` defaults to
`.env.e2e` (resolved against `eng/docker-compose`); pass the same custom file if you used one:

```pwsh
# PostgreSQL:
pwsh ./src/dms/tests/EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine postgresql -EnvironmentFile '.env.e2e'

# SQL Server:
pwsh ./src/dms/tests/EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine mssql -EnvironmentFile '.env.e2e'
```

The wrapper delegates to the project-scoped `start-local-dms.ps1 -d -v` primitive: the
`dms-local` Docker Compose project is the sole authority for which containers, networks, and
volumes are removed, and only the two known locally-built images are additionally removed by
exact name. It never touches unrelated containers, volumes, or databases.

> [!TIP]
> Switching branches or changing DMS debugging code invalidates the running stack. Tear down
> and let the next `E2ETest` run reprovision a fresh database and image, or you will silently
> test a stale build.

## Test authoring (Reqnroll)

This project uses [Reqnroll](https://reqnroll.net/) (an open-source SpecFlow successor). Install
the [Reqnroll extension for Visual Studio](https://marketplace.visualstudio.com/items?itemName=Reqnroll.ReqnrollForVisualStudio2022)
(the SpecFlow extension also works for syntax highlighting), or for VS Code the
[Cucumber extension](https://marketplace.visualstudio.com/items?itemName=CucumberOpen.cucumber-official),
to add tests and browse to step definitions.

Scenarios use Gherkin _Given_/_When_/_Then_ syntax and should read non-technically so the intent
is clear without knowing the implementation. The `Hooks` folder contains the environment and
Playwright setup/teardown.

## Troubleshooting

- **A filtered run reports "No test matches the given testcase filter".** The Release test
  assembly predates the feature-file tags. Run `build-dms.ps1 Build` (or drop `-SkipDockerBuild`)
  first so the current `@MssqlRepresentative` / other tags are compiled in.
- **All data-plane requests return 503 / `EffectiveSchemaHash` mismatch.** The provisioned
  database schema and the DMS runtime schema differ (often a `SCHEMA_PACKAGES` or engine
  mismatch). Tear down and rerun `E2ETest` so the database is reprovisioned to match the image.
- **SQL Server stack fails to start or is unhealthy.** Treat an unavailable or unhealthy
  `dms-mssql` container as an infrastructure failure, not a test result — inspect
  `docker logs dms-mssql` and the container's health, and confirm the `1435` host port is free.
- **`NODE_OPTIONS` errors.** Setting `NODE_OPTIONS` is not supported for these runs; clear it
  before invoking the build target.
- **Container names/ports.** DMS `ed-fi-api` (`8080`), Configuration Service
  `ed-fi-api-config-service` (`8081`), PostgreSQL `dms-postgresql` (`5435`), SQL Server
  `dms-mssql` (`1435`), Swagger UI `ed-fi-api-swagger-ui` (`8082`), Keycloak `dms-keycloak`.
- **Custom database names.** If you override `E2E_DATABASE_NAME` (or the connection strings),
  keep the setup, provisioning, and test-process values aligned to the same database, or the
  reset/provision step targets a different database than the test run.
