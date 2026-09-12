# api-schema-tools CLI

Command-line tool for Ed-Fi DMS schema hashing and DDL generation. Generates
deterministic SQL artifacts and manifests from `ApiSchema.json` inputs without
requiring database connectivity.

> For the end-to-end provisioning, schema-fingerprint validation, and testing
> workflow, see the [Relational Backend Developer Guide](../../../../docs/RELATIONAL-BACKEND.md).

## Installation

There are two ways to use `api-schema-tools`:

1. Install the published .NET tool package from Azure Artifacts.
2. Download the source code and compile the tool locally with `dotnet build`.

### Install from Azure Artifacts

Use this option when you want the published tool without cloning or building the
DMS repository.

```powershell
$feed = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json"
$version = "<published-version>"
dotnet tool install --global EdFi.Api.SchemaTools --source $feed --version $version
```

Use a published package version from the feed. To install the latest stable
package, omit `--version $version`.

The installed command is `api-schema-tools`.

### Build from source

Use this option when you have downloaded or cloned the DMS repository and want
to compile the tool locally.

```bash
dotnet build src/dms/clis/EdFi.DataManagementService.SchemaTools
```

The executable is output as `api-schema-tools` (or `api-schema-tools.exe` on Windows).

## Commands

### `hash` — Compute effective schema hash

Loads one or more `ApiSchema.json` files, normalizes them, and prints the
effective schema hash (SHA-256, lowercase hex).

```bash
api-schema-tools hash <coreSchemaPath> [extensionSchemaPath...]
```

**Arguments:**

| Argument | Required | Description |
|---|---|---|
| `coreSchemaPath` | Yes | Path to the core `ApiSchema.json` file |
| `extensionSchemaPath` | No | Path(s) to extension `ApiSchema.json` file(s) |

**Examples:**

```bash
# Core schema only
api-schema-tools hash core/ApiSchema.json

# Core + one extension
api-schema-tools hash core/ApiSchema.json extensions/tpdm/ApiSchema.json

# Core + multiple extensions
api-schema-tools hash core/ApiSchema.json extensions/tpdm/ApiSchema.json extensions/sample/ApiSchema.json
```

### `ddl emit` — Generate DDL SQL and manifests

Generates dialect-specific DDL scripts and manifest files to an output directory.
Does not require database connectivity.

```bash
api-schema-tools ddl emit --schema <paths...> --output <directory> [--dialect <dialect>] [--ddl-manifest]
```

**Options:**

| Option | Short | Required | Default | Description |
|---|---|---|---|---|
| `--schema` | `-s` | Yes | — | `ApiSchema.json` path(s). First is core, rest are extensions. |
| `--output` | `-o` | Yes | — | Output directory for generated files |
| `--dialect` | `-d` | No | `both` | SQL dialect: `pgsql`, `mssql`, or `both` |
| `--ddl-manifest` | — | No | `false` | Also emit `ddl.manifest.json` (dialect-independent normalized-SQL hash + statement count) for diagnostics |

**Examples:**

```bash
# Generate PostgreSQL DDL only
api-schema-tools ddl emit --schema core/ApiSchema.json --output ./ddl-output --dialect pgsql

# Generate both dialects
api-schema-tools ddl emit -s core/ApiSchema.json -o ./ddl-output -d both

# Core + extension, SQL Server only
api-schema-tools ddl emit -s core/ApiSchema.json -s extensions/tpdm/ApiSchema.json -o ./ddl-output -d mssql
```

**Output files:**

| File | Condition | Description |
|---|---|---|
| `pgsql.sql` | `--dialect pgsql` or `both` | PostgreSQL DDL script, without a built-in transaction wrapper |
| `mssql.sql` | `--dialect mssql` or `both` | SQL Server DDL script, without a built-in transaction wrapper |
| `relational-model.{dialect}.manifest.json` | Per selected dialect | Effective-schema-derived relational model inventory; fixed `dms` inventory is emitted in SQL and affects the optional DDL manifest hashes/counts instead |
| `effective-schema.manifest.json` | Always | Schema fingerprint, components, and resource key seed summary |
| `ddl.manifest.json` | Only with `--ddl-manifest` | Dialect-independent summary: normalized-SQL SHA-256 and statement count per dialect, for diagnostics |

All output files use Unix line endings (`\n`) for deterministic, byte-for-byte
stable output across platforms. `ddl emit` writes standalone SQL; callers who apply the
script manually own any desired transaction wrapper.

### `ddl provision` — Generate DDL and execute against a database

Generates dialect-specific DDL and executes the generated DDL against a target
database in a single transaction. Provisions one database at a time
(`--dialect both` is not accepted).

```bash
api-schema-tools ddl provision --schema <paths...> --connection-string <connstr> --dialect <dialect> [--create-database] [--timeout <seconds>]
```

**Options:**

| Option | Short | Required | Default | Description |
|---|---|---|---|---|
| `--schema` | `-s` | Yes | — | `ApiSchema.json` path(s). First is core, rest are extensions. |
| `--connection-string` | `-c` | Yes | — | ADO.NET connection string for the target database. |
| `--dialect` | `-d` | Yes | — | SQL dialect: `pgsql` or `mssql` (not `both`). |
| `--create-database` | — | No | `false` | Create the target database if it does not exist before provisioning. |
| `--timeout` | `-t` | No | `300` | Command timeout in seconds for DDL execution. |

**Examples:**

```bash
# Provision a PostgreSQL database
api-schema-tools ddl provision --schema core/ApiSchema.json --connection-string "Host=localhost;Port=5432;Database=edfi_dms;Username=postgres;Password=secret" --dialect pgsql --create-database

# Provision an existing SQL Server database (add --create-database to create it, as in the pgsql examples)
api-schema-tools ddl provision -s core/ApiSchema.json -c "Server=localhost;Initial Catalog=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true" -d mssql

# Core + extension, PostgreSQL
api-schema-tools ddl provision -s core/ApiSchema.json -s extensions/tpdm/ApiSchema.json -c "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret" -d pgsql --create-database
```

**Behavior:**

1. Loads and normalizes schema files, builds the effective schema set
2. Generates DDL (core tables, relational model, fixed DocumentCache inventory, seed DML)
   for the specified dialect
3. Optionally creates the database if `--create-database` is set
4. For SQL Server: configures Read Committed Snapshot Isolation (RCSI) on newly
   created databases; warns if RCSI is disabled on existing databases
5. Runs bounded preflight checks before any generated DDL
6. Executes all generated DDL in a single target-database transaction
7. Prints the provisioned database name and effective schema summary

SQL Server RCSI configuration for newly created databases, and RCSI warnings for
existing databases, run outside and before the generated DDL transaction.

Provisioning always creates the fixed `dms.DataStoreIdentity`, `dms.DocumentCache`,
`dms.DocumentProjectionWork`, and `dms.DocumentCacheState` objects. Their physical shape
is owned by
[`data-model.md`](../../../../reference/design/backend-redesign/design-docs/data-model.md);
cached-document semantics are owned by the
[`Cached Document Contract`](../../../../reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract);
transactional-enqueue semantics are owned by
[`Transactional Enqueue`](../../../../reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#transactional-enqueue);
create-only DDL behavior is owned by
[`ddl-generation.md`](../../../../reference/design/backend-redesign/design-docs/ddl-generation.md#provision-semantics-create-only-no-migrations);
schema/query integration is owned by
[`cdc-streaming.md`](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#schema-and-query-integration);
and the `CDC-INV-02` / `CDC-INV-03` traceability rows live under
[`Contract-to-Evidence Traceability`](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability).

This command is create-only. Its phase-zero checks are limited to the effective-schema
hash, `dms.DataStoreIdentity` and `dms.DocumentCacheState` singleton safety, known legacy
DocumentCache artifacts (`DocumentCache.Etag`, `UX_DocumentCache_DocumentUuid`, and
`IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt`), and PostgreSQL
enqueue-owner prerequisites. It does not migrate old cache shapes, reconcile arbitrary
drift, or classify every partial database state. A completed same-hash rerun preserves
`SourceIdentity`, projection lifecycle, `CacheAheadRecoveryRequired`, cache rows, pending
work, and enqueue timestamps, while compatible existence checks and replaceable
functions/triggers can finish or refresh generated definitions. Known legacy cache
artifacts require dropping and recreating the database.

PostgreSQL provisioning requires capability to create or refresh the locked-down
`NOLOGIN` `edfi_dms_enqueue_owner` role used to own the security-definer enqueue
functions. That role is not a runtime DMS credential. SQL Server uses same-owner trigger
execution over the referenced `dms` tables and emits no `EXECUTE AS`, enqueue user, or
enqueue role. Runtime projection, projection administration, health/readiness,
cache-backed reads, complete target eligibility validation, CDC capture objects, and CDC
reader grants are outside this CLI provisioning contract.

## Determinism guarantee

For a fixed set of `(ApiSchema.json inputs, dialect, relational mapping version)`,
`ddl emit` produces **byte-for-byte identical** output files across runs.
This enables reliable golden-file testing and change detection.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Error (invalid arguments, missing files, schema validation failure, etc.) |

Errors are written to stderr with descriptive messages.

## Integration tests

### PostgreSQL

PostgreSQL integration tests run automatically and require a PostgreSQL server.
The connection string is configured in `appsettings.json` in the
`SchemaTools.Tests.Integration` project (port 5432 for CI, overridden to 5435
locally via `appsettings.Test.json`). If PostgreSQL is unreachable, the tests
fail — this is intentional so CI detects infrastructure problems.

### SQL Server

SQL Server integration tests **run by default** — the project's committed `appsettings.json`
already supplies an `MssqlAdmin` connection string pointing at `localhost`. The skip guard
only checks that `MssqlAdmin` is set (it does not probe connectivity), so the tests fail on
connection errors if no SQL Server is reachable there. To point them at your own SQL Server
locally:

1. Create `appsettings.Test.json` in the `SchemaTools.Tests.Integration` project
   directory (this file is gitignored):

   ```json
   {
       "ConnectionStrings": {
           "PostgresAdmin": "Host=localhost;Port=5435;Database=postgres;Username=postgres;Password=abcdefgh1!;",
           "MssqlAdmin": "Server=localhost;Database=master;User Id=sa;Password=YourPassword;TrustServerCertificate=true;"
       }
   }
   ```

2. Ensure SQL Server is running (e.g., via Docker):

   ```bash
   docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourPassword" -p 1433:1433 -d mcr.microsoft.com/mssql/server:2025-latest
   ```

3. Run the integration tests:

   ```bash
   dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration
   ```

Because the committed `appsettings.json` always sets `MssqlAdmin`, the SQL Server tests run
and fail on any server issue — same behavior as the PostgreSQL tests. They report as skipped
only if `MssqlAdmin` is removed from the committed config.

## Breaking changes

Prior to DMS-950, the CLI accepted positional arguments for schema hashing:

```bash
# Old (no longer works)
api-schema-tools core/ApiSchema.json

# New equivalent
api-schema-tools hash core/ApiSchema.json
```

The `hash` subcommand is now required.

## CDC deployment commands

`api-schema-tools cdc` uses the deployment controllers in `Backend.Cdc`. The shipped
CLI composes the qualified single-worker, single-broker local Compose deployment
(`LocalSingleBroker` / `AuthorizationDisabledLocal`); it reports that this profile
has no ACL isolation proof. Other deployments must supply their own live deployment
authority adapters. DMS HTTP startup does not register connectors.

Use the **original** managed provisioning state root and the same DMS settings and
selected target as the eventual DMS host. The database must already have its managed
CREATE receipt and source-history record (`ddl provision --managed-state-path ...`).
`enable` requires the exclusively owned initial offline window, before any canonical
writer or seed process starts. It returns a writer-publication receipt only after
the temporary projection runtime has stopped. Configuration is not provenance.

These invocation shapes apply to PostgreSQL and SQL Server respectively:

```bash
api-schema-tools cdc enable --settings ./cdc-postgresql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc enable --settings ./cdc-mssql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc validate --settings ./cdc-postgresql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc status --settings ./cdc-mssql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc watch --settings ./cdc-postgresql.json --state-path "$PWD/.cdc-state" --maximum-passes 20 --json
api-schema-tools cdc stop --settings ./cdc-postgresql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc start --settings ./cdc-postgresql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc restart --settings ./cdc-mssql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc resume --settings ./cdc-mssql.json --state-path "$PWD/.cdc-state" --json
api-schema-tools cdc retire --settings ./cdc-postgresql.json --state-path "$PWD/.cdc-state" --generation 1 --destructive-cleanup --json
```

Each settings file contains the normal DMS configuration (including CMS access,
schema packages, and `DataManagement:DocumentCache:Targets`) plus a `Cdc` section. For example,
merge the following into your PostgreSQL DMS settings, replacing local paths and
principals with those of your deployment:

```json
{
  "AppSettings": { "Datastore": "postgresql" },
  "DataManagement": { "DocumentCache": { "Targets": [{ "DataStoreId": 42 }] } },
  "Cdc": {
    "Provider": "postgresql",
    "DeploymentKey": "local",
    "TenantKey": "",
    "DataStoreId": "42",
    "InstanceKey": "datastore-42",
    "Generation": 1,
    "TopicPrefix": "edfi",
    "PartitionCount": 1,
    "Schemas": ["/absolute/path/to/ApiSchema.json"],
    "SetupPrincipal": "postgres",
    "DatabaseConnectorPrincipal": "cdc_reader",
    "ConnectEndpoint": "http://127.0.0.1:8083",
    "WorkerMetricsEndpoint": "http://127.0.0.1:9404/metrics",
    "KafkaBootstrapServers": "dms-kafka1:9092",
    "KafkaAdminBootstrapServers": "127.0.0.1:9092",
    "MaxRecordBytes": 10000000,
    "DurabilityProfile": "LocalSingleBroker",
    "AuthorizationProfile": "AuthorizationDisabledLocal",
    "LagThresholdMilliseconds": 5000,
    "Worker": {
      "Key": "local-worker",
      "OffsetStorageTopic": "dms-connect-offsets",
      "HeapBytes": 536870912,
      "Principal": "worker",
      "ConnectorPrincipal": "connector",
      "AdministratorPrincipal": "administrator"
    },
    "Consumers": [],
    "ProviderConnectionProperties": {
      "database.hostname": "dms-postgresql",
      "database.port": "5432",
      "database.dbname": "edfi_cdc",
      "database.user": "cdc_reader",
      "database.password": "${env:CDC_DATABASE_PASSWORD}"
    },
    "KafkaClientSecurityProperties": {},
    "KafkaAdminProperties": {},
    "Compose": {
      "File": "/absolute/path/to/eng/docker-compose/kafka-cdc.yml",
      "EnvironmentFile": "/absolute/path/to/eng/docker-compose/.env",
      "Project": "dms-local",
      "BrokerSizeOverrideFile": "/absolute/path/to/retained/broker-size.json"
    },
    "Timing": {
      "CallMilliseconds": 30000,
      "WaitMilliseconds": 300000,
      "PollMilliseconds": 1000,
      "MaximumObservationAgeMilliseconds": 10000
    }
  }
}
```

For SQL Server use `AppSettings:Datastore=mssql`, `Cdc:Provider=sqlserver`, the
SQL Server setup/connector principals, port `1433`, and `database.names` instead of
`database.dbname`. The schema inputs must match the deployed schema, including
extensions. `KafkaBootstrapServers` is the address seen by the worker;
`KafkaAdminBootstrapServers` is reachable from the CLI. Worker identity, offset topic,
heap, image, ports and project must match the selected Compose deployment. The image
digest comes from the shipped qualification record; there is no image override.
The broker-size override's parent directory must already exist and survive restarts.

Environment variables with prefix `DMS_CDC__` override settings in memory. For
example, provide the setup connection through
`DMS_CDC__Cdc__SetupConnectionString`; this connection reaches the selected physical
DMS database as the setup principal. Supply normal CMS credentials through the same
prefix, such as `DMS_CDC__ConfigurationServiceSettings__ClientSecret`. Connector secret
properties must contain externalized references resolved by the worker; literal
connector passwords are rejected. The controller never emits connector payloads.

Commands emit one JSON result on stdout (also the default without `--json`), with
safe diagnostics and watch passes on stderr. Exit codes: `0` successful operation or
ready observation; `1` rejected/not-ready/unavailable/timed-out operation; `2` invalid
command/configuration input; `130` cancellation. `data` contains the operation's
typed result; `deploymentProfile` explicitly reports disabled local authorization and
`aclIsolationProven: false`. `binding`, when present, is validated scope for downstream command
input, not independent ownership evidence. `validate` never repairs. `status/watch`
can persist terminal incidents and stop affected connectors. `stop` retains all
artifacts; only a fresh verified stop result for every connector permits shared
worker shutdown. `start` requires retained STOPPED state and the managed stop receipt.
`retire` removes only this generation's governed artifacts while infrastructure stays
reachable, and retains source history and shared worker state.

Record-size increases require `increase-record-size --acknowledgement ./increase.json
--confirm-consumer-capacity` together with `--settings`, `--state-path`, and `--json`.
The acknowledgement has `operationId` (a stable UUID reused for retries),
`bindingIdentity` (the complete identity fields from the command result's binding),
`previousMaxRecordBytes`, `requestedMaxRecordBytes`, `requestedProducerBufferBytes`,
`operatorIdentity`, `noConsumers`, and `consumers`. Each consumer entry contains
`deploymentIdentity`, `revision`, `confirmingOwner`, and `evidenceReference`, using
credential-free opaque tokens. Complete identity fields are `deploymentKey`,
`tenantKey`, `dataStoreId`, `instanceKey`, `generation`, `provider`,
`physicalSourceFingerprint`, `connectorName`, and `topicName`.

The operator is responsible for complete consumer inventory and owner evidence of
fetch/deserialization capacity maintained through consumption, or an explicit
`noConsumers: true` with an empty inventory. The CLI does not certify consumers.
Every invocation requires the confirmation flag again; it creates a fresh invocation
ID and confirmation time. Retain the previous settings and the same acknowledgement
scope while retrying a partial rollout. After success, update normal settings to the
new ceilings. Increase `CallMilliseconds` (at most 300000) and `WaitMilliseconds`
when broker recreation requires more time. The same durable broker-size override
path is used for size changes and subsequent worker startup.

Intact binding, journal, source history and provider/offset evidence are required.
There is no adoption, force, import, or physical-source replacement operation. Native
worker/task recovery can consume before revalidation; later containment or readiness
does not certify unsampled continuity. Independently provisioning a new source and
retiring an old one does not certify migration or continuity.

Owning design: [initial enablement](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence),
[state continuity](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral),
[native recovery](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary),
[record-size increases](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase),
and [source replacement deferral](../../../../reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral).


### Bootstrap CDC handoff

On an exclusively owned Linux local deployment, supply the DMS/CDC settings above with
one explicit `DataManagement:DocumentCache:Targets` entry for the ID the configure phase
will select. Keep the CMS URL on its published loopback port and use a dedicated database
name distinct from the infrastructure initialization database:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -EnableKafkaCdc -SeparateConfigDatabase -DataStoreDatabaseName edfi_cdc -CdcSettingsPath ./cdc-postgresql.json -CdcBindingStatePath ./.cdc-state -EnableKafkaUI
pwsh eng/docker-compose/bootstrap-published-dms.ps1 -DatabaseEngine mssql -EnableKafkaCdc -SeparateConfigDatabase -DataStoreDatabaseName edfi_cdc -CdcSettingsPath ./cdc-mssql.json -CdcBindingStatePath ./.cdc-state
```

The wrappers require no running DMS/IDE writers or unrelated containers on the selected
CMS/database network. Shared CMS/database topology, route-qualified or multiple targets,
`-NoDataStore`, and `-DmsBaseUrl` are unsupported for initial CDC bootstrap. The physical
CREATE DATABASE receipt remains authoritative; preexisting databases do not qualify.
A selected-ID mismatch rejects before schema provisioning; it never inserts target membership.

The shared phase snapshots supplied settings into owner-only files under
`.bootstrap/cdc-runtime`, binds ordinary staged schema inputs, and uses the selected Compose
environment, project and original state root. Host-side runtime connections translate only
the known CMS Compose database address to its published loopback port. The eventual DMS
receives the same projection settings and CMS credentials through an explicit Compose
override; Kafka credentials stay with the controller. Bootstrap does not accept `DMS_CDC__`
environment overrides; place required controller secrets in the protected supplied settings.

`-LoadSeedData` runs only after publication authorization and DMS startup. Local `-InfraOnly`
completes CDC and remains offline; the controller's printed status command identifies its
retained settings and state. On failure, retain those files and use the controller to inspect
or resume the original unfinished workflow while writers remain stopped. This bootstrap
entry point resumes established deployments through the retained lifecycle inventory described below.

### Managed stack lifecycle

The DMS E2E harness accepts the same explicit CDC settings and original state root:

```powershell
pwsh src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1 -EnableKafkaCdc -CdcSettingsPath ./cdc-postgresql.json -CdcBindingStatePath ./.cdc-e2e -EnvironmentFile ./eng/docker-compose/.env.e2e
pwsh ./build-dms.ps1 E2ETest -DatabaseEngine mssql -EnableKafkaCdc -CdcSettingsPath ./cdc-mssql.json -CdcBindingStatePath ./.cdc-e2e-mssql -EnvironmentFile ./.env.e2e -Configuration Release -TestFilter 'FullyQualifiedName~Given_CdcE2ESetup'
```

Use a fresh owned stack and settings for the actual `E2E_DATABASE_NAME`, CMS-selected
data store ID, provider, worker, and metrics endpoint. Supply a pre-existing restricted
connector login (and database user on SQL Server); provider setup grants its CDC access.
E2E uses separate CMS topology, enables SQL Server Agent for the CDC lane,
and uses the selected E2E schema packages, including test extensions. Managed provisioning
creates the primary and retains its receipt; the legacy reset provisioner only prepares
the distinct `E2E_SNAPSHOT_DATABASE_NAME`. CDC admission completes before DMS or API
tests start. `-SkipDockerBuild` reuses existing local images in either entry point.

Failures prevent test launch, retain sanitized `.cdc-diagnostics` artifacts and original
CDC settings/state, and attempt governed stop. If shutdown cannot be verified,
infrastructure remains available for reconciliation. The standard E2E teardown invokes
governed retirement before volume deletion and retains source history and CDC settings.
Subsequent E2E reset attempts reject a retained CDC workspace; finish retirement and
archive its configuration/history before preparing a new workspace. Detailed API-driven
Kafka message scenarios remain part of the separate CDC E2E suite. The explicitly
selected `Given_CdcE2ESetup` fixture checks HTTP/database health without feature reset
hooks, so setup qualification retains the admitted source for governed teardown.

After initial bootstrap, both bootstrap wrappers and both `start-*-dms.ps1` primitives
recognize `.cdc-deployments/<project>.json`. This private, durable inventory is separate
from the bootstrap manifest and the controller journals. It retains every registered
settings snapshot, custom state root, generation, worker identity, effective environment,
and shared broker-size override. Removing command-line opt-in flags does not remove a
connector from management. Missing inventory with surviving managed containers or labeled
Kafka volumes rejects startup and teardown.

```powershell
# Stop all managed connectors, verify STOPPED/no tasks, then stop infrastructure; retain volumes.
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -d
# Restore infrastructure and worker REST; validate retained stopped connectors and resume them.
pwsh eng/docker-compose/bootstrap-local-dms.ps1
# Governed generation retirement before deleting this project's volumes.
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -d -v
# The published wrapper accepts the same lifecycle switches.
pwsh eng/docker-compose/bootstrap-published-dms.ps1 -d -v
```

Omit environment/provider/settings/state arguments to inherit the original selection.
Explicit arguments must agree with the retained deployment; the original base environment
path is also accepted. Compose environment overrides must match the original process
values. Settings identity, credentials, endpoints, schemas and worker policy remain fixed;
after a successful acknowledged size rollout, update only `Cdc:MaxRecordBytes` and
`Cdc:ProducerBufferBytes` in the retained settings. Controllers validate these operational
ceilings against live evidence before resuming.

The deployment lock serializes wrapper operations across all registered bindings. Each
binding still takes its own controller state lock and uses its original provenance.
`stop` requires each command's fresh `TargetShutdownVerified` result and an independent
complete REST inventory check before stopping the worker. Startup consumes that deployment
shutdown checkpoint, exposes REST through `cdc start-worker` with observational shared-offset
policy checks, verifies every retained connector is STOPPED, then invokes guarded `cdc start`
for each. The start command temporarily runs the existing selected projection executor so
retained work can drain while the HTTP host is offline; it disposes that executor on every
exit. No initial barrier or exact baseline is recertified. Unknown state, native recovery,
failed validation or resume prevents DMS startup; reconcile via controller inspection and a
fresh managed stop before retrying. Direct `start-worker` is an infrastructure building block;
the wrappers own the complete worker inventory and it never resumes connectors.

Multiple registered bindings may use separate original state roots but must share the same
worker, environment and broker-size override path. Additional explicitly provisioned handoffs
are registered with `Register-CdcDeploymentHandoff` from `cdc-lifecycle.psm1` before their
controller enable call. Automatic DMS startup combines their explicit DocumentCache targets
and requires matching other DMS host settings; local `-InfraOnly` restores CDC without launching
an HTTP host. Partial retirement retains infrastructure and inventory and retries each
controller's resumable cleanup. Per-binding retirement retains shared offset topics, peers and
source-history journals. Destructive stack teardown removes volumes only after all governed
cleanup and an empty worker inventory. The CDC settings workspace and all controller roots
remain retained, including through shared E2E teardown, so cleanup cannot accidentally erase
nested history or another deployment's settings.
