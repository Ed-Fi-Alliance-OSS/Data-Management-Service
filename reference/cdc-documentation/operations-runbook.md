# CDC Operations Runbook

[Entry point](README.md) · [Evidence index](cdc-inv-evidence.md)

This shared PostgreSQL/SQL Server runbook is under construction. PostgreSQL setup
and its E2E opt-in variant are documented; live qualification remains pending. Other
records reserve stable destinations and are **pending** their named tasks; do not
execute an unfinished workflow. Use the [shipped command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
for current command details and the linked design owners for support boundaries.

## Procedure Navigation

| Need | Procedure | Documentation task |
| --- | --- | --- |
| PostgreSQL local setup | [postgresql-setup](#postgresql-setup) | T02 — documented; T16 exercise pending |
| SQL Server local setup | [sql-server-setup](#sql-server-setup) | T03 — pending |
| DMS E2E opt-in | [dms-e2e-setup](#dms-e2e-setup) | PostgreSQL documented; SQL Server pending T03 |
| Preserve deployment state | [deployment-state](#deployment-state) | T04 — pending |
| Interrupted initial-enable retry | [initial-enable-retry](#initial-enable-retry) | T04 — pending |
| Established validation and restart preflight | [established-validation](#established-validation) | T04 — pending |
| Missing provenance and source mismatch | [unsupported-provenance](#unsupported-provenance) | T04 — pending |
| Managed shutdown and startup | [managed-lifecycle](#managed-lifecycle) | T05 — pending |
| Intact connector restart and resume | [intact-restart](#intact-restart) | T05 — pending |
| Native recovery and incomplete shutdown | [native-recovery](#native-recovery) | T05 — pending |
| Projection troubleshooting and administration handoff | [projection-handoff](#projection-handoff) | T06 — pending |
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
See [deployment state](#deployment-state) (T04 detail pending).

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
and [managed stop/start](#managed-lifecycle). Those detailed records remain pending
T04/T05; the delivered [SchemaTools managed lifecycle reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#managed-stack-lifecycle)
is the current command handoff. Never substitute raw Connect mutation, state deletion,
or another provisioning run for those controller operations. Ordinary API routing
is not gated by CDC status; see [readiness scope](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope).

<a id="sql-server-setup"></a>

## SQL Server local setup

**Pending T03; exercise T17.** Scope to deliver: Projection RCSI/nested-trigger correction separately from Agent, capture/cleanup, snapshot isolation and history prerequisites.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).

| Record | Value |
| --- | --- |
| Target/generation | Dedicated initial database and CMS-selected target/generation. |
| Authority/offline window | Pending T03: specify authority and applicable fence from the linked owner. |
| Retained inputs | Shared setup inputs plus restricted login/user; wrapper mssql versus CDC sqlserver tokens. Exact paths and substitutions pending T03. |
| Invocation | Reserved IDs: `cdc-sqlserver-settings`, `cdc-sqlserver-bootstrap-local`, `cdc-sqlserver-bootstrap-published`. Commands and fixture substitutions pending T03. |
| JSON/exit status | Pending T03: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T03: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T03: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="dms-e2e-setup"></a>

## DMS E2E opt-in

**PostgreSQL documented in T02; SQL Server substitutions pending T03. Live exercises
pending T16/T17.** These alternatives qualify setup wiring. API-driven message
scenarios remain [DMS-1325](../design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md).
[Local bootstrap/CI owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).
This is the DMS E2E suite, not Instance Management E2E.

| Record | Value |
| --- | --- |
| Target/generation | One fresh CMS-selected target for `E2E_DATABASE_NAME`; default `edfi_datamanagementservice_e2e`. Separate `E2E_SNAPSHOT_DATABASE_NAME` is not the CDC source. |
| Authority/offline window | Same exclusive deployment and initial writer/seed exclusion as PostgreSQL setup; test processes start only after controller admission and DMS startup. |
| Retained inputs | Full E2E DMS/CDC settings, original state root, base/effective environment and overlays, E2E core/extensions, emitted settings/receipts/inventory; `.cdc-diagnostics` on failure. |
| Invocation | PostgreSQL: `cdc-pg-e2e-setup` then optional `cdc-pg-e2e-test`, **or** `cdc-pg-e2e-build` on a separate fresh workspace. SQL Server IDs `cdc-sqlserver-e2e-setup` / `cdc-sqlserver-e2e-build` remain reserved. |
| JSON/exit status | Wrappers print progress, not a CLI JSON envelope. Internal admission requires the same matching `enable` publication result as local setup. On failure, the sanitized `e2e-setup` artifact includes `operation`, `succeeded`, `cancelled`, `cleanup`, `provider`, `failureCodes`. |
| Postcondition | Managed primary receipt retained, separate snapshot prepared with matching schema, CDC admitted before DMS/tests; selected setup-smoke tests pass without source-reset hooks. This does not qualify message scenarios. |
| Rejection/timeout action | Do not launch tests. Retain `.cdc-diagnostics` and original settings/state. `cleanup: "Stopped"` means governed stop completed; `"RetainedForReconciliation"` means stop failed and infrastructure remains for reconciliation; `"NotStarted"` is not proof of shutdown. Use status/initial-retry handoffs above and governed teardown below. |

### Prepare the E2E variant

Use a fresh owned stack/workspace, not the already admitted `edfi_cdc` example. Reuse
[PostgreSQL preparation](#postgresql-setup) with **all** substitutions below, including
the infrastructure/role preparation before the E2E entry point. Neither E2E wrapper
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

<a id="deployment-state"></a>

## Preserve deployment state

**Pending T04; exercise T18/T19.** Scope to deliver: Distinguish prepared inputs from retained state; the entire .bootstrap tree is not disposable.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral).

| Record | Value |
| --- | --- |
| Target/generation | Each target/generation and its shared deployment. |
| Authority/offline window | Pending T04: specify authority and applicable fence from the linked owner. |
| Retained inputs | Original controller roots, provisioning receipts/source history, bindings/journals/incidents, .cdc-deployments, .bootstrap/cdc-runtime settings, broker-size override; include custom roots. Exact paths and substitutions pending T04. |
| Invocation | Reserved IDs: `cdc-state-inventory`. Commands and fixture substitutions pending T04. |
| JSON/exit status | Pending T04: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T04: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T04: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="initial-enable-retry"></a>

## Interrupted initial-enable retry

**Pending T04; exercise T18/T19.** Scope to deliver: Classify intact initial retry separately from established validation; retain initial writer exclusion.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).

| Record | Value |
| --- | --- |
| Target/generation | Original unfinished initial target/generation. |
| Authority/offline window | Pending T04: specify authority and applicable fence from the linked owner. |
| Retained inputs | Original settings, state and initial workflow evidence. Exact paths and substitutions pending T04. |
| Invocation | Reserved IDs: `cdc-enable-retry`. Commands and fixture substitutions pending T04. |
| JSON/exit status | Pending T04: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T04: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T04: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="established-validation"></a>

## Established validation and restart preflight

**Pending T04; exercise T18/T19.** Scope to deliver: Operation-specific validation observations without claiming a fresh initial baseline.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity).

| Record | Value |
| --- | --- |
| Target/generation | Established target/generation. |
| Authority/offline window | Pending T04: specify authority and applicable fence from the linked owner. |
| Retained inputs | Retained settings, binding, journal and source history. Exact paths and substitutions pending T04. |
| Invocation | Reserved IDs: `cdc-validate`. Commands and fixture substitutions pending T04. |
| JSON/exit status | Pending T04: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T04: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T04: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="unsupported-provenance"></a>

## Missing provenance and source mismatch

**Pending T04; exercise T18/T19.** Scope to deliver: Rejection and escalation for missing/corrupt history, terminal incidents and mismatched source; no adoption or replacement recipe.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral).

| Record | Value |
| --- | --- |
| Target/generation | Affected retained target/generation and physical source. |
| Authority/offline window | Pending T04: specify authority and applicable fence from the linked owner. |
| Retained inputs | Available sanitized diagnostics and retained deployment evidence. Exact paths and substitutions pending T04. |
| Invocation | Reserved IDs: `cdc-provenance-rejection`. Commands and fixture substitutions pending T04. |
| JSON/exit status | Pending T04: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T04: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T04: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="managed-lifecycle"></a>

## Managed shutdown and startup

**Pending T05; exercise T18/T19.** Scope to deliver: Wrapper-managed shutdown/startup, complete worker inventory, narrow start-worker building block.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).

| Record | Value |
| --- | --- |
| Target/generation | Every registered binding on the shared worker. |
| Authority/offline window | Pending T05: specify authority and applicable fence from the linked owner. |
| Retained inputs | Deployment inventory, original roots/settings, shutdown checkpoint and broker-size override. Exact paths and substitutions pending T05. |
| Invocation | Reserved IDs: `cdc-managed-stop`, `cdc-managed-start`. Commands and fixture substitutions pending T05. |
| JSON/exit status | Pending T05: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T05: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T05: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="intact-restart"></a>

## Intact connector restart and resume

**Pending T05; exercise T18/T19.** Scope to deliver: Guarded connector operations, distinct from shared stack startup and native recovery.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).

| Record | Value |
| --- | --- |
| Target/generation | Selected intact established generation. |
| Authority/offline window | Pending T05: specify authority and applicable fence from the linked owner. |
| Retained inputs | Original settings, provenance and fresh controller observations. Exact paths and substitutions pending T05. |
| Invocation | Reserved IDs: `cdc-intact-restart`, `cdc-intact-resume`. Commands and fixture substitutions pending T05. |
| JSON/exit status | Pending T05: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T05: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T05: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="native-recovery"></a>

## Native recovery and incomplete shutdown

**Pending T05; exercise T21/T22.** Scope to deliver: Containment result, unavailable evidence, failed incident persistence or failed stop; later health does not certify the unsampled interval.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).

| Record | Value |
| --- | --- |
| Target/generation | Affected generation and worker/task incarnation. |
| Authority/offline window | Pending T05: specify authority and applicable fence from the linked owner. |
| Retained inputs | Retained incidents, shutdown evidence and fresh observations. Exact paths and substitutions pending T05. |
| Invocation | Reserved IDs: `cdc-native-recovery-watch`, `cdc-incomplete-shutdown-status`. Commands and fixture substitutions pending T05. |
| JSON/exit status | Pending T05: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T05: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T05: diagnostic-specific retry, containment or escalation and retained evidence. |

<a id="projection-handoff"></a>

## Projection troubleshooting and administration handoff

**Pending T06; exercise T25/T26.** Scope to deliver: Backlog/oldest work, poison, enqueue failure, lifecycle mismatch, Resetting, rebuild, scrub and history-gated administration.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration).

| Record | Value |
| --- | --- |
| Target/generation | Same target and physical-source fingerprint as projection administration. |
| Authority/offline window | Pending T06: specify authority and applicable fence from the linked owner. |
| Retained inputs | Production downstream-publication-history evidence and projection status. Exact paths and substitutions pending T06. |
| Invocation | Reserved IDs: `cdc-history-internal-only`, `cdc-history-rejected`. Commands and fixture substitutions pending T06. |
| JSON/exit status | Pending T06: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T06: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T06: diagnostic-specific retry, containment or escalation and retained evidence. |

E18 destinations: [status](../document-cache-documentation/operations-runbook.md#status-interpretation),
[enqueue versus processing](../document-cache-documentation/operations-runbook.md#enqueue-vs-processing-availability),
[poison remediation](../document-cache-documentation/operations-runbook.md#persistent-projection-failure-and-poison-remediation),
[lifecycle/Resetting](../document-cache-documentation/operations-runbook.md#lifecycle-mismatch-and-resetting),
[activation](../document-cache-documentation/operations-runbook.md#activation),
[deactivation](../document-cache-documentation/operations-runbook.md#deactivation),
[rebuild](../document-cache-documentation/operations-runbook.md#online-rebuild),
[scrub](../document-cache-documentation/operations-runbook.md#explicit-integrity-scrub),
[cache-ahead recovery](../document-cache-documentation/operations-runbook.md#cache-ahead-recovery),
and [SQL Server prerequisite correction](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction).
The CDC-specific downstream-history decision examples remain pending T06.

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
