# CDC Operations Runbook

[Entry point](README.md) · [Evidence index](cdc-inv-evidence.md)

This shared PostgreSQL/SQL Server runbook is under construction. Both providers’ setup
and DMS E2E opt-in variants, state preservation, managed lifecycle, recovery and
projection handoffs are documented; live qualification remains pending. Other
records reserve stable destinations and are **pending** their named tasks; do not
execute an unfinished workflow. Use the [shipped command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
for current command details and the linked design owners for support boundaries.

## Procedure Navigation

| Need | Procedure | Documentation task |
| --- | --- | --- |
| PostgreSQL local setup | [postgresql-setup](#postgresql-setup) | T02 — documented; T16 exercise pending |
| SQL Server local setup | [sql-server-setup](#sql-server-setup) | T03 — documented; T17 exercise pending |
| DMS E2E opt-in | [dms-e2e-setup](#dms-e2e-setup) | Both providers documented; T16/T17 exercises pending |
| Preserve deployment state | [deployment-state](#deployment-state) | T04 — documented; T18/T19 exercise pending |
| Interrupted initial-enable retry | [initial-enable-retry](#initial-enable-retry) | T04 — documented; T18/T19 exercise pending |
| Established validation and restart preflight | [established-validation](#established-validation) | T04 — documented; T18/T19 exercise pending |
| Missing provenance and source mismatch | [unsupported-provenance](#unsupported-provenance) | T04 — documented; T18/T19 exercise pending |
| Managed shutdown and startup | [managed-lifecycle](#managed-lifecycle) | T05 — documented; live exercise pending |
| Intact connector restart and resume | [intact-restart](#intact-restart) | T05 — documented; live exercise pending |
| Native recovery and incomplete shutdown | [native-recovery](#native-recovery) | T05 — documented; live exercise pending |
| Projection troubleshooting and administration handoff | [projection-handoff](#projection-handoff) | T06 — documented; T25/T26 exercise pending |
| Monitoring and provider retention | [monitoring-retention](#monitoring-retention) | T07 — pending |
| Security, topic retention and consumer evidence | [security-consumer-evidence](#security-consumer-evidence) | T08 — pending |
| Coordinated record-size increase | [record-size-increase](#record-size-increase) | T09 — pending |
| Guarded generation retirement | [generation-retirement](#generation-retirement) | T10 — pending |
| Destructive stack teardown | [stack-teardown](#stack-teardown) | T10 — pending |
| Compatible representation-restamp handoff | [representation-restamp](#representation-restamp) | T11 — pending |
| Sensitive-data disclosure response | [sensitive-data-response](#sensitive-data-response) | T11 — pending |

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
their executable fenced block with
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

Only marked runnable examples are inputs to the focused checks planned in T13–T15.
Illustrative output is separately identified and checked against production
serialization fixtures. Reserved IDs, unmarked blocks and prose must not be executed.
The [evidence index](cdc-inv-evidence.md#recording-results) distinguishes parser checks
from live provider exercises.

<a id="postgresql-setup"></a>

## PostgreSQL local setup

**Documented in T02; live exercise pending T16.** This is an initial setup on an
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
corresponding setting together. Leave multitenancy disabled and route qualifiers
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

The CLI writes one final JSON envelope to stdout (`operation`, `succeeded`,
`exitCode`, `diagnostics`, and operation-specific `data`; `binding` and
`deploymentProfile` when available). Diagnostics and watch passes go to stderr.
Retain the two streams separately in restricted files; publish only sanitized results.
[Host serialization](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/Cdc/CdcCommandHost.cs)
uses camel-case properties and string enum values; null properties may be omitted.
The local profile reports `deploymentProfile.aclIsolationProven: false`.

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

**Documented in T03; exact public snippets await T17 live exercise.** Use the same
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

Prepare the protected `eng/docker-compose/.env` with effective `MSSQL_PORT=1435`,
`MSSQL_DB_NAME=edfi_datamanagementservice` and a private `MSSQL_SA_PASSWORD`, plus
normal CMS/identity settings and the connector's `CDC_DATABASE_PASSWORD`.
The shipped [engine resolver](../../eng/docker-compose/env-utility.psm1) applies
[.env.mssql](../../eng/docker-compose/.env.mssql), preserving nonblank custom MSSQL
credentials/names/ports and producing an effective file under `.derived` when needed.
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
controller call budgets match; the bounded admission wait is 600 seconds.

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
| Agent, capture/cleanup jobs, retained LSN range | These are separate CDC prerequisites. The selected infrastructure enables Agent; initial provider setup owns expected capture artifacts/jobs and validates their state/progress. A running SQL service proves none of these. Preserve provider diagnostics and use [monitoring/retention](#monitoring-retention) (T07 detail pending); do not drop/recreate captures or reset offsets. |
| Snapshot isolation / row versions | New-database provisioning enables `ALLOW_SNAPSHOT_ISOLATION` as well as RCSI. CDC requires snapshot isolation for its initial snapshot; RCSI alone is insufficient. `CDC_SQLSERVER_SNAPSHOT_ISOLATION_OFF` rejects readiness. Preserve state and resolve the prerequisite under the initial/established boundary rather than forcing admission. |
| Internal schema history unavailable/inconsistent or LSN history lost | Internal Kafka schema history is required even with public schema-change events disabled. Retain it with offsets on ordinary stop/start. Established source-history incidents route to [unsupported provenance](#unsupported-provenance); no silent history recreation or same-binding resnapshot. |
| `CDC_SQLSERVER_CONNECTOR_LOGIN_MISSING`, `CDC_SQLSERVER_CONNECTOR_LOGIN_UNSUPPORTED`, `CDC_SQLSERVER_CONNECTOR_LOGIN_ELEVATED` | Deployment owner reviews the prepared login, type and effective server access; keep writers excluded. No automatic login/credential management or access broadening. Retry only if original workflow classification permits it. |
| `CDC_SQLSERVER_CONNECTOR_USER_MAPPING_MISMATCH`, elevated database-access rejection, or `CDC_SQLSERVER_CONNECTOR_USER_MISSING` | Preserve identity/access evidence privately and escalate. Existing users must match login SID/type and narrow effective access. Once provider completion is durable, even pre-registration retry is validation-only and must not recreate or remap a missing/conflicting user. |
| `CDC_SQLSERVER_SETUP_PRINCIPAL_FAILURE` | Setup administrator needs principal-definition visibility (`VIEW ANY DEFINITION`, implied by `sa` here), user-creation and provider setup authority. Correct setup authority without elevating the connector; retain state and follow initial retry classification. |

The owning [SQL Server contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server)
and [T29 implementation evidence](cdc-inv-evidence.md#sql-server-initial-user-mapping-t29)
define the mapping/retry boundary. T17 still must exercise these exact public commands,
including E2E variants, from a restricted login with no precreated target user.

<a id="dms-e2e-setup"></a>

## DMS E2E opt-in

**PostgreSQL documented in T02; SQL Server in T03. Live exercises pending T16/T17.** These alternatives qualify setup wiring. API-driven message
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

Finish with [governed stack/E2E teardown](#stack-teardown) (T10 detail pending).
Use the setup wrapper's printed teardown command with its exact resolved environment
and provider, or the existing [managed lifecycle command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#managed-stack-lifecycle).
Governed retirement must complete while infrastructure is reachable before volume
deletion. Retain source history and CDC settings afterward. Ordinary stop/start uses
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
The resolver preserves custom nonblank MSSQL credentials/ports/names, replaces
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

**Documented in T04; live exercise pending T18/T19.** Apply to both providers from
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

**Documented in T04; live exercise pending T18/T19.** Follow the
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

**Documented in T04; live exercise pending T18/T19.** The
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

**Documented in T04; live rejection exercises pending T18/T19.** Use the
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
those mutations. See the private path substitution table in `cdc-validate`.

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

**Documented in T05; live exercise pending T18/T19.** Use the
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

**Documented in T05; live exercise pending T18/T19.** These are selected-connector
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

<a id="monitoring-retention"></a>

## Monitoring and provider retention

**Pending T07; exercise T27/T28.** Scope to deliver: Metric names/units/freshness, absent fields, current lag versus percentiles; WAL/disk, capture/cleanup/LSN/version store, offsets/progress/history, cleaner health and bounded overhead evidence.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).

| Record | Value |
| --- | --- |
| Target/generation | Selected target/generation plus provider and shared-worker scope. |
| Authority/offline window | Pending T07: specify authority and applicable fence from the linked owner. |
| Retained inputs | Timestamped sanitized controller, projection and exporter observations. Exact paths and substitutions pending T07. |
| Invocation | Reserved IDs: `cdc-status`, `cdc-watch`, `cdc-telemetry-inspect`, `cdc-pg-retention-inspect`, `cdc-sqlserver-retention-inspect`. Commands and fixture substitutions pending T07. |
| JSON/exit status | Pending T07: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T07: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T07: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="security-consumer-evidence"></a>

## Security, topic retention and consumer evidence

**Pending T08; exercise T20.** Scope to deliver: Setup/connector/worker/controller/consumer access, private endpoints, externalized credentials, state permissions and sanitized evidence.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).

| Record | Value |
| --- | --- |
| Target/generation | Binding artifacts, deployment roles and consumer namespaces. |
| Authority/offline window | Pending T08: specify authority and applicable fence from the linked owner. |
| Retained inputs | Effective access and topic policies, barriers, durable checkpoints, deadline/renewal and retained-log capacity evidence. Exact paths and substitutions pending T08. |
| Invocation | Reserved IDs: `cdc-access-inspect`, `cdc-topic-policy-inspect`, `cdc-consumer-evidence`. Commands and fixture substitutions pending T08. |
| JSON/exit status | Pending T08: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T08: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T08: diagnostic-specific retry, containment or escalation and retained evidence. |

Additional owners: [public consumer bootstrap](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap) and [topic retention](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic).

<a id="record-size-increase"></a>

## Coordinated record-size increase

**Pending T09; exercise T23/T24.** Scope to deliver: Renewed invocation confirmation, interrupted rollout retry and coordinated ceilings.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase).

| Record | Value |
| --- | --- |
| Target/generation | Explicit binding generation and stable increase operation ID. |
| Authority/offline window | Pending T09: specify authority and applicable fence from the linked owner. |
| Retained inputs | Previous settings, acknowledgement and complete consumer-owner capacity evidence or explicit no-consumers inventory. Exact paths and substitutions pending T09. |
| Invocation | Reserved IDs: `cdc-size-no-consumers`, `cdc-size-increase`, `cdc-size-retry`. Commands and fixture substitutions pending T09. |
| JSON/exit status | Pending T09: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T09: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T09: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="generation-retirement"></a>

## Guarded generation retirement

**Pending T10; exercise T25/T26.** Scope to deliver: Partial-cleanup retry, controller result and retained source history; separate platform purge evidence.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).

| Record | Value |
| --- | --- |
| Target/generation | Explicit binding generation and destructive cleanup intent. |
| Authority/offline window | Pending T10: specify authority and applicable fence from the linked owner. |
| Retained inputs | Original state/settings, reachable infrastructure and cleanup evidence. Exact paths and substitutions pending T10. |
| Invocation | Reserved IDs: `cdc-retire`. Commands and fixture substitutions pending T10. |
| JSON/exit status | Pending T10: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T10: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T10: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="stack-teardown"></a>

## Destructive stack teardown

**Pending T10; exercise T25/T26.** Scope to deliver: Per-binding retirement before shared-volume deletion; document surviving state and sensitive-data handoff.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).

| Record | Value |
| --- | --- |
| Target/generation | All registered generations and shared stack volumes. |
| Authority/offline window | Pending T10: specify authority and applicable fence from the linked owner. |
| Retained inputs | Deployment inventory, original roots/settings and retirement results. Exact paths and substitutions pending T10. |
| Invocation | Reserved IDs: `cdc-stack-teardown`, `cdc-e2e-teardown`. Commands and fixture substitutions pending T10. |
| JSON/exit status | Pending T10: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T10: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T10: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="representation-restamp"></a>

## Compatible representation-restamp handoff

**Pending T11; exercise T25/T26.** Scope to deliver: E18 owns execution; distinguish canonical completion, queued work and later publication without a purge or exact-baseline claim.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction).

| Record | Value |
| --- | --- |
| Target/generation | Restamp operation, target, physical source and affected generation. |
| Authority/offline window | Pending T11: specify authority and applicable fence from the linked owner. |
| Retained inputs | Offline E18 manifest/result plus CDC observations. Exact paths and substitutions pending T11. |
| Invocation | Reserved IDs: `cdc-restamp-handoff-status`. Commands and fixture substitutions pending T11. |
| JSON/exit status | Pending T11: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T11: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T11: diagnostic-specific retry, containment or escalation and retained evidence. |

Execution remains in the [DocumentCacheAdmin restamp procedure](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#representation-restamp); compatibility is owned by [ADR 0002](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes).

<a id="sensitive-data-response"></a>

## Sensitive-data disclosure response

**Pending T11; exercise T25/T26.** Scope to deliver: Shipped containment/retirement results, consumer access and independently operated stores; deferred re-enablement remains unsupported.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction).

| Record | Value |
| --- | --- |
| Target/generation | Incident, affected generations/topics and downstream copies. |
| Authority/offline window | Pending T11: specify authority and applicable fence from the linked owner. |
| Retained inputs | Containment time, restamp ID, cleanup requests and platform purge confirmation. Exact paths and substitutions pending T11. |
| Invocation | Reserved IDs: `cdc-disclosure-containment-result`. Commands and fixture substitutions pending T11. |
| JSON/exit status | Pending T11: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T11: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T11: diagnostic-specific retry, containment or escalation and retained evidence. |
