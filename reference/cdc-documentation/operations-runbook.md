# CDC Operations Runbook

[Entry point](README.md) · [Evidence index](cdc-inv-evidence.md)

This shared PostgreSQL/SQL Server runbook is under construction. Both providers’ setup
and DMS E2E opt-in variants, state preservation, managed lifecycle, recovery and
projection handoffs, monitoring, retention, security, consumer-evidence checklists and
coordinated record-size increases, retirement/teardown and restamp/disclosure handoffs
are documented. Setup and observation snippets passed T16/T17; PostgreSQL lifecycle
snippets passed [T18](cdc-inv-evidence.md#postgresql-lifecycle-qualification-t18). Remaining
live procedure qualification is **pending** its named tasks.
Use the [shipped command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
for current command details and the linked design owners for support boundaries.

## Procedure Navigation

| Need | Procedure | Documentation task |
| --- | --- | --- |
| PostgreSQL local setup | [postgresql-setup](#postgresql-setup) | T02 — documented; T16 local setup passed |
| SQL Server local setup | [sql-server-setup](#sql-server-setup) | T03 — documented; T17 local/published setup passed |
| DMS E2E opt-in | [dms-e2e-setup](#dms-e2e-setup) | PostgreSQL / SQL Server direct setup passed T16/T17; build alternatives unexercised |
| Preserve deployment state | [deployment-state](#deployment-state) | PostgreSQL passed T18; SQL Server pending T19 |
| Interrupted initial-enable retry | [initial-enable-retry](#initial-enable-retry) | T04 — documented; exact retry snippet unexercised |
| Established validation and restart preflight | [established-validation](#established-validation) | PostgreSQL passed T18; SQL Server pending T19 |
| Missing provenance and source mismatch | [unsupported-provenance](#unsupported-provenance) | PostgreSQL passed T18; SQL Server pending T19 |
| Managed shutdown and startup | [managed-lifecycle](#managed-lifecycle) | PostgreSQL passed T18; SQL Server pending T19 |
| Intact connector restart and resume | [intact-restart](#intact-restart) | PostgreSQL passed T18; SQL Server pending T19 |
| Native recovery and incomplete shutdown | [native-recovery](#native-recovery) | T05 — documented; live exercise pending |
| Projection troubleshooting and administration handoff | [projection-handoff](#projection-handoff) | T06 — documented; T25/T26 exercise pending |
| Monitoring and provider retention | [monitoring-retention](#monitoring-retention) | T07 — documented; T27/T28 exercise pending |
| Security, topic retention and consumer evidence | [security-consumer-evidence](#security-consumer-evidence) | T08 — documented; T20 exercise pending |
| Coordinated record-size increase | [record-size-increase](#record-size-increase) | T09 — documented; T23/T24 exercise pending |
| Guarded generation retirement | [generation-retirement](#generation-retirement) | T10 — documented; live exercise T25/T26 pending |
| Destructive stack teardown | [stack-teardown](#stack-teardown) | T10 — documented; live exercise T25/T26 pending |
| Compatible representation-restamp handoff | [representation-restamp](#representation-restamp) | T11 — documented; T25/T26 exercise pending |
| Sensitive-data disclosure response | [sensitive-data-response](#sensitive-data-response) | T11 — documented; T25/T26 exercise pending |

## Procedure Record

Each procedure uses the same record. Before a procedure becomes runnable, replace
its pending entries with verified implementation details:

| Field | Required content |
| --- | --- |
| Target/generation | CMS-selected target, provider, physical-source and binding-generation scope; shared-worker inventory when relevant. |
| Authority/offline window | Required deployment/database/consumer authority, writer/seed exclusion or offline fence, who maintains it and when it may end; explicitly state when no offline window is required. |
| Retained inputs | Exact settings/state paths, schema inputs, receipts, acknowledgements and incident records; distinguish supplied inputs from controller-emitted state. |
| Invocation | Ordered marked commands/configuration, working directory, prerequisites and every substitution's source. |
| JSON/exit status | Actual case-sensitive result envelope and operation-specific fields, stdout/stderr handling, exit status and optional/unavailable observations from production fixtures. |
| Postcondition | Observable completion criterion scoped to this operation. |
| Rejection/timeout action | Sanitized diagnostic, evidence to retain, authority to keep fenced, retry or escalation procedure, and the observation that ends recovery. |

The [command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
and [command host](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/Cdc/CdcCommandHost.cs)
are the source for command names and output; do not infer success from a generic
exit-zero example. The [initial admission](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence),
[managed/native recovery](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary),
and [sensitive-data](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction)
owners distinguish writer admission, current observation, shutdown, and purge.

## Snippet Conventions

The procedure records reserve exact snippet IDs. Delivered examples wrap only
their command/configuration or result-example fenced block with
HTML comments `<!-- cdc-snippet: ID -->` and `<!-- /cdc-snippet: ID -->`, using the
same reserved ID at both ends. IDs are unique across this reference set and remain
stable when prose or headings change. Additional examples may receive new IDs;
do not reuse an ID for a different operation.

Immediately before each marked block, declare its shell or JSON type, repository-root
working directory (or explicit alternative), setup dependencies, destructive/fault
intent if applicable, and a substitution table: literal placeholder, operator source,
and permitted fixture replacement. Declare `none` if no replacement is needed.
Settings examples identify the full normal DMS configuration they extend. Do not
silently replace targets, schemas, endpoints, settings paths, state roots, generations,
or credentials in a test harness. Retained controller outputs are inputs to later
commands, not fixture-generated provenance.

Marked `cdc-output-*` blocks are JSON result excerpts, never executable input or retained
provenance. They need no substitutions; tests compare fixed selected fields and omit
volatile identities/timestamps. All other marked runnable examples are inputs to the focused checks. T13 checks CLI
commands, settings and acknowledgement inputs; T14 checks result excerpts, packaged
output and links. T15 checks wrapper invocations in the existing Contract/PR fixtures. Illustrative output is separately identified for
checks against production serialization fixtures. Reserved IDs, unmarked blocks
and prose must not be executed.
The [evidence index](cdc-inv-evidence.md#recording-results) distinguishes parser checks
from live provider exercises.

The test-only [wrapper snippet helper](../../eng/docker-compose/tests/cdc-runbook-snippets.ps1)
binds the exact selected `pwsh` invocation against the shipped parameter block;
it does not execute surrounding prose or wrapper infrastructure. Declared fixture
substitutions map `./.local/cdc/` beneath an owned temporary root and the selected
`.env`/`.env.e2e` to fixture environment paths. Existing wrapper seams supply the
CMS-selected target `42`, schema/provisioning receipts and provider admission results;
the lifecycle fixture supplies its retained peer inventory/custom roots. Provider tokens,
settings/state basenames, database selection, switches and test filter remain the marked
inputs. These checks prove binding/forwarding and failure ordering, not live readiness.
The [qualification handoff](cdc-inv-evidence.md#shared-helper-and-live-qualification-handoff)
identifies the existing live suite owners.

The test-only [live setup fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1)
reuses that binding helper and the existing bounded native-process harness to invoke
the shipped wrappers against real services. It requires an exclusively owned,
disposable `dms-local` stack (`CDC_RUNBOOK_OWNED_STACK=1`), fresh fixture settings and
the original managed state through retirement. Private stdout/stderr and credentials
remain beneath its owner-only fixture root; qualification exports named outcomes.
It prepares a restricted role before target provisioning, never capture artifacts or
manual schema repairs. PostgreSQL fixtures select host port `5435`, target `1`, the
documented primary names, and matching staged core/extensions. The masked setup input
explicitly includes `Username=postgres`; an absent `POSTGRES_USER` declaration in an
environment file still uses Compose's `postgres` default.
The local bootstrap fixture makes at most three intact initial wrapper attempts when a cold
infrastructure observation fails, retaining every attempt's private output. It
never retries a workflow that already authorized writer publication, changes a
retained input, or repairs provider/Connect artifacts between attempts.
E2E setup runs once: its destructive setup guard rejects retained CDC workspaces.

<a id="postgresql-setup"></a>

## PostgreSQL local setup

**Documented in T02; local bootstrap and observations passed T16.** This is an initial setup on an
exclusively owned Linux local deployment using the [supported profile](README.md#supported-deployment).
Follow the [initial-admission owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence)
and [PostgreSQL setup owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql).
The [SchemaTools reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
owns configuration and command definitions; the examples below assemble one deployment.

| Record | Value |
| --- | --- |
| Target/generation | Default tenant, PostgreSQL, one CMS-selected data store; example `1`, `datastore-1`, generation `1`, dedicated new `edfi_cdc`. The controller obtains physical-source identity from managed provisioning, not these settings. |
| Authority/offline window | Deployment owner controls Docker, CMS, setup credentials and all writers/seed processes. Keep every DMS/IDE writer and seed process stopped until the wrapper accepts the matching writer-publication receipt. |
| Retained inputs | Protected full input settings and effective environment; staged schemas; original absolute state root; emitted `.bootstrap/cdc-runtime` settings/Compose override and `.cdc-deployments` inventory. Retain controller receipts, history, journals and `broker-size.json`. |
| Invocation | `cdc-pg-infrastructure`, `cdc-pg-connector-role`, `cdc-pg-settings`, then exactly one of `cdc-pg-bootstrap-local` / `cdc-pg-bootstrap-published`; observe with `cdc-pg-status` / `cdc-pg-watch`. All commands start at repository root. |
| JSON/exit status | Wrapper progress is text; its internal `enable` must return exit `0`, `operation: "enable"`, `succeeded: true`, `exitCode: 0`, matching `data.workflowId` and non-default `data.authorizedAt`. Observation fields/codes are described below. |
| Postcondition | Controller has stopped its temporary projector and authorized writer publication; wrapper then starts DMS, unless local `-InfraOnly`. Optional seed starts after admission and DMS startup. |
| Rejection/timeout action | Keep writers excluded, retain settings/state and sanitized diagnostic codes, inspect using emitted paths. Use [initial retry](#initial-enable-retry) only for the original unfinished workflow; provenance/source incidents route to [unsupported provenance](#unsupported-provenance). Never delete state to retry. |

### Prepare the owned deployment

Use PowerShell 7, Docker Engine with Compose v2, the repository's .NET SDK/toolchain,
and the matching `api-schema-tools` build/package (see [tool preparation](../../eng/docker-compose/bootstrap-schema-tool.psm1)).
On Linux, check `systemctl status docker --no-pager`; start the service if necessary
and verify `docker ps` succeeds in the session. The scripts enforce Linux ownership
checks. Reserve the `dms` network, selected project and container names exclusively;
no unrelated containers on the CMS/database network and no DMS/IDE writers are allowed.

Use the pinned PostgreSQL image in [postgresql.yml](../../eng/docker-compose/postgresql.yml)
(`postgres:16.8-alpine`, including its digest), the broker in
[kafka-broker.yml](../../eng/docker-compose/kafka-broker.yml), and the qualified worker
in [kafka-cdc.yml](../../eng/docker-compose/kafka-cdc.yml). The worker digest is checked
against shipped qualification; an arbitrary `CDC_CONNECT_IMAGE` is not supported.
Image/telemetry qualification is owned by [pinned runtime](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#pinned-connector-runtime)
and [local telemetry](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry).

Prepare an owner-only environment file from [`.env.example`](../../eng/docker-compose/.env.example)
as `eng/docker-compose/.env`. Resolve its database, CMS and identity credentials
before starting; retain that file. Supply `CDC_DATABASE_PASSWORD` through that
protected environment file or the worker's process environment, with the same secret
as the restricted login created below. Never paste secrets into command arguments,
transcripts, evidence or source control. Keep environment/settings directories mode
`700` and files mode `600`. Review ambient Compose variables: they can override the
file. Remove any `DMS_CDC__*` overrides before using either wrapper.

The concrete endpoints below assume `POSTGRES_PORT=5432`,
`DMS_CONFIG_ASPNETCORE_HTTP_PORTS=8081`, `CONNECT_SOURCE_PORT=8083`,
`CDC_METRICS_PORT=9404`, `KAFKA_PORT=9092`, worker key `local-worker`, offset topic
`dms-connect-offsets`, and worker heap `512` MiB. If changing these, change every
corresponding setting together. Lifecycle qualification uses `CDC_WORKER_HEAP_MIB=1024`
and `Cdc.Worker.HeapBytes=1073741824` from initial creation for cold plugin-scan
headroom. Do not change a retained worker policy to retry a failed operation.
Leave multitenancy disabled and route qualifiers
empty. Use `-SeparateConfigDatabase`; shared CMS/DMS database topology is rejected.
The new `edfi_cdc` database must not exist and must differ from `POSTGRES_DB_NAME`
and the reserved CMS/system databases. Do not create it manually: the wrapper's
managed CREATE supplies the original receipt and initial-workflow purpose.

PowerShell, repository root; initial infrastructure only, before settings preparation.
This creates/starts owned database/CMS infrastructure; it does not admit writers.
For the published alternative, perform this preparation in a separate fresh workspace
using the published start script, never over an existing local deployment.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `./eng/docker-compose/.env` | Protected selected environment above; same absolute file in subsequent phases | Owned fixture environment |
| `start-local-dms.ps1` | Choose local build; published alternative is `start-published-dms.ps1` with the same switches | Corresponding shipped wrapper |

<!-- cdc-snippet: cdc-pg-infrastructure -->
```powershell
$environmentFile = (Resolve-Path './eng/docker-compose/.env').Path
pwsh ./eng/docker-compose/start-local-dms.ps1 -InfraOnly -EnableConfig `
    -SeparateConfigDatabase -CdcDatabaseInfrastructure -DatabaseEngine postgresql `
    -IdentityProvider self-contained -EnvironmentFile $environmentFile
if ($LASTEXITCODE -ne 0) { throw 'Infrastructure preparation failed; keep writers stopped.' }
```
<!-- /cdc-snippet: cdc-pg-infrastructure -->

The PostgreSQL initialization script enables logical WAL for a fresh volume. Have
the database owner verify the [provider prerequisites](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql)
and available replication capacity. Prepare the connector's existing `LOGIN REPLICATION`
role with no superuser, database-creation, role-creation, bypass-RLS or inherited
privileged membership. Provider setup grants its narrow table/heartbeat access;
do not manually create slots, publications, heartbeat tables or grant document writes.

PowerShell, repository root; requires the preceding healthy owned PostgreSQL container.
This creates a **new role**, once, and prompts for its password interactively. Enter
the worker's `CDC_DATABASE_PASSWORD` value. An existing role requires owner review,
not automatic recreation or escalation of privileges.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `dms-postgresql`, `postgres` | Selected container and setup user (`POSTGRES_USER`) | Fixture container/setup principal |
| `cdc_reader`, interactive password | Restricted connector identity and protected worker secret | Fixture restricted role/secret |

<!-- cdc-snippet: cdc-pg-connector-role -->
```powershell
docker exec -it dms-postgresql createuser -U postgres --login --replication `
    --no-superuser --no-createdb --no-createrole --pwprompt cdc_reader
if ($LASTEXITCODE -ne 0) { throw 'Connector role preparation failed; retain infrastructure for inspection.' }
```
<!-- /cdc-snippet: cdc-pg-connector-role -->

### Select the target and prepare complete settings

For this example, require a **brand-new CMS database**, no prior data-store inserts
(including deleted rows), and no concurrent CMS configuration. Its first registration
has ID `1` under the [shipped identity seed](../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0021_Create_DataStore_Table.sql).
The wrapper's configure phase obtains the authoritative ID from the CMS
`POST /v3/dataStores` response (`SelectedDataStoreIds` in
[configure-local-data-store.ps1](../../eng/docker-compose/configure-local-data-store.ps1));
it checks that ID against both settings entries **before** provisioning. After
registration, the CMS owner can inspect authenticated `GET /v3/dataStores` and select
the route-unqualified entry for this database, checking its `id`, provider and
connection destination privately. Do not publish connection strings.

`1` is a declared fresh-fixture expectation, not a general ID-discovery algorithm.
Do not run configure separately to discover an ID and then rerun bootstrap:
configure inserts another registration. An empty list after deletions does not prove
the next ID. For an existing CMS whose selected ID cannot be established in advance,
stop before this example and resolve deployment selection with the wrapper owner
[DMS-1323](../design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md);
do not guess, add `-NoDataStore`, or use SQL to reset the identity. A mismatch rejects
before schema/CDC effects; preserve the registration and diagnostics for that handoff.

Start with a **complete normal DMS settings file**, prepared from
[frontend appsettings.json](../../src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json)
plus this deployment's normal overrides. Store it privately at
`.local/cdc/dms-base.json`. Populate CMS `BaseUrl` (`http://127.0.0.1:8081`),
`ClientId`, `ClientSecret`, `Scope` and `EncryptionKey` with the effective CMS
registration/environment values (`CONFIG_SERVICE_CLIENT_*` and
`DMS_CONFIG_DATABASE_ENCRYPTION_KEY`); configure normal authentication/JWT and other
host settings for the same identity provider. Preserve normal projector options.
A copied default file with empty secrets is not ready to use. Schema package inputs
must match the eventual DMS host, including extensions. Local bootstrap selects its
Data Standard overlay (default 5.2); published bootstrap uses the environment unless
`-DataStandardVersion` is explicitly supplied. The example selects `5.2` explicitly
for both. See [schema workspace](../../eng/docker-compose/bootstrap-schema-workspace.psm1).

PowerShell, repository root; requires the protected base file, environment and role
above. Creates a **new** settings file and original state directory; refuses to
replace an existing settings file. Run in an owner-only `.local/cdc` directory.
The masked prompt accepts the full setup connection string for the **new** database,
for example the shape `Host=127.0.0.1;Port=5432;Database=edfi_cdc;Username=postgres;Password=…`.
Use a connection-string builder when values need escaping. This is setup authority,
not the connector role. Do not print the resulting object.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `.local/cdc/dms-base.json`, `.local/cdc/postgresql.json` | Complete private normal settings; new output file | Fixture full DMS settings/output |
| `.local/cdc/state-pg`, repository root | Original managed state root and checkout | Owned fixture paths; never fabricated receipts |
| `1`, `datastore-1`, `local`, generation `1` | Fresh CMS expectation and chosen initial deployment identity | Fixture's actual selected target/identity |
| `edfi_cdc`, `postgres`, `cdc_reader`, masked setup connection | Dedicated new database, setup/connector identities above | Matching owned database and principals |
| Endpoints, worker values, `./eng/docker-compose/.env` | Selected Compose values above | Matching fixture host/container endpoints, environment and worker |
| `5000`, `10000000` | Example lag policy (ms) and record ceiling (bytes), not capacity qualification | Fixture policy supported by the controller |

<!-- cdc-snippet: cdc-pg-settings -->
```powershell
$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Location).Path
$settingsPath = Join-Path $repoRoot '.local/cdc/postgresql.json'
$statePath = Join-Path $repoRoot '.local/cdc/state-pg'
if (Test-Path -LiteralPath $settingsPath) { throw 'Use retained settings; do not overwrite an existing deployment.' }
$settings = Get-Content './.local/cdc/dms-base.json' -Raw | ConvertFrom-Json -AsHashtable
$settings.AppSettings.Datastore = 'postgresql'
$settings.AppSettings.MultiTenancy = $false
$settings.AppSettings.RouteQualifierSegments = ''
$settings.DataManagement.DocumentCache.Targets = @(@{ DataStoreId = 1 })
$settings.Cdc = @{
    Provider = 'postgresql'; DeploymentKey = 'local'; TenantKey = ''
    DataStoreId = '1'; InstanceKey = 'datastore-1'; Generation = 1
    TopicPrefix = 'edfi'; PartitionCount = 1
    Schemas = @() # Bootstrap replaces with all staged core/extension schema paths.
    SetupPrincipal = 'postgres'; DatabaseConnectorPrincipal = 'cdc_reader'
    SetupConnectionString = Read-Host 'Setup connection string for edfi_cdc' -MaskInput
    ConnectEndpoint = 'http://127.0.0.1:8083'
    WorkerMetricsEndpoint = 'http://127.0.0.1:9404/metrics'
    KafkaBootstrapServers = 'dms-kafka1:9092'
    KafkaAdminBootstrapServers = '127.0.0.1:9092'
    MaxRecordBytes = 10000000; LagThresholdMilliseconds = 5000
    DurabilityProfile = 'LocalSingleBroker'
    AuthorizationProfile = 'AuthorizationDisabledLocal'
    Worker = @{
        Key = 'local-worker'; OffsetStorageTopic = 'dms-connect-offsets'
        HeapBytes = 536870912; Principal = 'worker'
        ConnectorPrincipal = 'connector'; AdministratorPrincipal = 'administrator'
    }
    Consumers = @()
    ProviderConnectionProperties = @{
        'database.hostname' = 'dms-postgresql'; 'database.port' = '5432'
        'database.dbname' = 'edfi_cdc'; 'database.user' = 'cdc_reader'
        'database.password' = '${env:CDC_DATABASE_PASSWORD}'
    }
    KafkaClientSecurityProperties = @{}; KafkaAdminProperties = @{}
    Compose = @{
        File = Join-Path $repoRoot 'eng/docker-compose/kafka-cdc.yml'
        EnvironmentFile = (Resolve-Path './eng/docker-compose/.env').Path
        Project = 'dms-local'; BrokerSizeOverrideFile = Join-Path $statePath 'broker-size.json'
    }
    Timing = @{
        CallMilliseconds = 30000; WaitMilliseconds = 300000
        PollMilliseconds = 1000; MaximumObservationAgeMilliseconds = 10000
    }
}
$null = New-Item -ItemType Directory -Path $statePath -Force
& chmod 700 $statePath
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect state directory.' }
$settings | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $settingsPath
& chmod 600 $settingsPath
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect settings file.' }
```
<!-- /cdc-snippet: cdc-pg-settings -->

This writes full normal settings plus `Cdc`, not a standalone partial fragment.
Choose call/wait budgets before creating the retained snapshot. A cold broker or
worker can exceed the example's 30-second call budget; the live setup fixture uses
120-second calls and a 600-second bounded wait. These are declared fixture policy
substitutions, not a changed controller default or an instruction to edit retained
configuration after admission. An initial timeout retains the original workflow.
It is a **wrapper input**: the empty `Schemas` array is deliberately filled by staging
and must not be passed directly to the CLI. Bootstrap snapshots it to owner-only
`eng/docker-compose/.bootstrap/cdc-runtime/bootstrap-*.settings.json`, replacing
`Cdc.Schemas`, `Cdc.Compose`, `AppSettings.UseApiSchemaPath` and `ApiSchemaPath`
with staged paths and the selected environment/project. The matching DMS Compose
override carries DocumentCache settings and CMS credentials, translating the CMS
URL to the container address. Only known CMS database addresses are translated to
published loopback ports for the host-side projection runtime.

The setup connection and CMS endpoint are **host-reachable**. Connector
`database.hostname` and `KafkaBootstrapServers` are **worker-reachable** container
addresses. The single-quoted `${env:CDC_DATABASE_PASSWORD}` is resolved by the
worker's enabled EnvVarConfigProvider; it is not a PowerShell substitution or a
literal connector password. Direct CLI commands support `DMS_CDC__` overrides in
memory, but wrappers reject them to keep snapshots and eventual DMS aligned. Supply
wrapper controller secrets in protected input settings. See the
[security owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).

### Enable and observe

PowerShell, repository root; requires all preceding preparation. Choose this local
build variant **or** the published variant below. Initial provisioning creates a
new database, controller state and Kafka artifacts within the owned offline window.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| Settings/state/environment paths, `edfi_cdc` | Prepared settings and matching new database above | Same owned fixture inputs |
| `5.2`, `self-contained` | Chosen schema overlay and identity provider | Matching supported schema/identity inputs in every phase |

<!-- cdc-snippet: cdc-pg-bootstrap-local -->
```powershell
pwsh ./eng/docker-compose/bootstrap-local-dms.ps1 -DatabaseEngine postgresql `
    -EnableKafkaCdc -SeparateConfigDatabase -DataStoreDatabaseName edfi_cdc `
    -CdcSettingsPath './.local/cdc/postgresql.json' -CdcBindingStatePath './.local/cdc/state-pg' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env').Path `
    -DataStandardVersion '5.2' -IdentityProvider self-contained
if ($LASTEXITCODE -ne 0) { throw 'CDC bootstrap failed; retain state and keep writers excluded.' }
```
<!-- /cdc-snippet: cdc-pg-bootstrap-local -->

PowerShell, repository root; same prerequisites and substitutions as the local
snippet, but requires the **published** infrastructure preparation and matching
published application/schema-tool inputs. This selects project `dms-published`;
the wrapper replaces the input Compose project accordingly. Run in its own fresh
workspace; local and published examples are alternatives, not sequential steps.

<!-- cdc-snippet: cdc-pg-bootstrap-published -->
```powershell
pwsh ./eng/docker-compose/bootstrap-published-dms.ps1 -DatabaseEngine postgresql `
    -EnableKafkaCdc -SeparateConfigDatabase -DataStoreDatabaseName edfi_cdc `
    -CdcSettingsPath './.local/cdc/postgresql.json' -CdcBindingStatePath './.local/cdc/state-pg' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env').Path `
    -DataStandardVersion '5.2' -IdentityProvider self-contained
if ($LASTEXITCODE -ne 0) { throw 'CDC bootstrap failed; retain state and keep writers excluded.' }
```
<!-- /cdc-snippet: cdc-pg-bootstrap-published -->

Local `-InfraOnly` completes CDC admission but leaves DMS offline; published bootstrap
has no `-InfraOnly` parameter. For an IDE continuation use the same target, CMS and
staged schema settings only after admission. `-DmsBaseUrl`, `-NoDataStore`, route-qualified
and multiple targets are rejected for initial CDC bootstrap. Optional `-LoadSeedData`
uses the wrapper's normal seed inputs and runs only after admission and DMS startup;
do not start a separate seed process while waiting or combine it with an offline-only
continuation. Optional `-EnableKafkaUI` does not replace controller readiness.

The wrapper prints `Inspect CDC: api-schema-tools cdc status ...` with the **retained**
settings and original state paths. Copy those exact paths for subsequent operations;
do not choose the newest file by timestamp or return to the un-staged input settings.
Preserve the whole retained handoff, including `.bootstrap` schema/configuration data,
`.cdc-deployments/<project>.json`, all custom controller roots and broker-size override.
See [deployment state](#deployment-state) (documented; live exercise pending).

PowerShell, repository root; `api-schema-tools` is the matching installed/resolved
SchemaTools executable. Requires retained paths from the wrapper's printed command.
Both commands observe and may persist incidents/stop connectors; they are not
side-effect-free health probes. No additional offline window is needed for ordinary
observation; an unfinished initial workflow must keep its existing writer exclusion.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>`, `<original-state-root>` | Exact emitted settings/state paths, including after a failed admission | Actual fixture-emitted paths, never generated provenance |
| `20` | Example bounded watch pass count; timing comes from retained settings | Positive fixture pass bound |

<!-- cdc-snippet: cdc-pg-status -->
```powershell
api-schema-tools cdc status --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-pg-status -->

PowerShell, repository root; same prerequisites, observation effects and substitutions
as `cdc-pg-status`; `20` is a bounded observation choice, not a recovery deadline.

<!-- cdc-snippet: cdc-pg-watch -->
```powershell
api-schema-tools cdc watch --settings '<retained-settings-path>' --state-path '<original-state-root>' --maximum-passes 20 --json
```
<!-- /cdc-snippet: cdc-pg-watch -->

See [checked JSON excerpts](#serialized-result-examples) for the output shapes.
The CLI writes one final JSON envelope to stdout (`operation`, `succeeded`,
`exitCode`, `diagnostics`, and operation-specific `data`; `binding` and
`deploymentProfile` when available). Diagnostics and watch passes go to stderr.
Retain the two streams separately in restricted files; publish only sanitized results.
[Host serialization](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/Cdc/CdcCommandHost.cs)
uses camel-case properties and string enum values; null properties may be omitted.
The local profile reports `deploymentProfile.aclIsolationProven: false`.

The current standalone CLI observes durable lifecycle, queue and provider state;
it does not import the hosted DMS worker's execution observations. The
[standalone runtime](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcProjectionRuntime.cs)
can therefore report projection health/caught-up as `Unknown` with
`RuntimeNotObserved`, even for an empty queue and a healthy hosted DMS. The
[CDC evaluator](../../src/dms/core/EdFi.DataManagementService.Core/DocumentCache/Cdc/CdcTargetStatusEvaluator.cs)
then reports `projection.state: "Unknown"`, aggregate `NotReady`, exit `1`, while
provider/connector components can be satisfied. Retain that uncertainty; do not
reinterpret HTTP health or initial writer-publication authority as a current ready
observation. The [readiness owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope)
distinguishes initial publication from later observational status.

| Operation/result | Completion/action |
| --- | --- |
| Initial `enable` | Wrapper verifies the publication fields in the procedure record against the provisioning workflow. It does not accept `data.aggregate.readiness` as writer authority. |
| `status` / `watch` | Exit `0` means `data.aggregate.readiness` is `"Ready"` for the current/final observation. Inspect `data.targets[].observedAt`, `status`, `details`, `diagnostics`, `incidentPersistence`, `containment` and `recovery`; missing lag/evidence is not zero lag. Watch emits its passes on stderr and returns the last observation. |
| Exit `1` | Rejected, not ready, unavailable or timed out; inspect diagnostics and retained workflow. Keep initial writers stopped. `incidentPersistence: "Failed"` or `containment: "Failed"` means attempted containment was incomplete. |
| Exit `2` | Invalid command/configuration. Correct the input discrepancy without changing target, provenance or original state; bootstrap selected-ID mismatch requires the selection handoff above. |
| Exit `130` | Cancellation. Reconcile retained state before retry; do not assume effects were rolled back. |

A status result cannot release initial writers, start a stopped connector, or certify
an unsampled recovery interval. These boundaries belong to
[managed/native recovery](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).
For an interrupted initial workflow follow [initial retry](#initial-enable-retry);
for an admitted deployment follow [established validation](#established-validation)
and [managed stop/start](#managed-lifecycle). The delivered [SchemaTools managed lifecycle reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#managed-stack-lifecycle)
also lists the shipped commands. Never substitute raw Connect mutation, state deletion,
or another provisioning run for those controller operations. Ordinary API routing
is not gated by CDC status; see [readiness scope](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope).

<a id="sql-server-setup"></a>

## SQL Server local setup

**Local and published setup passed [T17 live qualification](cdc-inv-evidence.md#sql-server-setup-qualification-t17).** Use the same
[owned local profile](README.md#supported-deployment), Linux/Docker/toolchain,
private-file protections, writer/seed exclusion, complete normal DMS settings and
CMS-selection rules in [shared preparation](#prepare-the-owned-deployment). Follow
the [SQL Server contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server)
and [initial-admission owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).

| Record | Value |
| --- | --- |
| Target/generation | Default tenant, SQL Server, fresh CMS-selected target `1` / `datastore-1`, generation `1`, dedicated new `edfi_cdc`. The original managed CREATE supplies source identity and receipt. |
| Authority/offline window | Deployment owner controls the entire local server/network, CMS and all writers; setup administrator `sa` has database creation, CDC, principal-metadata and owned-local server prerequisite authority. Keep every writer/seed offline until matching writer-publication authorization. Connector login has no such authority. |
| Retained inputs | Full `.local/cdc/sqlserver.json`, protected base/effective environment, matching staged core/extensions, original `.local/cdc/state-sqlserver`, emitted `.bootstrap/cdc-runtime` settings/Compose override, `.cdc-deployments` inventory and `broker-size.json`. Retain these after failure too. |
| Invocation | `cdc-sqlserver-infrastructure`, deployment-owned restricted login preparation, `cdc-sqlserver-settings`, exactly one of `cdc-sqlserver-bootstrap-local` / `cdc-sqlserver-bootstrap-published`, then `cdc-sqlserver-status` / `cdc-sqlserver-watch`. Repository root throughout. |
| JSON/exit status | Wrapper progress is text. Internal `enable` requires exit `0`, `operation: "enable"`, `succeeded: true`, `exitCode: 0`, matching `data.workflowId`, non-default `data.authorizedAt`. Status/watch use the shared observation envelope/codes below. |
| Postcondition | Temporary projector stopped and matching writer publication authorized; wrapper starts DMS unless local `-InfraOnly`; optional seed follows admission and DMS startup. Database-user mapping alone does not complete admission. |
| Rejection/timeout action | Keep writers excluded; preserve original state, effective settings, sanitized diagnostics. Use the failure table below, [initial retry](#initial-enable-retry), or [unsupported provenance](#unsupported-provenance); do not recreate the database, remap a user, or delete history to retry. |

### Prepare SQL Server infrastructure and login

Use [mssql.yml](../../eng/docker-compose/mssql.yml)'s SQL Server **2025** Developer
image (`mcr.microsoft.com/mssql/server:2025-latest`), not an old 2022 volume/container.
The immutable SQL Server and Connect images actually exercised for initial mapping
are recorded in [T29 evidence](cdc-inv-evidence.md#sql-server-initial-user-mapping-t29)
and its [image manifest](evidence/t29-sqlserver-initial-user-mapping.json).
The Compose SQL Server tag is mutable; retain the resolved image identity in each
qualification result rather than treating the tag as proof of the tested digest.
Use the qualified Connect image in [kafka-cdc.yml](../../eng/docker-compose/kafka-cdc.yml)
and the same broker/worker endpoints, profile and heap as shared preparation.
The shipped [mssql-cdc.yml](../../eng/docker-compose/mssql-cdc.yml) overlay enables
SQL Server Agent when `-CdcDatabaseInfrastructure` or CDC bootstrap is selected.
A successful SQL connection or container/HTTP health check does not prove Agent,
projection prerequisites, capture progress or CDC readiness.

Prepare the protected `eng/docker-compose/.env` with `DMS_DATASTORE=mssql`,
`DMS_CONFIG_DATASTORE=mssql`, effective `MSSQL_PORT=1435`,
`MSSQL_DB_NAME=edfi_datamanagementservice` and a private `MSSQL_SA_PASSWORD`, plus
normal CMS/identity settings and the connector's `CDC_DATABASE_PASSWORD`.
The shipped [engine resolver](../../eng/docker-compose/env-utility.psm1) applies
[.env.mssql](../../eng/docker-compose/.env.mssql), preserving nonblank custom MSSQL
credentials/names/ports **when the base file declares MSSQL** and producing an effective file under `.derived` when needed.
Reconcile ambient overrides before preparation. Retain base and effective files;
all phases must use the same resolved values. SQL Server hosts CMS too; use
`-SeparateConfigDatabase` so the dedicated CMS database differs from the new CDC
source. `edfi_cdc` must not exist or alias any infrastructure/CMS/system database.
Keep the fresh CMS/no-prior-inserts condition for the example ID `1`; the shared
CMS selection instructions explain authenticated inspection and rejection before
provisioning. Do not pre-register a store just to discover an ID.

PowerShell, repository root; starts owned infrastructure with DMS excluded. Requires
protected environment and exclusive ownership from shared preparation. Substitutions:

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `./eng/docker-compose/.env` | Protected selected SQL Server environment above | Owned fixture environment, same effective values in every phase |
| `start-local-dms.ps1` | Local build choice; published alternative uses `start-published-dms.ps1` with the same switches in its own fresh workspace | Corresponding shipped wrapper |
| `mssql`, `self-contained` | Wrapper database token and chosen identity provider | Keep provider token; matching supported identity settings |

<!-- cdc-snippet: cdc-sqlserver-infrastructure -->
```powershell
$environmentFile = (Resolve-Path './eng/docker-compose/.env').Path
pwsh ./eng/docker-compose/start-local-dms.ps1 -InfraOnly -EnableConfig `
    -SeparateConfigDatabase -CdcDatabaseInfrastructure -DatabaseEngine mssql `
    -IdentityProvider self-contained -EnvironmentFile $environmentFile
if ($LASTEXITCODE -ne 0) { throw 'Infrastructure preparation failed; keep writers stopped.' }
```
<!-- /cdc-snippet: cdc-sqlserver-infrastructure -->

Before bootstrap, have the deployment's SQL Server administrator prepare an enabled
SQL login named `cdc_reader` on this server using its protected credential tooling.
Its password must match the worker's `CDC_DATABASE_PASSWORD`. Grant no server-role
membership, ownership, elevated server permissions or document writes. Login creation
and password rotation are deployment-owned; neither wrapper nor provider performs them.
Do **not** create the target database or a target database user ahead of bootstrap.
During managed initial provider setup, the provider creates the same-name user for
this existing login, verifies the SID/type and applies narrow source/CDC/heartbeat
access. Set both `Cdc.DatabaseConnectorPrincipal` and connector `database.user` to
`cdc_reader`; arbitrary different login/user mappings are not supported by this path.
No operator pause or callback between provisioning and admission is required.

### Prepare complete SQL Server settings

Prepare `.local/cdc/dms-base.json` with full normal DMS settings and this deployment's
CMS client credentials, encryption key, authentication and projector options, as in
shared preparation. CMS is host-reachable at `http://127.0.0.1:8081` in this example.
The settings construction below retains those normal settings; it is not a partial
`Cdc` file. Local and published examples both select Data Standard `5.2`; stage the
same core/extensions that the eventual DMS host uses.

PowerShell, repository root; requires protected base settings, environment, existing
restricted login and owner-only `.local/cdc` directory. Creates new settings/state;
use retained inputs for retries. The masked setup connection has the shape
`Server=127.0.0.1,1435;Database=edfi_cdc;User Id=sa;Password=…;Encrypt=true;TrustServerCertificate=true;Command Timeout=180`.
Use a connection-string builder for escaped values. The 180-second SQL command and
controller call budgets match; the bounded admission wait is 600 seconds. The
one-minute observation-age policy includes live read-back and projector shutdown,
as in the existing SQL Server qualification fixture. Each retry still collects new
observations; expiry never supplies publication authority.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `.local/cdc/dms-base.json`, `.local/cdc/sqlserver.json`, `.local/cdc/state-sqlserver` | Complete private normal settings, new wrapper input and original state root | Owned fixture full settings/paths; never fabricated receipts |
| `1`, `datastore-1`, `local`, generation `1` | Fresh CMS expectation and initial deployment identity | Actual selected fixture identity in both target settings |
| `edfi_cdc`, `sa`, `cdc_reader`, masked connection | Dedicated new database, setup administrator and prepared same-name login/user | Matching owned database/principals/host port |
| Endpoints, worker values, environment path | Same local profile as shared preparation; host SQL port `1435`, worker `dms-mssql:1433` | Matching host/container fixture endpoints and environment |
| `5000`, `10000000`, timing values | Example lag policy (ms), record ceiling (bytes), bounded timeouts (ms); no scale claim | Supported fixture policy/budgets |

<!-- cdc-snippet: cdc-sqlserver-settings -->
```powershell
$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Location).Path
$settingsPath = Join-Path $repoRoot '.local/cdc/sqlserver.json'
$statePath = Join-Path $repoRoot '.local/cdc/state-sqlserver'
if (Test-Path -LiteralPath $settingsPath) { throw 'Use retained settings; do not overwrite an existing deployment.' }
$settings = Get-Content './.local/cdc/dms-base.json' -Raw | ConvertFrom-Json -AsHashtable
$settings.AppSettings.Datastore = 'mssql'
$settings.AppSettings.MultiTenancy = $false
$settings.AppSettings.RouteQualifierSegments = ''
$settings.DataManagement.DocumentCache.Targets = @(@{ DataStoreId = 1 })
$settings.Cdc = @{
    Provider = 'sqlserver'; DeploymentKey = 'local'; TenantKey = ''
    DataStoreId = '1'; InstanceKey = 'datastore-1'; Generation = 1
    TopicPrefix = 'edfi'; PartitionCount = 1
    Schemas = @() # Bootstrap replaces with all staged core/extension schema paths.
    SetupPrincipal = 'sa'; DatabaseConnectorPrincipal = 'cdc_reader'
    SetupConnectionString = Read-Host 'Setup connection string for edfi_cdc' -MaskInput
    ConnectEndpoint = 'http://127.0.0.1:8083'
    WorkerMetricsEndpoint = 'http://127.0.0.1:9404/metrics'
    KafkaBootstrapServers = 'dms-kafka1:9092'
    KafkaAdminBootstrapServers = '127.0.0.1:9092'
    MaxRecordBytes = 10000000; LagThresholdMilliseconds = 5000
    DurabilityProfile = 'LocalSingleBroker'
    AuthorizationProfile = 'AuthorizationDisabledLocal'
    Worker = @{
        Key = 'local-worker'; OffsetStorageTopic = 'dms-connect-offsets'
        HeapBytes = 536870912; Principal = 'worker'
        ConnectorPrincipal = 'connector'; AdministratorPrincipal = 'administrator'
    }
    Consumers = @()
    ProviderConnectionProperties = @{
        'database.hostname' = 'dms-mssql'; 'database.port' = '1433'
        'database.names' = 'edfi_cdc'; 'database.user' = 'cdc_reader'
        'database.password' = '${env:CDC_DATABASE_PASSWORD}'
        'driver.encrypt' = 'true'; 'driver.trustServerCertificate' = 'true'
    }
    KafkaClientSecurityProperties = @{}; KafkaAdminProperties = @{}
    Compose = @{
        File = Join-Path $repoRoot 'eng/docker-compose/kafka-cdc.yml'
        EnvironmentFile = (Resolve-Path './eng/docker-compose/.env').Path
        Project = 'dms-local'; BrokerSizeOverrideFile = Join-Path $statePath 'broker-size.json'
    }
    Timing = @{
        CallMilliseconds = 180000; WaitMilliseconds = 600000
        PollMilliseconds = 1000; MaximumObservationAgeMilliseconds = 60000
    }
}
$null = New-Item -ItemType Directory -Path $statePath -Force
& chmod 700 $statePath
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect state directory.' }
$settings | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $settingsPath
& chmod 600 $settingsPath
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect settings file.' }
```
<!-- /cdc-snippet: cdc-sqlserver-settings -->

The wrapper/application token is `mssql`; `Cdc.Provider` is `sqlserver`.
`database.names` contains exactly the one selected database, not `database.dbname`
or a multi-database list. Setup credentials use the host's published SQL port;
connector credentials use the container's `1433`. The certificate trust setting is
for this local self-signed container. `${env:CDC_DATABASE_PASSWORD}` is a literal
worker-resolved secret reference, not a PowerShell expansion. Keep it externalized.
Generated capture/key/snapshot/schema-history properties belong to the shipped
[connector contract](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands);
do not hand-render them here.

As in PostgreSQL setup, `Schemas = @()` is a **wrapper input** placeholder. Bootstrap
stages all core/extensions and emits private runtime settings, including the resolved
Compose environment/project and host port. Subsequent CLI commands must use the
emitted settings and original state root, not this un-staged input. Wrappers reject
`DMS_CDC__*` overrides; direct CLI override support does not change that rule.
Preserve the full [deployment-state inventory](#deployment-state).

### Enable SQL Server and observe

PowerShell, repository root; requires all preceding preparation with no target
database/user and every writer excluded. Choose local **or** published, never both
on the same source. Substitutions: the settings/state/environment/database/identity
inputs above and matching `5.2` schema overlay in every phase. Both commands perform
managed CREATE, initial provider mapping/admission, then DMS startup.

<!-- cdc-snippet: cdc-sqlserver-bootstrap-local -->
```powershell
pwsh ./eng/docker-compose/bootstrap-local-dms.ps1 -DatabaseEngine mssql `
    -EnableKafkaCdc -SeparateConfigDatabase -DataStoreDatabaseName edfi_cdc `
    -CdcSettingsPath './.local/cdc/sqlserver.json' -CdcBindingStatePath './.local/cdc/state-sqlserver' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env').Path `
    -DataStandardVersion '5.2' -IdentityProvider self-contained
if ($LASTEXITCODE -ne 0) { throw 'CDC bootstrap failed; retain state and keep writers excluded.' }
```
<!-- /cdc-snippet: cdc-sqlserver-bootstrap-local -->

PowerShell, repository root; same substitutions, but requires published infrastructure
and matching published application/schema-tool inputs in its own fresh workspace.
For qualification, `DMS_IMAGE_TAG` may select fixture-packaged images built from this
same branch and tagged under the published Compose image names; this exercises the
published wrapper, not a registry release. Record their immutable local image IDs.
The wrapper selects project `dms-published` in the emitted settings.

<!-- cdc-snippet: cdc-sqlserver-bootstrap-published -->
```powershell
pwsh ./eng/docker-compose/bootstrap-published-dms.ps1 -DatabaseEngine mssql `
    -EnableKafkaCdc -SeparateConfigDatabase -DataStoreDatabaseName edfi_cdc `
    -CdcSettingsPath './.local/cdc/sqlserver.json' -CdcBindingStatePath './.local/cdc/state-sqlserver' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env').Path `
    -DataStandardVersion '5.2' -IdentityProvider self-contained
if ($LASTEXITCODE -ne 0) { throw 'CDC bootstrap failed; retain state and keep writers excluded.' }
```
<!-- /cdc-snippet: cdc-sqlserver-bootstrap-published -->

Local `-InfraOnly` completes admission and leaves DMS offline; published bootstrap
has no `-InfraOnly`. An IDE continuation must use the same CMS target/staged schema
and wait for writer authorization. Optional `-LoadSeedData` runs only after admission
and DMS startup. The shared exclusions (`-NoDataStore`, `-DmsBaseUrl`, route-qualified
or multiple targets) also apply here.

Copy exact settings/state paths from the wrapper's printed `Inspect CDC` command.
PowerShell, repository root; matching `api-schema-tools` executable and retained
handoff required. Substitutions: `<retained-settings-path>` / `<original-state-root>`
are actual wrapper-emitted paths; a fixture must use its own emitted paths. `20` is
a positive watch-pass bound, not a readiness deadline. These observations may persist
incidents and stop connectors. Keep any unfinished initial writer exclusion in place.

<!-- cdc-snippet: cdc-sqlserver-status -->
```powershell
api-schema-tools cdc status --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-sqlserver-status -->

PowerShell, repository root; same retained inputs, substitutions and effects as status.

<!-- cdc-snippet: cdc-sqlserver-watch -->
```powershell
api-schema-tools cdc watch --settings '<retained-settings-path>' --state-path '<original-state-root>' --maximum-passes 20 --json
```
<!-- /cdc-snippet: cdc-sqlserver-watch -->

Use the [shared output and exit-code table](#enable-and-observe): `status`/`watch`
exit `0` means current/final `data.aggregate.readiness: "Ready"`; it cannot release
initial writers. Inspect target `observedAt`, `details`, `diagnostics`,
`incidentPersistence`, `containment` and `recovery`. Keep final stdout JSON and stderr
passes/diagnostics separately and privately. Exit `1` covers rejection/not-ready/timeout,
`2` invalid input and `130` cancellation. The local profile still reports
`deploymentProfile.aclIsolationProven: false`.
After admission, use [established validation](#established-validation) and
[managed stop/start](#managed-lifecycle). Stop/resume retains offsets and internal
schema history; raw Connect mutation is not the managed handoff.

### SQL Server prerequisite and failure handoffs

| Boundary or symptom | Required action and completion |
| --- | --- |
| Projection initialization / activation | RCSI and server `nested triggers` are projection prerequisites. Owned-local managed creation prepares them before schema/source admission; retained-schema retry inspects without repair. See [managed prerequisite tests](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcProjectionPrerequisiteTests.cs). A CREATE receipt followed by failed schema preparation is retained evidence, not permission to provision again. |
| `sqlServerPrerequisiteFailed` at target initialization with lifecycle `Disabled` | Keep writers excluded; use E18 [SQL Server prerequisite correction](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction), restart the target context and retry activation only under that procedure's authority. Do not turn it into a CDC-state reset. |
| Activation preflight rejection | Preflight changes no lifecycle/cache/work/latch/provider setting; the E18 owner permits correction and retry. Failed initial CDC workflow still needs its original [retry classification](#initial-enable-retry). |
| Prerequisite failure in `Tracking`, `Resetting` or `Rebuilding`; change after successful active validation | `unsupportedPrerequisiteIncident`: no supported correction-and-restart workflow or renewed projection-health/CDC-readiness guarantee. Contain/escalate through [projection handoff](#projection-handoff); post-validation prerequisite changes are outside v1 support. |
| Agent, capture/cleanup jobs, retained LSN range | These are separate CDC prerequisites. The selected infrastructure enables Agent; initial provider setup owns expected capture artifacts/jobs and validates their state/progress. A running SQL service proves none of these. Preserve provider diagnostics and use [monitoring/retention](#monitoring-retention); do not drop/recreate captures or reset offsets. |
| Snapshot isolation / row versions | New-database provisioning enables `ALLOW_SNAPSHOT_ISOLATION` as well as RCSI. CDC requires snapshot isolation for its initial snapshot; RCSI alone is insufficient. `CDC_SQLSERVER_SNAPSHOT_ISOLATION_OFF` rejects readiness. Preserve state and resolve the prerequisite under the initial/established boundary rather than forcing admission. |
| Internal schema history unavailable/inconsistent or LSN history lost | Internal Kafka schema history is required even with public schema-change events disabled. Retain it with offsets on ordinary stop/start. Established source-history incidents route to [unsupported provenance](#unsupported-provenance); no silent history recreation or same-binding resnapshot. |
| `CDC_SQLSERVER_CONNECTOR_LOGIN_MISSING`, `CDC_SQLSERVER_CONNECTOR_LOGIN_UNSUPPORTED`, `CDC_SQLSERVER_CONNECTOR_LOGIN_ELEVATED` | Deployment owner reviews the prepared login, type and effective server access; keep writers excluded. No automatic login/credential management or access broadening. Retry only if original workflow classification permits it. |
| `CDC_SQLSERVER_CONNECTOR_USER_MAPPING_MISMATCH`, elevated database-access rejection, or `CDC_SQLSERVER_CONNECTOR_USER_MISSING` | Preserve identity/access evidence privately and escalate. Existing users must match login SID/type and narrow effective access. Once provider completion is durable, even pre-registration retry is validation-only and must not recreate or remap a missing/conflicting user. |
| `CDC_SQLSERVER_SETUP_PRINCIPAL_FAILURE` | Setup administrator needs principal-definition visibility (`VIEW ANY DEFINITION`, implied by `sa` here), user-creation and provider setup authority. Correct setup authority without elevating the connector; retain state and follow initial retry classification. |

The owning [SQL Server contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server)
and [T29 implementation evidence](cdc-inv-evidence.md#sql-server-initial-user-mapping-t29)
define the mapping/retry boundary. [T17 procedure evidence](cdc-inv-evidence.md#sql-server-setup-qualification-t17)
exercises local, published and direct E2E setup from a restricted login with no target
database or precreated user. Published qualification uses matching branch images,
not a registry release.

<a id="dms-e2e-setup"></a>

## DMS E2E opt-in

**PostgreSQL and SQL Server direct setup passed T16/T17; build alternatives remain unexercised.** These alternatives qualify setup wiring. API-driven message
scenarios remain [DMS-1325](../design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md).
[Local bootstrap/CI owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).
This is the DMS E2E suite, not Instance Management E2E.

| Record | Value |
| --- | --- |
| Target/generation | One fresh CMS-selected target for `E2E_DATABASE_NAME`; default `edfi_datamanagementservice_e2e`. Separate `E2E_SNAPSHOT_DATABASE_NAME` is not the CDC source. |
| Authority/offline window | Same exclusive deployment and initial writer/seed exclusion as the selected provider setup; test processes start only after controller admission and DMS startup. |
| Retained inputs | Full E2E DMS/CDC settings, original state root, base/effective environment and overlays, E2E core/extensions, emitted settings/receipts/inventory; `.cdc-diagnostics` on failure. |
| Invocation | PostgreSQL: `cdc-pg-e2e-setup` then optional `cdc-pg-e2e-test`, **or** `cdc-pg-e2e-build` on a separate fresh workspace. SQL Server: `cdc-sqlserver-e2e-setup` or `cdc-sqlserver-e2e-build` with the SQL Server variant below. |
| JSON/exit status | Wrappers print progress, not a CLI JSON envelope. Internal admission requires the same matching `enable` publication result as local setup. On failure, the sanitized `e2e-setup` artifact includes `operation`, `succeeded`, `cancelled`, `cleanup`, `provider`, `failureCodes`. |
| Postcondition | Managed primary receipt retained, separate snapshot prepared with matching schema, CDC admitted before DMS/tests; selected setup-smoke tests pass without source-reset hooks. This does not qualify message scenarios. |
| Rejection/timeout action | Do not launch tests. Retain `.cdc-diagnostics` and original settings/state. `cleanup: "Stopped"` means governed stop completed; `"RetainedForReconciliation"` means stop failed and infrastructure remains for reconciliation; `"NotStarted"` is not proof of shutdown. Use status/initial-retry handoffs above and governed teardown below. |

### Prepare the E2E variant

Use a fresh owned stack/workspace, not the already admitted `edfi_cdc` example. Reuse
[PostgreSQL preparation](#postgresql-setup) with **all** substitutions below, or use
the [SQL Server variant](#sql-server-e2e-variant) for that provider. Complete the selected
provider's infrastructure/login preparation before the E2E entry point. Neither E2E wrapper
creates the restricted connector login for you. The initial ID `1` assumption requires
the same brand-new CMS/no-prior-inserts condition; actual selected ID is checked before
provisioning. Keep DMS and test processes offline during preparation.

| Input | E2E value/source | Permitted fixture replacement |
| --- | --- | --- |
| Environment | Absolute path to protected `eng/docker-compose/.env.e2e`; review its CMS credentials, database ports, identity settings and worker secret | Owned E2E environment |
| Primary database | Effective `E2E_DATABASE_NAME`; default `edfi_datamanagementservice_e2e`. Set both `Cdc.SetupConnectionString` database and `ProviderConnectionProperties.database.dbname` to it | Fixture's dedicated new primary |
| Snapshot | Effective `E2E_SNAPSHOT_DATABASE_NAME`; must differ from primary and reserved infrastructure/CMS databases | Fixture's dedicated snapshot |
| Target/identity | Same CMS-selected ID in `DocumentCache.Targets` and `Cdc.DataStoreId`; example `1`/`datastore-1`, generation `1` | Actual fixture target/identity |
| Settings/state | Write the complete merged settings as `.local/cdc/postgresql-e2e.json`; use original `.local/cdc/state-pg-e2e` from first provisioning | Owned fixture paths |
| Schemas | Selected E2E environment's `SCHEMA_PACKAGES`, including Sample/Homograph test extensions; staged identically for primary, snapshot and runtime | Matching fixture core/extensions |
| Endpoints/principals | Effective E2E host ports, container endpoints, CMS credentials, setup principal and existing `cdc_reader` | Matching fixture values |

In `cdc-pg-settings`, replace the base settings with the complete normal settings for
this E2E deployment, the settings/state/environment paths with this table's paths,
and **both** database references (including the masked prompt input) with the E2E
primary. Do not reuse the local example's staged schema paths. The E2E wrapper uses
its environment-file schema authority and fills `Cdc.Schemas` with the staged E2E
core/extensions. It uses separate CMS topology, managed provisioning for the primary,
and the legacy reset provisioner only for the distinct snapshot before admission.

Supply absolute environment paths as below to avoid caller-directory ambiguity.
The direct setup resolves relative environment paths from the Compose directory;
the build entry point has its own resolver. Direct setup applies an optional
`-DataStandardVersion` overlay then the engine overlay. Build applies an optional
`-EnvironmentOverlayFile` first, then Data Standard, then engine. PostgreSQL's engine
step is a no-op. The examples omit overlays to retain the E2E file's test extensions.
If selecting an overlay, inspect its effective package list before starting; the same
resolved file must select CMS registration, primary/snapshot provisioning and testing.

Schema guards suppress ambient `USE_API_SCHEMA_PATH`, `API_SCHEMA_PATH` and
`SCHEMA_PACKAGES` during these phases; other Compose-resolved settings, including
`E2E_DATABASE_NAME`, can still be overridden by the process environment. Reconcile
those before preparing the settings. The build path derives test-process settings
from that effective environment; direct `dotnet test` needs matching database,
provider, ports, credentials, API/CMS URLs and container selection from
[E2E appsettings](../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/appsettings.json).
See the shipped [E2E handoff](../../eng/docker-compose/e2e-cdc.psm1).

PowerShell, repository root; requires the E2E variant above, healthy owned infrastructure
and the existing restricted role. Creates primary/snapshot and admits CDC. Substitutions
are exactly the E2E table; use `-SkipDockerBuild` only with matching already-built images.

<!-- cdc-snippet: cdc-pg-e2e-setup -->
```powershell
pwsh ./src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1 `
    -DatabaseEngine postgresql -EnableKafkaCdc `
    -CdcSettingsPath './.local/cdc/postgresql-e2e.json' `
    -CdcBindingStatePath './.local/cdc/state-pg-e2e' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env.e2e').Path
if ($LASTEXITCODE -ne 0) { throw 'CDC E2E setup failed; do not launch tests.' }
```
<!-- /cdc-snippet: cdc-pg-e2e-setup -->

PowerShell, repository root; optional test after **successful direct setup**, with
matching test-process settings above. No provisioning or source reset is requested.
The literal database is the default E2E primary; replace it with the same effective
`E2E_DATABASE_NAME` if customized. Fixture replacement: matching test database and
project build configuration; keep the setup-smoke filter.

<!-- cdc-snippet: cdc-pg-e2e-test -->
```powershell
$env:AppSettings__DataStoreDatabaseName = 'edfi_datamanagementservice_e2e'
Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
dotnet test ./src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj `
    --configuration Release --filter 'FullyQualifiedName~Given_CdcE2ESetup'
if ($LASTEXITCODE -ne 0) { throw 'CDC setup smoke test failed; retain diagnostics before governed teardown.' }
```
<!-- /cdc-snippet: cdc-pg-e2e-test -->

PowerShell, repository root; alternative full setup/test entry point in a **fresh**
workspace, using the E2E preparation/table above. Do not run this after direct setup:
retained CDC workspaces reject another E2E setup/reset. This build command propagates
test-process settings and clears unsupported `NODE_OPTIONS`. Substitutions: the same
E2E paths/settings table; `Release` is the selected build configuration. Optional
`-SkipDockerBuild` requires matching images; `-UsePublishedImage` selects published
infrastructure and requires preparing that alternative consistently.

<!-- cdc-snippet: cdc-pg-e2e-build -->
```powershell
pwsh ./build-dms.ps1 E2ETest -Configuration Release -DatabaseEngine postgresql `
    -IdentityProvider self-contained -EnableKafkaCdc `
    -CdcSettingsPath './.local/cdc/postgresql-e2e.json' `
    -CdcBindingStatePath './.local/cdc/state-pg-e2e' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env.e2e').Path `
    -TestFilter 'FullyQualifiedName~Given_CdcE2ESetup'
if ($LASTEXITCODE -ne 0) { throw 'CDC E2E setup/test failed; preserve the retained deployment.' }
```
<!-- /cdc-snippet: cdc-pg-e2e-build -->

The [setup-smoke fixture](../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/Cdc/CdcSetupSmokeTests.cs)
checks HTTP/database health without feature reset hooks. Do not broaden this example
to reset-based API tests and call it CDC message qualification. `E2ETest -LoadSeedData`
is rejected. On success or failure, use the retained settings/state for the observation
commands above. Retain sanitized failure artifacts under
`eng/docker-compose/.cdc-diagnostics`; an attempted stop is not a verified stop.

Finish with [governed stack/E2E teardown](#stack-teardown).
Use the setup wrapper's printed teardown command with its exact resolved environment
and provider, or the existing [managed lifecycle command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#managed-stack-lifecycle).
Governed retirement must complete while infrastructure is reachable before volume
deletion. Source history survives; the teardown procedure distinguishes protected/custom
settings from eligible generated runtime files. Ordinary stop/start uses
[managed lifecycle](#managed-lifecycle), not teardown. Subsequent E2E setup refuses a
protected retained workspace; finish retirement and archive its configuration/history
before preparing another one. Do not bypass that guard by deleting `.bootstrap` or
`.cdc-deployments`. The governing cleanup/continuity boundary is
[deployment-owned binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).

### SQL Server E2E variant

Use a **fresh owned workspace**, with the [common E2E requirements](#prepare-the-e2e-variant)
for primary/snapshot isolation, schemas, CMS selection, environment resolution and
retained state. Apply [SQL Server preparation](#sql-server-setup) using every
substitution below. Infrastructure and the restricted login must exist first; the
primary database and its database user must not. The provider creates the primary
user during managed admission. The E2E wrapper's snapshot preparation is separate
and must not be used to insert a manual primary-user creation step.

| Input in SQL Server preparation | E2E value/source | Permitted fixture replacement |
| --- | --- | --- |
| `./eng/docker-compose/.env` everywhere, including infrastructure and `Cdc.Compose.EnvironmentFile` | Protected `./eng/docker-compose/.env.e2e`, with effective MSSQL/CMS/worker credentials and `MSSQL_PORT` | Owned E2E environment with matching effective endpoints/secrets |
| Complete base settings | Full normal DMS settings for this E2E deployment, including CMS credentials and self-contained identity | Fixture complete normal settings, not a standalone `Cdc` fragment |
| `.local/cdc/sqlserver.json`, `.local/cdc/state-sqlserver` | `.local/cdc/sqlserver-e2e.json`, original `.local/cdc/state-sqlserver-e2e` | Owned fixture settings/state roots |
| `edfi_cdc` in masked setup connection and `database.names` | Effective `E2E_DATABASE_NAME`, default `edfi_datamanagementservice_e2e` | Same dedicated primary in CMS registration, provisioner, connector and tests |
| Snapshot | Effective `E2E_SNAPSHOT_DATABASE_NAME`, default `edfi_datamanagementservice_e2e_snapshot`; distinct from primary/CMS/system databases | Fixture snapshot; never the CDC target |
| `1` / `datastore-1`, generation `1` | Fresh CMS-selected identity in `DocumentCache.Targets` and `Cdc.DataStoreId` | Actual selected fixture identity; same fresh-CMS rules |
| Host port `1435`, worker `dms-mssql:1433`, setup `sa`, connector `cdc_reader` | Effective SQL Server environment and existing restricted login; both connector-principal settings match | Same owned fixture server and principal |
| `5.2` local bootstrap overlay | Omitted in the E2E commands: effective E2E `SCHEMA_PACKAGES` with Sample/Homograph extensions owns primary/snapshot/runtime schemas | Matching E2E core/extensions; inspect optional overlays before use |

Run `cdc-sqlserver-settings` with these substitutions; this constructs full executable
wrapper input for E2E. Keep application `AppSettings.Datastore = 'mssql'`,
`Cdc.Provider = 'sqlserver'`, connector `database.names` and `driver.*` TLS settings.
Use the host SQL port in the setup connection (including `Command Timeout=180`)
and the worker's internal `1433` in connector properties.

Both entry points use the shipped `.env.mssql` composition after any Data Standard
overlay; build additionally applies its optional `-EnvironmentOverlayFile` first.
Set `DMS_DATASTORE=mssql` and `DMS_CONFIG_DATASTORE=mssql` in the base E2E
environment before infrastructure preparation. The resolver preserves custom nonblank
MSSQL credentials/ports/names only when the base file declares MSSQL, replaces
PostgreSQL-shaped database connection strings and forces both datastore tokens to
`mssql`. Ambient values still take precedence in Compose: clear stale PostgreSQL
connection-string overrides before starting. Use the wrapper's retained effective
file, staged E2E schemas and selected CMS target together. Do not apply the local
`5.2` overlay if it would replace required test extensions.

PowerShell, repository root; direct setup alternative after SQL Server E2E preparation
above. Substitutions are exactly the table. Creates primary/snapshot and admits CDC
before DMS; optional `-SkipDockerBuild` requires matching local images.

<!-- cdc-snippet: cdc-sqlserver-e2e-setup -->
```powershell
pwsh ./src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1 `
    -DatabaseEngine mssql -EnableKafkaCdc `
    -CdcSettingsPath './.local/cdc/sqlserver-e2e.json' `
    -CdcBindingStatePath './.local/cdc/state-sqlserver-e2e' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env.e2e').Path
if ($LASTEXITCODE -ne 0) { throw 'CDC E2E setup failed; do not launch tests.' }
```
<!-- /cdc-snippet: cdc-sqlserver-e2e-setup -->

For direct testing after successful setup, use the shared `cdc-pg-e2e-test` command
with the same effective primary database, and first configure the test process for
SQL Server: `AppSettings__DatabaseEngine=mssql`, host-side
`AppSettings__DataStoreAdminConnectionString`, container-side
`AppSettings__DataStoreConnectionString` and `AppSettings__DataStoreSnapshotConnectionString`,
plus matching API/CMS URLs, credentials and container selection. Supply connection
strings privately, never as shell arguments. The default E2E appsettings is PostgreSQL
and cannot be used unchanged. Preserve `Given_CdcE2ESetup` as the smoke filter and
clear unsupported `NODE_OPTIONS`; this procedure does not qualify reset-based message
tests or Instance Management E2E.

PowerShell, repository root; **alternative** full setup/test command on another fresh
workspace after the same SQL Server preparation. Do not run after direct setup.
Substitutions: the table above plus `Release` build configuration. The build entry
point propagates resolved database/provider/connection settings to the test process
and clears unsupported `NODE_OPTIONS`. `-UsePublishedImage` requires corresponding
published infrastructure preparation; `-SkipDockerBuild` requires matching images.

<!-- cdc-snippet: cdc-sqlserver-e2e-build -->
```powershell
pwsh ./build-dms.ps1 E2ETest -Configuration Release -DatabaseEngine mssql `
    -IdentityProvider self-contained -EnableKafkaCdc `
    -CdcSettingsPath './.local/cdc/sqlserver-e2e.json' `
    -CdcBindingStatePath './.local/cdc/state-sqlserver-e2e' `
    -EnvironmentFile (Resolve-Path './eng/docker-compose/.env.e2e').Path `
    -TestFilter 'FullyQualifiedName~Given_CdcE2ESetup'
if ($LASTEXITCODE -ne 0) { throw 'CDC E2E setup/test failed; preserve the retained deployment.' }
```
<!-- /cdc-snippet: cdc-sqlserver-e2e-build -->

Admission/output/failure rules are the common E2E record above: preserve
`.cdc-diagnostics`, original state and retained settings; failed containment is not
successful cleanup. Use SQL Server status/watch with emitted paths, and the wrapper's
printed `mssql` teardown command for [governed teardown](#stack-teardown). Ordinary
[managed stop/start](#managed-lifecycle) preserves the admitted source. T17 qualifies
these setup commands; DMS-1325 owns API-driven message scenarios.

<a id="deployment-state"></a>

## Preserve deployment state

**PostgreSQL passed [T18](cdc-inv-evidence.md#postgresql-lifecycle-qualification-t18); SQL Server pending T19.** Apply to both providers from
first managed provisioning onward. The [continuity/adoption owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral)
defines why current artifact health cannot replace historical evidence.

| Record | Value |
| --- | --- |
| Target/generation | Every retained deployment/instance/data-store/generation and its original physical source; include peer entries sharing the worker. |
| Authority/offline window | Deployment owner may inspect file metadata without an API offline window. Coordinate access with the state owner; preservation or incident work must not race controller writes. Keep an existing initial writer fence or managed shutdown in place. |
| Retained inputs | Full inventory below, including custom/nested state roots and external settings/schema/Compose paths. |
| Invocation | `cdc-state-inventory` locates selected retained roots; review the private inventory against the original handoff. This is metadata inspection, not a state validator or backup utility. |
| JSON/exit status | PowerShell file metadata, no controller JSON. A missing/unreadable required path fails the command; successful listing proves only path access. |
| Postcondition | Deployment owner has accounted for original state and referenced inputs, permissions and peer ownership; no continuity or restart authorization follows from this inventory. |
| Rejection/timeout action | Preserve available files and access diagnostics; use [unsupported provenance](#unsupported-provenance) for missing required evidence. Do not create an empty replacement root. |

### Retention inventory

| Artifact | Location/source and preservation requirement |
| --- | --- |
| Controller state root | Exact absolute path emitted by bootstrap and stored as `StatePath` in the retained deployment entry; custom roots may be outside the checkout or nested within `.bootstrap`. Preserve the whole root, not selected JSON files. |
| Managed CREATE receipt and source association | Controller `workflows/` journal and `source-history/` records retain authoritative creation/source evidence; preserve the provisioning handoff too. An input connection string, surviving database or independently generated DDL is not a managed CREATE receipt. |
| Binding and workflow evidence | `bindings/` and `workflows/` retain immutable binding identity, stage intents/completions, connector establishment, provider identity, writer-publication intent and lifecycle/rollout/retirement progress. Retain `kafka-preparation/` and controller coordination files with their root; never clear a lock file as a retry procedure. |
| Terminal incidents | `incidents/` records are created when a loss is latched. Absence before any loss can be normal; absence does not prove that history was never deleted. Do not fabricate an empty incident record. |
| Source publication history | `source-history/` outlives binding retirement and preserves downstream history. Binding absence or removal from settings cannot reestablish initial eligibility or internal-only status. |
| Wrapper inventory | `eng/docker-compose/.cdc-deployments/<project>.json`, including every entry and its settings/state paths, hashes and bootstrap handoff. Do not edit it to bypass mismatches or drop peers. |
| Runtime settings and Compose override | Exact emitted files under `eng/docker-compose/.bootstrap/cdc-runtime/`; retain referenced staged schemas, effective environment and `eng/docker-compose/.bootstrap/bootstrap-manifest.json`. Original input JSON alone does not reproduce this handoff. |
| Broker-size override | The retained `Cdc:Compose:BrokerSizeOverrideFile` (bootstrap uses `<original-state-root>/broker-size.json`), including completed or partial size-rollout state. Do not regenerate it from older settings. |
| Prepared inputs | Original full DMS/CDC settings, environment files, worker secret references, matching core/extension schemas and Compose inputs explain the deployment. They are inputs, not substitutes for receipts, bindings or journals. Keep secrets private and retain owner-only permissions. |

The [wrapper lifecycle implementation](../../eng/docker-compose/cdc-lifecycle.psm1)
protects nested roots and peer-owned configuration. The entire `.bootstrap` tree is
**not disposable** for CDC. Generic workspace-reset advice does not authorize its
removal. Use the [governed teardown handoff](#stack-teardown) only when its independent
requirements are met. Preserve database and Kafka persistent storage under deployment
ownership too; filesystem inventory does not prove provider history or offset durability.

PowerShell, repository root; run as the deployment state owner. Metadata-only, no
fault injection. Prerequisite: original bootstrap output or privately reviewed retained
inventory identifies the paths. Inspect all entries in a shared deployment, repeating
this bounded listing for each referenced root; do not print settings or history contents.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<original-state-root>` | Original managed provisioning/controller root | Fixture's actual retained root |
| `<retained-settings-path>` | Emitted runtime settings path | Fixture-emitted settings |
| `<deployment-inventory-path>` | Actual `.cdc-deployments/<project>.json` | Fixture-created wrapper inventory |
| `<retained-bootstrap-root>` | Actual staged `.bootstrap` root | Fixture-owned staged root |

<!-- cdc-snippet: cdc-state-inventory -->
```powershell
Get-Item -LiteralPath '<original-state-root>', '<retained-settings-path>', '<deployment-inventory-path>', '<retained-bootstrap-root>' -Force -ErrorAction Stop |
    Select-Object FullName, Attributes, LastWriteTimeUtc
```
<!-- /cdc-snippet: cdc-state-inventory -->

Store this metadata privately; paths can identify a deployment. A state backup may be
retained for investigation, but restoring it is not a supported continuity recovery.
The CLI cannot detect every rollback or certify an older backup. Suspected rollback
or incident-history deletion remains an incident even when files parse and live checks pass.

<a id="initial-enable-retry"></a>

## Interrupted initial-enable retry

**Exact `cdc-enable-retry` snippet unexercised.** Intact wrapper retries have
[T16](cdc-inv-evidence.md#postgresql-setup-qualification-t16) and
[T17](cdc-inv-evidence.md#sql-server-setup-qualification-t17) setup evidence. Follow the
[initial retry classification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral)
and [initial-admission sequence](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).

| Record | Value |
| --- | --- |
| Target/generation | Same controller-proven new physical database, CMS-selected target and unfinished initial generation. No previously admitted or replacement source. |
| Authority/offline window | Original deployment owner retains exclusive writer/seed exclusion throughout retry. CMS, database, Kafka and worker dependencies must be reachable with the original authority. |
| Retained inputs | Original state, managed provisioning receipt/history, exact emitted runtime settings and referenced schemas/environment. Keep settings overrides consistent with the original target. |
| Invocation | `cdc-enable-retry`; the controller classifies and reconciles its journal. Do not rerun independent provisioning or construct stage receipts. |
| JSON/exit status | Completion requires process exit `0`, `operation: "enable"`, `succeeded: true`, `exitCode: 0`, `data.workflowId` matching the original workflow and non-default `data.authorizedAt`. Failure may omit `data`, `binding` and `deploymentProfile`. |
| Postcondition | Temporary projector is stopped and the matching writer-publication result has returned. Only this result completes the initial writer handoff; `Ready` status or a journal checkpoint does not. |
| Rejection/timeout action | Keep writers excluded; retain stdout/stderr separately. Reconcile the actual failure before retrying the same command. If writer-publication intent already exists, initial retry is closed; use established inspection and escalate any uncertain writer handoff rather than replaying intent as permission. |

The shipped [initial workflow](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcInitialEnableWorkflow.cs)
and [retry fixtures](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcCommandEnableRetryTests.cs)
select the next permitted stage. This table explains their classification; it is not
an instruction to query or modify lifecycle rows manually.

| Retained/current evidence | Action |
| --- | --- |
| Original managed initial journal/source receipt, before binding reservation | Controller may continue its original reservation stage; this does not admit an established source missing a binding. |
| Exact binding, `Disabled`, clear cache-ahead latch | Retry guarded activation under the initial empty-source checks. |
| Exact binding, `Tracking`, clear latch, canonical/cache/work tables empty before capture setup | Reconcile committed activation and resume provider/topic/connector setup or validation. |
| Connector registration/establishment already reached | Reconcile existing artifacts and fresh continuity evidence; do not register a replacement connector or recreate capture artifacts. Retry performs fresh readiness/barrier work, not reuse of a saved ready bit. |
| `Tracking` without the required binding; missing/corrupt/contradictory initial evidence; changed physical source | Reject; preserve evidence and use [unsupported provenance](#unsupported-provenance). |
| Cache-ahead latch, `Resetting`/`Rebuilding`, or unexpected canonical/cache/work rows | Reject. Initial eligibility is not repaired with SQL; any unused-binding retirement or separate provisioning needs its own authority and is not continuity recovery. |
| Writer-publication intent, managed lifecycle or retirement evidence | Reject initial retry. A lost response after publication intent does not make that intent replayable. Inspect the established generation; never erase intent to reopen initial enablement. |

PowerShell, repository root; `api-schema-tools` must be available as in setup. All
original initial prerequisites and writer exclusion still apply. This can resume
provider/Kafka/connector mutations within the original workflow. Use the emitted
settings, not the unmodified pre-bootstrap input JSON. Bootstrap rejects `DMS_CDC__`
overrides; direct CLI accepts them, so verify privately that the current process
has no conflicting overrides before invoking it.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Bootstrap-emitted full runtime settings | Actual settings from the interrupted fixture workflow |
| `<original-state-root>` | Original managed provisioning root | Same interrupted fixture root; no recreated provenance |

<!-- cdc-snippet: cdc-enable-retry -->
```powershell
api-schema-tools cdc enable --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-enable-retry -->

Exit `1` is rejection, unavailable evidence or bounded timeout, not rollback. Exit
`2` is invalid command/configuration; correct only the discrepancy, preserving scope.
Exit `130` is cancellation; reconcile retained effects first. The same retry can
succeed after an interrupted provider, broker/worker start, preflight, registration,
barrier or metrics stage only while its original evidence remains eligible.
Provider/offset loss instead enters terminal containment. Retain **all** diagnostics:
`WorkflowState/Unavailable` and `Connect/Unavailable` can coexist when incident
persistence and connector containment both fail. Neither attempt means containment
completed. Use the [containment handoff](#native-recovery); The procedure distinguishes each failed action.

<a id="established-validation"></a>

## Established validation and restart preflight

**PostgreSQL passed [T18](cdc-inv-evidence.md#postgresql-lifecycle-qualification-t18); SQL Server pending T19.** The
[source-history owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
and [managed/native recovery boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
define the scope of fresh observations.

| Record | Value |
| --- | --- |
| Target/generation | Established binding, same selected target, physical source and generation with intact provenance. |
| Authority/offline window | Deployment owner with original controller/database observation access. Running validation needs no new writer-offline window. Preserve any existing incident or shutdown fence; validation does not release it. |
| Retained inputs | Original runtime settings/state; matching binding, workflow, CREATE/source history, provider identity and connector establishment; retained incident/rollout/lifecycle records. |
| Invocation | `cdc-validate` for running publication inspection. Guarded start/restart/resume perform their own fresh pre-start checks; use the lifecycle handoff below for a stopped deployment. |
| JSON/exit status | `operation: "validate"`; exit `0` and `succeeded: true` require `data.publicationReady: true`. When available, inspect `data.observedAt`, `preStartEligible`, `continuity`, `hasPendingRecordSizeIncrease`, `diagnostics` and `recovery`. Null result data can be omitted on early rejection. |
| Postcondition | A bounded observation of current running publication, without repair, writer authorization or certification of a new exact baseline. |
| Rejection/timeout action | Retain evidence and keep existing fences. Unavailable live evidence may be reobserved after restoring access to the same intact services; provenance failure, source mismatch or terminal loss goes to the next procedure. |

PowerShell, repository root; original deployment and services reachable, production
CLI available. Observational validation takes the state lock but does not repair,
start a projector, capture an initial barrier, latch an incident or stop a connector.
No fault injection or new source selection is part of this command.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Original emitted runtime settings | Established fixture's retained settings |
| `<original-state-root>` | Original managed provisioning/controller root | Established fixture's intact root |

<!-- cdc-snippet: cdc-validate -->
```powershell
api-schema-tools cdc validate --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-validate -->

The [CLI runner](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/Cdc/CdcCommandRunner.cs)
selects `RunningPublication`, not `PreStart`. Thus a stopped connector can return exit
`1` without proving history loss. Even `data.preStartEligible: true` is only an
observation, never reusable start authority. There is no CLI `--mode PreStart` flag.
Use [managed startup](#managed-lifecycle) or [intact restart/resume](#intact-restart)
(T05 detail pending; current [command handoff](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#managed-stack-lifecycle)).
Those controllers revalidate under their own session immediately before acting.

Serialized `data.continuity` values are `Healthy`, `Unknown` or `Lost`. `Unknown`
prevents controller start/restart/resume; it is not proof of terminal loss. A later
complete affirmative observation can restore current readiness. `Lost` is terminal
for that generation, including when current provider artifacts or offsets later
look healthy. `validate` itself does not contain it: use the governed status/stop
handoff below. A partial record-size rollout remains not ready and belongs to
[its retained-operation procedure](#record-size-increase), not a fresh enable.
The same `1`/`2`/`130` failure conventions described above apply. Native recovery can
publish before revalidation; a later healthy result cannot certify the unsampled interval.

<a id="unsupported-provenance"></a>

## Missing provenance and source mismatch

**PostgreSQL passed [T18](cdc-inv-evidence.md#postgresql-lifecycle-qualification-t18); SQL Server pending T19.** Use the
[adoption/state-loss boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral)
and [physical-source replacement deferral](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral).

| Record | Value |
| --- | --- |
| Target/generation | Affected original target/generation and source; retain evidence of any observed mismatch without rebinding. Account for shared-worker peers. |
| Authority/offline window | Deployment incident owner maintains any writer/consumer fence and governs containment. CDC status does not gate ordinary DMS request routing; containment must not be inferred from API health. |
| Retained inputs | Available original state, settings and inventory; private diagnostic streams, observation times, incident/containment outcomes and deployment-owner reports of rollback/deletion. Never export credentials, settings contents, raw offsets or payloads. |
| Invocation | `cdc-provenance-rejection` is the same non-repairing validation against retained inputs, used to record an existing incident's rejection. No production fault injection. Follow status/managed-stop handoffs only with their original authority. |
| JSON/exit status | Normally exit `1`, `succeeded: false`; retain `diagnostics[].component`, `.failure`, `.message`. Early failures may have no `data`; available validation data can report `publicationReady: false` or `continuity: "Lost"`. Exit `2` denotes invalid input, not a specific provenance diagnosis. |
| Postcondition | Evidence retained, restart/initial retry withheld, and incident escalated to deployment ownership. This procedure does not restore continuity. |
| Rejection/timeout action | No force/adopt/import/replacement operation exists. If containment cannot be verified, report it as incomplete and keep the incident open; do not delete shared infrastructure or attempt artifact repair. |

PowerShell, repository root; original settings/state paths are still known and CLI
available. Observe the already affected deployment; do not change its target to
manufacture a mismatch. Fixture substitutions may select only disposable fixture-owned
state already damaged by the existing test's fault seam; operators do not perform
those mutations. A source-mismatch fixture may supply a private copy of the same
settings with its setup connection selecting an independent fixture-owned empty or
populated database. The original settings, CMS selection, binding and source remain
unchanged; this is a deliberate invalid-selection test, not a replacement procedure.
See the private path substitution table in `cdc-validate`.

<!-- cdc-snippet: cdc-provenance-rejection -->
```powershell
api-schema-tools cdc validate --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-provenance-rejection -->

The [diagnostic model](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcDeploymentResults.cs)
deliberately emits broad safe codes. There is no CLI `MissingProvenance` or
`SourceMismatch` failure code; a code alone cannot identify a missing file or prove
that a service is absent. Use the original private inventory and incident context:

| Symptom / shipped evidence | Operator action |
| --- | --- |
| Required journal/history missing or unreadable; typically `WorkflowState/Unavailable` | Verify the exact original path and owner access without changing state. If provenance cannot be established, withhold restart and escalate. Never create replacement JSON. |
| Corrupt/contradictory journal/history or incomplete provider/connector completions; `WorkflowState/ValidationFailed` | Preserve damaged evidence and stop retrying setup. An exact binding alone is insufficient. Binding mismatch or missing binding can also produce this broad rejection. |
| Unsafe state permissions, lock/access timeout or authentication failure; `Unavailable`, `Timeout` or `AuthenticationFailed` at the reported component | Owner diagnoses access/contention against the original state. A timeout is not evidence of absence. Reobserve only after the access problem is resolved and intact provenance is established; do not remove coordination files or restore backups. |
| Normally absent incident file in an otherwise intact workflow | No file needs to be created. Missing required binding/journal/source history is a different condition. Absence cannot rebut a report that an incident was deleted. |
| Suspected incident-history deletion or state rollback | Preserve the report and withhold validation/restart authorization even if the CLI appears healthy. The controller fixtures exercise `IncidentHistoryDeletion` and `StateRollback` integrity reports; these are adapter inputs, not CLI flags or automatic rollback detectors. Escalate; no stale-backup restoration procedure exists. |
| Retained terminal incident or `data.continuity: "Lost"` | Keep the generation terminal. Use governed observation/containment and escalate. Recreated provider artifacts, changed offsets, snapshots or later health cannot clear it. |
| Empty **or populated** different physical source; validation/lifecycle rejection | Preserve both source and original binding evidence. No identity rotation, replacement capture, CMS cutover or rebinding is authorized. Empty tables do not establish initial eligibility for the old target. |
| Live provider/Connect/Kafka evidence unavailable; `Unknown` when classification is available | Restore observation access to the same intact services and reobserve within a bounded attempt. Do not claim zero lag, absence, healthy continuity or successful shutdown. |
| Status reports `incidentPersistence: "Failed"` or `containment: "Failed"` | Preserve every diagnostic and escalate the failed dimension. `containment: "Stopped"` can verify connector stop even when incident persistence failed; it does not establish durable incident protection. Successful persistence does not imply the connector stopped. A failed stop/readback leaves containment unverified. |

`status/watch` (the [existing observation examples](#postgresql-setup) apply to either
provider with its retained settings) may latch terminal incidents and stop affected
connectors. In `data.targets[]`, distinguish `incidentPersistence: "Persisted"` from
`containment: "Stopped"`; `NotRequired` proves neither action occurred. The
[status contracts](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcControllerStatus.Contracts.cs)
expose `details.incidentFailureCategory` when available. Missing historical evidence
can prevent these operations too. Use [managed stop](#managed-lifecycle) and
[incomplete-containment handling](#native-recovery) with reachable original
infrastructure; the wrapper procedure requires fresh evidence for every peer.

The [DMS-1323 provenance/source fixtures](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs)
restore deliberately damaged test files only to isolate test cases. That cleanup is
not an operator recovery recipe. Backup restoration, artifact recreation, state deletion,
replacement-binding JSON, guarded retirement and independently provisioning another
database cannot certify continuity or perform the deferred new-generation cutover.
Retirement requires [independent cleanup authority](#generation-retirement); missing
state supplies none and retirement does not erase source publication history.

<a id="managed-lifecycle"></a>

## Managed shutdown and startup

**PostgreSQL passed [T18](cdc-inv-evidence.md#postgresql-lifecycle-qualification-t18); SQL Server pending T19.** Use the
[managed/native recovery owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
and the shipped [lifecycle wrapper](../../eng/docker-compose/cdc-lifecycle.psm1).
These operations retain the generation; [destructive teardown](#stack-teardown)
is a separate procedure.

| Record | Value |
| --- | --- |
| Target/generation | Every registered binding on the selected project's shared worker, with each original target, physical source, generation and state root. A selected settings/root argument does not narrow this inventory to one peer. |
| Authority/offline window | Deployment owner controls the entire worker/stack and coordinates all peer owners. Arrange the DMS outage, stop external IDE writers and seed jobs, and keep them excluded through startup. Managed `start` runs temporary projection processing with the HTTP host offline; the wrapper launches DMS only after every target passes. |
| Retained inputs | Original `eng/docker-compose/.cdc-deployments/<project>.json`, per-entry settings and state roots, `.bootstrap/cdc-runtime` inputs, effective environment/Compose selections and broker-size override, plus all [deployment-state evidence](#deployment-state). |
| Invocation | `cdc-managed-stop`, then `cdc-managed-start`, from repository root with PowerShell 7.5+ (including empty environment-value preservation), Docker and the original SchemaTools build/package available. Published and local alternatives are declared below. |
| JSON/exit status | Wrappers emit progress text, not a JSON envelope; a nonzero exit is failure. They require each internal CLI envelope's matching `operation`/binding, `succeeded: true`, `exitCode: 0`, and actual native exit `0`. Stop additionally requires `data.succeeded: true`, `data.targetShutdownVerified: true`, `data.boundary: "VerifiedManagedStop"`; start requires `data.succeeded: true` and `data.ready: true`. |
| Postcondition | Stop: fresh verified shutdown for all bindings, complete STOPPED/no-task worker inventory, durable deployment `Phase: "Stopped"`, then successful infrastructure stop with containers/volumes retained. Start: retained stopped state read back, original provenance/history/offset evidence validated, every eligible connector ready, `Phase: "Active"`, then DMS launched unless local `-InfraOnly`. |
| Rejection/timeout action | Preserve the original files and reachable infrastructure. A failed peer or incomplete inventory forbids worker shutdown; failed startup forbids DMS launch. Inspect and reconcile with a fresh managed stop before retrying startup. Use [incomplete-shutdown handling](#native-recovery) and [unsupported provenance](#unsupported-provenance) when applicable. |

### Select the retained deployment

Choose the wrapper that created the deployment: local uses `dms-local`, published
uses `dms-published`. Omit provider, identity-provider, environment, settings and
state arguments to inherit the retained selection, including custom state roots.
Explicit arguments must match the inventory; the original base environment path is
also accepted, but the wrapper uses the retained effective snapshot. Do not compose
new overlays or supply a new root. The wrapper checks original settings/environment
and selected Compose-file hashes, identity, endpoints and worker policy, rejects
`DMS_CDC__` overrides, and applies retained process-environment values during its
infrastructure calls. Preserve the shared broker-size override after a size rollout;
only the acknowledged operational ceilings have the documented settings-update
exception in the [command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands).

The inventory accounts for all registered peers even if their state roots differ.
The wrapper serializes deployment operations and each controller holds its own
state lock. Automatic shared DMS startup requires compatible DMS host settings and
combines the explicit projection targets. A mismatch requires reconciliation by
the owners, not deleting a peer or bypassing the wrapper. Local `-InfraOnly` can
complete the managed CDC sequence without launching an HTTP host; retain the IDE
writer exclusion until that sequence succeeds. Partial startup/seed switches
(`-DbOnly`, `-DmsOnly`, `-DmsBaseUrl`, `-LoadSeedData`) are rejected for retained startup.

### Stop without deleting the deployment

PowerShell, repository root; initial enablement must already have produced the
retained deployment. This interrupts all selected stack services and preserves
containers, volumes, Connect configuration/target state, committed offsets, provider
artifacts and controller history. No fault injection or destructive cleanup.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `eng/docker-compose/bootstrap-local-dms.ps1` | Wrapper used for original setup | `eng/docker-compose/bootstrap-published-dms.ps1` only for a fixture/deployment originally published; no project switch on an existing deployment |

<!-- cdc-snippet: cdc-managed-stop -->
```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -d
```
<!-- /cdc-snippet: cdc-managed-stop -->

The wrapper records `Transition`, invokes controller `stop` for **every** binding,
then independently checks the live connector names equal the retained inventory
and each connector is `STOPPED` with zero tasks. Only then does it persist `Stopped`
and stop infrastructure. A REST acknowledgement, one successful target, or an old
shutdown receipt cannot satisfy this condition. If infrastructure stop fails while
the worker remains running, repeating this same stop performs fresh target and
inventory checks. If a previously verified deployment is already stopped with no
running worker, the wrapper can finish infrastructure stop using that checkpoint.
Do not add `-v` or `-RemoveBootstrap` to resolve a stop failure.

### Start from verified shutdown

PowerShell, repository root; require successful managed shutdown and all retained
inputs above. No destructive operation or new admission is requested.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `eng/docker-compose/bootstrap-local-dms.ps1` | Same wrapper as stop | `eng/docker-compose/bootstrap-published-dms.ps1` for the original published deployment |

<!-- cdc-snippet: cdc-managed-start -->
```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1
```
<!-- /cdc-snippet: cdc-managed-start -->

The wrapper requires `Stopped` and no running worker, consumes that checkpoint by
writing `Transition`, restores database/CMS infrastructure, then invokes
`cdc start-worker`. This narrower building block exposes worker REST after verified
shutdown and observational shared-offset-store checks; it neither resumes connectors
nor supplies complete shared-worker shutdown authority. Use the wrapper for the
full operation, rather than invoking `start-worker` independently.

Startup waits within retained `Cdc.Timing` bounds for REST and the **complete**
STOPPED/no-task inventory. Transient startup connection failures or temporarily
missing retained connector names can be polled; extra/unmanaged connectors,
malformed evidence, or a running task reject startup. It then invokes guarded
`cdc start` for each entry. Each start checks fresh provenance and source history,
uses the retained offsets, starts its temporary selected projector only after
preflight, and can drain retained work before returning fresh readiness. It disposes
that projector on every exit. Only after all starts succeed does the wrapper record
`Active` and launch DMS. This does not repeat initial admission or certify an exact
baseline. A failure after some peers resume can leave those peers running; there is
no whole-stack rollback. Keep external writers fenced and follow the recovery table.

<a id="intact-restart"></a>

## Intact connector restart and resume

**PostgreSQL passed [T18](cdc-inv-evidence.md#postgresql-lifecycle-qualification-t18); SQL Server pending T19.** These are selected-connector
operations on reachable existing infrastructure. For a stopped **stack**, use
[managed startup](#managed-lifecycle). The
[recovery boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
applies even when the resulting connector is healthy.

| Record | Value |
| --- | --- |
| Target/generation | One selected established binding with original physical source and generation; no connector registration/replacement or peer mutation is authorized. |
| Authority/offline window | Controller/deployment owner authorizes interruption/resumption of that connector. Restart/resume require no new API writer-offline window, but preserve any existing incident/offline fence and coordinate dependent consumers. Ordinary DMS routing is not gated by projection/CDC readiness. |
| Retained inputs | Emitted full runtime settings, original state root, intact provisioning/source history and binding/journal, original services and fresh provider/offset/worker/telemetry evidence. |
| Invocation | Choose `cdc-intact-restart` for restart, or `cdc-intact-resume` for resume; do not run them as a mandatory pair. |
| JSON/exit status | Matching envelope `operation: "restart"` / `"resume"`, native exit `0`, `succeeded: true`, `exitCode: 0`; `data.operation` is `"Restart"` / `"Resume"`, `data.succeeded: true`, `data.ready: true`. Inspect `data.observation`, `data.boundary`, `data.recovery` and diagnostics; `data.targetShutdownVerified` is not the completion criterion for these operations. |
| Postcondition | Guarded mutation and running-state readback reconciled, followed by a fresh ready observation for this target. No first-enable writer receipt or unsampled-interval certification. |
| Rejection/timeout action | Exit `1`: retain effects/state, inspect all diagnostics and current status before retry. Exit `2`: correct input discrepancy within original scope. Exit `130`: reconcile cancellation effects. Unknown/lost provenance or history prevents authorization; route to [unsupported provenance](#unsupported-provenance) or containment below. |

PowerShell, repository root, `api-schema-tools` on PATH as in setup; the worker,
broker, source and observation endpoints must be reachable. Direct CLI accepts
`DMS_CDC__` overrides, so privately verify no conflicting override changes the
retained selection. Both blocks share these explicit substitutions:

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Full runtime settings emitted for the selected binding | Same established fixture settings; not a new configuration |
| `<original-state-root>` | Original managed provisioning/controller root | Same established fixture root and provenance |

<!-- cdc-snippet: cdc-intact-restart -->
```powershell
api-schema-tools cdc restart --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-intact-restart -->

<!-- cdc-snippet: cdc-intact-resume -->
```powershell
api-schema-tools cdc resume --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-intact-resume -->

The packaged lifecycle commands start their invocation-owned projector after eligible
preflight so they can collect fresh process-health evidence. An already started
executor is retained. This explains why a successful restart/resume can report
`ready: true` while a separate standalone `validate` or status observation reports
unobserved runtime health. The result does not certify another DMS process's health;
see the [managed lifecycle owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).

Both operations require fresh affirmative pre-start evidence before the Connect
mutation, then fresh readiness after durable completion. A prior ready status or
successful `validate` is not reusable authorization. Unlike `start`, restart/resume
do not require a verified managed stop; they can report `data.boundary: "NativeRecovery"` and still later become ready. That field is a recovery classification,
not a claim the command bypassed preflight. Lost replies are reconciled by the
controller; unchanged running status alone does not prove a requested restart
occurred. Keep the result and inspect before issuing another operation.

<a id="native-recovery"></a>

## Native recovery and incomplete shutdown

**Documented in T05; live exercise pending T21/T22.** Native worker recovery, task
reassignment/internal recovery and incomplete shutdown follow the
[design-owned recovery boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).
Records may be consumed and published **before** controller revalidation. Later
health and eventual containment cannot certify continuity, absence of publication
in that interval, or an exact baseline. A task restart entirely between observations
on the same worker can be unobservable; polling is not a consumption fence.

| Record | Value |
| --- | --- |
| Target/generation | Affected original generation and observed worker/task assignment; inspect every affected peer on a shared worker. |
| Authority/offline window | Deployment/incident owner preserves any existing writer or consumer fence and coordinates response. Status/watch do not create an API routing fence. After failed managed startup, keep external writers excluded and do not launch DMS. |
| Retained inputs | Original settings/state/inventory, lifecycle intents/completions, incident files, sanitized operation diagnostics/timestamps, and fresh provider/offset/connector/task/metrics observations. |
| Invocation | `cdc-incomplete-shutdown-status` for bounded inspection; `cdc-native-recovery-watch` for bounded repeated observations. Both may persist incidents and attempt connector containment; they are not guaranteed read-only. |
| JSON/exit status | `status`/`watch` stdout is the final envelope; watch pass JSON goes to stderr. Exit `0` requires final `data.aggregate.readiness: "Ready"`; `1` includes not-ready, rejection, unavailable evidence or timeout; `2` invalid input; `130` cancellation. Use `data.targets[]` fields listed below, when present; early failures may omit `data`. |
| Postcondition | Fresh observation classifies current readiness and reports persistence/containment separately. A recoverable observation can become ready only after a fresh pass; a terminal incident remains terminal. Neither outcome verifies whole-worker shutdown. |
| Rejection/timeout action | Follow the table below. Keep original infrastructure/evidence available; escalate failed persistence or stop and missing provenance. Never infer containment from an attempted action or a generic nonzero/zero exit. |

PowerShell, repository root; same CLI prerequisites and protected retained selection
as restart/resume. No raw Connect mutation or fault injection. These substitutions
apply to both marked blocks:

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Affected binding's emitted runtime settings | Same affected fixture settings |
| `<original-state-root>` | Original root including incident/lifecycle history | Same affected fixture root; do not reconstruct history |
| `3` | Bounded observation count for this example | Same three passes; timing comes from retained settings, not an alert threshold |

<!-- cdc-snippet: cdc-incomplete-shutdown-status -->
```powershell
api-schema-tools cdc status --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-incomplete-shutdown-status -->

<!-- cdc-snippet: cdc-native-recovery-watch -->
```powershell
api-schema-tools cdc watch --settings '<retained-settings-path>' --state-path '<original-state-root>' --maximum-passes 3 --json
```
<!-- /cdc-snippet: cdc-native-recovery-watch -->

Inspect each target's `observedAt`, `status.readiness`, `status.sourceHistory`,
`details`, `diagnostics` and `recovery`. Recovery includes `boundary`,
`requiresFreshPass` and `unobservedIntervalCertified` (always `false`). An observed
recovery invalidates prior readiness/telemetry; the controller recollects provenance,
provider history, committed offsets, worker/task state and metrics. Missing telemetry
or provider/offset evidence is unavailable, not zero lag or proof of continuity.
Watch retains identity comparisons across its passes, not earlier readiness.

| Observation/diagnostic | Action and completion criterion |
| --- | --- |
| Stop acknowledgement but `targetShutdownVerified: false`, delayed tasks, missing/additional connector, or unavailable inventory | Do not stop the shared worker. Preserve all peers' inputs, restore observation access and repeat `cdc-managed-stop`. Completion requires every fresh verified target stop plus the complete STOPPED/no-task inventory. |
| `CDC startup-readiness timed out` or startup interrupted after checkpoint consumption (`Transition`) | Retain reachable infrastructure and inspect affected targets. Do not replay startup using the earlier checkpoint. Reconcile through a fresh wrapper-managed stop, then retry managed startup only from its verified `Stopped` outcome. |
| `CDC worker recovered outside managed startup` or `CDC managed worker startup requires complete verified shutdown` | Treat the interval as native recovery. Inspect/contain, perform fresh managed stop, then retry only when eligible. Do not relabel the inventory phase manually. |
| One peer started, later peer failed, or DMS launch failed | Keep the outage fence; do not assume rollback. Inspect all peers and use fresh managed stop before repeating the startup sequence. |
| Settings/selection/identity-provider mismatch, unreadable inventory, or surviving infrastructure without inventory | Retain files and infrastructure; use [state/provenance diagnosis](#unsupported-provenance). Correct caller selection to the original retained values only when that evidence is intact; no new defaults, backup rollback, or recreated inventory supplies continuity. |
| `recovery.requiresFreshPass: true` or current evidence unavailable | Discard earlier readiness. Resolve observation access to the same services and obtain a fresh bounded pass. Missing/unknown history cannot authorize restart/resume. Three watch passes are not a guaranteed recovery deadline. |
| Terminal history loss or retained incident | Generation stays terminal even after provider/offset/metrics become healthy. Status/watch attempt durable incident persistence and connector stop; retain both results and escalate under [unsupported provenance](#unsupported-provenance). No deletion/restoration or replacement workflow is supplied here. |
| `incidentPersistence: "Persisted"`, `containment: "Stopped"` | Incident was durably recorded and affected connector stop was verified for that observation. This neither clears terminal status nor verifies every peer or purges previously published data. |
| `incidentPersistence: "Failed"` with `WorkflowState/Unavailable` | Preserve the failure report externally and escalate durable-state access; even `containment: "Stopped"` does not provide durable incident protection. Keep the generation fenced; later healthy status cannot resolve the missing persistence. |
| `containment: "Failed"` with `Connect/Unavailable` (or another reported Connect failure) | Stop/readback failed or is unverified; publication may continue. Escalate immediately to deployment/incident authority, preserve reachable infrastructure and diagnostics, and use governed stop only with sufficient original evidence. Successful persistence alone is not containment. |
| Both persistence and containment fail | Retain **both** diagnostic dimensions; neither action completed. Do not report eventual healthy status as resolution of the interval. |

`NotRequired` in either action field means that action was not required by that pass;
it does not prove persistence or stop. A command deadline can end watch before its maximum
pass count; timeout handling may still return terminal containment evidence,
while caller cancellation is honored. Capture the actual result rather than assuming
that timeout cancelled every effect. These distinctions are exercised in the
[command containment fixtures](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcCommandContainmentTests.cs)
and [native recovery fixtures](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcNativeRecoveryTests.cs).
Projection/CDC status never gates ordinary DMS API routing; any incident fence is
maintained by the deployment owner. Sensitive-data incidents additionally require
the [disclosure-response handoff](#sensitive-data-response); connector stop is not purge.

<a id="projection-handoff"></a>

## Projection troubleshooting and administration handoff

**Documented T06; live snippet exercise pending T25/T26.** E18 owns projection
administration. This procedure selects that workflow and explains its production
history gate under the [projection administration owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration)
and [cache-ahead recovery owner](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-ahead-invariant-recovery).

| Record | Value |
| --- | --- |
| Target/generation | One CMS-selected normalized `(tenantKey, dataStoreId)` and its current opaque physical-source fingerprint. Source publication history follows the original creation identity across binding retirement; the administrative result's `targetGeneration` is a projection execution generation, not a CDC binding generation. |
| Authority/offline window | Deployment/database authority for the E18 operation. For each of the three internal-only commands, close admission and drain every canonical, projector, direct-fill, bulk, external and administrative writer before asserting `closedAndDrained`; retain the fence through completion or incident reconciliation. Connector stop alone does not supply this fence. |
| Retained inputs | Full DocumentCacheAdmin settings, matching schemas/CMS target, current fingerprint and projection status, plus the original protected controller root, CREATE receipt, workflow and source history from [state preservation](#deployment-state). Keep sanitized command results and incident evidence. |
| Invocation | Choose the E18 procedure below, configure its production history reader, then select exactly one operation for `cdc-history-internal-only`. `cdc-history-rejected` illustrates that same request against retained rejecting evidence in an owned qualification fixture; it is not a production health probe. |
| JSON/exit status | DocumentCacheAdmin `--json` emits one shared result document on stdout, diagnostics on stderr. Completion: exit `0`, `status: "completed"`, `classification: "succeeded"`. History rejection: exit `10`, `status: "rejectedNoMutation"`, `classification: "downstreamHistoryPresentOrUnknown"`, `mutated: false`. This is not the SchemaTools CDC envelope or exit-code scheme. |
| Postcondition | Accepted activation/recovery finishes in `Tracking` with a clear latch; deactivation finishes in `Disabled`. Verify the E18 operation's own status postconditions. Neither history admission nor projection completion grants CDC readiness, a replacement baseline, or consumer correctness. |
| Rejection/timeout action | Preserve the result and original evidence. History rejection authorizes no clearing or recovery. Follow [unsupported provenance](#unsupported-provenance) or [native recovery/containment](#native-recovery) as applicable. Exit `12` (`incompleteRetryable`) requires the same command, target and guards after reconciliation; retain the offline fence. Other failures follow the [Admin exit-code reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#exit-codes), not a guessed retry from lifecycle alone. |

### Select the projection procedure

Inspect the target's [E18 status](../document-cache-documentation/operations-runbook.md#status-interpretation)
first. The following are handoffs, not alternative repair implementations:

| Observation | E18 procedure and completion observation |
| --- | --- |
| `queueSummary.presence: "notEmpty"`, increasing `oldestWorkAgeSeconds`, `caughtUp.reason: "queueNotEmpty"` | [Enqueue versus processing availability](../document-cache-documentation/operations-runbook.md#enqueue-vs-processing-availability): correct or resume processing; require operational health, and a fresh `caughtUp.status: "caughtUp"` only when queue-empty proof is needed. |
| `documentDiagnostics`, `poisonTraversalDiagnostics`, persistent materialization/writer failure | [Poison remediation](../document-cache-documentation/operations-runbook.md#persistent-projection-failure-and-poison-remediation): fix the defect and verify ordinary processing resumes. Do not delete poison work or repeatedly rebuild it without correction. |
| `enqueueFailures`, missing/disabled `inventory.enqueueTrigger`, invalid work inventory | [Enqueue failure](../document-cache-documentation/operations-runbook.md#enqueue-vs-processing-availability): restore supported inventory/prerequisites and verify canonical writes enqueue atomically; a processing restart alone does not establish this. |
| `classification: "lifecycleMismatch"`, `Resetting`, interrupted `Rebuilding` | [Lifecycle and Resetting](../document-cache-documentation/operations-runbook.md#lifecycle-mismatch-and-resetting): reissue only the proven interrupted command with identical guards and require its completion; unknown intent is an unsupported incident. |
| Compatible rebuild needed, lifecycle `Tracking` or `Rebuilding`, latch clear | [Online rebuild](../document-cache-documentation/operations-runbook.md#online-rebuild): bounded clearing/seeding and drain return to `Tracking`. Canonical writes may remain online; no production-scale duration claim follows. |
| Suspected missing/mismatched work, restore or direct mutation | [Explicit integrity scrub](../document-cache-documentation/operations-runbook.md#explicit-integrity-scrub): intentional O(N) scan, admitted only in `Tracking` with a clear latch. Scrub can set, never clear, the latch; recheck status after work drains. CDC restore/provenance concerns also require [unsupported-provenance handling](#unsupported-provenance); scrub does not restore continuity. |
| Existing `Disabled` source needs activation, or internal-only deactivation requested | [Activation](../document-cache-documentation/operations-runbook.md#activation) / [deactivation](../document-cache-documentation/operations-runbook.md#deactivation): use the history gate below and the E18 offline fence; finish in the selected operation's lifecycle. New-empty activation is a separate guarded workflow. |
| `cacheAhead.recoveryRequired: true` | [Cache-ahead recovery](../document-cache-documentation/operations-runbook.md#cache-ahead-recovery): internal-only proof and the offline fence are required. Possible downstream observation routes to containment/escalation, not in-place internal recovery. |
| SQL Server `sqlServerPrerequisiteFailed` or `unsupportedPrerequisiteIncident` | [SQL Server correction](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction): preserve Disabled-only initialization correction/restart and activation-preflight retry boundaries. Other lifecycles and post-validation prerequisite changes have no renewed-readiness guarantee. |

During projector downtime in an enqueue-enabled lifecycle, canonical writes still
record durable work. Failure to persist that work rolls back the **whole canonical
transaction**. Projection backlog, poison, lifecycle or CDC status does not gate
ordinary API routing; see [enqueue/processing availability](../document-cache-documentation/operations-runbook.md#enqueue-vs-processing-availability)
and the [projection health owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness).

### Production history decision

The shipped [CdcDownstreamPublicationHistoryProvider](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcDownstreamPublicationHistoryProvider.cs)
is registered in DocumentCacheAdmin when `Cdc:PublicationHistory` is configured.
Use the [Admin configuration reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#configuration)
to point the complete settings at the **original** root and deployment key. This
selects evidence; it does not create or attest to provenance. The reader normalizes
the tenant for CDC lookup, matches the current physical-source fingerprint, and
holds the controller lock across the E18 command. A standalone read is `unknown`
and cannot be saved as reusable permission. The provider mutex and all E18 guards
still apply. See the [deployment-state continuity owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral).

| Trusted observation during the command | Decision for all three internal-only commands |
| --- | --- |
| `internalOnly`, matching target/source, intact managed creation history | History gate admits; lifecycle, provider, writer-admission and fingerprint guards must still pass. The packaged admitted fixture uses ordinary managed non-CDC creation (`SourceHistoryOnly`). |
| `possible`, `active`, `historical` | Reject without mutation. Reservation can already mean possible exposure; retirement retains historical exposure. |
| `unknown`, no configured reader, missing/empty/unreadable root, missing receipt/journal/history, corrupt or contradictory evidence, wrong deployment/target/provider/source | Reject without mutation; do not construct replacement evidence or choose a fresh root. A controller-lock failure also rejects without executing the command. |

Binding absence, stopped connectors, removal of runtime `DocumentCache:Targets`,
and successful retirement cannot substitute for internal-only history. The
[packaged fixtures](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/CdcPublicationHistoryTests.cs)
cover all three commands, rejection with unchanged database/provenance, historical
rejection after retirement, and both orders of a reservation/admin lock race.
Their fixture-only SQL and history faults are not operator recovery steps.
A previously admitted ordinary non-CDC source is also not eligible for fresh CDC
enablement merely because it has `internalOnly` history; use the
[initial-enable boundary](#initial-enable-retry).

### Admitted and rejected command examples

PowerShell, repository root. Prepare the matching packaged `dms-document-cache`
tool using its [installation instructions](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#installation).
Supply complete normal settings including CMS credentials/endpoint, schema workspace,
provider and selected projection target, with the history configuration above;
keep secrets in protected settings/environment, never command-line arguments.
Account for normal environment overrides; this tool does not use SchemaTools'
`DMS_CDC__` override convention. Retain the full settings and original state root.

Choose **one** E18 operation; these are alternatives, not a sequence:

| `$HistoryCommand` | `$HistoryConfirmation` | E18 prerequisite and successful end state |
| --- | --- | --- |
| `activate-offline` | `offlineActivation` | Existing `Disabled` target, internal-only proof and closed/drained writers; finish `Tracking`, clear latch. |
| `deactivate-offline` | `offlineDeactivation` | Admitted offline deactivation under the [E18 guards](../document-cache-documentation/operations-runbook.md#deactivation); finish `Disabled`. |
| `recover-cache-ahead` | `internalCacheAheadRecovery` | Internal-only cache-ahead incident, set latch and closed/drained writers under [E18 recovery](../document-cache-documentation/operations-runbook.md#cache-ahead-recovery); finish `Tracking`, clear latch. |

Both snippets use these declared substitutions. For rejected-history qualification,
fixtures establish a rejecting scenario before invocation; operators must not
inject faults or execute a destructive request merely to discover history.

| Placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `$HistoryCommand`, `$HistoryConfirmation` | Matching pair from the alternatives above after selecting the E18 procedure | All three matching pairs from `CdcPublicationHistoryFixture.Arguments`, each independently prepared |
| `$HistorySettings` | Absolute path to complete protected Admin settings pointing at original `Cdc:PublicationHistory` state/deployment | Fixture's complete settings with original disposable root and deployment `history`; rejection cases may select the explicit fixture-owned evidence faults |
| `$HistoryEnvironment` | Matching normal settings environment name | Packaged harness environment |
| `$HistoryProvider` | `postgresql` or `sqlserver` for the same CMS database | Fixture provider; `sqlserver` is the Admin CLI token even when application settings use `mssql` |
| `$HistoryTenant`, `$HistoryDataStoreId` | Normalized tenant and CMS-selected numeric ID for the source | Default tenant `''` and `1` in the packaged fixtures |
| `$HistoryFingerprint` | Current opaque fingerprint from that target's projection status, matched to retained managed source identity; never invent it | Fixture's provider-read fingerprint, equal to its creation receipt |
| `$HistoryTimeoutSeconds` | Finite command timeout chosen for the E18 operation/workload | `60` as in the packaged fixture |

Admitted example: `It_allows_all_three_commands_from_managed_non_CDC_creation`
prepares each operation separately, with trusted `internalOnly` creation history.
This request mutates cache/work/lifecycle according to the selected E18 procedure.

<!-- cdc-snippet: cdc-history-internal-only -->
```powershell
dms-document-cache $HistoryCommand --data-store-id $HistoryDataStoreId --tenant-key $HistoryTenant `
    --confirm $HistoryConfirmation --offline-writer-admission closedAndDrained `
    --expected-physical-source-fingerprint $HistoryFingerprint `
    --command-timeout-seconds $HistoryTimeoutSeconds `
    --settings $HistorySettings --environment $HistoryEnvironment --datastore $HistoryProvider --json
```
<!-- /cdc-snippet: cdc-history-internal-only -->

Rejected example: `It_rejects_untrusted_or_exposed_history_without_any_mutation`
uses the **same** selected command/target/fingerprint and changes only the fixture's
history scenario (`possible`, `active`, `historical`, unknown/missing/unreadable or
mismatched evidence). It asserts exit `10`, the history rejection classification,
`mutated: false`, and unchanged database and provenance snapshots. Do not retry by
removing the history reader.

<!-- cdc-snippet: cdc-history-rejected -->
```powershell
dms-document-cache $HistoryCommand --data-store-id $HistoryDataStoreId --tenant-key $HistoryTenant `
    --confirm $HistoryConfirmation --offline-writer-admission closedAndDrained `
    --expected-physical-source-fingerprint $HistoryFingerprint `
    --command-timeout-seconds $HistoryTimeoutSeconds `
    --settings $HistorySettings --environment $HistoryEnvironment --datastore $HistoryProvider --json
```
<!-- /cdc-snippet: cdc-history-rejected -->

Read `status`, `classification`, `mutated`, `targetKey`, `physicalSourceFingerprint`,
`lifecycle`, `cacheAheadRecoveryRequired` and `phaseDiagnostics` from the Admin
result. The shared result does **not** serialize `downstreamPublicationStatus`;
`internalOnly` is a trusted gate observation, not a promised stdout field.
The admitted fixtures assert exit `0`, mutation, the selected final lifecycle and
clear latch; the normal completion contract is `completed` / `succeeded`.
For rejection, keep the original offline fence while reconciling the incident;
no failed history check authorizes recovery or reopening publication. Possible
published cache-ahead values follow the [repair/containment owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).
V1 new-generation cutover remains [deferred](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deferred-new-topic-cutover).


The following stable result excerpts apply to all three guarded operations. The
[packaged history fixtures](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/CdcPublicationHistoryTests.cs)
compare these same fields after the real gate runs; their next provider exercises
remain T25/T26. Unit serialization checks alone do not prove admission.

An admitted `internalOnly` operation completes with exit `0`:

<!-- cdc-snippet: cdc-output-history-admitted -->
```json
{
  "status": "completed",
  "classification": "succeeded",
  "mutated": true
}
```
<!-- /cdc-snippet: cdc-output-history-admitted -->

Active, historical, possible or untrusted history rejects with exit `10`:

<!-- cdc-snippet: cdc-output-history-rejected -->
```json
{
  "status": "rejectedNoMutation",
  "classification": "downstreamHistoryPresentOrUnknown",
  "mutated": false
}
```
<!-- /cdc-snippet: cdc-output-history-rejected -->

<a id="monitoring-retention"></a>

## Monitoring and provider retention

**Documented T07; exact live snippets pending T27/T28.** Use the
[operations owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations),
[telemetry owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry),
and [source-history owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
for the behavioral contract. These observations diagnose one retained deployment;
they neither repair provider artifacts nor establish consumer correctness.

| Record | Value |
| --- | --- |
| Target/generation | Select the CMS target, physical-source fingerprint and binding generation from the original retained settings/state. Match provider queries to that database and artifact; worker and storage observations can also affect peer bindings. |
| Authority/offline window | Controller authority includes original state-root write/lock access and connector stop/read-back. Provider inspections use a separate DBA/monitoring identity with catalog/DMV access; never enlarge the restricted connector principal for monitoring. Ordinary inspection needs no offline window. Preserve any existing writer fence; containment or prerequisite correction follows its linked procedure. |
| Retained inputs | Original settings/state plus timestamped status stdout, watch stderr, projection status and private provider/exporter observations. Retain exit status and missing/denied evidence as well as successes; sanitize physical identifiers before sharing. |
| Invocation | `cdc-status`, `cdc-watch`, `cdc-telemetry-inspect`, `cdc-pg-retention-inspect`, `cdc-provider-disk-inspect`, `cdc-sqlserver-retention-inspect` below. Projection status invocation remains in the [E18 command reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md). |
| JSON/exit status | `status/watch` return the final envelope on stdout; watch passes/diagnostics use stderr. Exit `0` means the current/final aggregate is `Ready`; `1` is rejected/not-ready/unavailable/timeout, `2` invalid input, `130` cancelled. Inspect each target even on failure. SQL/exporter/disk commands have native output/exits, not the CDC JSON envelope. A successful query is only an observation. |
| Postcondition | The selected symptom has an identified owner and fresh, attributable evidence of resolution: e.g., advancing capture with retained history, drain progress with bounded oldest work, or restored current telemetry plus a fresh controller pass. Completion never rests on absent data, an HTTP health check, or a raw offset comparison. |
| Rejection/timeout action | Missing permissions, rows, metrics, stale identity or timeout means unavailable evidence. Correct monitoring access/connectivity through its owner and recollect; do not classify as zero. For terminal/possible-publication incidents or failed persistence/stop use [native recovery](#native-recovery); for missing provenance/source mismatch use [unsupported provenance](#unsupported-provenance). |

### Collect and interpret current observations

PowerShell, repository root; matching `api-schema-tools` on PATH, intact retained
paths from setup. These commands may persist incidents and stop connectors. No new
writer authority is granted by either command.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>`, `<original-state-root>` | Wrapper-emitted paths for this deployment | Actual fixture-emitted paths |
| `3` | Example maximum watch passes, using retained polling/deadline settings | Positive bounded fixture count |

<!-- cdc-snippet: cdc-status -->
```powershell
api-schema-tools cdc status --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-status -->

PowerShell, repository root; same prerequisites and substitutions as `cdc-status`.
Three passes are a deployment observation choice, not a DMS alerting default.

<!-- cdc-snippet: cdc-watch -->
```powershell
api-schema-tools cdc watch --settings '<retained-settings-path>' --state-path '<original-state-root>' --maximum-passes 3 --json
```
<!-- /cdc-snippet: cdc-watch -->

Read the [production CDC result fields](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcControllerStatus.Contracts.cs)
and [projection status fields](../../src/dms/core/EdFi.DataManagementService.Core/DocumentCache/DocumentCacheStatusContracts.cs)
in their own envelopes. The CLI uses camel-case property names with CDC enum values
such as `Ready`, `Unknown`, `Failed`; projection enums use values such as `tracking`
and `notEmpty`. Null optional CLI properties may be omitted; explicit null is also
unavailable, not zero. Do not require percentile fields to exist.

| Observation | Names, units and scope | Freshness/absence and use |
| --- | --- | --- |
| Projection status (`GET /health/document-cache` or Admin `status`) | Select `targets[].targetKey`, `targetGeneration`, `provider`, `physicalSourceFingerprint`; inspect `lifecycle`, `cacheAhead`, `operationalHealth`, `caughtUp`, `queueSummary.presence`. `queueSummary.oldestWorkAgeSeconds` is seconds; `backlogEstimate.kind` qualifies its optional `value` (rows). | Compare envelope `observedAt`, target `processObservedAt`, `durableObservedAt` and component timestamps. Process execution/diagnostics are local to that host, not a cluster inventory. Unavailable durable facts cannot be replaced with older process health. E18 [status interpretation](../document-cache-documentation/operations-runbook.md#status-interpretation) owns classification. |
| CDC status | `data.targets[].observedAt`, `.status`, `.details`, `.diagnostics`, `.recovery`, `.incidentPersistence`, `.containment`; aggregate readiness is `data.aggregate.readiness`. | Historical observation for this pass. `incidentPersistence: "Failed"` and `containment: "Failed"` are independent failures. Later readiness cannot certify an unsampled recovery interval; use [recovery](#native-recovery). |
| Current connector lag | `details.lagMilliseconds`, `details.lagThresholdMilliseconds`: integer milliseconds; `details.p50LagMilliseconds`, `p95LagMilliseconds`, `p99LagMilliseconds` are optional. | Only current lag decides lag threshold satisfaction. Percentiles are diagnostic, may be absent/null and are discarded if inconsistent. A low current value does not prove projection catch-up, provider history or consumer progress. |
| Provider continuity | `details.providerArtifactState`, `retainedRangeState`, `schemaHistoryState`, optional `incidentFailureCategory`; `status.sourceHistory.continuity` and `status.sourceHistory.incidentLatched`. | Categorized evidence with `status.sourceHistory.observedAt`, not a numeric retention-margin metric or reusable last-good proof. `details.positions` has safe `lsnProc`, `commitLsn`, `changeLsn`, optional `eventSerialNo`, `retainedRangeStart`, `retainedRangeEnd`, `unavailableFacts`. Do not subtract these into a time margin or treat raw positions as continuity proof. |
| Shared worker/rollout | `hasSharedOffsetStoreIssue`, `hasPendingRecordSizeIncrease` on each target. | A shared-store fault propagates to selected peers sharing the deployment/worker. Inspect the complete retained worker inventory; one target's success does not clear the others. |
| Consumer progress | Consumer-owner partition barriers, checkpoint commits, deadline/renewal evidence. | No field above measures this. Use the [public bootstrap owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap) and [consumer checklist](#security-consumer-evidence). |

Projection backlog counts coalesced document work, not queued API operations or Kafka
records. Keep oldest-work age, current source lag, retained-history headroom, and
consumer progress as separate signals under the
[projection/readiness owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness).
Choose alert thresholds, consecutive-sample rules and sample intervals for the
deployment's measured write rate, storage headroom and response time. The example
bounds below are inspection choices, not DMS capacity or alert defaults.

### Inspect the qualified worker exporter

The [production adapter](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcConnectorTelemetryAdapter.cs)
reads the configured worker directly, with worker/process and sole-running-task
identity checks before and after the scrape. `Cdc:Timing:MaximumObservationAgeMilliseconds` defaults to
10000 milliseconds (10 seconds); age starts before HTTP collection and is rechecked when evaluated.
Required evidence is not cached for another pass. `jmx_scrape_error` must be zero;
HTTP 200 alone is insufficient. Missing/duplicate/misattributed/malformed current
lag, lag `-1`, failed collection, changed worker/task or expired evidence cannot
establish readiness. Optional min/max/average/percentiles may be unavailable without
invalidating valid current lag. These are the
[qualified metric rules](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry),
not assumptions that a manual scrape can certify.

| Exported gauge | Unit and scope | Interpretation |
| --- | --- | --- |
| `edfi_cdc_source_lag_current_milliseconds` | Milliseconds; exactly the bound `connector` plus `provider="postgres"` or `provider="sql_server"` labels | Required finite nonnegative current source lag; fractional values round upward in controller integer output. A fresh scrape is Debezium's report, not a last-source-event timestamp. |
| `edfi_cdc_source_lag_min_milliseconds`, `edfi_cdc_source_lag_max_milliseconds`, `edfi_cdc_source_lag_average_milliseconds` | Milliseconds, same labels | Optional runtime statistics. Min/max/average are adapter diagnostics, not fields in `CdcControllerStatusDetails`. |
| `edfi_cdc_source_lag_p50_milliseconds`, `edfi_cdc_source_lag_p95_milliseconds`, `edfi_cdc_source_lag_p99_milliseconds` | Milliseconds, same labels | Optional historical statistics; absence is expected on runtimes without those beans. Do not fabricate quantiles from current lag. |
| `edfi_cdc_worker_start_time_seconds` | Unix epoch seconds, worker-wide scalar | Qualified image identity aid; a new worker process requires fresh evidence for all bindings. |
| `edfi_cdc_worker_heap_max_bytes` | Bytes, worker-wide scalar | Capacity diagnostic qualified with the image; compare with retained worker policy, not a per-connector budget. |
| `jmx_scrape_error` | Dimensionless scrape-error indicator | Nonzero/absent is failed or unavailable collection. |

PowerShell, repository root; `curl` 8.4+ is the native executable (no PowerShell alias).
Use the private endpoint from `Cdc:WorkerMetricsEndpoint`, not a public proxy, and a
pre-created restricted directory. This single read has a 10-second timeout and
4 MiB response cap. It writes private raw metrics; inspect only the gauge families
above and the selected connector locally. No fixture substitutes a previously saved
scrape for live evidence. This is diagnostic collection, without controller identity
bracketing or readiness authority.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<worker-metrics-url>` | Retained configured single worker `/metrics` URI | Live fixture worker URI |
| `<private-metrics-file>` | New file in operator-owned directory (0700, umask 0077 on Linux) | Private fixture artifact path |

<!-- cdc-snippet: cdc-telemetry-inspect -->
```powershell
curl --fail --silent --show-error --connect-timeout 5 --max-time 10 --max-filesize 4194304 --header 'Cache-Control: no-cache, no-store' --output '<private-metrics-file>' '<worker-metrics-url>'
if ($LASTEXITCODE -ne 0) { throw 'Metrics unavailable; do not use a partial file.' }
```
<!-- /cdc-snippet: cdc-telemetry-inspect -->

Denied/redirected/unavailable access or an invalid body requires correction of the
private management endpoint and a fresh controller pass. Do not upload the raw
worker scrape, raw offsets, schema history or document bodies as incident evidence.
Use the [sanitized evidence convention](cdc-inv-evidence.md#recording-results).

### PostgreSQL slot, WAL and disk inspection

Use the [PostgreSQL provider owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql)
and the controller's provider-artifact/retained-range classification first. For
storage diagnosis, the following reads one exact managed slot and server settings;
it does not scan source/work tables. A missing slot is not an empty/healthy result.

PowerShell, repository root; PostgreSQL `psql` on PATH. Prepare a private libpq
service/passfile for the selected database and monitoring role, with
`connect_timeout=5`; no password in command text. The slot name comes from original
managed binding/artifact evidence, not a reconstructed name. Each statement has a
5-second timeout and a 1-second lock wait bound. Feed the script on stdin so psql
substitutes the quoted variable safely; do not change this to `psql -c`.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<pg-monitor-service>` | Protected libpq service selecting the exact target and monitor identity | Fixture service/passfile |
| `<managed-slot>` | Original managed PostgreSQL slot identity | Actual fixture slot |

<!-- cdc-snippet: cdc-pg-retention-inspect -->
```powershell
@'
\set ON_ERROR_STOP on
SET statement_timeout = '5s';
SET lock_timeout = '1s';
SELECT clock_timestamp() AS observed_at, active, wal_status,
       pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn) AS retained_wal_bytes,
       pg_wal_lsn_diff(pg_current_wal_lsn(), confirmed_flush_lsn) AS unconfirmed_wal_bytes,
       safe_wal_size AS remaining_slot_budget_bytes,
       to_jsonb(s)->>'invalidation_reason' AS invalidation_reason
FROM pg_catalog.pg_replication_slots AS s
WHERE slot_name = :'slot' AND database = current_database()
LIMIT 1;
SELECT name, setting, unit
FROM pg_catalog.pg_settings
WHERE name IN ('max_slot_wal_keep_size', 'max_wal_size', 'wal_keep_size')
ORDER BY name;
'@ | psql --no-psqlrc --dbname 'service=<pg-monitor-service>' --set 'slot=<managed-slot>'
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL retention observation unavailable.' }
```
<!-- /cdc-snippet: cdc-pg-retention-inspect -->

`retained_wal_bytes` is a WAL-position distance, not filesystem usage or a time
lag. `safe_wal_size` is nullable, including unlimited slot retention and a lost
slot; null never means unlimited disk. `extended` means WAL exceeds `max_wal_size`
but is still retained; `unreserved` needs urgent diagnosis and `lost` cannot be
repaired by resuming the old stream. See PostgreSQL's
[slot column definitions](https://www.postgresql.org/docs/17/view-pg-replication-slots.html).
Use fresh controller classification for the continuity decision; do not drop or
advance a slot to reclaim space. Reducing retention can destroy needed history.

PowerShell on the Linux Docker host, repository root; Docker authority, native
`timeout` on the host and `df` in the provider container. Select actual data/WAL mounts from
deployment inventory (SQL Server data/log paths for that variant); WAL may be on
a different filesystem. One invocation, two explicit paths, 10-second wall bound.
The output includes mount paths: keep it private and export only sanitized capacity
values. Also inspect host backing-volume capacity through its storage owner.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<provider-container>`, `<data-path>`, `<wal-or-log-path>` | Owned container and mounted provider paths | Owned fixture container/mount paths |

<!-- cdc-snippet: cdc-provider-disk-inspect -->
```powershell
timeout 10s docker exec '<provider-container>' df -Pk -- '<data-path>' '<wal-or-log-path>'
if ($LASTEXITCODE -ne 0) { throw 'Provider filesystem capacity unavailable.' }
```
<!-- /cdc-snippet: cdc-provider-disk-inspect -->

`df -Pk` reports 1024-byte blocks, used/available capacity and percent use, not WAL
budget. On pressure, have the storage/database owner restore headroom and investigate
stalled connector progress; preserve the slot and original state. Repeat the bounded
checks after correction, then collect controller status. A stopped connector can
continue pinning WAL while other database writes continue; a verified stop alone
is not a disk-pressure resolution. Missing tooling/access is unavailable capacity
evidence, not a reason to remove managed volumes.

### SQL Server capture, cleanup, LSN and version-store inspection

[SQL Server provider setup](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server)
owns Agent, capture/cleanup, snapshot isolation and schema-history requirements.
RCSI and `nested triggers` are separate projection prerequisites; their lifecycle
correction belongs to [E18](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction).
Do not change them on an active target as monitoring remediation.

PowerShell, repository root; `sqlcmd` on PATH, SQL Server 2025 qualified target.
The DBA supplies a monitoring principal with catalog/CDC/msdb visibility and required
DMV permissions (including server performance/security state for these views), using
`SQLCMDPASSWORD` from a protected session. This is not the connector login. Retain the
normal deployment TLS trust configuration; the example does not bypass certificate
validation. Login timeout is 5 seconds, batch query timeout 10 seconds, lock timeout
1 second; all multirow results are capped, scoped or catalog aggregates. Execute once
per sample; `TOP` bounds output, not execution time. Missing required rows, denied
access and NULL facts remain unavailable unless explicitly inapplicable. An empty
active-transaction result with verified visibility simply means none were observed.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<sqlserver-host,port>`, `<target-database>`, `<monitor-login>` | Selected database and deployment monitoring connection | Owned fixture endpoint/database/monitor |
| `SQLCMDPASSWORD` | Protected monitor credential environment input, never logged | Fixture-injected monitor secret |

<!-- cdc-snippet: cdc-sqlserver-retention-inspect -->
```powershell
sqlcmd -S '<sqlserver-host,port>' -d '<target-database>' -U '<monitor-login>' -b -l 5 -t 10 -Q @'
SET NOCOUNT ON;
SET LOCK_TIMEOUT 1000;
SELECT SYSUTCDATETIME() AS observed_at, is_cdc_enabled,
       snapshot_isolation_state_desc, is_read_committed_snapshot_on,
       is_accelerated_database_recovery_on, log_reuse_wait_desc
FROM sys.databases WHERE database_id = DB_ID();
SELECT value_in_use AS nested_triggers
FROM sys.configurations WHERE name = N'nested triggers';
SELECT TOP (1) status_desc AS agent_status, last_startup_time
FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server Agent%';
SELECT TOP (2) c.job_type, j.enabled, c.continuous, c.pollinginterval,
       c.retention AS retention_minutes, c.threshold,
       a.start_execution_date, a.stop_execution_date
FROM msdb.dbo.cdc_jobs AS c
JOIN msdb.dbo.sysjobs AS j ON j.job_id = c.job_id
OUTER APPLY (SELECT TOP (1) start_execution_date, stop_execution_date
             FROM msdb.dbo.sysjobactivity WHERE job_id = c.job_id
               AND session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)) AS a
WHERE c.database_id = DB_ID() ORDER BY c.job_type;
SELECT TOP (10) c.job_type, h.run_status, h.run_date, h.run_time, h.run_duration
FROM msdb.dbo.cdc_jobs AS c
JOIN msdb.dbo.sysjobhistory AS h ON h.job_id = c.job_id
WHERE c.database_id = DB_ID() AND h.step_id = 0
ORDER BY h.instance_id DESC;
SELECT TOP (5) session_id, start_time, end_time, scan_phase, error_count, latency
FROM sys.dm_cdc_log_scan_sessions WHERE session_id <> 0 ORDER BY session_id DESC;
SELECT TOP (3) OBJECT_NAME(source_object_id) AS source_table,
       sys.fn_varbintohexstr(sys.fn_cdc_get_min_lsn(capture_instance)) AS retained_min_lsn,
       sys.fn_varbintohexstr(sys.fn_cdc_get_max_lsn()) AS captured_max_lsn
FROM cdc.change_tables
WHERE source_object_id IN
    (OBJECT_ID(N'dms.Document'), OBJECT_ID(N'dms.DocumentCache'), OBJECT_ID(N'dms.CdcHeartbeat'))
ORDER BY source_object_id;
SELECT reserved_space_kb AS tempdb_version_store_kb
FROM sys.dm_tran_version_store_space_usage WHERE database_id = DB_ID();
SELECT persistent_version_store_size_kb AS adr_off_row_version_store_kb
FROM sys.dm_tran_persistent_version_store_stats WHERE database_id = DB_ID();
SELECT TOP (10) s.session_id, s.elapsed_time_seconds
FROM sys.dm_tran_active_snapshot_database_transactions AS s
WHERE EXISTS (SELECT 1 FROM sys.dm_tran_database_transactions AS d
              WHERE d.transaction_id = s.transaction_id AND d.database_id = DB_ID())
ORDER BY s.elapsed_time_seconds DESC;
SELECT SUM(CONVERT(bigint, unallocated_extent_page_count)) * 8 AS tempdb_free_kb,
       SUM(CONVERT(bigint, version_store_reserved_page_count)) * 8 AS tempdb_version_store_kb
FROM tempdb.sys.dm_db_file_space_usage;
'@
if ($LASTEXITCODE -ne 0) { throw 'SQL Server retention observation incomplete or unavailable.' }
```
<!-- /cdc-snippet: cdc-sqlserver-retention-inspect -->

This inspects three source captures without reading change payloads. Unexpected or
missing captures require controller provider validation; the bounded query does not
prove capture inventory matches. A zero/null LSN or missing capture cannot establish
retention. Min/max LSNs delimit observed retained/captured positions, not seconds of
remaining outage tolerance. The controller compares committed source evidence under
the [continuity contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity).
Do not reset offsets, resnapshot, recreate capture instances or manually run cleanup
to make these observations look current.

Capture scan `latency` is seconds; session history resets at server restart/failover,
and the running continuous job may have no completed job-history row. Interpret job
schedule/configuration, latest outcomes and repeat capture progress together;
`run_duration` uses SQL Agent's HHMMSS representation, not milliseconds. Numeric
errors should be escalated with sanitized diagnostics, not raw job-message dumps.
See Microsoft's [capture scan DMV](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/change-data-capture-sys-dm-cdc-log-scan-sessions?view=azuresqldb-current).

Monitor `tempdb` version-store KiB and available file space together with underlying
disk capacity; long snapshot transactions can retain versions. The aggregated
[version-store view](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-tran-version-store-space-usage?view=sql-server-ver17)
avoids traversing individual version records. With ADR enabled, include the database's
[persistent version store](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-tran-persistent-version-store-stats?view=sql-server-ver17);
its off-row KiB excludes in-row versions and does not represent total storage use.
A DBA resolves capacity/long-transaction issues, confirms capture and cleanup resume
with required history still retained, then obtains fresh controller status. Do not
kill sessions or alter isolation/retention from this inspection recipe.

### Symptom to owner and completion

These handoffs implement the [operations](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations),
[shared offset-store](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup),
and [recovery](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
boundaries. Use the [security and consumer checklist](#security-consumer-evidence)
for topic-policy/access examples; status performs authoritative policy observations. No raw
Connect lifecycle mutation or new monitoring service is needed here.

| Symptom / observation | Action and owning procedure | Observation that ends diagnosis |
| --- | --- | --- |
| Growing oldest work, backlog, `targetBackoff`, poison or enqueue failures | [Projection handoff](#projection-handoff); distinguish unavailable enqueue (canonical writes roll back) from processing outage (work accumulates). | Corrected failing component plus fresh E18 health/queue observations and sustained drain; CDC readiness additionally needs its own checks. |
| Current lag unavailable/exceeded, optional statistics absent | Inspect bound worker gauges and identity; follow [native recovery](#native-recovery) for restart evidence. Optional absence alone needs no repair. | Fresh attributable current lag within the configured threshold; no outstanding recovery/history blocker. |
| PostgreSQL WAL growth or low free capacity | Slot/disk snippets; database/storage owner restores headroom and diagnoses inactive/stalled progress. `lost` or missing slot goes to history incident handling. | Capacity headroom and advancing progress demonstrated in repeat samples, with fresh controller continuity evidence. A manual slot observation alone cannot clear a latch. |
| SQL Server capture stalls/cleanup fails | Agent/job/scan/LSN snippet; DBA checks service, job outcomes, storage and permissions. Distinguish transient unavailable evidence from lost history. | Capture advances, cleanup operates within required history retention, capacity is available and controller evidence is fresh. |
| Version-store growth, long snapshots | SQL Server version-store plus provider disk observations; DBA investigates transactions/capacity under the projection [prerequisite boundary](#sql-server-setup). | Capacity and version-retention pressure resolved without unsupported active-target prerequisite changes. |
| `hasSharedOffsetStoreIssue`, `status.connectOffsetStore` not satisfied/unknown | Platform owner inspects the configured shared topic and worker policy via controller diagnostics: compact-only cleanup, partition/durability requirements, effective worker-only access, intact committed offsets. Use [managed lifecycle](#managed-lifecycle) before any worker operation. | Fresh authoritative validation for every affected retained target; unavailable evidence stays blocking. Never delete/recreate the shared topic or reset offsets; per-binding retirement cannot clean this store. |
| Progress topic unavailable; initial admission barrier not reached | Inspect sanitized Kafka/Connect/provider diagnostics and the binding-owned progress topic's policy/access; verify heartbeat/capture and committed source progress. Follow [established validation](#established-validation) or [initial retry](#initial-enable-retry) as appropriate. | Initial admission must reach its captured barrier. Established status instead checks current component/history evidence: `status.providerBarrier.state` is `NotApplicable`, and status captures no new barrier. Raw progress records/offsets are private, not proof by themselves. |
| SQL Server `details.schemaHistoryState` unknown/lost/invalid | Platform owner checks the original binding's history topic: one partition, delete-only policy, unlimited time/size retention and effective private access; use [SQL Server owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server). Topic existence alone is insufficient. | Fresh authoritative schema-history/continuity validation, or preserved terminal incident and verified containment. Never compact, recreate or seed history by hand. |
| Public topic retained volume grows, cleaner stalls | Platform owner checks effective compact-only policy, explicit tombstone retention, broker cleaner/compaction errors and backlog, partition earliest/end offsets and retained bytes using its existing broker tooling. Compare repeat samples and storage headroom; inspect no payloads. [Topic owner](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic) governs retention. | Cleaner progress and storage headroom plus measured retained-log scan capacity; topic-policy success alone proves no cleaner health, consumer completion or purge. Local Redpanda and other brokers use their own metrics; the Connect gauges above expose none of these cleaner measures. |
| Incident persistence or connector stop fails | [Native recovery/incomplete shutdown](#native-recovery); preserve original state and escalate authority/availability failures. | Independently verified persistence and containment outcomes. Later healthy telemetry cannot certify earlier publication or clear terminal state. |

### Projection tuning and evidence limits

Use [shipped settings/defaults](../../docs/CONFIGURATION.md#datamanagementdocumentcache)
and `effectiveSettings.projector` from the selected projection status. The baseline
is `PollInterval=00:00:05`, `PageSize=100`, `FailureBackoff=00:00:30`,
`MaxConcurrentTargets=2`, `BaselineHighWaterMark=1000`. These are execution settings,
not capacity promises. Tune one setting at a time, retaining input rate, oldest-work
age, queue estimate kind, drain outcomes, transaction waits and provider storage
observations before/after. Apply changes through the normal retained-settings/host
workflow; a shared worker restart still uses [managed lifecycle](#managed-lifecycle).

| Setting | What to observe before changing it |
| --- | --- |
| Poll interval | Shorter idle polling can reduce pickup delay but adds provider observations; compare idle cost and oldest-work trend under real writes. |
| Page size | More work per dispatch changes batch duration/resource use; inspect page/drain duration, poison traversal and fairness before raising it. |
| Failure backoff | Diagnose the provider/materializer failure first; shorter retry delays increase pressure on a failing target. |
| Target concurrency | Process-local target slots do not remove same-document write/acknowledgement contention; include all projector replicas and provider load when sizing. |
| Baseline high-water mark | Bounds baseline seeding pressure, not normal outage accumulation; use the [E18 rebuild workflow](../document-cache-documentation/operations-runbook.md#online-rebuild) and observe drain alongside seeding. |

The [.NET projection instruments](../../src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheProjectionTelemetry.cs)
include `edfi.dms.document_cache.projection.dispatch.duration` (histogram, ms),
`projection.dispatch.items` (full prefix `edfi.dms.document_cache.`, histogram,
items per dispatch) and `projection.item.outcomes` (same prefix, counter).
[Enqueue counters](../../src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheEnqueueTelemetry.cs)
are `edfi.dms.document_cache.enqueue.successes` and `.failures`.
[Writer histograms](../../src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheWriterTelemetry.cs)
include `edfi.dms.document_cache.writer.transaction.duration`,
`.cache_dml.duration`, `.acknowledgement.duration` and `.same_document_wait`, all in
milliseconds with the same writer prefix. These are .NET instrument names, not
promises of identically named Prometheus series on the Connect endpoint. Rates and
histograms need a chosen observation window; process restart resets local counters.
Keep the shipped bounded provider/target/outcome labels; never add document IDs,
bodies or raw tenant names as metric labels.

[E18 CDC-INV-03/04/09/10/15 evidence](../document-cache-documentation/cdc-inv-evidence.md#matrix)
covers transactional enqueue rollback, same-document acknowledgement contention
without blocking unrelated documents, bounded provider writer scenarios, functional
queue drain/restart and indexed status. The PostgreSQL and SQL Server
`DocumentCacheWriterPerformanceEvidence_it_compares_projector_and_direct_fill_workload_modes`
fixtures invoke the writer directly in `DurableWorkProjection` and `DirectFill`
modes with candidate/no-candidate, retries and contention cases; they are explicit
component evidence, not a production workload or measured API overhead guarantee.
No numeric result from an unexecuted fixture is claimed here. Canonical enqueue
and projection acknowledgement both add transactional work; projector downtime
allows durable work to accumulate while enqueue works. Coalescing limits repeated
work for one document, not total outage storage. Recovery requires demonstrated
drain under continuing writes and adequate WAL/CDC/version-store capacity.
[Production-scale performance qualification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
remains deferred; this task adds neither a benchmark harness nor throughput/latency
thresholds.

<a id="security-consumer-evidence"></a>

## Security, topic retention and consumer evidence

**Documented in T08; live snippet qualification pending T20.** Use the
[security owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations),
[topology/offset-store owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#kafka-connect-offset-store),
[public bootstrap owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap)
and [ADR 0002 topic contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic)
as the acceptance boundaries. Each consumer owner supplies evidence for its own store.

| Record | Value |
| --- | --- |
| Target/generation | Original CMS target, physical source, binding generation and public topic; inventory peer bindings, shared worker topics, actual deployment principals and every independent consumer namespace/group. Qualification fixtures create separate synthetic targets; they do not attest this inventory. |
| Authority/offline window | Deployment security/database/platform owners inspect effective permissions and policy; consumer owners inspect their durable state. Observation requires no new writer-offline window. Keep existing incident fences. Qualification commands below mutate and destroy only their disposable fixture resources; never point fault probes at a live deployment. |
| Retained inputs | Original settings/state and artifact inventory, timestamped policy/access observations, principal/group inventory, consumer barriers/checkpoints and capacity results. Keep credential material and physical names private; share only sanitized evidence with profile/image, case IDs and actual results. |
| Invocation | `cdc-topic-policy-inspect` for current local controller observations; `cdc-access-inspect` for separate secured/local Kafka qualification; `cdc-consumer-evidence` for the existing DMS-1324 provider/message lane. Complete all owner checklists below; these commands alone are insufficient. |
| JSON/exit status | Local `status` uses the monitoring envelope/exits below. Qualification runner writes `qualification.json`: per-suite `Name`, `Status`, `Total`, `Passed`, `Failed`, `Skipped`; exit `0` requires every report `Passed`. Exit `1`, `EnvironmentUnavailable`, `RunnerFailed`, missing results or skipped required cases mean no qualification. Test reports are not CDC command JSON. |
| Postcondition | Every applicable row has fresh evidence attributable to its target, principal, generation and consumer. Production ACL/durability proof comes from authorization-enabled deployment adapters and real access probes; consumer validity comes from that consumer's durable proof. Local readiness or reference fixture success cannot stand in for either. |
| Rejection/timeout action | Mark the failed/unavailable evidence explicitly; retain original state and route access/policy correction to its deployment owner. Use [native recovery/containment](#native-recovery) for publication incidents and [unsupported provenance](#unsupported-provenance) for state/source failures. Consumer uncertainty immediately follows the invalidation/bootstrap handoff below. |

### Role and artifact access checklist

Record actual identities privately and review **effective** access, including inherited,
wildcard and prefixed grants, superuser bypass and denies. A list of intended literal
ACLs is insufficient. The [shipped policy](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcDeploymentKafkaPolicy.cs)
and [secured fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcKafkaPolicyTests.cs)
provide the grant and probe examples; do not broaden connector access for inspections.

| Owner/role | Artifact and required evidence | Reject or hand off when |
| --- | --- | --- |
| Database setup owner | Separate setup authority for managed creation/provider setup. PostgreSQL restricted role is deployment-prepared; SQL Server restricted SQL login is deployment-prepared and initial setup maps its same-name user. Retain successful narrow source/heartbeat access validation from [PostgreSQL](#postgresql-setup) / [SQL Server](#sql-server-setup). | Missing login, conflicting SID, unsupported/elevated principal or inadequate setup authority: retain retry state. Completed setup stays validation-only; no identity remapping or credential repair. |
| Connector database principal | Effective provider-specific replication/CDC and narrow source/heartbeat permissions under the [PostgreSQL](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql) / [SQL Server](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server) contract; no document-table writes, projection-work-table access or general administrative access. | Connection success alone supplies no permission proof. Keep DBA monitoring credentials separate. |
| Connector Kafka principal | Literal `WRITE`, `DESCRIBE` on its public and progress topics; SQL Server history also has `READ`, `DESCRIBE_CONFIGS`. Probe that it cannot write the shared worker offset topic. | Effective access exceeds the deployment's scoped role, or required producer/history access is unavailable. |
| Worker service principal | Literal `READ`, `WRITE`, `DESCRIBE` on the configured shared `offset.storage.topic`; existing worker config/status topics and worker group remain private. Validate offsets before worker startup and on controller lifecycle/status checks. | A per-binding connector or instance consumer can use shared worker state; missing/unsafe offset policy affects all peer bindings. |
| Deployment controller/platform | Topic/configuration and ACL administration, authoritative effective-access inspection, private Connect lifecycle authority, original state-root lock/write access, provider observations and fresh telemetry. | Denied/incomplete evidence is unknown, not safe isolation. Observation cannot repair unsafe ACLs. Use deployment-owned admission/correction authority, never raw Connect lifecycle mutation. |
| Instance consumer owner | Literal `READ`, `DESCRIBE` on only its public topic and `READ` on only its configured consumer group. Prove allowed read plus denied peer-topic, peer-group, worker-group, progress, history, offset/config/status reads and denied public writes. | Cross-instance access or any internal-state access succeeds. Consumer-side filtering cannot provide isolation. |
| Platform/network/secrets owner | Private REST and exporter endpoints; authenticated production access/network controls; worker-resolved secret references, protected secret mounts and normal DMS settings. Confirm consumers cannot reach REST/metrics or obtain DB/worker/controller credentials. | Local loopback/SASL fixture defaults are being treated as production network or secret-management proof. |
| State/incident owner | Original roots and retained inputs in [state inventory](#deployment-state), owner-only directory/file permissions (local `700`/`600`), protected persistent storage and access-controlled backups. Share sanitized diagnostics/counts, no document payloads, credentials, raw source offsets or sensitive physical names. | Missing/unreadable/rolled-back state goes to continuity incident handling; a backup is not authority to restore continuity. Raw test logs stay private. |

Production deployment prerequisites include the
[topology and durability requirements](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup),
[durable binding authority](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding),
[continuity requirements](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral)
and the security owner above. Supply deployment adapters for those authorities and
protected persistent state. The shipped CLI accepts only its qualified local profile;
changing a profile token supplies none of these capabilities. This is not a cloud
installation guide. The separate three-broker secured fixture proves its tested access
and durability behavior, not a production installation or physical-source admission.

### Inspect topic policy and effective access

Use original generated artifact names; do not derive substitute topics from display names.
The values below are acceptance checks from the linked owners, not a second settings
catalog. Retain actual per-topic configuration sources and replica assignments privately
through the deployment's existing broker inspection/authority adapter.

| Artifact | Required policy/evidence | Completion limit |
| --- | --- | --- |
| Public document topic | Exactly `cleanup.policy=compact`; explicit per-topic `delete.retention.ms >= 604800000` (seven days); immutable binding partition count; record ceiling matching the accepted deployment policy. | Broker default inheritance and `compact,delete` fail. A stronger tombstone setting is allowed but does not extend the 24-hour consumer deadline. |
| Internal progress topic | Exactly one partition, `cleanup.policy=compact`; connector/control-plane only. | Not a consumer bootstrap source; public tombstone-retention minimum does not apply. |
| SQL Server schema-history topic | Exactly one partition, `cleanup.policy=delete`, `retention.ms=-1`, `retention.bytes=-1`; connector/control-plane only. PostgreSQL has none. | Finite retention or compaction fails. Retained source LSNs cannot compensate for missing history. |
| Shared worker offsets | Pre-created configured topic, `cleanup.policy=compact`, actual replicas and explicit topic-level ISR; worker-only data grants plus control-plane administration. | Shared across bindings; not a per-binding cleanup artifact. Raw offsets are not continuity proof. |
| All governed topics and shared offsets | Production: replication factor at least three and explicit `min.insync.replicas >= 2`; qualified local: one replica and explicit ISR `1`. Inspect actual assignments, not desired defaults. | Single-broker local success supplies no production durability or ACL proof. |
| Cleaner and capacity | Platform samples cleaner progress/errors/backlog, retained bytes, partition earliest/end offsets, storage headroom and skew using its broker tooling; pair with the [monitoring handoff](#monitoring-retention). | Configuration success supplies no cleaner-health or purge proof. Capacity includes the entire retained log, including dirty/uncompacted records, not just live keys. |

PowerShell, repository root; matching `api-schema-tools` on PATH and an intact running
local deployment. Same observation and possible incident/stop side effects as
`cdc-status`; preserve existing fences. This command reports policy components, not
raw broker configuration or consumer proof.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>`, `<original-state-root>` | Original wrapper-emitted runtime settings and controller root | Actual established fixture-emitted paths, never fabricated provenance |

<!-- cdc-snippet: cdc-topic-policy-inspect -->
```powershell
api-schema-tools cdc status --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-topic-policy-inspect -->

Inspect `data.targets[].status.kafkaPolicy` and `connectOffsetStore` (`state`,
`category`, `observedAt`), target `hasSharedOffsetStoreIssue`, `diagnostics`,
`incidentPersistence` and `containment`, plus `deploymentProfile.aclIsolationProven`.
Exit `0` requires the current aggregate `Ready`; `1` covers not-ready/rejected/timeout,
`2` invalid input and `130` cancellation. Unknown/missing evidence cannot pass.
Local `aclIsolationProven: false` remains false even when policy components pass.
For full field nesting and freshness see [monitoring](#monitoring-retention).

The existing [Kafka qualification lane](../../eng/ci/Invoke-CdcQualification.ps1)
performs authenticated broker probes and live policy inspection in disposable fixtures.
It intentionally changes grants, topic configuration and replica assignments, denies
inspection authority, and exercises cleanup; it must never be repointed at operator
resources. `Given_authorized_three_broker_cdc_policy` covers allow/deny behavior,
unsafe effective grants, denied ACL inspection, topic drift and stronger retention.
`Given_explicit_authorization_disabled_local_kafka_policy` checks honest local reporting.
The fixture's SASL/PLAIN credentials are ephemeral on its isolated Docker network and
loopback listeners; this is no production transport-security recipe.

PowerShell, repository root; .NET 10, PowerShell 7 and working Docker Engine with permission
to create/remove isolated containers/networks/volumes. Verify `systemctl status docker
--no-pager` and `docker ps` on Linux. Have the repository-pinned Kafka image and qualified
Connect image locally, or deliberately add the runner's `-PullImages` option. No provider
admin connection is needed for the Kafka lane. The runner uses a fresh results directory
and the existing allowlisted exporter; never upload the printed private log directory.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` | Shipped `CdcQualifiedWorkerImage.json` loaded below | Same exact qualified digest; no arbitrary tag |
| `<new-kafka-evidence-directory>` | New private results location, not already present | Fresh isolated fixture results path |
| Binding/principal/topic/group names and credentials | Created internally by the existing fixture | Existing fixture-owned synthetic identities only; no deployment settings/state substitution |

<!-- cdc-snippet: cdc-access-inspect -->
```powershell
$env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE = (Get-Content './src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json' -Raw | ConvertFrom-Json).image
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Kafka -Configuration Release -ResultsDirectory '<new-kafka-evidence-directory>'
```
<!-- /cdc-snippet: cdc-access-inspect -->

Retain both `kafka-secured` and `kafka-local` reports, case-level results and image identity.
Only the former's successful authenticated negative probes support ACL isolation within
its fixture scope. Effective wildcard/prefix grants and explicit denies must be evaluated,
not removed from evidence to make a literal ACL list match. Denied inspection is
`Unavailable` transport evidence / `Unknown` policy, not an empty safe grant set.
Failed assertions, absent cases and environmental skips remain failures to qualify.
Deployment owners must obtain equivalent fresh evidence for their own adapters/resources;
running this fixture never certifies their deployment.

### Consumer-owner proof and invalidation handoff

Use the [public bootstrap owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap)
and [public message/ordering contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md)
for each independent consumer, including every independently operated store using the same
principal. Kafka group lag or fetched offsets do not prove durable application.

| Consumer evidence to retain | Acceptance / failure action |
| --- | --- |
| Namespace and complete partition inventory | Match the binding generation/public topic and consumer store/group. Capture each partition's earliest offset and exclusive end barrier; include idle and empty partitions. Unexpected assignment or missing partition evidence invalidates the entire store. |
| Bootstrap start, durable apply and checkpoints | Begin at every earliest offset; durably apply through every captured barrier and persist the corresponding next-offset checkpoints. Finish within 24 hours from the first partition scan, including stalls, retries, rebalances and persistence. Reading through the barrier or applying without checkpoint durability is insufficient. |
| Incremental renewal | Continue from durable next offsets only after complete bootstrap. At least once every 24 hours capture fresh barriers for all partitions and durably complete them; idle partitions can use unchanged ends. Record proof completion time, not merely poll/read time. |
| Fault response | Deadline missed, lost/corrupt checkpoint, unexpected partition assignment or uncertain continuity: immediately stop advertising the entire local state as valid, discard it and repeat a complete earliest-offset bootstrap under the same 24-hour deadline. Never resume from the uncertain checkpoint; fence delayed writes/callbacks from discarded attempts. |
| Capacity and cleaner evidence | Measure the largest retained log claimed, dirty/uncompacted versions, skew, maximum-sized records, durable state writes and concurrent mutations, including persistence/stalls in elapsed time. Record fetch capacity against the accepted record ceiling and cleaner/storage observations. Repeated missed deadlines require more throughput/parallelism before production use. |
| Completion and responsibility | Consumer owner attests its store's actual evidence and supported workload. Reference fixtures and deployment topic validation cannot certify that store; live-key counts, a healthy connector or a larger retention setting cannot substitute. |

Consumer invalidation is scoped to consumer-owned state. It does **not** authorize deleting
controller provenance, resetting Connect offsets, changing binding generation or executing
the deferred [new-topic cutover](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deferred-new-topic-cutover).
If the earliest/end bounds cannot be observed, keep state invalid and escalate access or
availability to the platform owner; advertise validity only after a complete durable proof.

[DMS-1324 consumer fixtures](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractConsumerBrokerTests.cs)
are test-only reference evidence. The broker fixture uses real Kafka transport and synthetic
E18-derived public values, but simulates durable storage and clock. Its PostgreSQL fixture
hosts the broker/topic; the consumer assertions are provider-neutral, not SQL Server setup
qualification. Unit [bootstrap](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerBootstrapTests.cs),
[continuity](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerContinuityTests.cs)
and [ordering](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerOrderingTests.cs)
cases cover deadline boundaries, stale callbacks, checkpoint loss/corruption, idle renewal,
duplicates, lower versions and Kafka-null deletes. This harness is not a shipped consumer
product, benchmark or certification of an independently operated persistence system.

PowerShell, repository root; disposable Docker qualification environment and runner
prerequisites above. This broader existing MessageContract lane also exercises provider
and transform cases; it does not run against the retained operator deployment. Exact
execution and sanitized artifacts remain T20 work.

| Literal/input | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` | Qualified digest loaded by `cdc-access-inspect` | Same shipped digest |
| `CDC_CONNECTOR_TEMPLATE_REDPANDA_IMAGE`, `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` | Qualification owner's approved immutable image references, set privately before running | Images accepted by existing pinned-image fixture; record exact images with result |
| `<new-consumer-evidence-directory>` | New private results location | Fresh isolated fixture results path |
| Provider/target/records/clock/store | Existing PostgreSQL-hosted DMS-1324 fixture | Its existing synthetic rows, simulated persistence/clock and fixture-created targets only |

<!-- cdc-snippet: cdc-consumer-evidence -->
```powershell
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -Suite MessageContract -Configuration Release -ResultsDirectory '<new-consumer-evidence-directory>'
```
<!-- /cdc-snippet: cdc-consumer-evidence -->

Inspect `Postgresql-MessageContract` in `qualification.json` and its case-level report.
The fixture observation artifact identifies `Evidence`, `States` (`Phase`, `Valid`,
`RenewalInProgress`, `Attempt`, `ProofCompletedAt`, `NextOffsets`, `Checkpoints`,
`DocumentCount`) and `Scans` (partition bounds and record sizes/null flags); bodies and
physical topic names are omitted. Use the runner's exported artifacts rather than raw
attachments. The evidence index links stable `MC-CONSUMER-BROKER-*` cases; record actual
results and their simulated-store limitation, not an inferred consumer conformance pass.

<a id="record-size-increase"></a>

## Coordinated record-size increase

**Documented in T09; exact live snippets pending T23/T24.** Use the
[in-place increase owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase)
and [ADR sizing contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size).
The [SchemaTools reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
owns the acknowledgement schema and command options. This procedure applies to an
established, intact generation in either provider's qualified local deployment.

| Record | Value |
| --- | --- |
| Target/generation | Exact complete binding identity from the controller result, including physical source, connector, public topic and generation; one stable increase `operationId`. No new generation or offset reset. |
| Authority/offline window | Deployment operator controls the original state, database validation access, Connect and Kafka administration and broker recreation; obtains every consumer owner's capacity evidence. Plan a CDC publication interruption and possible shared-broker disruption. No DMS writer-offline fence is imposed by this size operation; projection remains caller-owned and must run to drain queued work. Readiness does not gate ordinary API routing or authorize writers. |
| Retained inputs | Original full runtime settings at the previous ceilings, state root, binding/history/journal, shared `Cdc:Compose:BrokerSizeOverrideFile`, protected acknowledgement file, complete consumer inventory and private evidence. Preserve partial changes and all invocation results. |
| Invocation | Prepare `cdc-size-no-consumers` only after establishing an empty inventory (otherwise use the consumer variant below); run `cdc-size-increase`. Resume an interrupted pending operation with `cdc-size-retry`, renewing confirmation every time. |
| JSON/exit status | `operation: "increase-record-size"`; exit `0` requires envelope `succeeded: true`, `exitCode: 0`, `data.succeeded: true`, `data.ready: true`, and the matching `data.operationId`. Exit `1` is operational rejection/not-ready/timeout; `2` invalid command/configuration/input; `130` caller cancellation. Inspect sanitized `diagnostics`; `data.observation` is optional, not a success prerequisite. |
| Postcondition | Fresh live broker/topic/producer, worker, provider/offset, projection and lag evidence passes, the acknowledged operation is durably completed, and a final ordinary publication-readiness pass succeeds. Only then update retained normal settings to the new ceilings. |
| Rejection/timeout action | Keep the target classified not ready while intent is pending; preserve previous settings, acknowledgement scope and broker override. Reconcile the actual diagnostic, renew evidence and retry the original pending operation. Lost provenance/terminal history follows [unsupported provenance](#unsupported-provenance); do not roll back limits or clear the journal. |

### Choose capacity and collect the acknowledgement

`MaxRecordBytes` bounds the pinned producer's local per-record estimate after the
key and value converters serialize the UUID key and public UTF-8 envelope, including
the producer's record-batch estimate. It is not `DocumentCache` payload length, the
HTTP body limit, or the complete wire request. Projection can inject links and the
envelope adds metadata. Compression stays `none`; a producer rejection fails the
task with no partial public record. The existing
[message-size fixtures](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRecordSizeTests.cs)
and [rollout/replay fixtures](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.Producer.cs)
qualify this boundary for their pinned runtime and workload, not every valid document
or production throughput. Their procedure-level live evidence remains
[pending](cdc-inv-evidence.md#procedure-evidence).

Choose a strictly larger record ceiling and a producer buffer at least that large
and no smaller than the previous effective buffer (default: greater of `33554432`
and the previous ceiling). Check worker heap headroom separately; the buffer is not
a total-memory cap. Each consumer owner must attest that deployed
`max.partition.fetch.bytes` and `fetch.max.bytes` meet the requested ceiling and
that deserialization at that ceiling was tested. Maintain this capacity through
rollout and subsequent consumption. Apply the
[consumer-owner checklist](#consumer-owner-proof-and-invalidation-handoff); an omitted inventory never
means no consumers, and the controller does not discover or certify them.

The local broker deployment adapter preserves stronger limits and ensures replica
fetch/response capacity and `socket.request.max.bytes >= requestedMaxRecordBytes +
1048576`. That extra MiB is minimum operational headroom, not a protocol-size
calculation. Requalify deployment request capacity when an increase or batching
change exceeds previously qualified capacity. Retain the same shared broker override
path and file for partial retries and all later worker starts; do not recreate it
from old settings. Coordinate shared-worker peers through the
[retained deployment inventory](#deployment-state).

JSON input, repository root: save the substituted block as a new owner-only file at
`<acknowledgement-path>` in a protected directory, retaining it for this operation.
Obtain `binding` from a retained controller JSON result for this exact managed target
(for example [setup observation](#postgresql-setup) or [established inspection](#established-validation)).
Copy just the nine identity fields below, preserving JSON types and exact values;
do not copy the full binding's version, partition or contract fields into this strict
input schema. This is input selection, never recreated provenance; the controller
independently exact-matches it. Missing/untrusted binding evidence stops this procedure.
No deployment mutation occurs when preparing this file.

| Literal placeholder/value | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<increase-operation-uuid>` | Generate one nonempty UUID once for this new operation; retain on every retry | New fixture UUID retained across its interruption |
| `<binding-deployment-key>`, `<binding-tenant-key>`, `<binding-data-store-id>`, `<binding-instance-key>` | Corresponding strings in the actual result's `binding` | Actual controller-returned fixture identity, including an empty tenant string where applicable |
| `generation: 1` | Replace `1` with the exact numeric `binding.generation` | Fixture's actual admitted generation |
| `<binding-provider>`, `<binding-physical-source-fingerprint>`, `<binding-connector-name>`, `<binding-topic-name>` | Exact `binding.provider`, `physicalSourceFingerprint`, `connectorName`, `topicName`; do not derive names or invent a fingerprint | Actual fixture binding; provider typically `Postgresql` or `SqlServer`, not the wrapper's `mssql` token |
| `10000000`, `20000000`, `33554432` | Example previous record ceiling, requested ceiling and requested buffer in bytes. Confirm effective retained settings use the first value and capacity supports the latter two; otherwise substitute all three consistently | Fixture's measured previous and qualified requested limits; include previous effective buffer in preconditions |
| `<operator-token>` | Credential-free opaque identity of the authorized operator | Fixture operator identity |
| `noConsumers: true`, `consumers: []` | Explicit complete empty inventory, renewed at invocation time | Isolated fixture with no affected independent consumers |

<!-- cdc-snippet: cdc-size-no-consumers -->
```json
{
  "operationId": "<increase-operation-uuid>",
  "bindingIdentity": {
    "deploymentKey": "<binding-deployment-key>",
    "tenantKey": "<binding-tenant-key>",
    "dataStoreId": "<binding-data-store-id>",
    "instanceKey": "<binding-instance-key>",
    "generation": 1,
    "provider": "<binding-provider>",
    "physicalSourceFingerprint": "<binding-physical-source-fingerprint>",
    "connectorName": "<binding-connector-name>",
    "topicName": "<binding-topic-name>"
  },
  "previousMaxRecordBytes": 10000000,
  "requestedMaxRecordBytes": 20000000,
  "requestedProducerBufferBytes": 33554432,
  "operatorIdentity": "<operator-token>",
  "noConsumers": true,
  "consumers": []
}
```
<!-- /cdc-snippet: cdc-size-no-consumers -->

For a nonempty inventory, keep the same complete envelope, set `noConsumers` to
`false`, and replace `consumers` with one entry per distinct deployment containing
all four fields: `deploymentIdentity`, `revision`, `confirmingOwner`, and
`evidenceReference`. For example, the illustrative entry
`{"deploymentIdentity":"consumer-a","revision":"revision-2","confirmingOwner":"owner-a","evidenceReference":"capacity-20000000-v2"}`
requires real owner evidence behind each substituted token; it is not an attestation
to copy unchanged. Operator and consumer fields use lowercase ASCII letters, digits,
dot, underscore and hyphen, without leading, trailing or consecutive separators.
Use opaque evidence IDs, not URLs, credentials or raw private reports. Keep the
acknowledgement at most 1 MiB. Unknown/duplicate fields and omitted required fields
are rejected. Do not add `invocationId` or `confirmedAt`: the CLI creates these anew
under the controller lock for each confirmed invocation.

### Execute the coordinated rollout

PowerShell, repository root; `api-schema-tools` is available as in setup. The file
above is fully substituted, consumer capacity is confirmed for this invocation,
projection is running, and original dependencies are reachable. This command can
stop/resume the connector, recreate the local broker while preserving its state,
and raise topic/producer limits. Keep other lifecycle operations serialized. Direct
CLI accepts `DMS_CDC__` overrides: eliminate conflicting process overrides and retain
the previous effective settings throughout a pending rollout. If broker recreation
needs longer than the configured deadline, use invocation-local timing overrides
`DMS_CDC__Cdc__Timing__CallMilliseconds` (up to `300000`) and
`DMS_CDC__Cdc__Timing__WaitMilliseconds`, recording the chosen values with the result.
Keep the retained settings file unchanged: wrapper identity hashing includes timing.
Remove these temporary overrides before invoking a wrapper, which rejects `DMS_CDC__`
overrides. Do not change identity or requested limits as a timeout workaround.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Original bootstrap-emitted full runtime settings at previous operational ceilings | Actual fixture settings at its previous ceilings |
| `<original-state-root>` | Original managed provisioning/controller root | Same fixture root, never recreated provenance |
| `<acknowledgement-path>` | Protected, substituted acknowledgement prepared above | Exact marked input with declared fixture identity/limits/evidence substitutions |

<!-- cdc-snippet: cdc-size-increase -->
```powershell
api-schema-tools cdc increase-record-size --settings '<retained-settings-path>' --state-path '<original-state-root>' --acknowledgement '<acknowledgement-path>' --confirm-consumer-capacity --json
```
<!-- /cdc-snippet: cdc-size-increase -->

The [shipped rollout](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcRecordSizeIncrease.cs)
persists intent plus the fresh acknowledgement, verifies connector stop, raises and
reads back broker limits, then the public-topic limit, then producer buffer, then
producer request size last. Each producer configuration update requires validated
stopped/task-free evidence before advancement. The controller resumes only after
these checks, waits within the deadline for usable fresh lag and projection catch-up,
and requires publication readiness before completion plus a final ordinary fresh
pass afterward. Lost mutation responses are reconciled from live state; a REST
acknowledgement alone does not establish success. No manual broker change, raw
Connect PUT/resume, offset reset or source projection repair is part of this procedure.

Capture the single JSON stdout result and stderr separately. The host writes sanitized
diagnostic text to stderr; process exit and envelope `exitCode` must agree. A successful
`data` contains `succeeded`, `ready`, `operationId` and `diagnostics: []`; it normally
omits `observation`. Failed results can include `data.observation` with the last target
status and diagnostics; failures before request construction can omit `data`, `binding`
and `deploymentProfile`. The local result still reports `aclIsolationProven: false`.
Do not require a fabricated writer-publication receipt or treat a status pass as a
replacement for the increase result. Packaged
[stdout/exit fixtures](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcPackagedCommandTests.cs)
cover the host contract; they do not themselves exercise a live increase.

After confirmed success, preserve the previous settings copy for investigation and
update only `Cdc:MaxRecordBytes` and `Cdc:ProducerBufferBytes` in the retained runtime
settings to the requested values (`20000000` and `33554432` in this example). Keep
all other retained settings and wrapper inventory identities unchanged. Preserve the
controller-written broker override; never hand-edit it or the journals. Use these
updated ceilings for subsequent [status/validation](#established-validation) and
[managed startup](#managed-lifecycle); the wrapper excludes only these two operational
settings from its identity comparison and revalidates their live effectiveness. No
new enablement or generation is needed.

### Interrupted rollout and rejection actions

PowerShell, repository root, with the same dependencies, mutation scope and placeholder
substitutions as `cdc-size-increase`. Use this only for the original **pending**
operation after reconciling the failure. Preserve previous settings and the same
operation UUID, complete binding, previous/requested ceilings and requested buffer.
Recheck the entire consumer inventory now; update consumer evidence when deployments
changed, retaining earlier evidence privately. The explicit flag renews confirmation
even for `noConsumers: true`; the saved file alone grants no retry authority.

<!-- cdc-snippet: cdc-size-retry -->
```powershell
api-schema-tools cdc increase-record-size --settings '<retained-settings-path>' --state-path '<original-state-root>' --acknowledgement '<acknowledgement-path>' --confirm-consumer-capacity --json
```
<!-- /cdc-snippet: cdc-size-retry -->

| Observation/rejection | Required action and completion boundary |
| --- | --- |
| Missing flag, malformed/missing/oversized file, duplicate/unknown fields, binding mismatch or previous ceiling inconsistent with settings | Input rejection (`Request` / `InvalidInput`, exit `2` for these parser/input cases). Correct the supplied input from retained evidence; never change state to match it. A pending intent remains pending. |
| Empty inventory without `noConsumers: true`, nonempty inventory with `true`, duplicate deployments or invalid evidence tokens | Acknowledgement validation rejects before advancing effects. Supply the complete truthful inventory and renewed flag. Inspect the actual diagnostic rather than assuming every acknowledgement rejection is a parser exit `2`. |
| Consumer revision/confirming owner changes, deployment is replaced/renamed, or a later operation requests a larger ceiling | Obtain a new evidence reference for the changed capacity scope. Journal validation rejects reusing the old reference in these cases. Unchanged consumers may renew the same applicable evidence on the same pending operation; the controller does not verify external truth. |
| Different operation ID, binding/source/topic or previous/requested record ceiling while an increase is pending | Scope mismatch rejects; restore the original request. Do not start another increase to bypass pending intent. After completion, a further increase needs a new UUID, the last completed ceiling as previous, and new higher-capacity evidence. |
| Lost response/cancellation/timeout after some effects, even if all live limits now align | Retain state and previous settings. Pending intent keeps ordinary status/start/restart from restoring readiness. Retry with renewed confirmation; the controller reads back live partial stages and resumes only eligible ordered work. No automatic rollback or lowering occurs. |
| Unknown intermediate limit or out-of-order topic/buffer/request change | Reject and preserve evidence; do not manually lower/repair values or change the requested buffer to fit drift. Escalate if the original acknowledged sequence cannot reconcile it. |
| Known lag/backlog after authorized resume | The same invocation can wait within its bounded deadline. Unknown/stale telemetry is not zero lag. Timeout retains pending intent; restore projection/telemetry dependencies via [monitoring](#monitoring-retention), then renew confirmation for retry. |
| Lost response after durable completion, or final observation fails after completion | Do not assume every nonzero exit means pending intent. Preserve result/state and inspect the original journal's operation/completion through deployment authority. A completed operation cannot be reopened; confirm its scope and reconcile fresh validation at the completed ceilings. Never edit completion records or infer completion from aligned live limits alone. |
| Provider/offset/provenance/worker loss, terminal incident, failed containment | Keep the incident and route to [unsupported provenance](#unsupported-provenance) or [native recovery](#native-recovery). Attempted stop is not verified containment; size changes cannot repair continuity. |

The [acknowledgement fixtures](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcRecordSizeAcknowledgementTests.cs)
cover renewal and changed-consumer evidence; the
[rollout fixtures](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcRecordSizeIncreaseTests.cs)
cover interruption boundaries, lost responses, not-ready gating and ordered effective
limits for both providers. T23/T24 must exercise these exact marked inputs/commands
through the live fixtures and record artifacts; this documentation does not claim
those runs have occurred.

<a id="generation-retirement"></a>

## Guarded generation retirement

Use this procedure only to permanently retire one explicitly selected generation.
[Managed stop/start](#managed-lifecycle) preserves the generation and its artifacts.
The [binding and cleanup owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
defines the authority and state-last deletion contract; the
[continuity boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral)
still applies to failed initial setups and terminal generations.

| Record | Value |
| --- | --- |
| Target/generation | Original complete binding identity: deployment, normalized tenant/data-store target, instance, provider, opaque physical-source fingerprint, connector/topic and positive generation. `--generation` must equal the retained settings/binding generation. |
| Authority/offline window | Explicit `--destructive-cleanup` plus independently sufficient original provenance, exact binding/retirement journal and source history. Deployment owners authorize destruction and coordinate the affected writers/consumers; for disclosure, use the offline/access fence below. Keep database, Connect, Kafka and protected controller state reachable until governed cleanup completes. |
| Retained inputs | Original emitted runtime settings, matching schema inputs, controller root, provisioning receipt, workflow/source history, incident evidence and wrapper inventory from [state preservation](#deployment-state). Preserve credentials and endpoint access needed for authoritative inspections and cleanup. |
| Invocation | `cdc-retire` below. Repeat with the same settings, root, identity and generation after a partial cleanup; no caller-supplied replacement operation ID or artifact list. Stack-wide destruction uses the separate wrapper procedure below. |
| JSON/exit status | Require process exit `0`, `operation: "retire"`, `succeeded: true`, `exitCode: 0`, the expected `binding`, and `data.succeeded: true` with nonempty `data.operationId`. `data.diagnostics` and top-level `diagnostics` are empty on success. This operation has no `ready`, publication receipt, artifact inventory or platform-purge field. Local `deploymentProfile.aclIsolationProven` remains `false`. |
| Postcondition | Controller verified the governed artifact scope absent and completed binding/incident removal last; journal and source-lifetime publication history remain. An interrupted retirement can retain partially deleted artifacts and binding/incident state until reconciliation completes. |
| Rejection/timeout action | Exit `1` means rejected/unavailable/timed-out operation; `2` means invalid input; `130` means cancellation. Preserve all surviving inputs and infrastructure. Inspect diagnostics and follow the failure table below; no volume deletion or reuse of the generation on an unverified result. |

### Retire one retained generation

PowerShell, repository root; use the installed/built `api-schema-tools` from setup.
This is destructive: it removes the selected generation's governed artifacts, not
just its connector process. Require the independent cleanup authority above; a
missing binding, configuration removal or suspected rollback is not authorization.
For shared stack teardown, use `cdc-stack-teardown` instead: it invokes this
controller for every retained entry with that entry's generation and cleanup flag.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Original emitted runtime settings for the affected generation | Owned fixture's original full settings; PostgreSQL or SQL Server without changing its binding |
| `<original-state-root>` | Original managed provisioning/controller root, including custom roots | Same fixture's intact root and durable cleanup checkpoints |
| `<binding-generation>` | Positive generation in the original settings and retained binding/inventory | Same fixture's generation; never increment it to bypass rejection |

<!-- cdc-snippet: cdc-retire -->
```powershell
api-schema-tools cdc retire --settings '<retained-settings-path>' --state-path '<original-state-root>' --generation '<binding-generation>' --destructive-cleanup --json
```
<!-- /cdc-snippet: cdc-retire -->

Retain stdout JSON, stderr diagnostics and the process exit separately in protected
operator evidence. Early rejection/cancellation can omit `binding` and `data`;
absence is not completion. The host writes sanitized diagnostics to stderr and
one result envelope to stdout. Do not treat a successful `status`, connector HTTP
404 or delete acknowledgement as a retirement result.

The [retirement controller](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcBindingRetirement.cs)
holds the original workflow lock and records the exact retirement scope before
cleanup. It verifies stopped connector/tasks, removes and verifies source offsets
before connector deletion, then reconciles provider artifacts, binding-local ACLs
and topics. It freshly reconciles the governed scope again beside state deletion.
A lost delete response can succeed only when authoritative inspection proves the
effect. Once retirement intent exists, enable/start/restart/resume cannot reuse the
generation. Retrying retirement preserves its operation ID and checkpoints; even
a completed retirement can be reconciled again while its infrastructure and inputs
remain available. Never-reserved initial failures use controller-proven absence,
not an operator assumption that nothing was created.

| Artifact scope | Governed retirement result and retained boundary |
| --- | --- |
| Connector and source offsets | Removes the exact connector's offsets before deleting the connector. Missing connector metadata alone cannot prove offset absence; the controller requires independent evidence or its own verified offset-removal checkpoint. The shared worker offset topic and peer connector namespaces survive. |
| PostgreSQL | Removes the exact inactive logical slot and exact publication after live source/ownership checks. Shared, broadened or unsafe artifacts reject cleanup; do not repair or drop them manually to make retirement pass. |
| SQL Server | Removes the three governed capture instances and gating role. Removes only owned, unused current-database capture/cleanup jobs with durable ownership evidence; peer captures or unproven/orphaned jobs block that step. Database, source tables and deployment-managed connector login/user remain. |
| Kafka | Removes generation-local public/progress topics and ACLs, plus SQL Server schema-history topic/ACLs. Shared worker configuration/status/offset topics, peer topics, shared principals and consumer-group grants are outside per-binding deletion. Local authorization-disabled success supplies no ACL-isolation proof. |
| State and configuration | Binding and terminal incident are removed only after verified cleanup. Original provisioning/workflow journal and source history survive. Direct CLI retirement removes neither retained CDC settings nor wrapper inventory, generated runtime inputs, database or stack volumes. |

These scopes are exercised by the
[provider cleanup cases](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcArtifactCleanupProviderTests.cs)
and [interrupted-retirement fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs).
They do not authorize deleting shared artifacts outside the binding.

### Partial cleanup and rejection actions

Diagnostics use `component`, `failure` and sanitized `message`; codes below are
[production classifications](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcDeploymentResults.cs),
not finer-grained invented incident names. A timeout can occur before an operation
ID is returned; retained controller state, rather than a recreated response, owns retry.

| Observation | Action and completion boundary |
| --- | --- |
| `Request` / `InvalidInput`, missing destructive flag, zero or mismatched generation | Compare the invocation with the originally selected identity. Correct only an invocation error; never edit binding identity or change generation. No cleanup completion is established. |
| `WorkflowState` / `ValidationFailed`, absent/corrupt/contradictory provenance or missing binding before the controller's durable state-deletion intent | Preserve evidence and use [unsupported provenance](#unsupported-provenance). Stop/escalate; no state recreation, history deletion, force/import operation or independent provisioning as recovery. |
| `Connect`, `Kafka`, `ProviderSetup` or `Worker` / `Unavailable`, `AuthenticationFailed`, `Timeout`, `Conflict` or `ValidationFailed` | Keep services reachable and preserve surviving artifacts. Owner corrects availability/access for the original scope, then repeats the same retirement. Unverified stop, retained offsets, wrong physical source, shared provider artifacts or unsupported ownership cannot be bypassed by raw Connect calls or manual SQL. |
| Nonzero result after some artifacts were removed, or lost response/cancellation | Preserve root/settings and retry the same invocation only when independent cleanup prerequisites can again be met. Controller reobserves effects and retains its original operation; missing connector, partially absent topics or an old checkpoint alone is insufficient. Require the full successful result before proceeding to later cleanup. |
| Verified retirement of a formerly exposed source | Exposure history remains historical and the target cannot become initial/internal-only again. [Projection administration gates](#projection-handoff) still reject exposed history. Retirement does not establish migration continuity or support source replacement, a fresh binding on the surviving source, or terminal-generation restart. |

For a sensitive-data incident, use the ordered [disclosure response](#sensitive-data-response)
before retirement. It requires verified connector and consumer fencing, offline
correction, protected audit and independent platform purge evidence. Governed
cleanup alone cannot close that incident or authorize re-enablement.

<a id="stack-teardown"></a>

## Destructive stack teardown

This procedure destroys the selected stack's data volumes after retiring **every**
managed generation. It is separate from direct per-binding retirement and ordinary
[managed stop/start](#managed-lifecycle), which retains artifacts. The
[local bootstrap owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci)
and [cleanup owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
govern its ordering; use the original wrapper/inventory, not raw Compose teardown.

| Record | Value |
| --- | --- |
| Target/generation | All entries in the retained `dms-local` or `dms-published` deployment inventory, including peers with different state roots. E2E teardown attempts both projects. Authorize destruction of every affected project/database/volume. |
| Authority/offline window | Explicit `-d -v`; original inventory and every generation's independently sufficient retirement authority. Exclude external/IDE writers and coordinate affected consumers for the destructive window. Preserve live infrastructure until all retirements and the empty-worker inventory check succeed. |
| Retained inputs | Entire [state inventory](#deployment-state), original effective environment, Compose inputs, runtime settings/schema inputs, shared broker-size override and custom roots. Inherit original selection; clear direct-CLI `DMS_CDC__` overrides before wrapper invocation. |
| Invocation | `cdc-stack-teardown` or, for the owned DMS E2E environment, `cdc-e2e-teardown`. The wrapper supplies each retained generation and destructive flag; do not substitute an empty state root or remove a target from configuration. |
| JSON/exit status | Wrappers emit operational output, not a single CDC JSON envelope. Require successful wrapper process completion (exit `0`); each internal `retire` result must match operation, binding deployment/instance/data-store/generation and connector, with both success flags, exit `0` and a nonempty operation ID. The wrapper also requires an authoritative empty Connect inventory. |
| Postcondition | All selected governed retirements completed, worker inventory verified empty, project-scoped Compose volume removal completed and eligible generated runtime cleanup reconciled. Protected source history/settings may remain as described below; success is neither purge proof nor permission to reuse a source. |
| Rejection/timeout action | Nonzero exit/exception is incomplete teardown. Retain surviving infrastructure, inventory and files; repeat the same wrapper after correcting the reported prerequisite. Use its durable phase to distinguish partial retirement, partial infrastructure removal and partial generated-file cleanup. Do not erase checkpoints or invoke a broader prune. |

### Select the destructive wrapper

PowerShell, repository root; requires the retained deployment from setup and the
authority above. Destructive intent applies to all selected project volumes. Omit
provider/environment/settings/root overrides to inherit the original selection,
including custom roots. No fault injection.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `eng/docker-compose/bootstrap-local-dms.ps1` | Original local bootstrap (`dms-local`) | `eng/docker-compose/bootstrap-published-dms.ps1` only for a deployment/fixture created by the published wrapper |

<!-- cdc-snippet: cdc-stack-teardown -->
```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -d -v
```
<!-- /cdc-snippet: cdc-stack-teardown -->

Both bootstrap wrappers request protected bootstrap-workspace cleanup after their
project teardown. The underlying `start-local-dms.ps1` / `start-published-dms.ps1`
`-d -v` entry points also use the shared lifecycle controller; without
`-RemoveBootstrap` they do not request removal of the whole staged workspace.
Do not use optional workspace removal to bypass protected state.

For **DMS E2E**, use the setup wrapper's printed teardown command and resolved
environment path. PowerShell, repository root; destructive to both `dms-local` and
`dms-published`, so require authority over both, not just the last test run. Both
projects must agree with the selected engine/environment if present. The wrapper
attempts each project even if another fails, then reports combined failures; a
failure does not mean no project was removed. Instance Management CDC is out of scope.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `postgresql` | Engine selected at E2E setup | `mssql` for the SQL Server variant; never CDC's `sqlserver` token |
| `<original-e2e-environment-path>` | Resolved original environment file printed by setup | Owned fixture's original file; relative names resolve under `eng/docker-compose`, so prefer the emitted absolute path |

<!-- cdc-snippet: cdc-e2e-teardown -->
```powershell
pwsh src/dms/tests/EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1 -DatabaseEngine postgresql -EnvironmentFile '<original-e2e-environment-path>'
```
<!-- /cdc-snippet: cdc-e2e-teardown -->

E2E teardown removes the shared staged workspace only after both project teardowns
succeed and CDC workspace protection permits it. It then removes only the two
known local images, `ed-fi-api-local` and `ed-fi-api-config-local`. It does not prune
unrelated resources, published images or the external shared `dms` network. See
[the shipped E2E teardown module](../../eng/docker-compose/e2e-teardown.psm1).

### Verify ordering and retained state

The [lifecycle implementation](../../eng/docker-compose/cdc-lifecycle.psm1) persists
`Retiring`, runs governed retirement for every retained entry, verifies the worker
inventory is empty, then persists `Retired` before destructive Compose removal.
An unknown extra connector or unavailable inventory blocks volume deletion even
when individual retirement calls succeeded. From a verified `Stopped` deployment,
it restores database/worker infrastructure for inspection and retirement without
resuming connectors or starting DMS writers. A proven early initial failure may
also use controller-managed inspection startup; operators do not start workers
independently to bypass provenance checks.

| Durable phase / symptom | Retained authority and retry |
| --- | --- |
| `Retiring`, controller failure, nonempty/unavailable worker inventory | Infrastructure remains for reconciliation. Repeat the same destructive wrapper; all entries are reconciled again before volume removal. One successful peer does not authorize shared cleanup. |
| `Retired`, Compose removal failed or process exited after removing some/all services | Every retirement and the empty inventory were already verified. Repeat `-d -v`; wrapper resumes infrastructure removal from this checkpoint without requiring already removed services to be recreated. Ordinary startup/stop is rejected at this phase. |
| `RuntimeCleanup`, interrupted generated-file removal | Compose removal completed and exact generated-file paths/hashes were durably recorded. Repeat destructive teardown to finish eligible file cleanup; wrapper does not reread already removed settings or repeat controller/Compose operations. Changed files, peer references and source-state protection still block deletion. |
| Missing/changed/unreadable inventory, original settings or effective environment | Preserve remaining resources and escalate through [provenance handling](#unsupported-provenance); configuration removal supplies no authority. Do not manufacture a `Retired`/`RuntimeCleanup` checkpoint. |
| `surviving protected source-state root` or `runtime cleanup is incomplete` | Keep inventory, custom/nested roots and remaining configuration. Protection may reject workspace removal after volume removal already succeeded. Resolve retention with the deployment owner; no recursive-delete workaround and no continuity recovery by moving/restoring state. |

Direct retirement retains all settings. Successful **stack** teardown can remove
only the inventoried generated `bootstrap-<id>.settings.json` and
`bootstrap-<id>.dms.json` immediately under `.bootstrap/cdc-runtime`, after hash and
peer/state checks. An empty runtime directory can then be removed nonrecursively.
Custom/external settings are not that generated-file cleanup scope. Unrelated files
in `cdc-runtime`, generated files referenced by another deployment, and any files
inside protected source roots are retained or block cleanup. The project inventory
can be removed after eligible cleanup only when no surviving source root intersects
`.bootstrap`; otherwise it remains to protect that root on subsequent attempts.

All controller roots, including external custom roots, retain their provisioning
journals and source history. Roots nested in or encompassing `.bootstrap` block
recursive workspace deletion even after generation retirement. A peer inventory or
remaining `cdc-runtime` directory also retains the workspace. When no protection
remains, bootstrap/E2E workspace cleanup can remove other staged inputs there;
archive required incident/configuration evidence before the destructive command.
A new E2E setup still rejects a protected retained workspace. Do not delete history
to release it. Preserve the shared broker-size override for surviving peers; direct
per-binding cleanup is not authority to remove shared files or volumes.

[Lifecycle wrapper fixtures](../../eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1)
cover peer ordering, interrupted retirement/Compose/file cleanup, settings conflicts
and nested state. [Provider-specific evidence rows](cdc-inv-evidence.md#procedure-evidence)
map these procedures to T25/T26; exact live snippet exercises remain pending. Neither
wrapper exit `0` nor removed local volumes supplies platform byte-purge evidence,
initial eligibility, migration continuity or a supported new-generation cutover.
Use the [sensitive-data handoff](#sensitive-data-response) for disclosure evidence.

<a id="representation-restamp"></a>

## Compatible representation-restamp handoff

Use this handoff only for a compatible representation correction whose previously
published bytes do **not** require purging. The
[offline correction owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction)
and [v1 compatibility owner](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes)
keep the same key, topic, fields, types and ordering semantics. If superseded
sensitive bytes require removal, go directly to [disclosure response](#sensitive-data-response);
same-topic correction is unavailable. E18 owns the actual
[offline prerequisites, preview, execute/resume and verification commands](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#representation-restamp).

| Record | Value |
| --- | --- |
| Target/generation | E18 normalized tenant/data-store target and physical-source fingerprint must match the affected CDC binding. Retain the original generation and topic; this handoff neither creates nor replaces them. |
| Authority/offline window | Deployment owner stops and verifies every DMS replica, API reader/writer, projector, direct-fill writer, bulk/seed loader, administrative peer and external writer. Mark the CDC target not ready in deployment operations. Hold the full fence through preview, execute and every retry; only corrected instances may start afterward. The utility's offline confirmation does not establish the fence. |
| Retained inputs | Original full runtime settings, schema inputs and controller root from [state preservation](#deployment-state); E18 durable manifest, immutable scope/mode/reason/boundary, operation ID and result in the deployment's protected audit store. Do not recreate a manifest or mutate CDC state to resume. |
| Invocation | Follow E18's linked restamp procedure. Only after its successful completion and corrected-instance startup, run `cdc-restamp-handoff-status` below for Tracking with an existing CDC binding; follow [monitoring](#monitoring-retention) for projection observations. No CDC observation is required to claim Disabled canonical-only completion. |
| JSON/exit status | E18 execute exit `0` with `result.state: "completed"` and `result.claimLevel: "projectionWorkQueued"` means Tracking canonical work finished and projection work queued; `"canonicalOnlyComplete"` means Disabled canonical-only completion. CDC `status` uses a different envelope: exit `0`, `operation: "status"`, `succeeded: true`, `exitCode: 0`, `data.aggregate.readiness: "Ready"` report only current readiness. |
| Postcondition | Tracking: corrected projection/connector processing may eventually replace affected public state with higher `contentVersion` and changed `document._etag`; verification of those records is separate from restamp/status completion. Disabled: verify E18 relational API validators and Change Query visibility, with no projection or Kafka publication expectation. |
| Rejection/timeout action | Keep the full fence for incomplete/rejected restamp; use the E18 retry rules below. After corrected startup, non-ready/unavailable CDC observations route to [projection handoff](#projection-handoff), [native recovery](#native-recovery) or [provenance diagnosis](#unsupported-provenance); never reset offsets, force readiness or substitute a new generation. |

### Preserve the fence through the E18 handoff

1. Confirm compatibility and absence of a purge requirement with the incident and
   consumer owners. The deployment owner marks the target not ready and verifies
   the full offline fence before the E18 preview. CDC status does not gate ordinary
   API routing, and SchemaTools has no manual `mark-not-ready` command. Do not edit
   a binding or manufacture a terminal incident to represent this operational hold.
2. Deploy the corrected materializer/composer while offline. Use the E18 procedure
   with explicit affected-document scope and the mode matching durable lifecycle:
   `tracking` for `Tracking`, or `disabled` for `Disabled`, with a clear cache-ahead
   latch. `Resetting`, `Rebuilding`, a set latch or mismatched mode reject restamp;
   do not use an internal-only reset or manual SQL to bypass the guard. The
   [projection/history gate](#projection-handoff) still applies to its three commands.
3. Preserve the E18 manifest and operation ID across interruption. Exit `12`,
   `status: "incompleteRetryable"` or `result.state: "incomplete"` requires the same
   execute target/operation, renewed offline confirmation and reacquired mutex as
   described by [E18 execute/resume](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#execute-or-resume).
   Keep the fence; do not preview a replacement operation or retry a completed ID.
   For rejection, configuration or pre-mutation failure, use
   [E18 exit classifications](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#exit-codes)
   and retain the diagnostic before correcting the original inputs.
4. After successful completion, start only corrected DMS/projector instances for
   Tracking, or corrected DMS API instances for Disabled, following
   [E18 verification](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#verify-and-restore-service).
   For compatible Tracking, the existing connector may remain registered against
   the same binding/topic. If it was deliberately stopped, the original-generation
   [managed lifecycle](#managed-lifecycle) guards still govern startup; restamp is
   not restart authorization. Never follow this startup step during disclosure.

PowerShell, repository root; built/installed `api-schema-tools` as in setup, with
original services reachable. Run only at step 4 for Tracking and an existing
binding, after corrected instances start. Inspect conflicting `DMS_CDC__` overrides
privately before use. Status may persist incidents and attempt containment.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Original complete runtime settings for the affected binding | Owned PostgreSQL/SQL Server fixture's retained settings for the same restamped source |
| `<original-state-root>` | Original managed provisioning/controller root | Same fixture's intact root, binding and history |

<!-- cdc-snippet: cdc-restamp-handoff-status -->
```powershell
api-schema-tools cdc status --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-restamp-handoff-status -->

Retain sanitized stdout JSON, stderr diagnostics and process exit separately. Exit
`1` includes not-ready/unavailable observations or rejection; `2` is invalid input;
`130` is cancellation. Early failures may omit `data`. When present, inspect
`data.targets[].observedAt`, `status`, `details`, `diagnostics`, `containment` and
`incidentPersistence` using [monitoring](#monitoring-retention) and
[native recovery](#native-recovery). Later `Ready` is not retrospective certification.

Under the [correction contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction),
restamp does not drain the projection queue, publish Kafka records, verify connector
or broker delivery, reset offsets, purge older bytes, or certify an exact replacement
baseline. Use authorized consumer-owner inspection to verify eventual affected
record versions/ETags without copying payloads into evidence. The existing
[real-restamp publication fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/RepresentationRestampCdcStateTests.cs)
checks this separate projection-and-publication outcome for both providers; it is
not purge evidence or a live exercise of this marked status command. Exact snippet
qualification remains [pending T25/T26](cdc-inv-evidence.md#procedure-evidence).

<a id="sensitive-data-response"></a>

## Sensitive-data disclosure response

Follow the [disclosure owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction)
when prior Kafka values contain sensitive information that must be removed.
Containment takes priority over continued CDC availability. A higher-version upsert,
tombstone, compaction request or successful restamp establishes at most eventual
current state; none proves destruction of superseded bytes.

| Record | Value |
| --- | --- |
| Target/generation | Incident inventory of every affected original binding generation, public topic and physical source, plus platform remote/tiered copies and independent consumer stores/exports. Privately reconcile identity from retained settings/bindings; do not reconstruct names. |
| Authority/offline window | Incident/deployment owner maintains the not-ready hold and full E18 offline fence. Controller authority stops every affected connector/task; Kafka/platform security authority revokes effective consumer access. Keep publication and consumer access fenced through correction, retirement and purge verification. |
| Retained inputs | Original settings, state roots, provisioning/source history and wrapper inventory; protected incident audit record with containment observations/times, restamp manifest/result if used, retirement operation ID, deletion requests and independent platform purge confirmations. Keep cleanup infrastructure and original authority available. |
| Invocation | Ordered steps below: deployment hold, `cdc-disclosure-containment-result` for each affected binding and deployment-owned consumer revocation, E18 offline correction if needed, then existing `cdc-retire`. No public manual not-ready, access-revocation, purge-certification or new-generation command exists in SchemaTools. |
| JSON/exit status | For `stop`, require exit `0`, `operation: "stop"`, `succeeded: true`, `exitCode: 0`, expected `binding`, `data.operation: "Stop"`, `data.succeeded: true`, `data.targetShutdownVerified: true`, `data.ready: false`, `data.boundary: "VerifiedManagedStop"`. This verifies only that target's stop. Retirement uses the distinct [retire result](#generation-retirement); neither result attests consumer revocation or physical purge. |
| Postcondition | Affected generation remains unavailable and is governed-retired; incident closure additionally requires the platform's purge evidence and independently owned downstream response evidence. There is no old-topic restart or supported replacement-generation workflow. |
| Rejection/timeout action | Stop exit `1`/missing verification means containment is unproven; `2` invalid input, `130` cancellation. Preserve evidence and escalate under the failure table below. Partial retirement retries retain original identity/state. Missing platform confirmation leaves the incident open. |

### Contain, correct and retire in order

1. **Establish the operational hold.** The incident/deployment owner marks the
   affected target not ready in deployment operations, excludes all writers and
   API readers named in the E18 offline prerequisites, and prevents automated
   start/restart/resume of affected connectors. This is an explicit deployment
   authority handoff, not a new CLI command or a state-file edit. Current CDC health
   cannot override the hold; it does not gate ordinary API traffic.
2. **Verify connector fencing and revoke consumer access before corrected publication.**
   Run the marked stop below for each affected retained binding; require verified
   stopped connector/tasks, not just a REST acknowledgement. The security/platform
   owner revokes and verifies effective consumer access to every affected public
   topic through that deployment's access controls, covering existing consumers
   and alternate identities/access paths. Record both boundaries and their times.
   The local `AuthorizationDisabledLocal` profile reports
   `aclIsolationProven: false`; it cannot demonstrate ACL revocation. Its owner
   must use deployment network/process isolation and evidence, or leave containment
   unverified and escalate. Do not present local stop as consumer isolation.
3. **Correct while offline.** After both connector fencing and consumer-access
   exclusion are verified, deploy the corrected materializer and, when needed,
   follow [E18 restamp](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#representation-restamp)
   with the full writer fence intact. Do not follow the compatible handoff's
   service/connector startup step. Queued corrected work is not authorization to
   publish to the old topic or to reopen access.
4. **Retire the original affected generation.** Use [guarded generation retirement](#generation-retirement)
   and its `cdc-retire` snippet with explicit generation and destructive intent.
   Keep database, Connect, broker and controller state reachable. Let the controller
   enforce cleanup order: verified stop, source offsets before connector deletion,
   governed provider/topic/ACL cleanup including public/progress topics and SQL
   Server schema history, then binding/incident removal last. Retain the external
   disclosure audit even when controller incident files are removed. Shared-volume
   teardown is a separate procedure and cannot substitute for retirement or purge proof.
5. **Obtain purge and downstream evidence.** The broker/managed-platform owner
   supplies the confirmation required by its deletion guarantee for the public
   topic and covered remote/tiered copies. Consumer owners account for independent
   stores, caches, exports and their copies under the deployment's disclosure
   response. Keep the incident open if required confirmation is unavailable.
   Never recreate/restart the old binding or topic. CDC remains unavailable;
   [new-generation topic, consumer namespace, fresh snapshot and publication-barrier cutover](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deferred-new-topic-cutover)
   remain deferred, not an executable re-enablement procedure.

PowerShell, repository root; built/installed `api-schema-tools` and original retained
settings/state, reachable worker/Connect, and deployment-owned hold from step 1.
This stops publication for one affected binding without deleting artifacts. It does
not establish the writer fence or revoke consumer access. Check conflicting
`DMS_CDC__` overrides privately before invoking; repeat separately for every affected
binding using that binding's original inputs.

| Literal placeholder | Operator source | Permitted fixture replacement |
| --- | --- | --- |
| `<retained-settings-path>` | Original full runtime settings of the affected generation | Same owned fixture's retained settings; faults only in explicitly selected containment cases |
| `<original-state-root>` | Original controller root with retained identity/authority | Same fixture's root; no reconstructed binding or incident |

<!-- cdc-snippet: cdc-disclosure-containment-result -->
```powershell
api-schema-tools cdc stop --settings '<retained-settings-path>' --state-path '<original-state-root>' --json
```
<!-- /cdc-snippet: cdc-disclosure-containment-result -->

Retain the sanitized JSON, stderr and process exit independently. Successful stop
keeps offsets, binding and other artifacts; `data.ready: false` is expected. It
neither latches a disclosure incident nor fences future deployment actions. Hold
restart authority externally. Do not stop a shared worker based on one target's
result; [managed shutdown](#managed-lifecycle) requires every managed target and a
complete fresh worker inventory. Preserve services needed for governed retirement.

| Observation | Required action and completion boundary |
| --- | --- |
| Stop rejected/unavailable/timed out/cancelled; absent `data` or `targetShutdownVerified: false` | Treat connector containment as unverified; publication may continue. Keep the offline/access hold, preserve original state and diagnostics, and escalate immediately to deployment authority for infrastructure fencing. Retry governed stop only with sufficient original authority and fresh readback. No raw Connect mutation or recreated state bypass. |
| Status reports `containment: "Failed"` or `incidentPersistence: "Failed"` | Apply [native recovery](#native-recovery) to each failed dimension. Persist sanitized failure evidence in the protected external incident record; attempted stop/persistence is not completion. Later healthy status cannot certify the unsampled interval. |
| Consumer-access exclusion cannot be verified, including authorization-disabled local tooling | Keep the incident open and escalate to the security/platform owner; do not restamp/rebuild into possible publication or claim isolation from connector stop. |
| Restamp rejects or returns incomplete | Keep all fences, retain the same E18 manifest and operation ID, and follow E18 rejection/resume rules. No new preview, forced latch/lifecycle change or old-topic startup. |
| Retirement partially succeeds or rejects | Preserve infrastructure and surviving evidence; follow [partial cleanup](#partial-cleanup-and-rejection-actions) with the same generation/operation. Never manually remove provenance, remaining artifacts or shared volumes to manufacture success. |
| Retire succeeds but purge confirmation is missing | Governed artifact cleanup completed, physical purge remains unproven. Keep access closed and incident open pending platform evidence. Delete success, absent metadata, configuration removal, corrective upsert, tombstone, compaction or volume removal is insufficient. |

### Protected audit and closure evidence

The [disclosure contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction)
and [security owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations)
govern the audit record. Retain these in protected incident storage; link sanitized
artifact references from the [evidence index](cdc-inv-evidence.md#recording-results):

| Evidence | Owner and scope |
| --- | --- |
| Incident, target, physical source, binding generation and public topic | Incident owner reconciles original identities and affected downstream copies. Keep the exact identities private; use sanitized references in shared evidence. |
| Containment time and verification | Deployment/controller owner records not-ready hold, each connector/task fence, full offline writer fence; security owner records effective consumer-access revocation and time. Distinguish partial attempts from the time all required fences were verified. |
| Restamp operation ID and immutable manifest, if used | E18 owner retains scope, reason, mode, affected UUIDs and bounded completion/retry result. Record when restamp was unnecessary rather than inventing an operation ID. |
| Retirement operation ID, generation/topic and deletion request | Controller/deployment owner retains the scoped command result and request reference/time even after binding/incident cleanup. |
| Broker/managed-platform purge confirmation | Platform owner supplies the evidence required by its deletion guarantee, including covered remote/tiered copies; no controller field provides this attestation. |
| Independent consumer stores/exports | Each owner records disclosure-response completion for its copies; Kafka retirement does not erase independent data. |

Manifest identities, scopes, reasons and UUIDs are operational audit data, not
metric labels or unsanitized log values. Do not put credentials, connection strings,
document bodies or API/stream response payloads into manifests, reasons, examples,
diagnostics, telemetry or shared artifacts. Retain only necessary protected identity
and sanitized administrative outcomes; verify record differences without copying
sensitive content. Neither restamp's bounded claim nor controller cleanup authorizes
restoring Kafka access or closing the incident. Exact marked command exercises remain
[pending T25/T26](cdc-inv-evidence.md#procedure-evidence); platform/consumer attestations
remain deployment-owned even after those fixtures pass.

<a id="serialized-result-examples"></a>

## Checked JSON result excerpts

These excerpts select stable fields from production serialization; they are not
complete output documents or live qualification evidence. The tests select the
fields independently, so missing, renamed or incorrectly cased example fields fail.
Preserve the complete actual result privately. Identity, timestamps and unrelated
fields are omitted here for readability. See [status interpretation](#monitoring-retention),
[containment](#native-recovery) and the operation-specific procedures before acting.

A ready established status has current lag but need not have percentile statistics:

<!-- cdc-snippet: cdc-output-ready -->
```json
{
  "operation": "status",
  "succeeded": true,
  "exitCode": 0,
  "data": {
    "aggregate": {
      "readiness": "Ready"
    },
    "targets": [
      {
        "details": {
          "queuePresence": "Empty",
          "lagMilliseconds": 1
        },
        "incidentPersistence": "NotRequired",
        "containment": "NotRequired",
        "status": {
          "sourceHistory": {
            "incidentLatched": false
          }
        }
      }
    ]
  }
}
```
<!-- /cdc-snippet: cdc-output-ready -->

Projection backlog is independently not ready even with low connector lag:

<!-- cdc-snippet: cdc-output-backlog -->
```json
{
  "operation": "status",
  "succeeded": false,
  "exitCode": 1,
  "data": {
    "aggregate": {
      "readiness": "NotReady"
    },
    "targets": [
      {
        "details": {
          "queuePresence": "NotEmpty",
          "lagMilliseconds": 1
        },
        "incidentPersistence": "NotRequired",
        "containment": "NotRequired",
        "status": {
          "sourceHistory": {
            "incidentLatched": false
          }
        }
      }
    ]
  }
}
```
<!-- /cdc-snippet: cdc-output-backlog -->

Unavailable telemetry omits current lag; it does not report zero:

<!-- cdc-snippet: cdc-output-unavailable -->
```json
{
  "operation": "status",
  "succeeded": false,
  "exitCode": 1,
  "data": {
    "aggregate": {
      "readiness": "NotReady"
    },
    "targets": [
      {
        "details": {
          "queuePresence": "Empty"
        },
        "incidentPersistence": "NotRequired",
        "containment": "NotRequired",
        "status": {
          "sourceHistory": {
            "incidentLatched": false
          }
        }
      }
    ]
  }
}
```
<!-- /cdc-snippet: cdc-output-unavailable -->

A terminal history incident can persist and stop successfully while remaining not ready:

<!-- cdc-snippet: cdc-output-terminal -->
```json
{
  "operation": "status",
  "succeeded": false,
  "exitCode": 1,
  "data": {
    "aggregate": {
      "readiness": "NotReady"
    },
    "targets": [
      {
        "details": {
          "queuePresence": "Empty"
        },
        "incidentPersistence": "Persisted",
        "containment": "Stopped",
        "status": {
          "sourceHistory": {
            "incidentLatched": true
          }
        }
      }
    ]
  }
}
```
<!-- /cdc-snippet: cdc-output-terminal -->

A successful stop verifies only this target's shutdown. It does not establish
readiness or certify an unsampled interval. The optional `data.observation` is
absent in this representative result; the recovery explanation is omitted here.

<!-- cdc-snippet: cdc-output-stop -->
```json
{
  "operation": "stop",
  "succeeded": true,
  "exitCode": 0,
  "diagnostics": [],
  "data": {
    "operation": "Stop",
    "succeeded": true,
    "targetShutdownVerified": true,
    "ready": false,
    "boundary": "VerifiedManagedStop",
    "diagnostics": [],
    "recovery": {
      "boundary": "VerifiedManagedStop",
      "requiresFreshPass": false,
      "unobservedIntervalCertified": false
    }
  }
}
```
<!-- /cdc-snippet: cdc-output-stop -->

A retirement result has its own operation ID (a fixed example value here).
It is not purge confirmation or permission to restart the generation:

<!-- cdc-snippet: cdc-output-retire -->
```json
{
  "operation": "retire",
  "succeeded": true,
  "exitCode": 0,
  "diagnostics": [],
  "data": {
    "succeeded": true,
    "operationId": "11111111-1111-1111-1111-111111111111",
    "diagnostics": []
  }
}
```
<!-- /cdc-snippet: cdc-output-retire -->

The packaged host emits one final JSON document on stdout. These watch failure
excerpts omit diagnostic messages but retain the component and process exit.
Early failures omit `data`; parser rejection uses `operation: "cdc"`. Diagnostic text and cancellation advice use stderr.
Invalid arguments are rejected by the production executable before controller dispatch.


<!-- cdc-snippet: cdc-output-failure -->
```json
{
  "operation": "watch",
  "succeeded": false,
  "exitCode": 1,
  "diagnostics": [
    {
      "component": "Request",
      "failure": "Unavailable"
    }
  ]
}
```
<!-- /cdc-snippet: cdc-output-failure -->

<!-- cdc-snippet: cdc-output-invalid -->
```json
{
  "operation": "cdc",
  "succeeded": false,
  "exitCode": 2,
  "diagnostics": [
    {
      "component": "Request",
      "failure": "InvalidInput"
    }
  ]
}
```
<!-- /cdc-snippet: cdc-output-invalid -->

<!-- cdc-snippet: cdc-output-cancelled -->
```json
{
  "operation": "watch",
  "succeeded": false,
  "exitCode": 130,
  "diagnostics": [
    {
      "component": "Request",
      "failure": "Unavailable"
    }
  ]
}
```
<!-- /cdc-snippet: cdc-output-cancelled -->

For watch, each pass's data document goes to stderr; only the final envelope goes
to stdout. This controlled process-routing example uses unavailable readiness and
exit `1`; it is not a live controller observation:

<!-- cdc-snippet: cdc-output-watch -->
```json
{
  "operation": "watch",
  "succeeded": false,
  "exitCode": 1,
  "data": {
    "aggregate": {
      "readiness": "Unknown"
    }
  }
}
```
<!-- /cdc-snippet: cdc-output-watch -->

An established source mismatch rejects validation without an adoption/replacement
override. The classification is contextual; this broad diagnostic also serves
other projection validation failures. Preserve original evidence and follow
[unsupported provenance/source mismatch](#unsupported-provenance).

<!-- cdc-snippet: cdc-output-source-mismatch -->
```json
{
  "operation": "validate",
  "succeeded": false,
  "exitCode": 1,
  "diagnostics": [{ "component": "Projection", "failure": "ValidationFailed" }]
}
```
<!-- /cdc-snippet: cdc-output-source-mismatch -->
