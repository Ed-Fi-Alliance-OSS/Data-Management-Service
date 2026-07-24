# Docker Compose Test and Demonstration Configurations

> [!WARNING]
> **NOT FOR PRODUCTION USE!** Includes passwords in the default
> configuration that are visible in this repo and should never be used in real
> life. Be very careful!

> [!NOTE]
> This document describes a reference architecture intended to assist in
> building production deployments. This reference architecture will not be tuned
> for real-world production usage. For example, it will not include service
> clustering, may not be well secured, and it will not utilize cloud providers'
> managed services.

## Starting Services with Docker Compose

This directory contains several Docker Compose files, which can be combined to
start up different configurations:

1. `kafka.yml` covers Kafka
2. `kafka-ui.yml` covers KafkaUI
3. `postgresql.yml` starts only PostgreSQL
4. `mssql.yml` starts SQL Server for the DMS datastore (see "Running on the MSSQL backend" below)
5. `local-dms.yml` runs the DMS from local source code.
6. `published-dms.yml` runs the latest DMS `pre` tag as published to Docker Hub.
7. `keycloak.yml` runs KeyCloak (identity provider).
8. `swagger-ui.yml` covers SwaggerUI

The scripts read local settings from a `.env` file; on first run they seed it
automatically as a copy of the tracked `.env.example`, so a clean checkout
needs no manual step. Edit `.env` to customize — `.env.example` itself is
documentation only and is never consumed at runtime.

Kafka and Kafka UI compose files remain available for local infrastructure
testing. The relational DMS CDC/Kafka design uses an explicit CDC opt-in for
connector registration; until that implementation lands, this compose setup does
not register DMS source connectors.

Convenience PowerShell scripts have been included in the directory, which start
the appropriate services.

* `start-all-services.ps1` launches `postgresql.yml`, without starting the DMS.
  Useful for running DMS in a local debugger.
* `start-local-dms.ps1` launches the DMS local build along with all necessary
  services.
* `start-published-dms.ps1` launches the DMS published build along with all
  necessary services.
* `start-postgresql.ps1` only starts PostgreSQL.
* `start-keycloak.ps1` only starts KeyCloak.

You can pass `-d` to each of these scripts to shut them down. To delete volumes,
also append `-v`. Examples:

```pwsh
# Start everything
./start-local-dms.ps1

# Start everything for E2E testing (file-based schema packages)
../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1

# Stop the services, keeping volumes
./start-local-dms.ps1 -d

# Stop the services and delete volumes
./start-local-dms.ps1 -d -v
```

The turnkey `bootstrap-local-dms.ps1` entry point accepts the same teardown flags, so the
documented start command also stops the stack. `bootstrap-local-dms.ps1 -d [-v]` delegates to
`start-local-dms.ps1 -d [-v -RemoveBootstrap]`; `-d -v` additionally removes the staged
`.bootstrap/` workspace. Pass the same infrastructure flags you started with so teardown targets
the same compose shape — in particular the same `-DatabaseEngine`, otherwise `-d -v` selects the
default (PostgreSQL) compose file and will not remove the other engine's named data volume.

```pwsh
# Stop the turnkey stack, keeping volumes
./bootstrap-local-dms.ps1 -d

# Stop the turnkey stack, delete volumes, and remove the .bootstrap workspace
./bootstrap-local-dms.ps1 -d -v
```

By default, authentication uses the Self-Contained (OpenIddict) identity provider. The environment and startup scripts are pre-configured for Self-Contained mode, and Keycloak is not required unless explicitly selected.

When an E2E environment file defines `E2E_DATABASE_NAME`, that database must be
provisioned and DMS must observe the provisioned `dms.EffectiveSchema` before
it can serve requests. The DMS E2E setup wrapper starts infra and CMS, configures
the CMS data store, provisions the E2E database, then starts DMS:

```pwsh
../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1 -EnvironmentFile ./.env.e2e
```

To run the late phases manually after `start-local-dms.ps1 -InfraOnly` has
already brought up infra and CMS:

```pwsh
./provision-e2e-database.ps1 -EnvironmentFile ./.env.e2e
./start-local-dms.ps1 -DmsOnly -EnvironmentFile ./.env.e2e -AddExtensionSecurityMetadata
```

The same E2E provisioning requirement is handled by the `E2ETests` build target
(`build-dms.ps1` -> `Initialize-E2EDatabase`).

If DMS starts before provisioning has run (or against a database missing
`dms.EffectiveSchema`), DMS will start successfully but requests to the
affected data stores return HTTP 503 (Service Unavailable). To recover, stop or
restart the running DMS process after provisioning so it reloads database state.
The E2E setup wrapper avoids that state by not starting DMS until provisioning is
complete.

If you want to use Keycloak as the identity provider, pass the `-IdentityProvider keycloak` parameter to the startup script. This will configure the environment to use Keycloak authentication, and you must ensure Keycloak is running and properly configured.

```pwsh
# Start everything (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -IdentityProvider self-contained

# Start everything for E2E testing (Self-Contained/OpenIddict mode, file-based schema by default)
../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1

# Stop the services, keeping volumes (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -d

# Stop the services and delete volumes (Self-Contained/OpenIddict mode)
./start-local-dms.ps1 -d -v
```

You can set up the Kafka UI containers for testing by passing the -EnableKafkaUI option.

```pwsh
# Start everything with Kafka UI
./start-local-dms.ps1 -EnableKafkaUI
```

You can launch Swagger UI as part of your local environment to explore DMS
endpoints from your browser.

```pwsh
# To enable Swagger UI
./start-local-dms.ps1 -EnableSwaggerUI
```

```pwsh
# To set up the Keycloak container for enabling authorization 
./start-local-dms.ps1
```

You can also pass `-r` to `start-local-dms.ps1` to force rebuilding the DMS API
image from source code.

```pwsh
./start-local-dms.ps1 -r
```

## Running on the MSSQL backend

The stack can run the DMS datastore on SQL Server instead of PostgreSQL using the
`-DatabaseEngine mssql` switch, on both the local-build and published-image workflows. Pass it to
`bootstrap-local-dms.ps1` / `bootstrap-published-dms.ps1` (turnkey) or `start-local-dms.ps1` /
`start-published-dms.ps1` (infrastructure). No `-EnvironmentFile` is required for the wrapper
entry points or `start-local-dms.ps1` (they seed `.env` from `.env.example` when absent); direct
`start-published-dms.ps1` invocation expects an existing `.env` (or an explicit
`-EnvironmentFile`):

```pwsh
# Turnkey, local build: stand up SQL Server, provision the relational schema, and (optionally) load seed data
./bootstrap-local-dms.ps1 -DatabaseEngine mssql -EnableSwaggerUI -LoadSeedData

# Turnkey, published image: same shape, against the published DMS image
./bootstrap-published-dms.ps1 -DatabaseEngine mssql -EnableSwaggerUI -LoadSeedData

# Tear down the local stack (delete volumes and remove the .bootstrap workspace)
./bootstrap-local-dms.ps1 -DatabaseEngine mssql -d -v
```

`mssql.yml` runs `mcr.microsoft.com/mssql/server:2022-latest` (Developer Edition), the same
image used in CI, publishing host port `1435`. `-DatabaseEngine mssql` composes the `.env.mssql`
overlay (`DMS_DATASTORE=mssql`, `DMS_CONFIG_DATASTORE=mssql`, the `MSSQL_*` keys, and the SQL
Server connection strings) onto the base environment file automatically — the same composition
mechanism used by `-DataStandardVersion` (see "Selecting a Data Standard version" below), so
every phase (configure, provision, and the DMS container itself) sees a consistent engine
selection. Everything else (`SCHEMA_PACKAGES`, Kafka, Keycloak, identity-provider token
endpoints, etc.) still comes from the base environment file; pass `-EnvironmentFile` only to
override those base settings, and the overlay still composes on top of it.

> [!NOTE]
> **Database topology.** Local deployments default to a *shared* database on both engines: the CMS
> `dmscs` schema and the self-contained (OpenIddict) identity stores live inside the same database
> as the DMS datastore (`POSTGRES_DB_NAME` on PostgreSQL, `MSSQL_DB_NAME` on MSSQL). Pass
> `-SeparateConfigDatabase` for a production-like *separate* topology in which the Configuration
> Service uses a dedicated `edfi_configurationservice` database while the DMS datastore selection is
> unchanged. See "Configuration Service database topology" below.

A few things are specific to the MSSQL path:

* **It is single-engine.** SQL Server hosts everything: the DMS datastore, the Configuration
  Service (CMS SQL Server backend), and the self-contained (OpenIddict) identity stores.
  `mssql.yml` swaps in for `postgresql.yml` - both define the same `db` service that
  `local-config.yml` health-gates on - and no PostgreSQL container runs. In the default
  self-contained flow, `setup-openiddict.ps1 -InitDb` creates the shared database during the
  infrastructure phase; CMS then deploys its `dmscs` schema before the provision phase deploys
  the DMS relational schema into that database (see "Database topology" above).
* **Relational backend only.** MSSQL is supported through the relational backend
  (`DMS_DATASTORE=mssql`). Schema is provisioned by `provision-dms-schema.ps1`,
  which selects the dialect from the effective `DMS_DATASTORE` (not the data-store connection
  string) and invokes `api-schema-tools ddl provision --dialect mssql --create-database`.
* **No Debezium CDC.** The relational backend serves both writes and queries directly from
  SQL, so Kafka, OpenSearch, and the Debezium source connector are not started on this path.
* **Seed data** uses the same API-based `-LoadSeedData` (BulkLoadClient) path as PostgreSQL;
  it is database-engine agnostic.
* **CI publishes database-template packages for both engines.** `build-minimal-template.yml` and
  `build-populated-template.yml` are reusable workflows parameterized by `database_engine`
  (`postgresql` or `mssql`); the `EdFi.Api.Minimal.Template.MsSql.yml` and
  `EdFi.Api.Populated.Template.MsSql.yml` caller workflows invoke them with
  `database_engine: mssql` to build and publish `EdFi.Api.Minimal.Template.MsSql.*` and
  `EdFi.Api.Populated.Template.MsSql.*` NuGet packages alongside the existing PostgreSQL ones.
  On MSSQL the package's data dump is a native `BACKUP DATABASE` `.bak` file (restored with
  `RESTORE DATABASE`); that `.bak` is coupled to the
  `mcr.microsoft.com/mssql/server:2022-latest` image line it was built and verified against
  (a moving tag tracking the latest SQL Server 2022 build - the coupling is at the SQL Server
  2022 level, not an exact build), the same way the PostgreSQL `.sql` dump is coupled to the
  PostgreSQL major version it was built against.
  Every published package is restore-verified in CI
  (`eng/DatabaseTemplates/verify-template-restore.ps1`) before publishing. Base environment files'
  `DATABASE_TEMPLATE_PACKAGE` value (e.g. `EdFi.Api.Populated.Template.PostgreSql.5.2.0`) carries
  the PostgreSQL package id; composing the `.env.mssql` overlay for `-DatabaseEngine mssql`
  rewrites only its engine segment to `MsSql` (`Convert-TemplatePackageToken` in
  `env-utility.psm1`), so the same base `-EnvironmentFile` names the matching package id for
  either engine. This section documents the CI build/publish/verify pipeline only; no local
  bootstrap flow currently restores these packages.

After the stack is up, run the smoke tests the same way as for PostgreSQL:

```pwsh
../smoke_test/Invoke-NonDestructiveApiTests.ps1 -BaseUrl "http://localhost:8080" -Key $key -Secret $secret
```

## Configuration Service database topology

The Configuration Service (CMS) database is selected independently of the database engine through
`DMS_CONFIG_DATABASE_NAME`, the single configuration-database name that both PostgreSQL and SQL
Server interpolate into `DMS_CONFIG_DATABASE_CONNECTION_STRING`. Two topologies are available on
both engines; the choice is never inferred from the engine:

* **Shared (default).** `DMS_CONFIG_DATABASE_NAME` resolves to the DMS datastore database
  (`POSTGRES_DB_NAME` / `MSSQL_DB_NAME`), so CMS shares that database.
* **Separate (`-SeparateConfigDatabase`).** `DMS_CONFIG_DATABASE_NAME` resolves to the dedicated
  `edfi_configurationservice` database. The DMS datastore selection is unchanged; only CMS moves.
  Selecting separate mode when the DMS datastore is the same physical database as
  `edfi_configurationservice` (under the engine's identity semantics - PostgreSQL is case-sensitive, SQL
  Server case-insensitive) is rejected with a clear diagnostic, because the topology would not actually be
  separate.

`-SeparateConfigDatabase` is available on `start-local-dms.ps1`, `start-published-dms.ps1`,
`bootstrap-local-dms.ps1`, `bootstrap-published-dms.ps1`, the shared bootstrap wrapper, and
`build-dms.ps1 StartEnvironment`, and is forwarded consistently through every phase.

The seam requirement is scoped to these full-stack entry points: every full-stack environment profile they
consume must define `DMS_CONFIG_DATABASE_NAME` and route its CMS connection string through it. The standalone
Configuration Service lane (`start-local-config.ps1` and the `build-config.ps1` E2E profiles
`.env.config.e2e`, `.env.config.mssql.e2e`, and `.env.config.mssql.multitenant.e2e`) does not expose
`-SeparateConfigDatabase`; it owns one explicit CMS target, so those profiles are outside the topology switch
and deliberately do not define the seam.

As a preflight, the start scripts resolve the effective runtime settings by asking Docker Compose itself
(`docker compose config`, which applies your shell environment over the env file exactly as `up`
will) and validate them once. This preflight may resolve Compose configuration and resolve, reuse, or
build the host validator tool; where no host tool is available, the published start instead pulls the
selected published image and runs an isolated `--network none` validator container. It completes before
any stack lifecycle mutation - no DMS/config Docker-image build, Compose up/down, volume deletion,
network creation, stack-service startup, or identity/CMS initialization. Each service has its own
independently interpolated runtime provider, so each is checked against the selected engine separately:

* **DMS service** (when its compose file is in the set): its runtime provider (`DMS_DATASTORE`, the
  `dms` service's `AppSettings__Datastore`) must be exactly `postgresql` or `mssql` and match the
  selected engine; and the topology datastore database is the engine-specific `POSTGRES_DB_NAME` /
  `MSSQL_DB_NAME` that Compose resolves on the `db` service - the authoritative anchor, never
  `DATABASE_CONNECTION_STRING_ADMIN` (a readiness/admin connection whose database can differ). A shell
  override of that key - direct or through a referenced variable - is reflected; the runtime contract
  validates that concrete resolved value, and host-side configuration and registration consume it by
  default (an explicit `-DataStoreDatabaseName` can replace the registered datastore target without
  changing this topology anchor).
* **Configuration Service** (when its compose file is in the set): its runtime provider
  (`DMS_CONFIG_DATASTORE`, the `config` service's `AppSettings__Datastore`) must be exactly `postgresql`
  or `mssql` and match the selected engine; and the connection string must be a valid connection for
  that engine (parsed with the exact runtime providers - Npgsql / Microsoft.Data.SqlClient - via the
  `api-schema-tools connection validate` verb, so any keyword the driver rejects is caught here) and
  target the effective `DMS_CONFIG_DATABASE_NAME`.
* **Database infrastructure** (always): on SQL Server the SA password must be non-blank.

Because `DMS_DATASTORE` and `DMS_CONFIG_DATASTORE` are separate variables, a shell override of either
cannot silently point its container at a different engine than the one that starts. Each service is
validated only when its compose file is actually selected (a published Keycloak start whose compose set
omits the local Configuration Service skips only the CMS checks - the DMS and stack checks still run;
several modes include CMS, so participation is decided by the composed file set, not by `-EnableConfig`
alone). Anything else fails fast with a clear diagnostic before any stack lifecycle mutation.

> Because connection strings are parsed with the exact runtime providers, the start scripts need the
> `api-schema-tools` executable (the same tool the provisioning phase uses). How they obtain it differs by
> lane, because only the published lane can run a source-less host.
>
> The **local start scripts** (`start-local-dms.ps1` and the standalone `start-local-config.ps1`) call
> `Resolve-DmsSchemaTool -BuildIfMissing`, which resolves a host executable as follows:
>
> * an explicit `DMS_SCHEMA_TOOL_PATH` is authoritative (it must exist);
> * otherwise a prebuilt copy is discovered under `eng/docker-compose/.bootstrap/tools/api-schema-tools`
>   or the SchemaTools build output;
> * otherwise, when no candidate exists and the .NET SDK and SchemaTools project are available, the
>   start lane publishes the tool from source once to `.bootstrap/tools/api-schema-tools` and re-probes,
>   so a clean checkout self-heals without a manual build;
> * finally, an `api-schema-tools` already on `PATH` is accepted only when
>   `DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK=true` opts in; otherwise resolution throws rather than silently
>   using an unpinned tool.
>
> An already-existing executable is reused as-is; after pulling changes to SchemaTools, rebuild or
> re-publish it (`dotnet publish src/dms/clis/EdFi.DataManagementService.SchemaTools -c Release -o eng/docker-compose/.bootstrap/tools/api-schema-tools`,
> or `build-dms.ps1 BuildAndPublish` / the bootstrap flow) so the resolver picks up the current tool. These
> local lanes have **no image fallback**: on a host with neither a resolvable tool nor the SDK, resolution
> fails with build/configuration guidance. (`prepare-dms-schema.ps1` likewise does not auto-publish - it
> requires an already-resolvable prebuilt tool.)
>
> `start-published-dms.ps1` instead calls `Resolve-DmsConnectionValidator`, which first attempts that same
> host-first resolution (`Resolve-DmsSchemaTool -BuildIfMissing`, so a prebuilt host tool or an SDK build is
> still preferred when present) and, **only** when no explicit `DMS_SCHEMA_TOOL_PATH` was supplied and no
> host tool can be resolved or built - the clean Docker/PowerShell-only **published-image** host with no .NET
> SDK and no source build - falls back to running the exact same `connection validate` verb **inside the DMS
> image**, which bundles the `api-schema-tools` CLI at `/app/ApiSchemaTools/` (invoked as
> `dotnet /app/ApiSchemaTools/api-schema-tools.dll ...` with `docker run --network none`, so it only parses
> the string and never connects). Connection strings are therefore still validated with the exact runtime
> providers - the parser is never weakened - without a host SDK. An explicit `DMS_SCHEMA_TOOL_PATH` stays
> authoritative and is never masked by the image: a wrong or stale explicit path fails hard rather than
> silently falling back to the container. A published Keycloak start with no local Configuration Service
> still resolves the validator, because the DMS datastore validation above remains active even when the CMS
> checks are skipped. Only the database-only diagnostic startup (`-DbOnly`) and teardown skip the validator
> entirely - they do not participate in the DMS/CMS runtime contract or register a datastore, so there is
> nothing for the preflight to validate.

```pwsh
# Shared (default): CMS shares the DMS datastore database
./bootstrap-local-dms.ps1 -EnableSwaggerUI

# Separate: CMS uses a dedicated edfi_configurationservice database
./bootstrap-local-dms.ps1 -SeparateConfigDatabase -EnableSwaggerUI

# Separate on SQL Server (topology is orthogonal to the engine)
./bootstrap-local-dms.ps1 -DatabaseEngine mssql -SeparateConfigDatabase -EnableSwaggerUI
```

### Who creates the configuration database

Creation of a missing configuration database depends only on the identity provider, and is
idempotent on both engines:

* **Keycloak.** The Configuration Service's engine-neutral DbUp `EnsureDatabase` deployment creates
  the database during normal CMS startup, for both PostgreSQL and SQL Server.
* **Self-contained (OpenIddict).** `setup-openiddict.ps1 -InitDb` runs before CMS starts and creates
  the database: SQL Server through its guarded `master`-database creation, PostgreSQL through an
  equivalent guarded creation via the `postgres` maintenance database (with database-name
  validation). It then initializes the OpenIddict key store that CMS reads at startup.

The PostgreSQL container entrypoint (`postgresql-init.sh`) creates only the DMS datastore database
on a fresh volume; it never creates a dedicated Configuration Service database, so the two creation
owners above are not duplicated. (In the shared topology the datastore database is also the
configuration database, and the guarded pre-CMS creation then no-ops idempotently.)

## Selecting a Data Standard version (bootstrap)

`bootstrap-local-dms.ps1` (and `bootstrap-published-dms.ps1`) accept `-DataStandardVersion` to
select the Data Standard without hand-editing environment files. When the overlay is composed
(see the composition-gating note below), it is layered as a local-bootstrap overlay
(`.env.bootstrap.ds52` / `.env.bootstrap.ds61`) onto `-EnvironmentFile` (derived file written to
the gitignored `.derived/`), and every phase — schema staging, claims, provisioning, and the DMS
container itself — runs from the composed result:

```pwsh
# DS 5.2 (core + TPDM) on MSSQL
./bootstrap-local-dms.ps1 -DatabaseEngine mssql -DataStandardVersion 5.2 -EnableSwaggerUI

# DS 6.1 (core only; TPDM is folded into core in 6.1) on MSSQL
./bootstrap-local-dms.ps1 -DatabaseEngine mssql -DataStandardVersion 6.1 -EnableSwaggerUI
```

Notes:

* **Surfaces are minimal by design**: DS 5.2 stages core + TPDM; DS 6.1 stages core only. These
  local-bootstrap overlays are deliberately distinct from the shared `.env.ds52` / `.env.ds61`
  overlays used by the *start scripts'* `-DataStandardVersion`, which carry the E2E/SDK surfaces
  (including the Sample/Homograph test extensions required by CI).
* `-DataStandardVersion` defaults to `5.2`, but composition is gated per entry point:
  `bootstrap-local-dms.ps1` **always** composes the overlay, so every local run goes through the
  same canonical surface regardless of the base env file's own `SCHEMA_PACKAGES` value.
  `bootstrap-published-dms.ps1` composes the overlay **only when `-DataStandardVersion` is
  explicitly passed**; otherwise the base env file's own `SCHEMA_PACKAGES` drives the run
  unchanged.
* **Always tear down before switching Data Standard versions** — the provisioned database and
  staged workspace are version-specific, and DMS refuses to start against a database whose
  effective schema hash does not match. For local bootstraps, tear down with
  `bootstrap-local-dms.ps1 -d -v` **and the same `-DatabaseEngine` you started with**, so the
  provisioned database volume is actually removed — teardown selects the compose file (and
  therefore the named volume) by engine, so a mismatched engine leaves that volume behind. For the
  MSSQL examples above: `bootstrap-local-dms.ps1 -DatabaseEngine mssql -d -v` (it delegates to
  `start-local-dms.ps1 -d -v -RemoveBootstrap`, removing both the SQL Server volume and the staged
  `.bootstrap/` workspace). For published bootstraps (`bootstrap-published-dms.ps1`), tear down
  with `start-published-dms.ps1 -d -v -RemoveBootstrap` instead — the published stack runs as a
  separate compose project (`dms-published`), which `bootstrap-local-dms.ps1 -d -v` does not
  touch.

## Schema Selection

DMS supports two modes for staging the ApiSchema bootstrap workspace. Both modes produce the
same normalized `.bootstrap/ApiSchema/` workspace that downstream phases consume.

### Standard mode (package-backed)

Standard mode resolves DS-qualified asset-only ApiSchema NuGet packages from the Ed-Fi package
feed and stages them into the bootstrap workspace. This is the recommended path for most
developers. It is driven by the effective env file's `SCHEMA_PACKAGES` value - core plus any
listed extension packages (the DS 5.2 default stages core + TPDM); custom or unpublished schema
sets use Expert mode below.

> **Requirement - `api-schema-tools` tool:** `prepare-dms-schema.ps1` needs the in-repo `api-schema-tools`
> CLI published as a native executable. Build it once before running the prepare command (the
> publish step is safe to re-run after branch switches).

```pwsh
# 1. Publish the api-schema-tools tool (required on a clean checkout)
$schemaToolProject = "../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
$schemaToolOutput  = ".bootstrap/tools/api-schema-tools"
dotnet publish $schemaToolProject -c Release -p:UseAppHost=true -o $schemaToolOutput

# 2. Point to the platform-appropriate executable
$schemaToolExe = if ($IsWindows) { "$schemaToolOutput/api-schema-tools.exe" } else { "$schemaToolOutput/api-schema-tools" }
```

Standard mode (omit `-ApiSchemaPath`) stages the packages listed by the env file's
`SCHEMA_PACKAGES` value; invoked without `-EnvironmentFile` (as below) it falls back to the
catalog-pinned core-only default. There is no `-Extensions` parameter.

```pwsh
./prepare-dms-schema.ps1 -SchemaToolPath $schemaToolExe
./prepare-dms-claims.ps1
./bootstrap-local-dms.ps1
```

After staging, run the `bootstrap-local-dms.ps1` wrapper (which sequences
infrastructure → configure → provision → DMS over the staged workspace), not bare
`start-local-dms.ps1` — see the note under Expert mode below for why a bare start leaves
the stack unconfigured.

To bootstrap an **extension-containing** schema set, either list the published extension
ApiSchema packages in `SCHEMA_PACKAGES` (standard mode - the DS 5.2 default already stages TPDM
this way) or use Expert mode (`-ApiSchemaPath`) below, which stages core plus any extensions
present in the supplied directory. Extension security metadata is bootstrap-managed for the
built-in extensions: `prepare-dms-claims.ps1` auto-stages the Sample and Homograph claim
fragments, and recognizes TPDM as already covered by the embedded DS 5.2 claims (no fragment is
staged for TPDM). Only extensions that are **not** bootstrap-mapped (a custom extension you
supply) require an additional `-ClaimsDirectoryPath` argument to `prepare-dms-claims.ps1`.

**Wrapper shorthand:** `bootstrap-local-dms.ps1` stages standard mode from the effective
`SCHEMA_PACKAGES` in-line (when no workspace is staged, or when the staged workspace's recorded
package identity no longer matches) and then starts the stack. It auto-discovers the
`api-schema-tools` executable published to `.bootstrap/tools/api-schema-tools` in step 1 (or set
`DMS_SCHEMA_TOOL_PATH` to point elsewhere).

```pwsh
./bootstrap-local-dms.ps1
```

> [!NOTE]
> Standard mode is driven by the effective env file's `SCHEMA_PACKAGES` value: prepare resolves
> and stages every listed package (core plus any extensions, each at its own pinned version) so
> the staged `.bootstrap/ApiSchema` workspace's effective schema hash matches what the DMS
> container downloads at startup. Only when prepare runs without an env file (direct diagnostic
> invocation with no `-EnvironmentFile`, or an env file lacking `SCHEMA_PACKAGES`) does it fall
> back to the catalog-pinned core-only default (`EdFi.DataStandard52.ApiSchema` at `1.0.333`).

### Expert mode (filesystem)

Expert mode stages ApiSchema files directly from a local directory. Use this path for custom
or in-repo schema directories that are not published as NuGet packages.

`../../src/dms/EdFi.DataStandard52.ApiSchema` below is the conventional local materialization
directory (reserved in `.gitignore`); it is absent on a fresh checkout, so populate it with the DS 5.2
core plus any extension `ApiSchema*.json` files first — or point `-ApiSchemaPath` at any local directory
that already contains them.

```pwsh
# Stage schema and claims, then start DMS.
# -ClaimsDirectoryPath is only needed for a non-bootstrap-mapped (custom) extension. Core plus the
# built-in extensions (Sample, Homograph, TPDM) need no fragment argument: Sample and Homograph
# stage claim fragments, while TPDM's claims ship in the embedded DS 5.2 set (no fragment staged).
./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath $schemaToolExe
./prepare-dms-claims.ps1 [-ClaimsDirectoryPath <directory-with-custom-extension-claimset-fragment>]
./bootstrap-local-dms.ps1
```

As of DMS-1153, `start-local-dms.ps1` is infrastructure-lifecycle-only: it no
longer creates data stores or provisions the DMS schema. Running it bare after
staging leaves the stack without a configured instance or provisioned schema.
Use the `bootstrap-local-dms.ps1` wrapper as above, or run the phases manually:

```pwsh
./start-local-dms.ps1 -InfraOnly
./configure-local-data-store.ps1
./provision-dms-schema.ps1
./start-local-dms.ps1 -DmsOnly
```

Each of the four commands above accepts `-DatabaseEngine mssql` (default `postgresql`); pass it
consistently on all four to run this manual flow against the SQL Server datastore - every phase
composes the same `.env.mssql` overlay (see "Running on the MSSQL backend" above), so a
mismatched flag on any one command leaves that phase reading the wrong engine.

`start-local-dms.ps1` and `start-published-dms.ps1` also accept a narrower `-DbOnly` switch that
starts only the database container and waits for it to become ready, then stops. It is mutually
exclusive with `-InfraOnly` and `-DmsOnly` and is not part of this four-command flow; it exists
for diagnostics and for other tooling to sequence a database-only startup around.

Expert mode (`-ApiSchemaPath`) and standard mode (omit `-ApiSchemaPath`) are the two schema-selection
paths; there is no `-Extensions` parameter.

`prepare-dms-claims.ps1` stages `*-claimset.json` fragments into
`.bootstrap/claims`. When a bootstrap manifest is present, startup activates
`USE_API_SCHEMA_PATH`/`API_SCHEMA_PATH` and mounts `.bootstrap/ApiSchema` into
the DMS container via `bootstrap-dms.yml`; staged claims are activated per
`claims.mode` in the manifest. The staged workspace is runtime-authoritative in
bootstrap mode.

The full DS 5.2 ApiSchema set - core plus the Sample, Homograph, and TPDM
extensions - is bootstrap-mapped end to end (see Expert mode above for how each
is handled), so staging it needs no `-ClaimsDirectoryPath`. This applies to Data
Standard 5.2, where TPDM is a separate extension; Data Standard 6.1 folds TPDM
into core.

Bootstrap mode provisions the relational DMS schema only. Relational DMS
CDC/Kafka connector registration is pending a separate implementation and should
be controlled by an explicit CDC opt-in such as `-EnableKafkaCdc`; bootstrap
startup does not register DMS source connectors today. The planned opt-in keeps
immutable deployment-owned binding records under a separate persistent `.cdc-state`
root (or an explicit `-CdcBindingStatePath`) and never stores them in the bootstrap
manifest. Runtime DMS receives only explicit `DocumentCache:Targets` and exposes
per-database projection health; deployment automation owns connector registration and
combined CDC readiness.

The DMS E2E setup wrappers stay on the non-bootstrap `SCHEMA_PACKAGES` flow.
Those env files use `USE_API_SCHEMA_PATH=true` to download and materialize
file-based ApiSchema package content, and the wrappers clear any stale
`.bootstrap/` workspace before startup to prevent bootstrap mode from activating
unintentionally.

If `prepare-dms-schema.ps1` or `prepare-dms-claims.ps1` fail with a
fingerprint-mismatch teardown-guidance error after a branch switch or input
change, recover by running `./bootstrap-local-dms.ps1 -d -v` (which removes the
local `.bootstrap/` workspace by delegating to
`start-local-dms.ps1 -d -v -RemoveBootstrap`) or the matching E2E
`teardown-local-dms.ps1` script, then rerun the prepare commands. Add the same
`-DatabaseEngine` you started with (e.g. `-DatabaseEngine mssql`) so teardown
also removes that engine's data volume; the workspace removal itself is
engine-independent. If you started the stack with `start-local-dms.ps1`
directly, `./start-local-dms.ps1 -d -v -RemoveBootstrap` with the same flags
you started with remains the equivalent direct recovery — including options
the bootstrap wrapper does not accept, such as `-EnableKafka`.

These recovery commands target the `dms-local` stack. The prepare commands and
the `.bootstrap/` workspace are shared with the published-image flow
(`bootstrap-published-dms.ps1`), so if the running stack is `dms-published`,
recover with `./start-published-dms.ps1 -d -v -RemoveBootstrap` plus the same
compose-shaping options you started with (e.g. `-IdentityProvider keycloak`,
`-EnableKafka`, `-EnableSwaggerUI`); the published wrapper itself has no
teardown flags. Running the `dms-local` recovery instead would leave the
published stack up while deleting the workspace its containers bind-mount.

> **Note on `-RemoveBootstrap`:** `./bootstrap-local-dms.ps1 -d -v` removes the
> `.bootstrap/` workspace for you — it delegates to
> `start-local-dms.ps1 -d -v -RemoveBootstrap`. Invoking the start scripts
> directly is different: by default `./start-local-dms.ps1 -d -v` and
> `./start-published-dms.ps1 -d -v` do **not** delete the `.bootstrap/`
> workspace. Pass `-RemoveBootstrap` explicitly when you want the workspace
> wiped (e.g. after a branch switch). The E2E teardown wrappers always remove
> it unconditionally.

## IDE Debugging Workflow

Use the IDE debugging workflow when you want to run DMS from your IDE (e.g. Visual Studio or Rider)
against Docker-managed infrastructure (PostgreSQL, Config Service, Keycloak/OpenIddict).

### Starter configuration artifact

Copy the starter file from the DMS frontend project into the same directory and rename it:

```pwsh
cp src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json.example `
   src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json
```

The artifact includes:

| Key | Value |
|-----|-------|
| `ConnectionStrings:DatabaseConnection` | `host=localhost;port=5435;...` (matches `.env.example` PostgreSQL port) |
| `ConfigurationServiceSettings:BaseUrl` | `http://localhost:8081` (Config Service host port) |
| `ConfigurationServiceSettings:ClientId` | `CMSReadOnlyAccess` (created by identity setup) |
| `ConfigurationServiceSettings:ClientSecret` | `<local-cms-readonly-secret>` (replace with the `CMSReadOnlyAccess` client secret; local-dev default `ValidClientSecret1234567890!Abcd`) |
| `ConfigurationServiceSettings:Scope` | `edfi_admin_api/readonly_access` |
| `ConfigurationServiceSettings:EncryptionKey` | `<dms-config-database-encryption-key>` (replace with value of `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` from `.env`; `.env.example` default `DefaultEncryptionKey32CharactersX1`) |
| `AppSettings:UseApiSchemaPath` | `true` (use staged bootstrap workspace schema; see activation note below) |
| `AppSettings:ApiSchemaPath` | `<repo-root>/eng/docker-compose/.bootstrap/ApiSchema` (replace `<repo-root>` with your absolute path) |
| `AppSettings:AuthenticationService` | `http://localhost:8081/connect/token` |
| `JwtAuthentication:Authority` | `http://localhost:8081` |
| `JwtAuthentication:ClientRole` | `dms-client` |
| `JwtAuthentication:RoleClaimType` | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` |

Replace `<local-cms-readonly-secret>` with the `CMSReadOnlyAccess` client secret. Identity setup
(`setup-keycloak.ps1` / `setup-openiddict.ps1`) creates the client with the local-dev default
`ValidClientSecret1234567890!Abcd` and does not print the value; if you override the secret at
client creation, use your override here. Replace `<dms-config-database-encryption-key>`
with the value of `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` from your `.env` file (`.env.example`
default `DefaultEncryptionKey32CharactersX1`). Replace `<repo-root>` with the absolute path
to the repository root on your machine.

> **Activation note:** `AppSettings:UseApiSchemaPath` and `AppSettings:ApiSchemaPath` point at
> the staged bootstrap workspace. With `UseApiSchemaPath=true`, DMS reads discovery/specification
> JSON and XSD content from the staged workspace via the bootstrap asset manifest, with no
> schema assemblies loaded on this path.

### Pre-DMS infrastructure setup

Run the bootstrap wrapper with `-InfraOnly` to start infrastructure, provision the schema, and
stop before launching DMS:

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -EnableConfig -IdentityProvider self-contained
```

The wrapper prints IDE next-step guidance (staged schema path and, when `CONFIG_SERVICE_CLIENT_*`
keys are set in the env file, `CMSReadOnlyAccess` client id and scope — never the secret value)
after provisioning completes.

### Two IDE workflow shapes

**Shape 1 — Pre-DMS stop (terminal):** `-InfraOnly` alone starts infrastructure, creates or confirms the
DMS instance in Config Service, provisions the schema, prints IDE configuration guidance, then stops.
Start DMS in your IDE using the printed settings; this invocation does not wait for it.

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -IdentityProvider self-contained
# → prints appsettings guidance (see the starter configuration table for the CMSReadOnlyAccess secret); stops before DMS startup
# Start DMS in your IDE now using the printed settings.
```

**Shape 2 — Health-wait continuation:** `-InfraOnly -DmsBaseUrl <url>` runs the same pre-DMS phases, then
waits (up to 300 seconds) for the IDE-hosted DMS process to return HTTP 200 from `<url>/health`.
`-DmsBaseUrl` is withheld from the initial infrastructure invocation and used only for the post-provision
health wait. When `-LoadSeedData` is also requested, seed loading runs against `<url>` after the health
wait passes.

> [!IMPORTANT]
> The two shapes are alternatives, not a sequence. If a previous wrapper run already created the
> data store (for example a Shape 1 run on the same stack), add `-NoDataStore` to the follow-up run
> so the configure phase reuses the existing data store instead of creating a duplicate:
> `./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl <url> -NoDataStore [-LoadSeedData ...]`
>
> `-NoDataStore` supports exactly one existing route-unqualified data store. If the earlier run
> used `-SchoolYearRange` (or otherwise created route-qualified data stores), do **not** re-run the
> wrapper — re-supplying `-SchoolYearRange` creates a new set of data stores instead of selecting
> the existing ones. Use the explicit phase commands against the data stores the earlier run
> created: `./start-local-dms.ps1 -InfraOnly -DmsBaseUrl <url>` for the health wait, then
> `./load-dms-seed-data.ps1 -DmsBaseUrl <url> -SchoolYear <years...>` for seed loading.

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198" -IdentityProvider self-contained
# → starts infra, provisions schema, waits for DMS at http://localhost:5198/health
# Start DMS in your IDE before the 300-second timeout elapses.

# With seed loading:
./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198" -IdentityProvider self-contained `
    -LoadSeedData -SeedTemplate Minimal
```

**Manual phase flow:** When running phases individually, complete configure and provision before invoking
the health wait, then start DMS in the IDE between phases:

```pwsh
cd eng/docker-compose
./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath ...
./prepare-dms-claims.ps1
./start-local-dms.ps1 -InfraOnly -IdentityProvider self-contained
./configure-local-data-store.ps1 -AddSmokeTestCredentials
./provision-dms-schema.ps1
# Start DMS in your IDE now.
./start-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198"  # post-provision health wait only
```

**Fail-fast rules:**

- `-DmsBaseUrl` without `-InfraOnly` is rejected: `start-local-dms.ps1` and `bootstrap-local-dms.ps1`
  both require `-InfraOnly` when `-DmsBaseUrl` is set.
- `-LoadSeedData -InfraOnly` without `-DmsBaseUrl` is rejected on the wrapper: seed loading requires a
  live DMS endpoint.
- `bootstrap-published-dms.ps1` does not accept `-InfraOnly` or `-DmsBaseUrl`; these shapes are
  local-only.

### School-year token endpoint (self-contained `-SchoolYearRange` variant)

When `configure-local-data-store.ps1` was run with `-SchoolYearRange`, each data store maps to
a school-year-qualified route. In that configuration, clients obtain tokens using the
school-year-scoped endpoint:

```
POST http://localhost:8081/connect/token/{schoolYear}
```

where `{schoolYear}` is the four-digit school year (e.g. `2025`). The standard
`/connect/token` endpoint remains available for non-school-year-qualified data stores.

## Default URLs

* The DMS API: [http://localhost:8080](http://localhost:8080)
* Kafka UI: [http://localhost:8088/](http://localhost:8088/)
* Swagger UI: [http://localhost:8082](http://localhost:8082)

## Multi-Data-Store Testing with Route Qualifiers

The DMS supports multi-data-store routing with route qualifiers, allowing you to route requests to different databases based on URL path segments.

### Step 1: Configure Environment for Multi-Data-Store

```powershell
# Navigate to docker-compose directory
cd eng/docker-compose

# Copy the environment template if you haven't already
cp .env.example .env

# Edit .env and set the ROUTE_QUALIFIER_SEGMENTS line:
# Find this section in .env:
#   # Multi-Data-Store Route Qualifiers (Optional)
#   #ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
#
# Change to (uncomment the line):
#   ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
```

### Step 2: Deploy DMS with Multi-Data-Store Configuration

```powershell
# Deploy with multi-data-store configuration enabled
./start-local-dms.ps1 -EnableConfig -r
```

Wait for all services to start (check with `docker ps`).

### Step 3: Create Additional Databases

The main `.env` file creates the main database, but you need to create the additional databases for each data store:

```powershell
# Create databases
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2024;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2025;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255902_sy2024;"
```

### Step 4: Deploy Schema to Each Database

You need to copy the DMS schema from the main database to each test database:

```powershell
# Export the schema from the main database (schema only, no data)
docker exec -i dms-postgresql pg_dump -U postgres -d edfi_datamanagementservice --schema-only > $env:TEMP\dms_schema.sql

# Apply schema to each test database
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024

# Verify schema was applied (should show 61 tables in dms schema)
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT COUNT(*) as table_count FROM information_schema.tables WHERE table_schema = 'dms';"
```

### Step 5: Run the REST Client Tests

1. Open `src/dms/tests/RestClient/multi-data-store-route-qualifiers.http` in VS Code
2. Execute the requests in order (they build on each other)
3. Follow the instructions in the comments

Key testing steps:

1. **Get Config Service Token** - Authenticates with the configuration service
2. **Create Data Stores** - Sets up 3 data stores with different route contexts:
   * Data store 1: District 255901, School Year 2024
   * Data store 2: District 255901, School Year 2025
   * Data store 3: District 255902, School Year 2024
3. **Create Application** - Creates an app associated with all data stores
4. **Get DMS Token** - Authenticates with DMS using app credentials
5. **Test Routing** - Creates descriptors via different routes and verifies they go to the correct database

### Step 6: Verify Data Routing

You can verify that data is going to the correct database by checking the descriptors created:

```powershell
# Check District 255901, School Year 2024 (should have "District255901-2024")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"

# Check District 255901, School Year 2025 (should have "District255901-2025")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"

# Check District 255902, School Year 2024 (should have "District255902-2024")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"
```

### Expected Behavior

When you make requests to:

* `/255901/2024/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255901_sy2024`
* `/255901/2025/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255901_sy2025`
* `/255902/2024/data/ed-fi/contentClassDescriptors` → Routes to `edfi_datamanagementservice_d255902_sy2024`

### Troubleshooting

**Route qualifiers not being parsed:**

* Check that `ROUTE_QUALIFIER_SEGMENTS` is set correctly in the `.env` file (comma-separated format)
* Verify the environment variable in the container: `docker exec ed-fi-api printenv | rg ROUTE`
* Verify the DMS logs: `docker logs ed-fi-api`

**404 - No database data store found or "No candidates found for the request path":**

* **Most common cause**: DMS container needs to be restarted after creating data stores in the Configuration Service
* Verify DMS loaded all data stores by checking the logs:

  ```powershell
  docker logs ed-fi-api | rg "Successfully fetched"
  # Should show: "Successfully fetched 4 data stores" (or your expected count)
  ```

* Verify route contexts are created correctly in the Configuration Service

* Check that the application is associated with the data stores
* Verify the JWT token includes the correct `dataStoreIds` claim

**Connection errors:**

* Ensure all databases are created
* Verify schema is deployed to each database
* Check PostgreSQL is running: `docker ps | grep postgresql`

**Check DMS logs:**

```powershell
docker logs ed-fi-api --follow
```

**Check Config Service logs:**

```powershell
docker logs ed-fi-api-config-service --follow
```

### Cleanup

To tear down the environment:

```powershell
cd eng/docker-compose
pwsh ./start-local-dms.ps1 -d -v
```

## Kafka UI

```powershell
# Open http://localhost:8088
```

## Accessing Swagger UI

Open your browser and go to <http://localhost:8082>. The dropdown reflects the
API definitions advertised by DMS at `/metadata/specifications`, including Resources
and Descriptors and, when the selected API schema advertises it, Change-Queries.
Swagger UI consumes the DMS endpoints published on the host (localhost and the
port defined in DMS_HTTP_PORTS).
>[!NOTE]
> The user that is configured to use swagger must have the Web Origins
> configuration in Keycloak to allow CORS. To do this you must search for your
> Client in keycloak and add Web Origins (Example: Web origins:
> <http://localhost:8082>).

## Tips

### Setup Keycloak

`setup-keycloak.ps1` will setup all the required realm, client, client
credentials, required role, and custom claims.

Keep the Keycloak client secret aligned with the CMS `IdentitySettings:ClientSecretValidation`
settings in `appsettings.json` or environment variables. CMS startup will
reject configured secrets whose length falls outside the configured
minimum/maximum range.

```pwsh

$parameters = @{
    KeycloakServer = "http://localhost:8065"  # Keycloak URL
    Realm = "your_realm"                      # Realm name (default: edfi)
    AdminUsername = "admin"                   # Admin username (default: admin, If you used a different admin
    # username during the Keycloak setup, please ensure you use that specific value instead of the default 
    # 'admin' username when running this script.)
    AdminPassword = "admin"                   # Admin password (default: admin, If you used a different admin
    # password during the Keycloak setup, please ensure you use that specific value instead of the default 
    # 'admin' password when running this script.)
    NewClientRole = "dms-client"               # Client role (default: dms-client), If you want to setup 
    # Configuration service client, then use "cms-client"
    NewClientId = "test-client"                # Client id (default: test-client)
    NewClientName = "test-client"              # Client name (default: Test client)
    NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Client secret example
    ClientSecretMinimumLength = 32                # Must match IdentitySettings:ClientSecretValidation:MinimumLength
    ClientSecretMaximumLength = 128               # Must match IdentitySettings:ClientSecretValidation:MaximumLength
    ClientScopeName = "sis-vendor"             # Scope name (default: sis-vendor) We are including the 
    # claim set name as a scope in the token. This can be customized to any claim set name (e.g., 'Ed-Fi-Sandbox').
    # Please note that the claim name cannot contain spaces; use a hyphen (-) instead.
    TokenLifespan  = 1800                      # Token life span (default: 1800)
}

# To set up the Keycloak with Realm and client 
./setup-keycloak.ps1  @parameters

# Optionally use the $SetClientAsRealmAdmin switch to assign realm admin role to the new client. 
# This is required for DMS Configuration Service. 
$parameters = @{
    NewClientRole = "config-service-ap"         # Defined in Configuration Service appsettings
    NewClientId = "DmsConfigurationService"     # Defined in Configuration Service appsettings
    NewClientName = "DmsConfigurationService"   
    NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Must match Configuration Service appsettings. Use appsettings.developer.json
    ClientSecretMinimumLength = 32                        # Must match IdentitySettings:ClientSecretValidation:MinimumLength
    ClientSecretMaximumLength = 128                       # Must match IdentitySettings:ClientSecretValidation:MaximumLength
}
./setup-keycloak.ps1  @parameters -SetClientAsRealmAdmin
```

### Setup OpenIddict

`setup-openiddict.ps1` configures the required application, client credentials, roles, scopes, and custom claims for OpenIddict. It uses an environment file for configuration and supports database initialization.

`NewClientSecret` must stay within the configured `IdentitySettings:ClientSecretValidation`
minimum/maximum range and include at least one lowercase letter, one uppercase
letter, one number, and one supported special character. The script exposes
`ClientSecretMinimumLength` and `ClientSecretMaximumLength` so local setup can
match the runtime settings.

```pwsh
$parameters = @{
  OpenIddictServer = "http://localhost:8081"    # OpenIddict URL
  NewClientRole = "dms-client"                  # Client role (default: dms-client); use "cms-client" for Configuration Service
  NewClientId = "test-client"                   # Client id (default: test-client)
  NewClientName = "test-client"                 # Client name (default: Test client)
  NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Client secret example
  ClientSecretMinimumLength = 32                # Must match IdentitySettings:ClientSecretValidation:MinimumLength
  ClientSecretMaximumLength = 128               # Must match IdentitySettings:ClientSecretValidation:MaximumLength
  ClientScopeName = "sis-vendor"                # Scope name (default: sis-vendor); can be customized (e.g., 'Ed-Fi-Sandbox')
  EnvironmentFile = "./.env"                    # Path to environment file for DB and other settings
}

# To set up OpenIddict with client and roles
./setup-openiddict.ps1 @parameters

# Optionally, use the $InitDb switch to initialize the database schema and keys before inserting data.
# Use $InsertData to control whether client, roles, scopes, and claims are inserted.

# Example: Initialize DB and insert data
./setup-openiddict.ps1 @parameters -InitDb -InsertData

# Example: Only insert data (skip DB initialization)
./setup-openiddict.ps1 @parameters -InsertData

# Example: Only initialize DB (skip data insertion)
./setup-openiddict.ps1 @parameters -InitDb

# To set up a Configuration Service client, use these parameters:
$parameters = @{
  NewClientRole = "config-service-ap"           # Defined in Configuration Service appsettings
  NewClientId = "DmsConfigurationService"       # Defined in Configuration Service appsettings
  NewClientName = "DmsConfigurationService"
  NewClientSecret = "ValidClientSecret1234567890!Abcd"  # Must match Configuration Service appsettings. Use appsettings.developer.json
  EnvironmentFile = "./.env"
}
./setup-openiddict.ps1 @parameters -InsertData
```

**Notes:**

* `EnvironmentFile` is required for DB connection and other settings.
* `$InitDb` initializes the database schema and keys.
* `$InsertData` inserts the OpenIddict client, roles, scopes, and claims.
* `ClientSecretMinimumLength` and `ClientSecretMaximumLength` should match the configured `IdentitySettings:ClientSecretValidation` bounds used by CMS.
* `NewClientSecret` must also satisfy the CMS complexity rule: lowercase, uppercase, number, and one special character from `!@#$%^&*()-_=+[]{}:;,.?`.
* Both switches can be used together or separately as needed.
