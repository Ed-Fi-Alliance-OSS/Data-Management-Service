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

> [!IMPORTANT]
> Configuration Service variables start with `CONFIGURATION_SERVICE_`, and the Ed-Fi API's
> settings for calling it start with `DMS_CONFIGURATION_SERVICE_`. A `.env` created before
> this change still uses `DMS_CONFIG_*` and `CONFIG_SERVICE_*`, which are no longer read; the
> setup scripts warn about each one. Rename them as described in
> [Migrating from the earlier variable names](../../docs/CONFIGURATION.md#migrating-from-the-earlier-variable-names).

> [!NOTE]
> `provision-dms-schema.ps1` and `configure-local-data-store.ps1` decrypt the connection strings
> the Configuration Service stores with AES-GCM, which requires PowerShell 7.4 or later, for them
> and for every flow that runs them, such as `setup-local-dms.ps1`. The other setup scripts still
> need only PowerShell 7. Upgrade the Configuration Service, the Ed-Fi
> API and these scripts together: earlier releases cannot read the connection strings this
> Configuration Service writes. See
> [ConfigurationServiceSettings](../../docs/CONFIGURATION.md#configurationservicesettings).

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

### Selecting published images

`DMS_IMAGE_TAG` selects a tag for both published images at once, which is what a
deployment wants when the Ed-Fi API and the Configuration Service are released
under the same tag. It cannot express a digest: a digest identifies an image
within one repository, so the same value cannot be correct for both.

`CONFIGURATION_SERVICE_DOCKER_IMAGE` therefore overrides the Configuration Service's whole
image reference, the same name and meaning `local-config.yml` already gives it.
Left unset it expands to `edfialliance/ed-fi-api-configuration-service:${DMS_IMAGE_TAG:-pre}`,
so the shipped behaviour is unchanged. Set it to pin that service by digest
independently of the Ed-Fi API.

The scripts read local settings from a `.env` file; on first run they seed it
automatically as a copy of the tracked `.env.example`, so a clean checkout
needs no manual step. Edit `.env` to customize — `.env.example` itself is
documentation only and is never consumed at runtime.

Kafka and Kafka UI compose files remain available for local infrastructure
testing. Managed relational connector registration is available through
`-EnableKafkaCdc` on the local/published bootstrap wrappers. Start with the
[CDC operator reference](../../reference/cdc-documentation/README.md) for PostgreSQL
or SQL Server setup. Ordinary infrastructure startup does not register connectors;
the [local bootstrap contract](../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci)
owns the opt-in boundary.

Convenience PowerShell scripts have been included in the directory, which start
the appropriate services.

The local and published DMS launchers add `dms-cms-proxy.yml` when their Compose set includes
both DMS and CMS, including the infrastructure and DMS-only phases. This overlay creates an internal
`cms-proxy` network and explicitly enables forwarding with exact DMS peer trust, keeping public token
callers separate from system-token refresh. Include it after both service files when composing a
paired stack manually. The CMS-only launcher omits it, preserving
`CONFIGURATION_SERVICE_USE_REVERSE_PROXY_HEADERS=false` and existing standalone proxy settings.
If `172.30.148.0/29` overlaps
another network, change `DMS_CMS_PROXY_SUBNET`, `DMS_CMS_PROXY_IP` and `CMS_PROXY_IP` together before
starting. Parallel paired stacks need different subnets. See
[ConnectRateLimit](../../docs/CONFIGURATION.md#connectratelimit) for defaults and custom topologies.

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

Teardown does not remove locally built images, so the turnkey start reuses them by default. After
updating the checkout, pass `-Rebuild` (alias `-r`) to rebuild the local images before the
infrastructure starts:

```pwsh
./bootstrap-local-dms.ps1 -Rebuild
```

Because `-d -v` removes `.bootstrap/`, the next bootstrap reuses an eligible tool from the build
output directories or publishes it from source. The .NET 10 SDK is required when publishing is
needed. Clear or update `DMS_SCHEMA_TOOL_PATH` if it points into the deleted workspace; an
explicit path fails closed rather than triggering automatic publishing.

By default, authentication uses the Self-Contained (OpenIddict) identity provider. The environment and startup scripts are pre-configured for Self-Contained mode, and Keycloak is not required unless explicitly selected.

When an E2E environment file defines `E2E_DATABASE_NAME`, that database must be
provisioned and DMS must observe the provisioned `dms.EffectiveSchema` before
it can serve requests. The DMS E2E setup wrapper starts infra and the Configuration Service, configures
the Configuration Service data store, provisions the E2E database, then starts DMS:

```pwsh
../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1 -EnvironmentFile ./.env.e2e
```

To run the late phases manually after `start-local-dms.ps1 -InfraOnly` has
already brought up infra and the Configuration Service:

```pwsh
./provision-e2e-database.ps1 -EnvironmentFile ./.env.e2e
./start-local-dms.ps1 -DmsOnly -EnvironmentFile ./.env.e2e -AddExtensionSecurityMetadata
```

The same E2E provisioning requirement is handled by the `E2ETests` build target
(`build-dms.ps1` -> `Initialize-E2EDatabase`).

Before any reset, provisioning refuses an E2E database that is not dedicated. It compares the E2E
name with each protected name: `POSTGRES_DB_NAME`, `MSSQL_DB_NAME`, and every `Database`, `DB` or
`Initial Catalog` value it reads from `DATABASE_CONNECTION_STRING_ADMIN` and
`CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING`. The comparison ignores case and trailing spaces,
ideographic spaces (U+3000), tabs, line feeds, carriage returns and form feeds, so a protected
`'edfi_dms '` blocks an E2E database named `edfi_dms`. Once that trailing whitespace is removed,
an E2E or protected name must be 1 to 63 ASCII letters, digits or underscores; any other name stops
provisioning, on either engine, because the check cannot compare it reliably. The length limit
exists because PostgreSQL keeps only the first 63 bytes of a database name, so two longer names
that share their first 63 characters are one PostgreSQL database. Connection-string values are
checked as the connection-string parser reads them, without env-file rules applied a second time.
Provisioning also refuses an environment file that declares a protected key, or a name one of
them references, more than once or in more than one letter case, because Compose could then use a
different value than the one checked. It likewise refuses a file where Compose, which resolves the
file line by line with case-sensitive names, would give a protected key a different value or
presence than the check reads: for example a reference to a name declared on a later line, a
reference spelled in another letter case than its declaration, or a protected key declared only in
lowercase. The Instance Management route of `build-dms.ps1`, the Instance Management
`setup-local-dms.ps1` and the CDC E2E setup run these declaration checks before they start
infrastructure, and provisioning repeats them before any reset. A value set in the shell overrides
the file, so the file's declarations of that name are not checked; the shell value is still
compared with the E2E name.

When `POSTGRES_DB_NAME` is absent from both the environment file and the shell, the check protects
`edfi_datamanagementservice`, the name `postgresql.yml` uses by default. Other absent keys are
skipped, so Compose can still give a container a database name the check never sees: without
`CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING` the Configuration Service builds its connection
from `CONFIGURATION_SERVICE_DATABASE_NAME` or `POSTGRES_DB_NAME`. Declare the engine's database-name
key and both connection strings in any environment file used for E2E provisioning, especially when
running `provision-e2e-database.ps1` directly.

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

`mssql.yml` runs `mcr.microsoft.com/mssql/server:2025-latest` (Developer Edition), the same
image used in CI, publishing host port `1435`. `-DatabaseEngine mssql` composes the `.env.mssql`
overlay (`DMS_DATASTORE=mssql`, `CONFIGURATION_SERVICE_DATASTORE=mssql`, the `MSSQL_*` keys, and the SQL
Server connection strings) onto the base environment file automatically — the same composition
mechanism used by `-DataStandardVersion` (see "Selecting a Data Standard version" below), so
every phase (configure, provision, and the DMS container itself) sees a consistent engine
selection. Everything else (`SCHEMA_PACKAGES`, Kafka, Keycloak, identity-provider token
endpoints, etc.) still comes from the base environment file; pass `-EnvironmentFile` only to
override those base settings, and the overlay still composes on top of it.

> [!WARNING]
> **SQL Server 2022 volume transition.** `mssql.yml` mounts the major-versioned
> `dms-mssql-2025` volume so an ordinary `docker compose up` never reuses a legacy
> `dms-mssql` volume with the SQL Server 2025 image. Microsoft currently supports persisted
> container upgrades only between release candidates and GA, not from SQL Server 2022 to 2025.
> Existing 2022 volumes remain intact for backup with the SQL Server 2022 image. The current
> `-d -v` teardown removes only the 2025 volume; after stopping the old stack and confirming its
> backups, locate legacy volumes with
> `docker volume ls --filter label=com.docker.compose.volume=dms-mssql` and remove each intended
> volume explicitly with `docker volume rm <volume-name>`.
> See [Microsoft's SQL Server container upgrade guidance](https://learn.microsoft.com/en-us/sql/linux/containers/deploy?view=sql-server-ver17#upgrade-sql-server-in-containers).

> [!NOTE]
> **Database topology.** By default, local deployments host the Configuration Service and DMS in
> one shared physical database on both engines: the Configuration Service `dmscs` schema, and the self-contained
> (OpenIddict) identity stores, live inside the same database as the DMS datastore
> (`POSTGRES_DB_NAME` on PostgreSQL, `MSSQL_DB_NAME` on MSSQL). An opt-in
> `-SeparateConfigDatabase` switch moves the Configuration Service (and the OpenIddict stores)
> into a dedicated `edfi_configurationservice` database instead — see
> "Configuration Service database topology" below.

A few things are specific to the MSSQL path:

* **It is single-engine.** SQL Server hosts everything: the DMS datastore, the Configuration
  Service (Configuration Service SQL Server backend), and the self-contained (OpenIddict) identity stores.
  `mssql.yml` swaps in for `postgresql.yml` - both define the same `db` service that
  `local-config.yml` health-gates on - and no PostgreSQL container runs. In the default
  self-contained flow, `setup-openiddict.ps1 -InitDb` creates the Configuration Service's
  database during the infrastructure phase (the shared datastore database by default; the
  dedicated one under `-SeparateConfigDatabase`); the Configuration Service then deploys its `dmscs` schema before the
  provision phase deploys the DMS relational schema into the datastore database (see
  "Configuration Service database topology" below).
* **Relational backend only.** MSSQL is supported through the relational backend
  (`DMS_DATASTORE=mssql`). Schema is provisioned by `provision-dms-schema.ps1`,
  which auto-detects the SQL Server dialect from the data-store connection string and invokes
  `api-schema-tools ddl provision --dialect mssql --create-database`.
* **CDC is opt-in.** Use the [SQL Server CDC setup](../../reference/cdc-documentation/operations-runbook.md#sql-server-setup)
  for managed Debezium registration; ordinary SQL Server bootstrap does not enable it.
  Projection and CDC prerequisites are distinct under the
  [SQL Server provider contract](../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).
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
  `mcr.microsoft.com/mssql/server:2025-latest` image line it was built and verified against
  (a moving tag tracking the latest SQL Server 2025 build - the coupling is at the SQL Server
  2025 level, not an exact build), the same way the PostgreSQL `.sql` dump is coupled to the
  PostgreSQL major version it was built against.
  This `.bak` coupling is a one-way compatibility boundary.
  A package built and verified on SQL Server 2025 is not expected to restore onto a SQL Server
  2022 instance.
  Packages published previously against SQL Server 2022 do restore forward onto the SQL Server
  2025 runtime.
  Compatibility level 170 is the supported-runtime baseline because it is the SQL Server 2025
  default for newly created databases.
  The template content gates only verify that level; they never set it.
  A database restored from a SQL Server 2022-built package keeps the compatibility level it had
  at backup time; restoring does not upgrade it to 170.
  Every published package is restore-verified in CI
  (`eng/DatabaseTemplates/verify-template-restore.ps1`) before publishing. Base environment files'
  `DATABASE_TEMPLATE_PACKAGE` value (e.g. `EdFi.Api.Populated.Template.PostgreSql.5.2.0`) carries
  the PostgreSQL package id; composing the `.env.mssql` overlay for `-DatabaseEngine mssql`
  rewrites only its engine segment to `MsSql` (`Convert-TemplatePackageToken` in
  `env-utility.psm1`), so the same base `-EnvironmentFile` names the matching package id for
  either engine. This bullet covers the CI build, publish, and verify pipeline; to restore one
  of these packages into a local stack, see
  [Restoring the datastore from a database template](#restoring-the-datastore-from-a-database-template-bootstrap-restore)
  below.

For a self-contained smoke run, set `CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=-1`
in your `.env` before starting or recreating the stack: the Smoke Test Utility requests
a new token for each resource GET and otherwise exceeds the default limit of 15. A self-contained
DMS also proxies each token request to the Configuration Service's `/connect/token`, so also raise
`CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_SOURCE_PERMIT_LIMIT` and
`CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_CLIENT_PERMIT_LIMIT` for the run, or set
`CONFIGURATION_SERVICE_CONNECT_RATE_LIMIT_ENABLED=false`. The rate-limit raise is only needed for stacks
not started from `.env.smoke*` (for example custom or `.env.example`-based stacks), since the tracked
smoke profiles already raise them.
Restore the configured values and recreate the stack after the smoke run.

After the stack is up, run the smoke tests the same way as for PostgreSQL. The script imports its
modules relative to the current directory, so run it from `eng/smoke_test`. For `$key` and `$secret`, see
[Smoke-test credentials](#smoke-test-credentials--addsmoketestcredentials).

```pwsh
cd ../smoke_test
./Invoke-NonDestructiveApiTests.ps1 -BaseUrl "http://localhost:8080" -Key $key -Secret $secret
```

## Smoke-test credentials (`-AddSmokeTestCredentials`)

`-AddSmokeTestCredentials` creates a "Smoke Test Application" (vendor "Smoke Test Vendor", claim set
`EdFiSandbox`) in the Configuration Service for `eng/smoke_test/Invoke-NonDestructiveApiTests.ps1`. The
Configuration Service returns the application's secret **only once**, in its create response, so each
entry point hands it to you in exactly one place.

* **`bootstrap-local-dms.ps1` / `bootstrap-published-dms.ps1`:**
  * The wrapper prints a smoke-test credential summary as its **last** output, on every run shape: Docker-hosted, `-LoadSeedData`, `-InfraOnly`, `-InfraOnly -DmsBaseUrl`, and `-EnableKafkaCdc`.
  * If a later phase fails after the credential was created, it prints the summary under a "created before the failure; the stack is NOT ready" header, then reports the original error. This covers phases that fail with an error. It does not cover **Ctrl+C**: PowerShell stops the pipeline and discards output written while stopping, so an interrupted run prints no summary. If you interrupt a run after its configure phase, the application may exist with no copy of its secret. Remove the "Smoke Test Application" before you run with the flag again.
* **`configure-local-data-store.ps1`:** the structured result gains a `SmokeTestCredential` property (`Key`, `Secret`, `ApplicationName`). Capture it, because the secret is not shown anywhere else:

  ```pwsh
  $configure = ./configure-local-data-store.ps1 -AddSmokeTestCredentials -EnvironmentFile ./.env.ide
  $configure.SmokeTestCredential.Key
  $configure.SmokeTestCredential.Secret
  ```

  Running it without capturing the result prints the whole object, secret included. Treat the result as sensitive: do not log or persist it.
* **`start-published-dms.ps1`** (direct full start, a transitional flag):
  * creates the application after it registers the data stores, for those data stores and the configured tenant (with `-NoDataStore`, for the first existing data store in that tenant);
  * then prints the summary.
  * It does **not** provision the DMS schema. Use it against an already-provisioned database, or use `bootstrap-published-dms.ps1`.
  * With self-contained identity, running `start-published-dms.ps1` again against an already-running provisioned stack keeps the existing active OpenIddict signing key: `setup-openiddict.ps1 -InitDb` creates a key only when none is active. The running DMS therefore accepts newly issued tokens without a restart. For an encryption-key mismatch, see [Signing key on rerun](#signing-key-on-rerun--initdb). Keep the delivered key/secret; do not rerun `-AddSmokeTestCredentials`, because CMS rejects a duplicate Smoke Test Application.

For a completed run with a single route-unqualified data store and no tenant route, the summary includes a
copyable command. Every value is a
single-quoted PowerShell literal, so characters such as `$` in a generated secret stay literal:

```text
--- Smoke test credentials ---
Application: Smoke Test Application
Key:         <key>
Secret:      <secret>
The Configuration Service returns this secret only once. These bootstrap scripts do not write it to logs or files; your terminal, transcripts, or host logging may still retain it. Store it now.
Run the non-destructive API smoke tests:
  Set-Location -LiteralPath '<repo>\eng\smoke_test'
  ./Invoke-NonDestructiveApiTests.ps1 -BaseUrl 'http://localhost:8080' -OAuthUrl 'http://localhost:8080/oauth/token' -Key '<key>' -Secret '<secret>'
```

* **DMS URL not known yet** (`-InfraOnly` without `-DmsBaseUrl`): the command uses a `<dms-base-url>` placeholder for you to replace once the IDE-hosted DMS is healthy.
* **IDE-hosted DMS** (`-InfraOnly`, with or without `-DmsBaseUrl`): whether the summary adds a tenant segment follows `DMS_MULTI_TENANCY` from the env file, but the IDE-hosted DMS reads its own `AppSettings:MultiTenancy` (default `false`). The summary states the value it assumes; set the IDE configuration to match.
* **Tenant routes:** the summary lists the tenant-qualified DMS base and token URLs instead of a command. DMS serves tenant routes only with `DMS_MULTI_TENANCY=true`; a `DMS_CONFIGURATION_SERVICE_TENANT` used only by the Configuration Service does not qualify the URLs. Smoke runs against qualified routes have not been validated with the Smoke Test Utility.
* **Data stores with route contexts** (school-year data stores from `-SchoolYearRange`, or a `-NoDataStore` direct start that selects a data store qualified by `schoolYear`, `districtId` or several contexts): the summary lists each data store's contexts as `key=value`, with no command or URL. Whether and in what order those values appear in the route depends on the target DMS's `ROUTE_QUALIFIER_SEGMENTS` (`AppSettings:RouteQualifierSegments`), which the Configuration Service records do not establish.
* **Self-contained identity:** the summary also reminds you that `CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=-1` and a raised (or disabled) `/connect/*` rate limit must be set **before the stack starts** (see above and `eng/smoke_test/SMOKETEST.md`).

> [!IMPORTANT]
> Pass `-AddSmokeTestCredentials` only once per Configuration Service. The application name is unique
> per vendor, so a later run with the flag against the same Configuration Service fails, even after an
> earlier run failed. That includes a `-NoDataStore` follow-up wrapper run. Omit the flag on follow-up runs,
> or remove the existing "Smoke Test Application" first.

The flag is not honored by `start-local-dms.ps1` (which has no such parameter), by
`start-published-dms.ps1 -InfraOnly`, or by the retained-CDC lifecycle and CDC retry paths of the
wrappers.

## Configuration Service database topology

The Configuration Service — its `dmscs` schema and, in the self-contained flow, the
OpenIddict identity stores — can live in one of two places, on either database engine:

* **Shared (default).** The Configuration Service lives inside the DMS datastore database (`POSTGRES_DB_NAME` on
  PostgreSQL, `MSSQL_DB_NAME` on MSSQL). This is today's behavior, unchanged; omitting the switch
  changes nothing.
* **Separate (opt-in).** Pass `-SeparateConfigDatabase` and the Configuration Service lives in a dedicated
  `edfi_configurationservice` database on the same server container. The database is created
  during the infrastructure phase if absent (`setup-openiddict.ps1 -InitDb` in the self-contained
  flow; the Configuration Service's own startup deploy otherwise). The switch leaves the DMS datastore's selection and
  lifecycle unchanged — ordinary startup and provisioning still create and use the datastore
  database exactly as in shared mode; it just no longer hosts the Configuration Service.

The switch exists on every public entry point and is forwarded unchanged:
`start-local-dms.ps1`, `start-published-dms.ps1`, `bootstrap-local-dms.ps1`,
`bootstrap-published-dms.ps1`, and `build-dms.ps1 StartEnvironment`.

```pwsh
# Turnkey local stack with a dedicated Configuration Service database (either engine)
./bootstrap-local-dms.ps1 -SeparateConfigDatabase
./bootstrap-local-dms.ps1 -DatabaseEngine mssql -SeparateConfigDatabase

# Same through the build script, which lives at the repository root
../../build-dms.ps1 StartEnvironment -SeparateConfigDatabase -DatabaseEngine postgresql
```

### Predicting the effective Configuration Service database

| Invocation shape | Switch omitted | With `-SeparateConfigDatabase` |
|---|---|---|
| `start-local-dms.ps1` / `start-published-dms.ps1`, default or `-InfraOnly` | Datastore database (`POSTGRES_DB_NAME` / `MSSQL_DB_NAME`) | `edfi_configurationservice` |
| `start-published-dms.ps1` with `-IdentityProvider keycloak` and no `-EnableConfig`/`-InfraOnly` | the Configuration Service does not run at all (`published-config.yml` is opt-in on the published flow) | `edfi_configurationservice` — the switch also adds `published-config.yml` to the compose set so Configuration Service actually runs to create it |
| `-DmsOnly`, `-DbOnly`, teardown (`-d [-v]`) | No effect either way: these shapes never start the Configuration Service. The switch is accepted so continuation invocations of a running stack don't have to drop it; the stack keeps the topology it was started with |
| `bootstrap-local-dms.ps1` / `bootstrap-published-dms.ps1` / `build-dms.ps1 StartEnvironment` | Same as the start script they invoke | Same — forwarded unchanged |

The switch moves only the Configuration Service seam: the DMS relational datastore itself and
`DATABASE_CONNECTION_STRING_ADMIN` are unaffected. The configure and provision phases do consume
the topology declaration, though, because each enforces one half of the same rule — in separate
mode every DMS datastore, newly registered or reused, must stay distinct from
`edfi_configurationservice`. `configure-local-data-store.ps1 -SeparateConfigDatabase` judges a
datastore name it is about to register; `provision-dms-schema.ps1 -SeparateConfigDatabase` judges
the database each selected target actually resolves to, which is the only place a *reused* data
store's stored connection string (`-NoDataStore`) is known. The turnkey entry points forward the
switch to both, so you only pass it yourself when running the phases by hand — and a
separate-topology `-InfraOnly` start prints both commands with the switch already on them.

### How the selection travels

The seam is the `CONFIGURATION_SERVICE_DATABASE_NAME` environment variable, which
`CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING`'s `database=` segment references. The checked-in
environment files alias it to the datastore name (shared mode); a `-SeparateConfigDatabase` start
writes a derived environment file under `.derived/` that redirects it to
`edfi_configurationservice` — your source `-EnvironmentFile` is never edited. The derived file
also carries an internal `DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE` marker recording the selection.
When the connection string is entirely absent on PostgreSQL, the Compose fallback in
`local-config.yml` / `published-config.yml` resolves the same seam
(`${CONFIGURATION_SERVICE_DATABASE_NAME:-${POSTGRES_DB_NAME}}`). That fallback is PostgreSQL-shaped — Compose
interpolation cannot branch on the engine — so MSSQL always requires an explicit
`CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING` (the `.env.mssql` overlay supplies it), and an MSSQL run
with the key entirely absent fails clearly rather than starting the Configuration Service against an unusable fallback.

Invocations that start the Configuration Service validate the result before any container starts: the resolved connection
string's database, host, and port must agree with the selected topology, and a hand-edited
`CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING` that disagrees fails fast with a
`Configuration Service database topology mismatch` error rather than silently starting the Configuration Service against the wrong
database. In separate mode the datastore database must also be genuinely distinct from
`edfi_configurationservice`, so a datastore name (or `-DataStoreDatabaseName`) that would land in it
is rejected rather than quietly re-sharing the database the switch exists to split.

What counts as a collision follows the whole path a name travels to the database server, so the
inputs are not checked identically — and the two engines answer at different moments: PostgreSQL's
rules are exact models of its creation mechanisms and are applied before any container starts, while
SQL Server's answer can only come from the running instance itself.

`POSTGRES_DB_NAME` reaches `postgresql-init.sh`, which passes it to `createdb` as a single quoted
command argument. `createdb` quotes the identifier, so the database it creates is named the literal
value and only `edfi_configurationservice` itself is rejected: a mixed-case name such as
`EDFI_ConfigurationService`, or one differing only by surrounding whitespace, is a genuinely separate
database you may use.

`-DataStoreDatabaseName` takes a different route, and differs in exactly one respect. It is serialized
into the data-store record's connection string, that string is parsed again before use, and SchemaTools
then creates the database with a *quoted* identifier — so here too only `edfi_configurationservice`
itself is rejected. The one exception is a trailing newline: connection-string serialization and parsing
can collapse it, so such a name is rejected because the server really would receive the reserved
database, whereas `createdb` preserves it and the initialized path treats it as distinct.

On SQL Server, quoting decides nothing, and no name is pre-judged offline: database-name identity is
decided by the *instance's* collation semantics, whose equivalences reach far beyond letter case and
trailing spaces — measured examples that all resolve to the reserved database under the default
collation include full-width letterforms, the `fi` ligature, and characters with no collation weight
at all — and which differ between instances: a case variant that collides under the default
collation is a genuinely distinct database on a case-sensitive one. The only entity that can answer
is the SQL Server that will host these databases, so *every* SQL Server start that runs the
Configuration Service verifies its database names against the running instance itself: after the
database container starts and reports ready, and before OpenIddict, the Configuration Service, DMS, or the data-store
registration touch it, the names are checked on the server. In shared mode the check is that the
Configuration Service's target — the `CONFIGURATION_SERVICE_DATABASE_NAME` seam and every `Database` /
`Initial Catalog` segment of its connection string — really is the DMS datastore database. In
separate mode the same targets must be the dedicated `edfi_configurationservice`, and the datastore
name — plus any `-DataStoreDatabaseName`, as the value the provider actually receives — must be
distinct from it. All SQL Server name verdicts happen there. Valid Unicode names are fully
supported and no longer require renaming: a name is refused only when the instance itself reports
the wrong relationship, or when the verification cannot be completed, in which case the start fails
closed rather than proceeding unverified. A verification failure names the offending key or
parameter and never prints the configured value.

Shapes that never start the Configuration Service — `-DmsOnly`, `-DbOnly`, teardown, and a bare
published Keycloak start that omits `published-config.yml` — get structural screening only, and
only on SQL Server: every connection string the `.env.mssql` overlay owns must be present,
non-blank, and carry a SQL Server data-source keyword (`Server=`, `Data Source=`, `Address=`,
`Addr=`, or `Network Address=`) at a segment boundary. That is the entire check. The value is not
parsed with ADO.NET, no database segment is discovered or validated, and no host or port is
inspected. On PostgreSQL these shapes get no engine-composition check at all. No SQL Server name is
judged for any of them: `-DbOnly` does start a database server, but the Configuration Service takes
no part in these shapes, so Configuration Service topology consistency is not evaluated — and where nothing is
running there is no instance to ask, and no offline rule can answer in its place.

To continue the manual phases from a `-SeparateConfigDatabase -InfraOnly` start, declare the same
topology to **both** datastore phases — the start script prints each command with the switch already
on it in its next-step guidance:

```pwsh
./configure-local-data-store.ps1 -SeparateConfigDatabase
./provision-dms-schema.ps1 -SeparateConfigDatabase
```

Each re-derives the same effective environment the start wrote — the topology marker lives only in
the derived file — and each enforces one half of the same rule.
`configure-local-data-store.ps1` checks the name it is about to register, on SQL Server against the
running instance and on PostgreSQL against the reserved name as the provider parses it, before it
creates any Configuration Service state. `provision-dms-schema.ps1` checks the database each
selected target actually resolves to, after placeholder resolution and decryption of the stored
connection string and before SchemaTools runs — the only point at which a *reused* data store
(`configure-local-data-store.ps1 -NoDataStore`) reveals where its schema would land, since that run
registers no name for the configure phase to judge. Omit the switch and both phases behave exactly
as they do for a shared-topology stack.

Switching an existing stack between modes re-points the Configuration Service but does not move data: `dmscs` content
already written in one database stays there. For a clean switch, tear down with `-d -v` and start
fresh, or migrate the Configuration Service data yourself.

## Restoring the datastore from a database template (bootstrap restore)

`-RestoreTemplate Minimal|Populated` on either bootstrap wrapper replaces the DMS datastore
from a trusted database-template package instead of provisioning an empty schema. The flow is
deliberately paranoid: the exact `.nupkg` bytes are authenticated against the template trust
policy (fail-closed - there is no unsigned or local-origin bypass), the package's restore
manifest is cross-checked against a freshly prepared candidate workspace, the artifact is first
replayed into a throwaway scratch database and validated (schema inventory, effective-schema
facts, physical baseline, privileges), and only then is the target database replaced - behind
stop proofs, with the `.bootstrap` workspace committed as a whole tree. The previous tree is
renamed aside and renamed back if publishing the candidate fails, and a schema tool published to
`.bootstrap/tools/api-schema-tools` is carried into the new workspace. `provision-dms-schema.ps1`
is skipped in restore mode: the restored artifact already carries the schema and data.

```powershell
# Restore the Populated template from the configured feed
# (requires DATABASE_TEMPLATE_NUGET_VERSION in the effective env; never floats to latest)
./bootstrap-local-dms.ps1 -RestoreTemplate Populated

# Restore from a local directory holding exactly one matching template .nupkg
# plus its sibling .nupkg.attestation.json (no NuGet version key needed)
./bootstrap-local-dms.ps1 -RestoreTemplate Minimal -PackageDirectory C:\path\to\packages

# Same through the build script, which lives at the repository root
../../build-dms.ps1 StartEnvironment -RestoreTemplate Populated

# Optional supplemental seed AFTER the restored stack is up (explicit source required;
# a bare -LoadSeedData is rejected in restore mode - the template already carries data)
./bootstrap-local-dms.ps1 -RestoreTemplate Minimal -LoadSeedData -SeedTemplate Minimal
```

The tracked `template-trust-policy.json` currently lists no trusted producers, so the feed
example above refuses every package for now. That empty policy is intentional fail-closed
behavior: a feed restore requires a package attested by a producer configured as a trust anchor
in the tracked policy or the local overlay. Older packages that lack the restore manifest and
attestation are not eligible for `-RestoreTemplate`. The database engine could still read their
`.sql` or `.bak` artifact, but engine-level backup compatibility does not make a package eligible
for the guarded bootstrap restore.

Restore works on both database engines (`-DatabaseEngine`) and on both wrappers
(`bootstrap-local-dms.ps1` and `bootstrap-published-dms.ps1`). It rejects `-InfraOnly`,
`-NoDataStore`, and `-SchoolYearRange`: one restore invocation replaces exactly one
route-unqualified physical target. That target is the effective env's `POSTGRES_DB_NAME` or
`MSSQL_DB_NAME`; an explicit `-DataStoreDatabaseName` must repeat it, and a different name is
refused before the package is resolved or anything is restored, in either Configuration Service
topology. A PostgreSQL restore is also refused while `POSTGRES_USE_TMPFS=true`: the database would
live in container memory and be lost when the stack restarts the database container after the
restore.
These refusals also happen before anything is restored:

* `-EnableKafkaCdc`: a restore skips the provision phase that produces the CDC provisioning
  receipt. Enable CDC on a bootstrap that provisions its own schema.
* `-IncludeE2EClaimSets` (an option of the shared `Invoke-BootstrapWrapper` that the Kafka CDC
  E2E setup passes; the wrapper scripts do not expose it): the restore candidate stages claims
  without the test-only E2E claim sets, so they would be silently dropped.
* `-Rebuild` on `bootstrap-local-dms.ps1`: it reaches only the start that follows the target
  replacement. A restore that needs an image build runs through `build-dms.ps1 StartEnvironment
  -RestoreTemplate`, which builds the image after package and candidate validation and before
  database preflight.
* `-d` (with or without `-v`) together with `-RestoreTemplate` or `-PackageDirectory`: teardown
  and restore are separate invocations, and nothing is torn down.
* A retained CDC deployment for the wrapper's compose project (`dms-local` or `dms-published`):
  the request is refused before the retained deployment's lifecycle runs. Retire that deployment
  with `-d -v` first.

The whole-tree commit also refuses to replace an active `.bootstrap` workspace that holds retained
CDC configuration or state for either compose project; that refusal comes after scratch validation,
before the target is touched.

The package identity comes from the effective `DATABASE_TEMPLATE_PACKAGE`, with the
`Minimal|Populated` and engine segments swapped to match the request. Setting it in your base
`.env` does not always decide the effective value:

* A `DATABASE_TEMPLATE_PACKAGE` in the process environment wins over every env file, as it does
  for Compose.
* `bootstrap-local-dms.ps1` always layers the local-bootstrap Data Standard overlay
  (`.env.bootstrap.ds52` or `.env.bootstrap.ds61`, chosen by `-DataStandardVersion`, default `5.2`)
  over `-EnvironmentFile`. That overlay sets `DATABASE_TEMPLATE_PACKAGE`, so the base file's value
  is replaced and the package id's Data Standard segment follows `-DataStandardVersion`.
* `bootstrap-published-dms.ps1` layers the overlay only when `-DataStandardVersion` is passed;
  otherwise the base env file's value is used as written.

The overlays do not set `DATABASE_TEMPLATE_NUGET_VERSION` or `DATABASE_TEMPLATE_FEED_URL`, so those
come from the base env file (or the process environment). See `.env.example` for the three version
concepts (package ID's Data Standard segment, the manifest's `dataStandardVersion`, and
`DATABASE_TEMPLATE_NUGET_VERSION` - the package's own NuGet version). Trusted producers are declared in `template-trust-policy.json` plus the git-ignored
`template-trust-policy.local.json` overlay; a locally built package must be attested by a
development producer registered there (see the next section).

### Building a dev-signed package locally

Restore never accepts an unattested package, so a package built on your machine must be signed
by a producer the trust policy knows. These are the commands the
`tests/Invoke-BootstrapRestoreSmoke.ps1` smoke runs. Run them in one PowerShell session, starting
from `eng/docker-compose`, and choose the template kind and package directory once; every later
step reads them.

```powershell
$kind = 'Minimal'   # or 'Populated'; selects the seed, settings file, package, and restore
$packageDirectory = 'C:\path\to\packages'
```

1. Register a development signer, once per machine. The private key lands in the git-ignored
   `eng/DatabaseTemplates/.dev-trust/` directory, and the public half is registered as the
   `local-dev` producer in the git-ignored `template-trust-policy.local.json` overlay. An
   existing key file or producer entry is never overwritten.

   ```powershell
   ../DatabaseTemplates/new-template-dev-trust.ps1 -Purpose Dev
   ```

2. Bring up a source stack seeded with the same template kind and a dedicated Configuration
   Service database. The package build refuses a source database that also holds the
   Configuration Service `dmscs` schema or the OpenIddict stores, which is where the default
   shared topology puts them. Add the same `-DatabaseEngine`, `-DataStandardVersion`, and
   `-EnvironmentFile` you will restore with.

   ```powershell
   ./bootstrap-local-dms.ps1 -SeparateConfigDatabase -LoadSeedData -SeedTemplate $kind
   ```

3. Build and attest the package. `Template-Management.psm1` resolves its `../` module imports
   and `Build-TemplateNuGetPackage` writes its outputs relative to the current directory, so
   import and run both from `eng/DatabaseTemplates`. `-StandardVersion` is the Data Standard
   segment of the package id (`5.2.0` or `6.1.0`); `-PackageVersion` is the package's own NuGet
   version. Use `-DatabaseEngine mssql` for SQL Server, and `-DatabaseName` when the env file
   changes `POSTGRES_DB_NAME` or `MSSQL_DB_NAME`. `-SchemaFilePath` names the ApiSchema files the
   source stack was provisioned from; the build refuses a source holding any object their DDL
   does not define. The source stack's staged workspace lists them.

   ```powershell
   $schemaFiles = & (Import-Module ./bootstrap-restore.psm1 -PassThru) {
       (Get-RestoreCandidateSchemaFact -CandidateDirectory (Resolve-Path ./.bootstrap).Path).SchemaFilePaths
   }
   Push-Location ../DatabaseTemplates
   Import-Module ./Template-Management.psm1 -Force
   Build-TemplateNuGetPackage `
       -ConfigFilePath "./${kind}TemplateSettings.psd1" `
       -TemplateKind $kind `
       -StandardVersion 5.2.0 `
       -PackageVersion 1.0.999 `
       -DumpAllUserSchemas `
       -DatabaseEngine postgresql `
       -SchemaFilePath $schemaFiles `
       -AttestationSignerKeyPath ./.dev-trust/local-dev.pem `
       -AttestationProducer local-dev
   Pop-Location
   ```

4. Move the package and its sibling attestation into the package directory, which must hold no
   other template package, and delete the other build outputs (the `.sql` or `.bak` dump and
   the `.csproj` are not git-ignored).

   ```powershell
   $packages = New-Item -ItemType Directory -Path $packageDirectory -Force
   Get-ChildItem ../DatabaseTemplates -File -Filter "EdFi.Api.$kind.Template.*" | ForEach-Object {
       if ($_.Extension -in '.nupkg', '.json') { Move-Item -LiteralPath $_.FullName -Destination $packages }
       else { Remove-Item -LiteralPath $_.FullName }
   }
   Remove-Item ../DatabaseTemplates/restore-manifest.json -ErrorAction SilentlyContinue
   ```

   The directory then holds the template `.nupkg`, its `.nupkg.attestation.json`, and the
   companion `.Attestation.` package, which restore ignores.

5. Stop the source stack (volumes are kept), then restore the same kind. Pass the same
   infrastructure flags to both commands.

   ```powershell
   ./bootstrap-local-dms.ps1 -d
   ./bootstrap-local-dms.ps1 -RestoreTemplate $kind -PackageDirectory $packageDirectory
   ```

### Restoring into a stack with a separate Configuration Service database

With `-SeparateConfigDatabase` the restore replaces only the DMS datastore; the dedicated
`edfi_configurationservice` database is never touched, so on a stack that ran before it still holds
the data stores earlier runs registered - for this target, and for other targets restored by earlier
explicit invocations (each invocation restores one database). After the restore the wrapper's
configure phase lists the Configuration Service data stores and decrypts each stored connection
string with `CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY`. A data store serves the restored target
only when its engine (provider and connection-string form), its server and port (the composed
database service, `dms-postgresql:5432` or `dms-mssql,1433`), and its database (exactly the
restored database) all match; a matching database name alone is not enough. The phase then:

* reuses the one route-unqualified data store that serves the restored target, with its id and
  without changing it;
* otherwise registers a new data store for the restored target under the first free name of
  `Local Development Data Store`, `Local Development Data Store (2)`, and so on. A different
  `-DataStoreDatabaseName` never gets this far: it is refused before anything is restored;
* leaves every other target's data store in place, and returns only the restored target's id;
* refuses what it cannot verify: two data stores serving the target, a route-qualified one, a
  letter-case variant of the target's name, a stored value of this engine that cannot be decrypted
  or read, or a listing that reaches the 500-item query page.

Restoring database A, then B, then A again therefore reuses A's registration on the third run, and B
keeps its own.

This check runs **after** the target database was replaced. A refusal here stops before DMS starts,
but unlike a scratch-validation refusal (next section) it does not leave the target untouched: the
target already holds the restored package. Correct or remove the stale data store registration in
the Configuration Service, then rerun the restore. The refusal names the data store id and the
property that differs; it never prints the connection string, its credentials, or the database
names.

### Recovering from a failed scratch validation

When scratch validation refuses a package, the restore drops the scratch and preflight
databases, stops the database-only slice it started (with the same compose files and env file,
never `down` and never volumes), removes its candidate workspace and generated env file, and leaves
the target database and the active `.bootstrap` workspace as they were. The Configuration Service
and DMS are not started. Each cleanup step runs even when another fails, and the error that ended
the run stays the error; a step that fails is reported as a warning naming what remains. The same
cleanup runs for any failure after the database-only slice started, including a partial startup.

If that stop did not complete, the database container keeps running and the next restore attempt
refuses at its first stop proof. On the local PostgreSQL stack the refusal reads as follows; the
container is `dms-mssql` on SQL Server, and the project is `dms-published` for
`bootstrap-published-dms.ps1`:

```text
Compose project 'dms-local' still has running containers: dms-postgresql. Stop the stack before the restore touches the bootstrap workspace or the target database.
```

To recover, stop the stack **without** `-v`, then retry with a package that passes:

```powershell
# Keeps the data volumes and the active .bootstrap workspace; pass the same
# -DatabaseEngine, -EnvironmentFile, and other infrastructure flags as the restore
./bootstrap-local-dms.ps1 -d
./bootstrap-local-dms.ps1 -RestoreTemplate Minimal -PackageDirectory C:\path\to\packages
```

Do not use `-d -v` here: it deletes the data volumes, including the untouched target database,
and removes the active `.bootstrap` workspace.

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

> **Requirement - .NET 10 SDK:** When no eligible `api-schema-tools` executable is available,
> `prepare-dms-schema.ps1` automatically publishes the in-repo CLI as a native executable. This
> adds publish time on the first run. After changing the tool or its dependencies, including by
> switching branches, refresh the cached tool with the command below or delete
> `.bootstrap/tools/api-schema-tools` so the next bootstrap can resolve it again. The cache takes
> precedence over build output, so building the solution alone does not refresh it. Teardown with
> `-d -v` also clears the cache.

```pwsh
# Optional: manually refresh the cached api-schema-tools executable
$schemaToolProject = "../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
$schemaToolOutput  = ".bootstrap/tools/api-schema-tools"
dotnet publish $schemaToolProject -c Release -p:UseAppHost=true -o $schemaToolOutput
```

To use an executable outside the discovered locations, set `DMS_SCHEMA_TOOL_PATH` (or pass
`-SchemaToolPath` to `prepare-dms-schema.ps1`). Explicit paths take precedence and must exist;
an invalid override fails without automatic publishing. Clear the environment override with
`Remove-Item Env:DMS_SCHEMA_TOOL_PATH` to restore automatic resolution.
Use the environment override when preparation and provisioning must use the same external tool;
`-SchemaToolPath` selects the tool only for the prepare phase.

Standard mode (omit `-ApiSchemaPath`) stages the packages listed by the env file's
`SCHEMA_PACKAGES` value; invoked without `-EnvironmentFile` (as below) it falls back to the
catalog-pinned core-only default. There is no `-Extensions` parameter.

```pwsh
./prepare-dms-schema.ps1
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
`SCHEMA_PACKAGES` in-line when no workspace is staged, then starts the stack. If an existing
standard workspace has a different recorded package identity, the wrapper fails fast before
package downloads or Docker/CMS side effects; stop the stack and remove `.bootstrap/` before
retrying. Expert filesystem workspaces are reused as staged. The wrapper resolves the schema tool
from eligible existing executables or publishes it from source when needed. Set
`DMS_SCHEMA_TOOL_PATH` to select an explicit executable.

```pwsh
./bootstrap-local-dms.ps1
```

> [!NOTE]
> Standard mode is driven by the effective env file's `SCHEMA_PACKAGES` value: prepare resolves
> and stages every listed package (core plus any extensions, each at its own pinned version) so
> the staged `.bootstrap/ApiSchema` workspace's effective schema hash matches what the DMS
> container downloads at startup. Only when prepare runs without an env file (direct diagnostic
> invocation with no `-EnvironmentFile`, or an env file lacking `SCHEMA_PACKAGES`) does it fall
> back to the catalog-pinned core-only default (`EdFi.DataStandard52.ApiSchema` at `1.0.335`).

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
./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema
./prepare-dms-claims.ps1 [-ClaimsDirectoryPath <directory-with-custom-extension-claims-fragment>]
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

`prepare-dms-claims.ps1` stages `*-claims.json` fragments into
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

For managed CDC bootstrap, follow the repository-root
[PostgreSQL](../../reference/cdc-documentation/operations-runbook.md#postgresql-setup)
or [SQL Server](../../reference/cdc-documentation/operations-runbook.md#sql-server-setup)
procedure. Both require separate Configuration Service topology, a dedicated new database, protected
complete settings and an original managed state root. The
[SchemaTools reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#bootstrap-cdc-handoff)
owns command/configuration details; the
[enablement owner](../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence)
defines writer admission. The manual schema-only flow above does not perform that admission.

The shipped CLI profile is local single-worker/single-broker and reports
`aclIsolationProven: false`. Other deployments require their own live authority adapters
and evidence under the [topology](../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup)
and [security](../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations)
owners. Changing profile tokens does not install those capabilities.

For an existing managed deployment, use [state preservation](../../reference/cdc-documentation/operations-runbook.md#deployment-state),
[shutdown/startup](../../reference/cdc-documentation/operations-runbook.md#managed-lifecycle)
and [destructive teardown](../../reference/cdc-documentation/operations-runbook.md#stack-teardown).
Original controller roots, `.cdc-deployments` inventory and retained `.bootstrap/cdc-runtime`
settings are governed by the [state-continuity contract](../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral).
Deleting a workspace is not a CDC recovery procedure.

The DMS E2E setup wrappers use the `SCHEMA_PACKAGES` flow.
Those env files use `USE_API_SCHEMA_PATH=true` to download and materialize
file-based ApiSchema package content. The non-CDC path clears stale disposable bootstrap
inputs before startup; managed CDC settings/history are retained. Use the
[DMS E2E CDC variant](../../reference/cdc-documentation/operations-runbook.md#dms-e2e-setup)
for its environment/schema/target selection and teardown requirements under the
[local bootstrap contract](../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).
This does not add CDC support to the Instance Management E2E setup.

For a non-CDC workspace, if `prepare-dms-schema.ps1` or `prepare-dms-claims.ps1` fail with a
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
`-EnableKafka`, `-EnableSwaggerUI`). The published bootstrap wrapper also accepts
`-d -v` for the options it supports. Running the `dms-local` recovery instead would leave the
published stack up while deleting the workspace its containers bind-mount.

> **Note on `-RemoveBootstrap`:** `./bootstrap-local-dms.ps1 -d -v` removes the
> disposable `.bootstrap/` inputs for you — it delegates to
> `start-local-dms.ps1 -d -v -RemoveBootstrap`. Invoking the start scripts
> directly is different: by default `./start-local-dms.ps1 -d -v` and
> `./start-published-dms.ps1 -d -v` do **not** delete the `.bootstrap/`
> workspace. Pass `-RemoveBootstrap` explicitly for disposable non-CDC inputs
> (e.g. after a branch switch). Managed CDC teardown preserves source history,
> custom settings and protected nested roots; eligible inventoried generated settings
> can be removed through the [governed teardown procedure](../../reference/cdc-documentation/operations-runbook.md#stack-teardown).
> Neither this switch nor E2E teardown authorizes recursive removal of protected state.

## IDE Debugging Workflow

Use the IDE debugging workflow when you want to run DMS from your IDE (e.g. Visual Studio or Rider)
against Docker-managed infrastructure (PostgreSQL, Configuration Service, Keycloak/OpenIddict).

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
| `ConfigurationServiceSettings:BaseUrl` | `http://localhost:8081` (Configuration Service host port) |
| `ConfigurationServiceSettings:ClientId` | `CMSReadOnlyAccess` (created by identity setup) |
| `ConfigurationServiceSettings:ClientSecret` | `<local-cms-readonly-secret>` (replace with the `CMSReadOnlyAccess` client secret; local-dev default `ValidClientSecret1234567890!Abcd`) |
| `ConfigurationServiceSettings:Scope` | `edfi_admin_api/readonly_access` |
| `ConfigurationServiceSettings:EncryptionKey` | `<dms-config-database-encryption-key>` (replace with value of `CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY` from `.env`; `.env.example` default `secret!_32_chars_xxxxxxxxxxxxxxx`) |
| `AppSettings:ApiSchema:UseCustomPath` | `true` (use staged bootstrap workspace schema; see activation note below) |
| `AppSettings:ApiSchema:Path` | `<repo-root>/eng/docker-compose/.bootstrap/ApiSchema` (replace `<repo-root>` with your absolute path) |
| `AppSettings:AuthenticationService` | `http://localhost:8081/connect/token` |
| `JwtAuthentication:Authority` | `http://localhost:8081` (must exactly match the self-contained Configuration Service issuer configured in `.env.ide`) |
| `JwtAuthentication:MetadataAddress` | `http://localhost:8081/.well-known/openid-configuration` (must share the advertised JWKS origin) |
| `JwtAuthentication:RequireHttpsMetadata` | `false` (local http Configuration Service; the shipped default is `true`, see [docs/CONFIGURATION.md](../../docs/CONFIGURATION.md#jwtauthentication-parameters-in-appsettingsjson-dms-api-service)) |
| `JwtAuthentication:ClientRole` | `dms-client` |
| `JwtAuthentication:RoleClaimType` | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` |

Replace `<local-cms-readonly-secret>` with the `CMSReadOnlyAccess` client secret. Identity setup
(`setup-keycloak.ps1` / `setup-openiddict.ps1`) creates the client with the local-dev default
`ValidClientSecret1234567890!Abcd` and does not print the value; if you override the secret at
client creation, use your override here. Replace `<dms-config-database-encryption-key>`
with the value of `CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY` from your `.env` file (`.env.example`
default `secret!_32_chars_xxxxxxxxxxxxxxx`). Replace `<repo-root>` with the absolute path
to the repository root on your machine.

For the self-contained IDE workflow, create a dedicated environment file before running bootstrap:

```pwsh
$baseEnvironmentFile = if (Test-Path .env) { ".env" } else { ".env.example" }
Copy-Item $baseEnvironmentFile .env.ide
```

Use [.env.ide.example](.env.ide.example) as the documented override template. In `.env.ide`, set both
values below to the host-reachable Configuration Service origin:

```dotenv
SELF_CONTAINED_DMS_JWT_AUTHORITY=http://localhost:8081
SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS=http://localhost:8081/.well-known/openid-configuration
```

Pass `-EnvironmentFile ./.env.ide` to every bootstrap invocation for that IDE environment. The
self-contained startup uses these values for the Configuration Service issuer and DMS JWT settings, so metadata and
the advertised JWKS URL share the `localhost:8081` origin without a hosts-file entry. Use this file
for every manual phase, but only combine it with `-InfraOnly` when invoking a DMS start or bootstrap
command; a containerized DMS cannot reach the Configuration Service through its own `localhost`. Keep
`ConfigurationServiceSettings:BaseUrl` and `AppSettings:AuthenticationService` on `localhost:8081`
for their separate host-side calls.

> **Activation note:** `AppSettings:ApiSchema:UseCustomPath` and `AppSettings:ApiSchema:Path`
> point at the staged bootstrap workspace. With `ApiSchema:UseCustomPath=true`, DMS reads discovery/specification
> JSON and XSD content from the staged workspace via the bootstrap asset manifest, with no
> schema assemblies loaded on this path.

### Pre-DMS infrastructure setup

Run the bootstrap wrapper with `-InfraOnly` to start infrastructure, provision the schema, and
stop before launching DMS:

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -EnableConfig -IdentityProvider self-contained -EnvironmentFile ./.env.ide
```

The wrapper prints IDE next-step guidance (staged schema path and, when `DMS_CONFIGURATION_SERVICE_CLIENT_*`
keys are set in the env file, `CMSReadOnlyAccess` client id and scope — never the secret value)
after provisioning completes. With `-AddSmokeTestCredentials`, it ends with the
[smoke-test credential summary](#smoke-test-credentials--addsmoketestcredentials).

### Two IDE workflow shapes

**Shape 1 — Pre-DMS stop (terminal):** `-InfraOnly` alone starts infrastructure, creates or confirms the
DMS instance in Configuration Service, provisions the schema, prints IDE configuration guidance, then stops.
Start DMS in your IDE using the printed settings; this invocation does not wait for it.

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -IdentityProvider self-contained -EnvironmentFile ./.env.ide
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
> so the configure phase reuses the existing data store; without it, the configure phase registers
> the same name again and the Configuration Service rejects it with 400:
> `./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl <url> -NoDataStore -EnvironmentFile ./.env.ide [-LoadSeedData ...]`
> Do not repeat `-AddSmokeTestCredentials` on that follow-up run: the Configuration Service rejects a
> second "Smoke Test Application" for the same vendor.
>
> `-NoDataStore` supports exactly one existing route-unqualified data store. If the earlier run
> used `-SchoolYearRange` (or otherwise created route-qualified data stores), do **not** re-run the
> wrapper — re-supplying `-SchoolYearRange` tries to create these data stores again, and the Configuration Service rejects
> the repeated names with 400. Use the explicit phase commands against the data stores the earlier run
> created: `./start-local-dms.ps1 -InfraOnly -DmsBaseUrl <url> -EnvironmentFile ./.env.ide` for the health wait, then
> `./load-dms-seed-data.ps1 -DmsBaseUrl <url> -SchoolYear <years...> -EnvironmentFile ./.env.ide` for seed loading.
>
> A fresh run recomposes the environment from its own switches, so if the stack was started with
> `-SeparateConfigDatabase`, carry that switch on every follow-up command too — dropping it
> continues a separate-mode stack in shared mode. Both terminal guidance blocks print the switch
> for you when it applies.

```pwsh
cd eng/docker-compose
./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198" -IdentityProvider self-contained -EnvironmentFile ./.env.ide
# → starts infra, provisions schema, waits for DMS at http://localhost:5198/health
# Start DMS in your IDE before the 300-second timeout elapses.

# With seed loading:
./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198" -IdentityProvider self-contained -EnvironmentFile ./.env.ide `
    -LoadSeedData -SeedTemplate Minimal
```

**Manual phase flow:** When running phases individually, complete configure and provision before invoking
the health wait, then start DMS in the IDE between phases:

```pwsh
cd eng/docker-compose
./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema
./prepare-dms-claims.ps1
./start-local-dms.ps1 -InfraOnly -IdentityProvider self-contained -EnvironmentFile ./.env.ide
$configure = ./configure-local-data-store.ps1 -AddSmokeTestCredentials -EnvironmentFile ./.env.ide
$configure.SmokeTestCredential  # Key, Secret, ApplicationName - returned only once; store them now
./provision-dms-schema.ps1 -EnvironmentFile ./.env.ide
# Start DMS in your IDE now.
./start-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198" -EnvironmentFile ./.env.ide  # post-provision health wait only
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

1. **Get Configuration Service Token** - Authenticates with the Configuration Service
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

**Check Configuration Service logs:**

```powershell
docker logs ed-fi-api-config-service --follow
```

### Cleanup

To tear down the environment:

```powershell
cd eng/docker-compose
pwsh ./start-local-dms.ps1 -d -v
```

## Loading plugins

A plugin is an already-published directory of assemblies that DMS loads at startup.
DMS ships no fetcher: getting a plugin directory under the plugin root is a
deployment step that happens before the process starts, and it comes in two
recipes. Each is an overlay added with its own `-f`, and they are **alternatives**
rather than additions: `/app/plugins` is one mount target and two overlays cannot
both claim it. `local-dms.yml` and `published-dms.yml` are unchanged by either.

Add the overlays by naming them in `DMS_PLUGINS_COMPOSE_FILES` and starting the
stack through `bootstrap-local-dms.ps1`. That variable is a semicolon-delimited
list, added in the order written, each entry either absolute or relative to
`eng/docker-compose`. Leave it unset and nothing changes; a path that does not
exist, or an empty entry from a stray separator, fails the launcher by name before
it touches Docker.

**`DMS_PLUGINS_COMPOSE_FILES` is read from the environment file only.** The launcher
reads it the same way it reads every other setting that shapes the compose command,
and that reader deliberately ignores the process environment so a phase invoked
directly cannot depend on ambient shell state. Exporting the variable therefore adds
no overlay at all, and DMS starts successfully with no plugins. Put it in the
environment file `bootstrap-local-dms.ps1` uses, which is `.env` unless you pass
`-EnvironmentFile`. The four variables the overlays themselves declare are
interpolated by Compose rather than read by the launcher, so those do honour an
exported value; keeping all of them in one file is still the recipe below, because a
deployment described in two places is a deployment nobody can read back.

The launcher is the entry point rather than a convenience. An overlay only adds a
mount and a fetch step to the DMS service; it provisions no schema and configures
no Configuration Service, and DMS validates at startup that both were done. A bare
`docker compose up` of `postgresql.yml` and `local-dms.yml` with an overlay added
therefore cannot produce a working deployment on a fresh environment. Compose those
files directly only against an environment already provisioned and configured by a
previous `bootstrap-local-dms.ps1` run.

### Recipe 1: a pre-populated plugin root

`plugins-dms.yml` bind-mounts a directory of plugin directories read-only.

```powershell
cd eng/docker-compose

# In the environment file the launcher reads (.env by default):
#   DMS_PLUGINS_COMPOSE_FILES=plugins-dms.yml;my-plugins-allowed-dms.yml
#   DMS_PLUGINS_MOUNT_SOURCE=C:/plugins

pwsh ./bootstrap-local-dms.ps1
```

Nothing in this recipe writes into the mounted directory. The plugin directories are
whatever you put there, so replacing one is your own step and DMS reads the bytes
that are there when it starts.

### Recipe 2: a pinned package fetched by the deployment

`plugins-fetch-dms.yml` adds a one-shot `fetch-plugins` service that downloads a
`.nupkg`, verifies it against a digest the operator pinned, extracts the plugin
directory into a named volume and exits. `depends_on: service_completed_successfully`
orders it ahead of DMS, which then mounts that volume read-only.

```powershell
cd eng/docker-compose

# In the environment file the launcher reads (.env by default):
#   DMS_PLUGINS_COMPOSE_FILES=plugins-fetch-dms.yml;my-plugins-allowed-dms.yml
#   PLUGIN_PACKAGE_URL=https://feed.example/v3-flatcontainer/acme.dms.identity/1.2.0/acme.dms.identity.1.2.0.nupkg
#   PLUGIN_PACKAGE_SHA256=9f2c...the digest you pinned...
#   PLUGIN_NAME=Acme.Dms.Identity

pwsh ./bootstrap-local-dms.ps1
```

`PLUGIN_PACKAGE_URL` is the package's download address in the feed's
`PackageBaseAddress` form, `<base>/<id>/<version>/<id>.<version>.nupkg` in lower
case, where `<base>` is what the feed's `index.json` publishes for that resource.
It differs between feed hosts, which is one more reason the address belongs to the
deployment rather than to DMS.

#### Re-fetching requires stop, fetch, start

**Stop DMS before fetching again.** The fetcher clears and rewrites the plugin
directory inside the volume DMS mounts, and `service_completed_successfully` orders
container *startup*; it does not keep a completed one-shot service away from the
files of a container that is already running. Re-running the launcher against a
stack that is already up starts the fetcher while DMS keeps running on the same
volume, and because assemblies load lazily, a request arriving afterwards can find
a file missing or load a dependency from a version other than the one the startup
inventory recorded.

Tear the stack down and bring it back up, which is what makes the inventory event a
statement about the bytes the process is serving:

```powershell
cd eng/docker-compose
pwsh ./bootstrap-local-dms.ps1 -d
pwsh ./bootstrap-local-dms.ps1
```

`-d` without `-v` is deliberate. What a re-fetch needs is DMS stopped, and `down`
alone does that; `-v` would additionally delete every volume in the stack, the
PostgreSQL data volume included, so a routine plugin update would destroy the
deployment's data. Keeping the `plugins` volume is safe, because the fetcher clears
the plugin directory before it extracts. Add `-v` only when you actually want the
whole environment reset.

### Required variables

All four are declared with `:?` rather than defaults, so composing an overlay in
without setting its variables fails immediately instead of starting a deployment
that silently loads nothing. They are listed, commented out, in `.env.example`.

| Variable | Overlay | Meaning |
| -------- | ------- | ------- |
| `DMS_PLUGINS_MOUNT_SOURCE` | `plugins-dms.yml` | Host path holding the plugin directories. |
| `PLUGIN_PACKAGE_URL` | `plugins-fetch-dms.yml` | `.nupkg` download address, `PackageBaseAddress` form. |
| `PLUGIN_PACKAGE_SHA256` | `plugins-fetch-dms.yml` | SHA-256 of the pinned `.nupkg`. |
| `PLUGIN_NAME` | `plugins-fetch-dms.yml` | Plugin directory name inside the package. Must be a single path segment matching `[A-Za-z0-9][A-Za-z0-9._-]*`; anything else fails the fetch before it deletes or moves anything. |

### Enabling a plugin is a separate step

**Neither overlay allowlists anything.** Acquiring the bytes and deciding which of
them may load are separate decisions, and `Plugins:Allowed` is the only switch:
a plugin runs if and only if its directory name appears there. It ships empty, so a
deployment that adds an overlay and nothing else mounts a plugin root and loads
nothing from it.

The DMS service's environment is declared in the base compose files, which stay
unchanged, so the allowlist arrives in a small deployment-owned overlay of its own.
Write it once and name it in `DMS_PLUGINS_COMPOSE_FILES` after the acquisition
overlay, as the two recipes above do:

```yaml
# my-plugins-allowed-dms.yml
services:
  dms:
    environment:
      Plugins__Allowed: Acme.Dms.Identity
```

The order written is the order plugins are invoked in. See the `Plugins` section of
[docs/CONFIGURATION.md](../../docs/CONFIGURATION.md) for the full configuration
surface.

### Loading plugins into the Configuration Service

The Configuration Service loads plugins the same way, from its own plugin root and
its own allowlist; a plugin allowlisted for DMS is not thereby allowlisted for the
Configuration Service, and the reverse. `plugins-config.yml` is the Configuration
Service counterpart of `plugins-dms.yml`: it bind-mounts a pre-populated directory
of plugin directories read-only at `/app/plugins` on the `config` service, and
`local-config.yml` and `published-config.yml` are unchanged by it. There is no fetch
overlay for the Configuration Service.

`start-local-config.ps1` has no setting that adds this overlay.
`DMS_PLUGINS_COMPOSE_FILES` is the DMS launchers' setting for the DMS overlays
above and is not a Configuration Service mechanism. Add `plugins-config.yml` with its
own `-f` after the base file, followed by a deployment-owned allowlist override for
the `config` service, because this overlay allowlists nothing:

```yaml
# my-plugins-allowed-config.yml
services:
  config:
    environment:
      Plugins__Allowed: Acme.Cms.VaultResolver
```

```powershell
cd eng/docker-compose
# CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE is required by plugins-config.yml; set it here or in the env file.
$env:CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE = "C:/cms-plugins"
docker compose -f postgresql.yml -f local-config.yml -f keycloak.yml `
  -f plugins-config.yml -f my-plugins-allowed-config.yml `
  --env-file .env -p cs-local up -d config
```

These are the files `start-local-config.ps1` composes for a PostgreSQL stack, plus
the two plugin files. As with DMS, compose them directly only against an environment
a previous `start-local-config.ps1` run already set up: that script also creates the
identity clients the Configuration Service needs, which no overlay does.

| Variable | Overlay | Meaning |
| -------- | ------- | ------- |
| `CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE` | `plugins-config.yml` | Host path holding the Configuration Service's plugin directories. Declared with `:?`, and listed commented out in `.env.example`. |

See [Configuration Service plugins](../../docs/CONFIGURATION.md#configuration-service-plugins)
for the Configuration Service's configuration surface.

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

Keep the Keycloak client secret aligned with the Configuration Service `IdentitySettings:ClientSecretValidation`
settings in `appsettings.json` or environment variables. Configuration Service startup will
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
# This is required for the Configuration Service.
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
* `ClientSecretMinimumLength` and `ClientSecretMaximumLength` should match the configured `IdentitySettings:ClientSecretValidation` bounds used by the Configuration Service.
* `NewClientSecret` must also satisfy the Configuration Service complexity rule: lowercase, uppercase, number, and one special character from `!@#$%^&*()-_=+[]{}:;,.?`.
* `NewClientSecret` is always the literal secret, even when it begins with `ENV:`. Use `NewClientSecretEnvironmentVariable` (naming the variable, in place of `NewClientSecret`) only when the secret must be read from that process's environment instead of the argument list.
* Both switches can be used together or separately as needed.

#### Signing key on rerun (`-InitDb`)

With self-contained identity, `start-local-dms.ps1`, `start-local-config.ps1` and
`start-published-dms.ps1` run `-InitDb` on every start, not only the first. So do the bootstrap
wrappers, which call them.

* An existing active signing key is kept. `-InitDb` creates a key only when none is active.
* If the newest active key cannot be decrypted with the configured
  `CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY`, `-InitDb` stops with an error and changes no
  key. This happens, for example, after that value is changed in `.env` while the database is
  kept, and the stack does not finish starting.
  * To recover, restore the encryption key that the signing key was created with.
  * Starting again with the mismatched value fails the same way, because startup never rotates
    the key.
* Replacing the signing key is an explicit operation, not something startup repairs. For a
  disposable local stack, the simplest way is to remove it with its volumes (`-d -v`) and start
  again.
* On PostgreSQL, the database can reject the check itself, for example because of pgcrypto
  permissions. `-InitDb` then reports that the key could not be checked and changes nothing; this
  does not show that either key is wrong.

### Expired token cleanup (self-contained identity)

The self-contained (OpenIddict) identity provider runs a background sweep that deletes expired
rows from `dmscs.OpenIddictToken` so the table does not grow without bound. Keycloak mode has no
equivalent setting, because Keycloak owns its own token housekeeping.

| Variable | Default | Description |
| -------- | ------- | ----------- |
| `CONFIGURATION_SERVICE_IDENTITY_TOKEN_CLEANUP_ENABLED` | `true` | Enables the expired-token cleanup sweep. |
| `CONFIGURATION_SERVICE_IDENTITY_TOKEN_CLEANUP_INTERVAL_MINUTES` | `30` | Interval, in minutes, between cleanup sweeps. |

These map to `IdentitySettings__TokenCleanup__Enabled` and
`IdentitySettings__TokenCleanup__IntervalMinutes` in `local-config.yml` and `published-config.yml`.
The interval accepts values from 1 to 71,582 minutes; a value outside that range is logged as a
warning and falls back to the 30-minute default.
