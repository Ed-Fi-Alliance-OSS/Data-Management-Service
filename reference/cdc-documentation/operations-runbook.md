# CDC Operations Runbook

This runbook covers the shipped deployment-owned CDC operator surface for PostgreSQL and
SQL Server. Begin with the prerequisites below. Monitoring and incident routing are available below;
provider setup exercises are available below; recovery procedures remain pending in
[delivery and evidence](cdc-inv-evidence.md#pending-delivery). Do not infer a runnable
recovery procedure from a planned section.

Use the [CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md)
for installation, command syntax, confirmations, and exit codes, and the
[configuration catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc) for
settings and defaults. [Design owners](README.md#design-owners) remain authoritative.

<a id="prerequisites"></a>
## Deployment prerequisites

Before initial enablement, the provisioning owner must hold writer admission closed on
a new physical database created for that CDC provisioning. Admission must never have
opened. Existing/admitted databases are ineligible for retrofit; neither an empty table
nor current-schema validation proves provisioning authority. A retry of interrupted
initial enablement retains the same target and generation and requires the original
evidence. The deployment owns external write fencing; DMS status polling supplies no
runtime writer gate. See [initial admission](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).

Supply these deployment facts before issuing a CDC mutation:

- A generated/provisioned schema matching the DMS ApiSchema workspace, CMS credentials
  and target metadata, and matching datastore provider across CMS and DMS. Select an
  explicit projection target on the running projector and in the control plane's
  configuration. A CLI target argument alone does not configure the running DMS.
- Provider setup authority through the selected source connection, a named setup principal,
  and a distinct least-privilege connector database account. PostgreSQL requires logical
  replication and suitable publication/slot/grant authority; SQL Server requires CDC
  setup authority and running capture infrastructure. Setup remains owned by the shipped
  provider workflow; do not independently create slots, publications, capture instances,
  or connector JSON. See [PostgreSQL](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql)
  and [SQL Server](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).
- SQL Server projection prerequisites (`READ_COMMITTED_SNAPSHOT` and server `nested triggers`)
  plus connector snapshot-isolation support. Follow the
  [E18 correction scope](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction);
  changing settings after successful validation on an active target is outside supported
  v1 recovery. A restart is not a general renewed-readiness guarantee.
- A qualified Ed-Fi Connect image selected by digest, including the Ed-Fi transforms.
  Local opt-in takes `DMS_CDC_CONNECT_IMAGE`; obtain its actual qualified digest, never
  substitute a floating tag or unmodified Debezium image. Qualification is owned by the
  [pinned runtime](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#pinned-connector-runtime).
- Network access from the control plane to CMS, the source database, Kafka, Connect REST,
  the metrics bridge, and DMS; and from the worker to the database and broker. The local
  one-shot `cdc-setup` container joins the `dms` network. Container service names and
  advertised broker addresses need not resolve from a host-side CLI. Match all addresses
  to the actual invocation context.
- A running DMS mapping `GET /health/document-cache` with
  `DataManagement:DocumentCache:Status:RequiredRole` and an operator token whose role claim
  satisfies it. Supply the token through protected configuration. Projection-evidence
  workflows need it; adoption/retirement do not depend on that endpoint.
- Named worker secret references, separate Java connector and librdkafka admin security
  settings, declared consumers/groups, record budget, and durable deployment state.
  Resolve required values using the catalog, not another operator's binding record.

The local `-EnableKafkaCdc` bootstrap wrapper orchestrates initial enablement; Kafka UI
alone does not enable it. The published deployment start script does not accept that
local switch. Production-like deployments supply their own admission authority,
configuration, credentials, connectivity, qualified image, and persistent state. See
[local/bootstrap ownership](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).

<a id="deployment-state"></a>
## Durable deployment-state handoff

Record the resolved host state root, container mount, target, and generation in the
deployment record. Setup, status, stop, and retirement must all use the same store.
Follow the [catalog's exact path precedence](../../docs/CONFIGURATION.md#cdc-timeouts-and-durable-state)
and the shipped [resolver](../../eng/docker-compose/env-utility.psm1) and
[Compose mount](../../eng/docker-compose/cdc-setup.yml). The container path `/state` is
not the host mount source. Direct CLI invocations need their own matching root setting.

The shipped filesystem backend supports a single controller, not distributed coordination
or a delivered remote adapter. Restrict the root to the operator service account; the
store rejects group/other-writable Unix directories and non-owner-only files. Keep state
on durable storage outside ephemeral container/bootstrap workspaces. Protect backups of
all binding, incident, and retirement records, and preserve retirement history through
destructive local teardown. Store backup access and retention with the deployment's
operational records.

A backup or redacted connector manifest is not authority to edit binding identity or
recreate offsets. Missing state requires complete-record `cdc adopt` with live validation;
that procedure is pending T05. Binding, incident, and retirement behavior remains owned
by the [binding design](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).

<a id="procedure-format"></a>
## Procedure conventions

Every delivered executable procedure uses an explicit stable anchor and the following
sequence, shared across providers unless a provider-specific step differs:

1. **Scope and effect:** diagnostic or mutating operation, affected components, and
   destructive scope before any commands. CDC `status` is an observation with containment
   effects: proven history loss latches an incident and fences the connector; later polls
   retry an unapplied fence. Loss evidence alone does not prove the stop succeeded.
2. **Starting directory and prerequisites:** name the shell, repository or installed-tool
   working directory, configuration file, network context, credentials by reference,
   provider authority, and any external admission/fencing requirement.
3. **Target/generation selection:** inspect the intended logical target, physical-source
   evidence, generation, and resolved state root. Use synthetic identifiers such as
   tenant `district-lab`, data store `42`, deployment `cdc-lab`, instance `school-year-lab`,
   and generation `1`. For the default tenant, translate record `default` to CLI empty.
4. **Commands:** use packaged `dms-document-cache cdc` verbs or shipped local wrappers.
   Keep shared setup in one place; do not hand-author connector JSON or recovery SQL.
   Refer to secrets by name, such as `CDC_SOURCE_PASSWORD` and `CDC_OPERATOR_TOKEN`.
5. **Expected result:** name the JSON contract/outcome and exit code, linking a captured
   fixture artifact. Help commands have text output and no JSON contract. Do not fabricate
   operational JSON: capture raw output first, sanitize it, and preserve relevant fields.
6. **Verification:** inspect outcome and component evidence separately. Successfully
   produced CDC `notReady` and `unknown` answers exit zero; stop/restart success is not
   end-to-end readiness. Distinguish DMS projection `status` from deployment CDC `status`.
7. **Interruption/retry:** state what may already have changed, preserved evidence,
   same-target/generation retry rules, and conditions requiring containment or escalation.
   A timeout does not prove rollback. Never prescribe offset resets, same-topic
   resnapshots, or unimplemented baseline recovery.
8. **Evidence:** link the design owner and evidence-index row with exact test identities,
   provider/layer, result, artifacts, and any pending downstream verification owner.

CDC polling and initial-readiness scope are owned by
[continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
and [readiness](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope).
Routine observation uses existing status and indexed queue facts. Explicit O(N) scrub
is separately admitted expensive work, not a health probe. Reuse
[E18 projection procedures](../document-cache-documentation/operations-runbook.md) for
queue/poison, enqueue failure, lifecycle, rebuild, and scrub; follow the
[CDC repair handoff](#projection-repair-handoff). Current/historical CDC state disqualifies simple internal-only toggles;
stopping a connector does not clear downstream publication history.

<a id="local-setup"></a>
## Fresh local provider exercises

**Scope/effect:** disposable `dms-local` setup, including database creation, generated
schema provisioning, explicit projector configuration, provider capture/grants, generated
connector registration, and initial admission. Run the providers sequentially: the Compose
project, network, and container names are shared. Never point this exercise at an operator
source. The E2E provisioner **drops and recreates both named E2E databases**. A second setup
is another destructive provisioning run, not a connector restart or enable retry.

**Starting directory/prerequisites:** a dedicated PowerShell 7 session, repository directory
`src/dms/tests/EdFi.DataManagementService.Tests.E2E`; Docker, .NET SDK, package access,
and the [deployment prerequisites](#prerequisites). Use the tracked
[`.env.e2e`](../../eng/docker-compose/.env.e2e) as the prepared environment's starting
point; `./.env.e2e` resolves through the wrapper to that Compose file. It selects the
self-contained identity provider and schema packages. Put any customized credentials in
an access-restricted environment file and pass its absolute path. Supply
`QUALIFIED_CDC_CONNECT_IMAGE` externally with the **qualified** Ed-Fi image reference
ending in its real `@sha256:` digest, and `DMS_CDC_CONNECTOR_PASSWORD` through the local
secret environment. The wrapper checks digest syntax; that alone does not qualify an image.
Do not print either the environment or the rendered Compose configuration into evidence.

Before switching branches/providers, retire and tear down the previous disposable stack
using its own engine, effective environment, and state root; see
[cleanup handoff](#local-cleanup). Keep unrelated deployments out of `dms-local`.
Hold external writers closed throughout initial setup, including while DMS is reachable.
Kafka UI (`-EnableKafkaUI`) supplies no CDC enablement authority.

<a id="local-postgresql"></a>
### PostgreSQL

Use a disposable PostgreSQL server/volume with logical replication available, a setup
administrator with publication/slot/grant authority, and a separate connector login.
The shipped phase provisions that login and the provider workflow generates the capture
artifacts. Do not run provider setup SQL or submit hand-authored connector JSON. Follow
[PostgreSQL's design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql).
From the E2E directory in the PowerShell session described above:

```powershell
$engine = 'postgresql'
$cdcEnv = './.env.e2e'
$env:E2E_DATABASE_NAME = 'cdc_lab_pg'
$env:E2E_SNAPSHOT_DATABASE_NAME = 'cdc_lab_pg_snapshot'
$env:DMS_CDC_BINDING_STATE_PATH = '/var/tmp/cdc-lab-postgresql-state'
$env:DMS_CDC_CONNECT_IMAGE = $env:QUALIFIED_CDC_CONNECT_IMAGE
pwsh ./setup-local-dms.ps1 -EnvironmentFile $cdcEnv -DatabaseEngine postgresql -EnableKafkaCdc
$setupExit = $LASTEXITCODE
```

These Linux host paths must be durable and private to the exercise operator. Do not reuse
an existing database with either synthetic name. Retain the environment exports in this
session for observation and cleanup. Continue with [shared verification](#local-setup-verification).

<a id="local-sqlserver"></a>
### SQL Server

Use the local SQL Server 2025 Compose service, its setup administrator, and a separate
connector login. Confirm capture infrastructure and snapshot-isolation prerequisites;
initialization/validation of RCSI and nested triggers follows the
[E18 provider procedure](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction).
The generated provider workflow owns CDC objects and grants; no independent capture
creation is part of this exercise. Follow [SQL Server's design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).
From the same E2E starting directory, after the other provider's cleanup:

```powershell
$engine = 'mssql'
$cdcEnv = './.env.e2e'
$env:E2E_DATABASE_NAME = 'cdc_lab_mssql'
$env:E2E_SNAPSHOT_DATABASE_NAME = 'cdc_lab_mssql_snapshot'
$env:DMS_CDC_BINDING_STATE_PATH = '/var/tmp/cdc-lab-mssql-state'
$env:DMS_CDC_CONNECT_IMAGE = $env:QUALIFIED_CDC_CONNECT_IMAGE
pwsh ./setup-local-dms.ps1 -EnvironmentFile $cdcEnv -DatabaseEngine mssql -EnableKafkaCdc
$setupExit = $LASTEXITCODE
```

Keep the **resolved engine-overlay environment path** printed by setup. For both providers,
assign that printed absolute path to `$cdcEnv` before subsequent commands; do not infer
its name. The published `start-published-dms.ps1` does not accept `-EnableKafkaCdc`.
[Local `bootstrap-local-dms.ps1`](../../eng/docker-compose/bootstrap-local-dms.ps1) also
supports opt-in with `-IdentityProvider self-contained -EnableKafkaCdc` and an explicit
`-CdcBindingStatePath`, but is a different full provisioning entry point. Do not run it
on top of this E2E exercise. `start-local-dms.ps1 -EnableKafkaCdc` alone starts infrastructure;
it does not configure the projector or admit a database. `build-dms.ps1 E2ETest` has no
`-EnableKafkaCdc` parameter and cannot substitute for this CDC setup.

<a id="local-setup-verification"></a>
### Shared setup verification and state selection

**Expected result:** setup exits `0` after its enable phase succeeds. The one-shot CLI
emits `CdcAdmission` JSON (`admitted`, `notAdmitted`, or `unknown`); successful admission
exits `0`, admission not opened is `12`, invalid/missing/mismatched binding is `10`, and
store access failure is `11`. Preserve the CLI JSON, native exit, and wrapper diagnostics
separately. Wrapper progress text and its internal `Status=Enabled` phase result are not
CDC status contracts. Admission examples are source/help-reviewed here; serialized live
admission evidence is **pending T14/T15**, not invented. Compare later status with the
[existing serialized status fixtures](cdc-inv-evidence.md#t03-monitoring-review).

**Target/generation:** setup derives exactly one non-route-qualified target from the CMS
configure result and puts it into both DMS and the control-plane environment **before**
DMS starts. Inspect the printed target and the durable record; do not assume ID `1`.
Local binding keys are `deploymentKey=local`, `instanceKey=ds<DataStoreId>`. The allocator
uses an existing live generation for retry, or allocates above every live/retired generation.
A fresh store begins at `1`. A record's `tenantKey=default` maps to the empty E18 CLI key;
the discovery helper below performs this translation. Passing literal `default` as
`--tenant-key` selects a tenant the deployment does not have.

**State persistence:** precedence is explicit `-CdcBindingStatePath` where supported,
then ambient `DMS_CDC_BINDING_STATE_PATH`, then the selected environment file, then
`eng/docker-compose/.cdc-state`. Relative explicit paths use the caller's directory;
relative environment/file paths use the Compose directory. The resolver returns an
absolute path and wrappers export it for the `/state` bind mount. E2E setup has **no**
`-CdcBindingStatePath` parameter, so these examples retain an absolute ambient value.
The one-shot tool uses `--cdc-binding-state-path /state`; a direct host CLI would use the
host path. Preserve the entire root, including `bindings`, `incidents`, and `retirements`,
in an access-restricted backup after each operation has settled. Follow
[permissions and missing-state diagnosis](#deployment-state); do not edit records.

**Commands:** remain in the E2E PowerShell session, with `$cdcEnv` now the absolute
**effective** path printed by setup. This uses exported shipped argument builders to run
packaged `dms-document-cache cdc` verbs through the same one-shot service as setup/teardown.
It adds no provider or controller implementation. The dedicated state root must contain
exactly one live binding for this fresh exercise; stop to investigate any other shape.

```powershell
$composeRoot = (Resolve-Path '../../../../eng/docker-compose').Path
Import-Module "$composeRoot/env-utility.psm1" -Force
Import-Module "$composeRoot/cdc-enable.psm1" -Force
Import-Module "$composeRoot/cdc-teardown.psm1" -Force
$envValues = ReadValuesFromEnvFile $cdcEnv
$stateRoot = Resolve-CdcBindingStateRoot -EnvValues $envValues
$bindings = @(Get-CdcRetirableBinding -BindingStateRoot $stateRoot)
if ($bindings.Count -ne 1) { throw 'Expected one live exercise binding; inspect state.' }
$binding = $bindings[0]
$record = Get-Content -LiteralPath $binding.RecordPath -Raw | ConvertFrom-Json
$binding | Select-Object DeploymentKey, TenantKey, DataStoreId, InstanceKey, Generation
$runtime = Get-CdcRuntimeEnvOverride -TenantKey $binding.TenantKey `
    -DataStoreId $binding.DataStoreId -BindingStateRootPath $stateRoot
foreach ($name in $runtime.Keys) {
    [Environment]::SetEnvironmentVariable($name, [string]$runtime[$name])
}
$principal = Get-CdcConnectorPrincipalConfiguration -EnvValues $envValues
$sourceDatabase = $env:E2E_DATABASE_NAME
$verbArgs = @('--data-store-id', $binding.DataStoreId,
    '--deployment-key', $binding.DeploymentKey, '--instance-key', $binding.InstanceKey,
    '--generation', [string]$binding.Generation)
if ($binding.TenantKey) { $verbArgs += @('--tenant-key', $binding.TenantKey) }
$containerEnv = @(Get-CdcConnectorEnvArgument -DatabaseEngine $engine `
    -SourceDatabaseName $sourceDatabase -ConnectorPrincipal $principal)
$containerEnv += @('-e', 'DataManagement__DocumentCache__Cdc__DmsBearerToken')
if ([string]::IsNullOrWhiteSpace($env:CDC_OPERATOR_TOKEN)) {
    throw 'Supply the current DocumentCache operator token through CDC_OPERATOR_TOKEN.'
}
$env:DataManagement__DocumentCache__Cdc__DmsBearerToken = $env:CDC_OPERATOR_TOKEN
$statusArgs = Get-CdcSetupComposeArgument -ComposeProjectName dms-local `
    -EnvironmentFile $cdcEnv -DatabaseEngine $engine -VerbName status `
    -EnvironmentArgument $containerEnv -VerbArgument $verbArgs
& docker @statusArgs
$statusExit = $LASTEXITCODE
```

Supply a current `CDC_OPERATOR_TOKEN` from the configured DocumentCache operator identity
before that status invocation; the token goes into the container by environment-variable
**name**, not command-line value. The E2E wrapper restores its transient runtime environment
on exit; the explicit target export above preserves the same configuration for subsequent
one-shot runs. Verify it agrees with the running DMS target, rather than treating this
export as evidence that the running projector was configured.

The control plane joins `dms`: provider hosts are `dms-postgresql:5432` or
`dms-mssql:1433`, broker `dms-kafka1:9092`, Connect `kafka-postgresql-source:8083` on
**both** providers, and DMS `ed-fi-api:8080`. A host CLI cannot assume these names resolve;
a localhost broker port alone does not fix the broker's advertised container address.
The worker resolves the generated `${env:CDC_DATABASE_PASSWORD}` reference. Keep the
connector password consistent with the local secret environment and retain generated
redacted artifacts for review. See the [credential catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc)
and [lag bridge procedure](#inspect-lag) for address overrides and unavailable metrics.

**Verification/retry:** inspect `readiness`, `primaryBlockingCategory`, and each target's
component evidence; status `notReady`/`unknown` can exit `0`. Follow
[status containment semantics](#observe-cdc) and [incident routing](#incident-routing).
If initial enable was interrupted, keep admission closed and preserve the same source,
target, generation, state, and original provisioning evidence. Do not rerun E2E setup
as recovery: it drops the database. The shipped `enable-kafka-cdc.ps1` phase supports
`-DatabaseCreatedByThisRun $false -ResumeInterruptedEnable` with the same explicit
`-ComposeProjectName`, `-EnvironmentFile`, `-TenantKey`, `-DataStoreId`, `-DatabaseEngine`,
and `-SourceDatabaseName`. Use it only when an existing live record belongs to an enable
that **never finished and never admitted writes**. The full bootstrap wrapper calls that
assertion `-ResumeInterruptedCdcEnable`. A live record or currently empty database does
not prove it. If state is missing or admission history is uncertain, stop and use the
[continuity route](#route-continuity-incident); adoption instructions belong to T05.

<a id="local-api-smoke"></a>
### API upsert/delete observation handoff — unmet E19-06 dependency

After admitted setup and acceptable current evidence, use the
[E19-06 API/consumer harness](../design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md)
against the **same provisioned database**, target, generated topic, and generation. It
must capture API upsert/delete responses and the resulting public consumer records using
its own consumer helpers. Set test-process `AppSettings__DataStoreDatabaseName` to the
chosen `E2E_DATABASE_NAME`; on Linux clear unsupported `NODE_OPTIONS` for `dotnet test`.

That relational API-to-Kafka harness and its exact test filter are absent in this checkout.
The setup wrapper exists, but no runnable smoke command or successful API/broker result
can be supplied here. **The API smoke exercise remains unmet**, assigned to E19-06;
T14/T15 must record the upstream exact test identities, nonzero counts, and provider
results when available. Do not substitute ordinary resource tests, handcrafted provider
writes, message fixtures, or a new shell consumer. Retain setup evidence and proceed only
to the bounded component stop/restart exercise if its own prerequisites are satisfied.

<a id="local-stop-restart"></a>
## Planned connector stop and guarded restart

**Scope/effect:** fence only the selected connector and restart its worker without deleting
artifacts. Preserve connector configuration, committed offsets, provider capture artifacts,
public/progress/history topics, shared worker state, and all deployment records. These
steps do not toggle projection or close API writer admission. Budget the planned downtime
against provider retention; stopping the worker alone does not fence its persisted running
target. Follow [continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity).

**Starting directory/prerequisites:** same E2E PowerShell session and effective environment,
state, `$binding`, `$record`, `$principal`, and `$statusArgs` from shared verification.
Connect must still be reachable, the operator token current, and this must be the only
controller. Set `$connectHostUri` to the worker's published host URL (default
`http://localhost:8083`, or the actual `CONNECT_SOURCE_PORT`). The following GET is a
bounded diagnostic; only the shipped CLI mutates connector target state.

```powershell
$stopArgs = Get-CdcStopArgument -ComposeProjectName dms-local `
    -EnvironmentFile $cdcEnv -DatabaseEngine $engine `
    -BindingRecord $binding -ConnectorPrincipal $principal
& docker @stopArgs
$stopExit = $LASTEXITCODE
$connectHostUri = 'http://localhost:8083'
$connectorPath = [Uri]::EscapeDataString($record.connectorName)
$stopped = Invoke-RestMethod "$connectHostUri/connectors/$connectorPath/status" -TimeoutSec 30
if ($stopExit -ne 0 -or $stopped.connector.state -ne 'STOPPED') {
    throw 'Stop not verified; retain evidence and resolve containment before restarting the worker.'
}
```

**Expected result/verification:** `cdc stop` emits `CdcStatus` JSON. Exit `0` means the
stop applied or the connector was absent; absence alone cannot pass this planned registered
connector exercise. The adapter issues the persisted stop and waits for `STOPPED` read-back;
the additional GET records it before the planned worker restart. The status can still
be `notReady`/`unknown`; [T12 serialized CLI fixtures](evidence/t12/cdc-contract-captures.json)
show successful stop/restart without readiness, with mocked dispatch only. Exit `10`
means refusal, `11` state-store failure; retain diagnostics and inspect current state.
A timeout or lost response does not prove that no stop was applied.

Only after that verification, restart the existing worker container, then confirm that
its persisted stop survived **before** issuing guarded restart:

```powershell
docker restart kafka-postgresql-source
$workerExit = $LASTEXITCODE
$stillStopped = Invoke-RestMethod "$connectHostUri/connectors/$connectorPath/status" -TimeoutSec 30
if ($workerExit -ne 0 -or $stillStopped.connector.state -ne 'STOPPED') {
    throw 'Worker restart has no verified fence; investigate containment.'
}
$restartArgs = Get-CdcRestartArgument -ComposeProjectName dms-local `
    -EnvironmentFile $cdcEnv -DatabaseEngine $engine -TenantKey $binding.TenantKey `
    -DataStoreId $binding.DataStoreId -SourceDatabaseName $sourceDatabase `
    -Generation $binding.Generation -ConnectorPrincipal $principal
& docker @restartArgs
$restartExit = $LASTEXITCODE
& docker @statusArgs
$statusExit = $LASTEXITCODE
```

If Connect is still starting, retain the failed GET and repeat that bounded observation
when reachable; do not advance to restart on a missing response. Guarded `cdc restart`
requires affirmative continuity and its artifact prerequisites. A successful applied
restart exits `0` even if subsequent `CdcStatus` is `notReady`/`unknown`; inspect the
separate status and component evidence. A refused restart (`10`) leaves the fence in
place; diagnose its reason before retrying the same target/generation. It never clears
terminal history loss. Do not reset offsets, recreate capture, or directly resume through
Connect to make the check pass. Live fence persistence/read-back remains T14/T15 evidence.

**Full local stop alternative and interruption:** from this same directory,
`pwsh "$composeRoot/start-local-dms.ps1" -d -EnableKafkaCdc -EnableConfig -DatabaseEngine $engine -EnvironmentFile $cdcEnv -CdcBindingStatePath $stateRoot`
fences discovered bindings while Connect is reachable, then stops the stack without
`-v`. Verify each registered connector as above before planned shutdown. The wrapper can
warn `CDC stop: dms-document-cache cdc stop failed ... Its connector may resume publishing`
and still proceed. If that warning occurred, **do not start the worker again without
containment verification**: recover reachability under deployment-controlled isolation
and prove the fence, routing unavailable or lost evidence through
[continuity containment](#route-continuity-incident). A post-start status poll alone may
be too late because the worker restores persisted running connectors automatically.
Preserve warnings, stop result, record, and last read-back. Restarting existing containers
retains their runtime settings; reprovisioning with `setup-local-dms.ps1` does not.

<a id="local-cleanup"></a>
## Disposable cleanup and retirement-history handoff

**Scope/effect:** destructive, unlike planned stop. From the same E2E PowerShell session,
use the setup-printed teardown command with its **same effective environment and engine**,
while retaining `DMS_CDC_BINDING_STATE_PATH=$stateRoot`. Before issuing it, back up binding,
incident, and retirement records and confirm the stack/databases are the disposable target.

```powershell
pwsh ./teardown-local-dms.ps1 -EnvironmentFile $cdcEnv -DatabaseEngine $engine
$teardownExit = $LASTEXITCODE
```

The wrapper delegates `-d -v` to the engine-aware local lifecycle path. It retires bindings
before deleting volumes, names each record's generation, and retains retirement history
in the host state root. Inspect per-binding `CdcCleanupProof` outcomes and final exit;
a failed retirement retains its binding and normally aborts volume deletion, preserving
resources for retry. An absent store is not proof of an absent deployment. Do not discard
or bypass unreadable state or opt into abandonment as routine cleanup. Keep the root and
protected post-retirement backup after successful teardown. Close the dedicated PowerShell
session after collecting evidence to release its token/runtime overrides. Dedicated destructive scope,
original-source selection, partial cleanup and same-operation retry are pending T06/T17;
[the binding owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
defines the limits of cleanup proof, including no platform-purge claim.

[Authoring review and exact behavior-test results](cdc-inv-evidence.md#t02-setup-review)
support these procedures. No live provider setup, API smoke, stop/restart, or cleanup
success is claimed until the T14/T15 replays supply it.

<a id="monitoring"></a>
## Monitoring scope

Use projection status to investigate durable work and the running projector, and CDC
status to investigate one deployment-owned binding. Neither replaces ordinary API health.
After initial writer admission, component observations are eventually consistent;
`ready` does not promise that every document committed during the poll is already public.
Only the initial new-database sequence establishes first-write admission evidence. See
[readiness scope](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope)
and [status ownership](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness).

Projection downtime can leave successfully enqueued canonical writes waiting for workers.
Failure to persist enqueue work rolls back the complete canonical transaction and is an
API write-availability incident. Projection status never gates ordinary API routing.
Use indexed queue presence/oldest-work observations for routine monitoring; do not poll
with exact backlog counts, source/cache scans, or integrity scrub. Suspected restore or
unsupported direct mutation requires the separately admitted
[E18 scrub procedure](../document-cache-documentation/operations-runbook.md#explicit-integrity-scrub)
before relying on queue-empty status. See
[transactional enqueue](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#transactional-enqueue).

<a id="observe-projection"></a>
## Observe projection health and queue progress

**Scope/effect:** diagnostic inspection of one projection target. The CLI `status` and
DMS `GET /health/document-cache` use the projection contract; the CLI's local execution
view is not evidence that the deployed DMS projector is running. CDC's correlation reader
uses the running DMS endpoint. Use the endpoint for that process's execution/diagnostic
view and the CLI for its selected target. See
[E18 status interpretation](../document-cache-documentation/operations-runbook.md#status-interpretation).

**Starting directory/prerequisites:** repository root, Bash, restored .NET SDK, configured
CMS/provider access. Set `CDC_SETTINGS` to the protected deployment appsettings file and
`CDC_PROVIDER` to `postgresql` or `sqlserver`. The file/environment must resolve the
intended target; use named secret references `CDC_SOURCE_PASSWORD` and
`CDC_OPERATOR_TOKEN` through the [configuration handoff](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc).
For the DMS endpoint, use the role/token and address from [prerequisites](#prerequisites).
Keep stdout/stderr in an owner-only incident workspace; do not publish raw captures.

**Target selection:** this synthetic example selects tenant `district-lab`, data store
`42`. Check the returned `targetKey`, `provider`, `physicalSourceFingerprint`,
`processObservedAt`, and `durableObservedAt`. Projection `targetGeneration` is the
process target generation, not the CDC binding's generation. Translate a default CDC
record tenant `default` to an omitted/empty CLI tenant argument.

```bash
umask 077
observation_dir=$(mktemp -d)
if dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  status --tenant-key district-lab --data-store-id 42 \
  --settings "$CDC_SETTINGS" --environment Production --datastore "$CDC_PROVIDER" \
  --status-observation-timeout-seconds 5 --status-timeout-seconds 30 --json \
  > "$observation_dir/projection.json" 2> "$observation_dir/projection.stderr.txt"; then
  projection_exit=0
else
  projection_exit=$?
fi
```

**Expected result/verification:** a produced projection status exits `0`; inspect its
`targets[]` component `status`/`reason` fields, not a CDC `readiness` field. The
[generated help](evidence/t03/status-help.txt) verifies these options; projection fields
are owned by the [shared contract](../../src/dms/core/EdFi.DataManagementService.Core/DocumentCache/DocumentCacheStatusContracts.cs)
and [E18 evidence](../document-cache-documentation/cdc-inv-evidence.md). No live projection
output is claimed here; deployed endpoint capture remains T14/T15 evidence.
Compare queue presence and oldest-work age across at most three scheduled observations
at your deployment's monitoring interval. A null/unavailable age is not zero work, and
an old successful observation is not current success. Route the current reasons with the
[incident table](#incident-routing).

**Interruption/retry:** an interrupted observation proves nothing about current progress.
Keep its exit/stderr, correct access or timeouts, and repeat the same target observation.
Do not change lifecycle or clear durable work to improve a health result.
[Authoring evidence](cdc-inv-evidence.md#t03-monitoring-review).

<a id="observe-cdc"></a>
## Observe CDC readiness and containment evidence

**Scope/effect:** operational observation with mutations on proved history loss. Status
validates existing artifacts without provisioning topics, grants, or provider objects,
but attempts to durably latch loss and fence the named connector. Poll using a controller
account authorized for those effects and the single-controller state store. A lost
binding does not stop unrelated bindings or alter DMS API routing. See
[continuity owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity).

**Starting directory/prerequisites:** repository root, Bash, the preceding protected
settings and observation workspace, and all [deployment prerequisites](#prerequisites).
Set `CDC_STATE_ROOT` to the exact resolved durable host root from setup; a host-side CLI
must resolve the broker/worker/DMS addresses in its own network context. Configure bounded
requests using the [timeout catalog](../../docs/CONFIGURATION.md#cdc-timeouts-and-durable-state).
Do not overlap controller invocations.

**Target/generation selection:** inspect the deployment record and select its exact
logical target, provider, deployment/instance keys, generation, source binding, and state
root. The example uses synthetic `cdc-lab` / `school-year-lab`, generation `1`.
These flags must identify the existing deployment, not invent a new binding for a poll.

```bash
if dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  cdc status --tenant-key district-lab --data-store-id 42 \
  --deployment-key cdc-lab --instance-key school-year-lab --generation 1 \
  --cdc-binding-state-path "$CDC_STATE_ROOT" \
  --settings "$CDC_SETTINGS" --environment Production --datastore "$CDC_PROVIDER" --json \
  > "$observation_dir/cdc.json" 2> "$observation_dir/cdc.stderr.txt"; then
  cdc_exit=0
else
  cdc_exit=$?
fi
```

**Expected result:** `--json` emits one shared `CdcStatus` document, without an outer
command-result wrapper or `outcome` property. Its outcome is `readiness`; a successfully
produced `ready`, `notReady`, or `unknown` answer exits `0`. A nonzero exit or missing
contract requires inspecting stderr and the [CLI exit-code reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#exit-codes).
Do not turn process exit zero into a readiness or containment alarm-clear condition.

Captured controller fixtures, passed through the CLI JSON executor, illustrate:

| Captured answer | Outcome / exit | Evidence to inspect |
| --- | --- | --- |
| [Ready](evidence/t03/ready.json) | `ready` / `0` | Target identity, every component, observation times, `sourceHistory.continuity=healthy`. |
| [Missing binding](evidence/t03/binding-missing.json) | `notReady` / `0` | `primaryBlockingCategory=bindingMissing`; no governed artifacts observed. |
| [Unavailable lag](evidence/t03/lag-unavailable.json) | `unknown` / `0` | `lag.state=unknown`, category `statusObservationUnavailable`, diagnostic `connectorLagUnavailable`. |
| [Lost, fence refused](evidence/t03/lost-fence-refused.json) | `notReady` / `0` | `sourceHistory.continuity=lost`, `incidentLatched=true`, diagnostic `statusIncidentFenceNotApplied`. |
| [Lost, fence accepted on retry](evidence/t03/lost-fence-retried.json) | `notReady` / `0` | Latch retained; fence-failure diagnostic absent. Earlier runtime observation still appears satisfied. |
| [Loss latch unavailable](evidence/t03/lost-latch-unavailable.json) | `notReady` / `0` | `incidentLatched=false`, `binding.state=unknown`, `statusSourceHistoryLatchNotDurable` and `localStateUnavailable`; fence still attempted. |

Fixtures use synthetic default tenant, data store `1`, deployment `dms`, instance
`binding`, generation `7`; they are not output from the example deployment. Full contract
fields and timestamps are preserved. [Capture provenance and limits](cdc-inv-evidence.md#t03-monitoring-review).

**Verification:** first match `targets[].targetIdentity`, including the string
`dataStoreId` and binding `generation`. Inspect aggregate and target `readiness` and
`primaryBlockingCategory`, then every component's `state`, `category`, and `observedAt`
plus `diagnostics[]`. A primary blocker is only one blocker. CDC includes summarized
`binding`, `projection`, `providerSetup`, `providerBarrier`, `sourceHistory`, `kafkaPolicy`,
`connectOffsetStore`, `connectorConfig`, `connectorRuntime`, and `lag` components. It does
not embed the projection queue contract, raw Connect task arrays, or raw lag observations.

For a lost history, inspect three things separately: the loss classification; whether the
incident became durable; and whether the worker accepted and subsequently retained the
stop. Status collects runtime before fencing and does not refresh that component after a
successful stop. Absence of `statusIncidentFenceNotApplied` is not fresh stopped-state
read-back. On the next bounded poll, inspect the new runtime evidence; if needed, have the
Connect operator read the governed connector's persisted `STOPPED` target state and task
states through the authenticated management interface, preserving only sanitized state
fields. Do not restart a worker while containment is unverified. Detailed stop/restart
and live read-back exercises remain T02/T14/T15.

**Interruption/retry:** a timeout can occur after latching or stopping; it is not rollback.
Preserve the same binding/generation/store and retry the observation after correcting
reachability. Later polls retry an unapplied fence while leaving a durable incident
latched; even a failed latch does not cancel the stop obligation. Use
[continuity incident routing](#route-continuity-incident) for unresolved loss and
[binding routing](#route-binding-incident) for missing/mismatched state. No poll clears a
terminal loss. [Controller evidence](cdc-inv-evidence.md#t03-monitoring-review).

<a id="incident-routing"></a>
## Symptom to evidence to action

Projection paths below are relative to the selected projection `targets[]`; CDC paths
are relative to the selected CDC `targets[]`. Keep the full bounded diagnostics alongside
`primaryBlockingCategory` so simultaneous failures remain visible.

| Symptom | Shipped evidence | Procedure / next action | Design owner |
| --- | --- | --- | --- |
| Backlog or oldest work growing | Projection `caughtUp.reason=queueNotEmpty`, `queueSummary.presence=notEmpty`, `queueSummary.oldestWorkFirstEnqueuedAt`, `queueSummary.oldestWorkAgeSeconds`; CDC `projection.category=projectionBacklog` | [Observe projection](#observe-projection); compare bounded observations and worker progress. | [Projection health](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness) |
| Poison processing / worker backoff | Projection `documentDiagnostics`, `poisonTraversalDiagnostics`, `targetDiagnostics`, `executionState.status`; `operationalHealth.reason=targetBackoff` | [E18 poison remediation](../document-cache-documentation/operations-runbook.md#persistent-projection-failure-and-poison-remediation); retain document IDs in incident evidence, not metric labels. | [Bounded execution](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#bounded-in-process-execution-policy) |
| Canonical write enqueue failure | Projection `enqueueFailures.recentEvents`, `byCategory`, `evictedCount`; `inventory.enqueueTrigger`, `inventory.work`; `enqueueTriggerUnavailable` or `inventoryInvalid` | [E18 enqueue availability](../document-cache-documentation/operations-runbook.md#enqueue-vs-processing-availability); treat as complete-transaction rollback, investigate provider write boundary. | [Transactional enqueue](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#transactional-enqueue) |
| Provider prerequisites invalid | CDC `providerSetup.category=providerSetupInvalid`; projection `providerPrerequisites.sqlServerReadCommittedSnapshot`, `sqlServerNestedTriggers`, reasons `sqlServerPrerequisiteFailed` / `unsupportedPrerequisiteIncident` | [Prerequisite handoff](#prerequisites), [E18 supported correction scope](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction); escalate active-target unsupported incidents. | [Provider setup](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup) |
| Heartbeat / source barrier not advancing | CDC `providerBarrier.category=providerBarrierNotReached` or `statusObservationUnavailable`; inspect its timestamp and diagnostics | [Observe CDC](#observe-cdc); check provider setup and runtime evidence together. Do not use a progress-topic message or lag alone as barrier proof. | [Provider barrier](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#provider-source-position-barrier) |
| Snapshot incomplete / task not running | CDC `connectorRuntime.category=snapshotIncomplete` / `connectorNotRunning` / `statusObservationUnavailable` | [Observe CDC](#observe-cdc), then [continuity routing](#route-continuity-incident) before any start; inspect sanitized worker task state. | [Readiness](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness) |
| Lag excessive or unavailable | CDC `lag.category=lagExceeded` / `statusObservationUnavailable`, `connectorLagExceeded` / `connectorLagUnavailable` / `connectorLagMetricsAbsent` / `connectorLagMalformedResponse` diagnostics | [Inspect lag](#inspect-lag); missing quantiles or bridge failure is unknown evidence. | [Telemetry](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations) |
| Topic/ACL, connector configuration, or shared-offset mismatch | CDC `kafkaPolicy.category=kafkaPolicyInvalid`, `connectorConfig.category=connectorConfigInvalid`, `connectOffsetStore.category=connectOffsetStoreInvalid` | [Configuration triage](#check-cdc-configuration); preserve expected/observed diagnostics; status does not repair grants or topics. | [Topology/offset store](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#kafka-connect-offset-store), [isolation](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations) |
| Physical source or binding mismatch | CDC `sourceMismatch` / `bindingMismatch` component categories and diagnostics; projection `physicalSourceFingerprint` | [Binding routing](#route-binding-incident); stop recovery decisions until target and physical source are reconciled. | [Binding identity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding) |
| Missing deployment state | CDC `binding.category=bindingMissing`; unavailable store uses `statusObservationUnavailable` with `localStateUnavailable` diagnostics | [Binding routing](#route-binding-incident); verify exact root/mount before considering adoption. | [Binding storage](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding) |
| Continuity unproved | CDC `sourceHistory.continuity=unknown`, category `providerHistoryUnknown` or `statusObservationUnavailable` | [Continuity routing](#route-continuity-incident); restore observations and recheck; do not start/resume on unknown. | [Continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity) |
| Terminal history loss / incomplete containment | CDC `sourceHistory.continuity=lost`, `incidentLatched`; `statusIncidentFenceNotApplied` or `statusSourceHistoryLatchNotDurable` | [Continuity routing](#route-continuity-incident); verify durability and fence independently, preserve terminal generation. | [Continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity) |
| Lifecycle or cache-ahead incident | Projection `lifecycle.state`, `cacheAhead.recoveryRequired`; CDC `projection.category=projectionNonOperational` | [E18 lifecycle triage](../document-cache-documentation/operations-runbook.md#lifecycle-mismatch-and-resetting), [CDC repair handoff](#projection-repair-handoff); stopping CDC does not authorize an internal-only reset. | [Lifecycle](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#durable-work-and-lifecycle) |

<a id="check-cdc-configuration"></a>
### Configuration and provider triage

Use the selected component's diagnostic `code`, `component`, `artifactKind`,
`artifactName`, `expected`, and `observed` with the [configuration catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc)
and the deployment record. Check network context, qualified image, rendered connector
settings, declared consumers, and shared-offset policy with the responsible platform
owner. Do not edit binding identity to match drift or delete shared offsets. Retry
[CDC observation](#observe-cdc) after an authorized correction; a healthy component does
not replace continuity evidence. Provider setup exercises are pending T02, security
procedures T07, and retention/capacity guidance T08 in the [delivery index](cdc-inv-evidence.md#pending-delivery).

<a id="route-binding-incident"></a>
### Binding and physical-source incident routing

First verify [state-root and mount precedence](#deployment-state) and the exact selected
logical target/generation. A missing record is different from an unreadable store;
inspect diagnostics and permissions without recreating state. Compare the deployment's
binding with current source evidence, retaining opaque fingerprints in the incident
record. A backup alone is not authority to restore a controller record or edit its source.
Complete-record adoption and physical-source replacement procedures are pending T05/T13;
use the [binding owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
and [pending handoff](cdc-inv-evidence.md#pending-delivery). Until resolved, no readiness
or safe restart is established. Correct an invocation/root error and repeat
[the same-generation observation](#observe-cdc).

<a id="route-continuity-incident"></a>
### Continuity incident routing

For `unknown`, preserve evidence, correct the unavailable provider/Connect observation,
and repeat [bounded CDC status](#observe-cdc). Unknown does not itself latch a proved loss;
a start/resume still requires affirmative continuity. For `lost`, verify durable latching
and subsequent stopped-state read-back independently, addressing `localStateUnavailable`
and `statusIncidentFenceNotApplied` with the state-store/Connect operators. Re-poll the
same generation to retry containment. Do not restart it to see whether the error clears.

Loss stays terminal even if provider artifacts reappear or lag becomes small. No v1
same-topic resnapshot, offset reset, or baseline-replacing recovery is supported. Detailed
continuity and destructive-retirement procedures remain T05/T06 work; this section routes
the incident, not a recovery authorization. Follow the
[continuity owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
and [deferred repair boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).

<a id="projection-repair-handoff"></a>
## Projection and CDC repair handoff

Start with [projection observation](#observe-projection) for the selected tenant/data store
and current physical-source fingerprint; correlate the CDC binding generation through
[CDC observation](#observe-cdc). Projection process generation is not binding generation.
Keep projection operations in the E18 runbook:

| Projection concern | Existing procedure | CDC boundary |
| --- | --- | --- |
| Queue growth or poison work | [Processing/poison remediation](../document-cache-documentation/operations-runbook.md#persistent-projection-failure-and-poison-remediation) | Projector downtime permits canonical writes to queue; ordinary API routing does not wait for CDC. |
| Enqueue failure | [Enqueue versus processing](../document-cache-documentation/operations-runbook.md#enqueue-vs-processing-availability) | Failed enqueue rolls back the complete canonical transaction; connector restart cannot fix it. |
| Lifecycle/configuration mismatch or interrupted `Resetting` | [Lifecycle and retry](../document-cache-documentation/operations-runbook.md#lifecycle-mismatch-and-resetting) | Reissue only the known interrupted operation; removing runtime configuration grants no clearing authority. |
| Cache rebuild with a clear latch | [Bounded online rebuild](../document-cache-documentation/operations-runbook.md#online-rebuild) | Lifecycle must be `Tracking` or `Rebuilding`; rebuilding does not restamp changed public bytes or certify a CDC baseline. |
| Suspected restore/direct mutation or missing work | [Explicit O(N) scrub](../document-cache-documentation/operations-runbook.md#explicit-integrity-scrub) | Admission requires clear-latch `Tracking`; scrub never clears a set latch, and queue-empty status alone cannot rule out restore damage. |
| Activation/deactivation | [Activation](../document-cache-documentation/operations-runbook.md#activation), [deactivation](../document-cache-documentation/operations-runbook.md#deactivation), [packaged history gate](../document-cache-documentation/operations-runbook.md#packaged-downstream-history) | Initial new-database enablement uses its owning setup workflow. Offline toggles remain rejected in packaged v1. |
| SQL Server RCSI/`nested triggers` | [Provider correction scope](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction) | Target initialization and activation validate both. Initialization correction/restart is `Disabled`-only; activation preflight can retry after correction. Post-validation changes on active targets are unsupported, with no renewed-readiness promise. |

These procedures retain the [projection lifecycle owner](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#durable-work-and-lifecycle)
and [repair owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).
Rebuild/scrub success never clears published-state risk. Reuse the
[E18 evidence matrix](../document-cache-documentation/cdc-inv-evidence.md#matrix) for provider
prerequisites, scan-free restart, reset/rebuild interruption, and scrub guards; downstream
assertions and live exercises are tracked in the [T04 evidence handoff](cdc-inv-evidence.md#t04-projection-handoff-review).

<a id="cache-ahead-containment"></a>
### Cache-ahead and packaged offline-command rejection

A set `cacheAhead.recoveryRequired` latch routes here from
[E18 cache-ahead diagnosis](../document-cache-documentation/operations-runbook.md#cache-ahead-recovery).
Preserve bounded projection/CDC status, source fingerprint, generation, cache/work/latch
evidence, and any command rejection before choosing containment. The packaged CDC-backed
provider proves `active` or `historical` from matching binding/retirement records; all other
production evidence is `unknown`. It never supplies the same-target/source `internalOnly`
proof required by `activate-offline`, `deactivate-offline`, or `recover-cache-ahead`.

Once earlier guards pass, their command result is `rejectedNoMutation`, classification
`downstreamHistoryPresentOrUnknown`, `mutated=false`, exit `10`. Preserve any earlier
source, lifecycle, or prerequisite rejection instead of assuming every failure is history.
[History/guard evidence](cdc-inv-evidence.md#t04-projection-handoff-review) distinguishes
real-provider rejection from test-only `internalOnly` acceptance. Current or historical
binding/consumer state disqualifies the simple read-acceleration toggle. Stopping a
connector, removing a runtime target, or retiring a binding does not erase that history.

Use [continuity containment routing](#route-continuity-incident) and the
[cache-ahead recovery owner](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-ahead-invariant-recovery)
for possibly published higher versions. Connector fencing and durable incident evidence
must be verified separately; stopping publication does not unlock an E18 reset. Restamp,
rebuild, and scrub cannot turn cache-ahead into a same-topic lower-version correction.
A replacement namespace/baseline workflow is deferred, not an available recovery command.

<a id="representation-correction-handoff"></a>
### Representation correction versus disclosure

Use the [E18 restamp boundary](../document-cache-documentation/operations-runbook.md#restamp-scope-boundary)
and its independently owned utility guidance. This checkout provides the design/work-package
handoff only; it has no executable restamp procedure or restamp evidence to reuse yet.

| Incident decision | Handoff and admission boundary |
| --- | --- |
| Compatible representation bytes change; prior records require no purge | [Offline byte-changing correction owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction). Corrected public bytes need fresh canonical `ContentVersion` values; cache rebuild at the old version is insufficient. Projection/publication mode requires clear-latch `Tracking`. Same binding/topic eligibility is conditional on compatibility and no purge requirement. |
| Canonical-only representation correction | Same owner; explicit clear-latch `Disabled` mode changes API validators/Change Query visibility, records no projection work, and makes no Kafka publication claim. It does not unlock packaged offline activation later. |
| Cache-ahead latch or possibly published higher version | [Cache-ahead containment](#cache-ahead-containment); neither correction mode admits a set latch. Do not reinterpret this as ordinary materializer correction. |
| Sensitive records must be purged | [Disclosure correction owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction); same-topic correction is ineligible. The dedicated containment procedure is pending T07 in [delivery](cdc-inv-evidence.md#pending-delivery). Preserve the incident and route to connector fencing, operator-owned access revocation, guarded retirement, and platform purge evidence under that owner. Higher versions, tombstones, or compaction alone do not prove purge. |

All restamp work requires externally stopping affected readers, DMS replicas, canonical
writers, projector/direct-fill writers, bulk/administrative paths, and external writers.
The utility's confirmation does not implement or certify that fence. `Resetting`,
`Rebuilding`, or any set latch is ineligible. Follow the owning utility's mode/manifest/retry
guards when delivered; do not replace it with manual stamp SQL. After projection/publication
correction, higher-version public records and projection/connector status are eventual
observations, not a replacement exact CDC baseline or an API admission gate. Disclosure
re-enablement remains deferred by its owner.

<a id="inspect-lag"></a>
## Inspect connector lag evidence

**Scope/effect and invocation:** use [CDC observation](#observe-cdc), with its repository
root, selected binding/generation, protected settings, bounded requests, output/exit
handling, and containment effects. Inspect `lag` and its diagnostics from that one
observation; this is not a separate CLI verb. Repeat at most three scheduled observations
after a metrics correction, without overlapping controller calls. Interrupted evidence
is unavailable; keep the same generation and never replace it with an older success.

The shipped [lag reader](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control/CdcConnectorLagReader.cs)
reads the worker's Jolokia bridge. Match `ConnectMetricsBaseUri` to the invocation's
network context; blank derives the Connect scheme/host with port `8778` and root `/`.
The qualified image enables the bridge with `ENABLE_JOLOKIA=true`. The reader appends
`jolokia/read/...` to the base URI and bounds requests with `Timeouts.ConnectRequest`.
See the [catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc) for values,
validation, and `LagThreshold`; reachability of Connect REST alone does not verify metrics.

Its streaming MBean pattern is
`debezium.<provider>:type=connector-metrics,context=streaming,server=<topic.prefix>,*`,
with provider domain `postgres` or `sql_server`. The wildcard permits SQL Server's task
property; multiple matching MBeans are ambiguous evidence. Required attributes are
`MilliSecondsBehindSource`, `MilliSecondsBehindSourceP50`, `MilliSecondsBehindSourceP95`,
and `MilliSecondsBehindSourceP99`, all milliseconds. Do not infer quantiles from the
progress topic or copy current lag into missing fields.

**Expected evidence:** the internal `CdcConnectorLagObservation` carries `lagState`,
`currentLagMilliseconds`, `thresholdMilliseconds`, and the three
`p50LagMilliseconds` / `p95LagMilliseconds` / `p99LagMilliseconds` fields.
[Complete fixture](evidence/t03/lag-observation-Succeeded.json) and
[unavailable fixture](evidence/t03/lag-observation-Unavailable.json) show the actual
serialized contracts; they are internal observation captures, not standalone CLI stdout.
CDC status summarizes them into its `lag` component and diagnostics.

| Reader result | Status diagnostic / interpretation |
| --- | --- |
| Transport error, timeout, HTTP failure, or non-success Jolokia status | `connectorLagUnavailable`; no usable reading. |
| HTTP success with Jolokia status `404`, absent/ambiguous MBeans, missing/unusable attributes (including negative sentinels) | `connectorLagMetricsAbsent`; no usable reading. |
| Malformed JSON/response shape | `connectorLagMalformedResponse`; no usable reading. |
| A supplied reading with a negative value or out-of-order quantiles at the observation mapper | `connectorLagUnusableReading` / `connectorLagQuantilesOutOfOrder`; no usable reading. |
| Complete reading above threshold | `connectorLagExceeded`, category `lagExceeded`; measured lag, still not ready. |

Unknown readings clear all five numeric fields, including the threshold, rather than
reporting zeros. A complete reading within threshold only satisfies the lag component.
The bridge response body and stack traces are not copied into diagnostics; the reader's
failure summary uses transport/status facts. [Lag fixture evidence](cdc-inv-evidence.md#t03-monitoring-review).

<a id="telemetry"></a>
## Shipped telemetry for bounded observation

Subscribe to the `EdFi.DataManagementService.DocumentCacheProjection` meter through the
deployment's telemetry collection. These are actual instruments, with their emitted
units; they are not a promise of a built-in metrics HTTP endpoint. See
[telemetry ownership](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).

| Instrument | Kind / unit | Use |
| --- | --- | --- |
| `edfi.dms.document_cache.status.observations` | Counter / `{observation}` | Status volume by current component verdict. |
| `edfi.dms.document_cache.status.provider_observation.duration` | Histogram / `s` | Observation latency/outcome; distinguish failure from a successful empty queue. |
| `edfi.dms.document_cache.status.oldest_work.age` | Histogram / `s` | Sampled oldest durable work age; emitted only when an age exists. |
| `edfi.dms.document_cache.projection.target_state` | Counter / `{observation}` | Worker execution-state observations. |
| `edfi.dms.document_cache.projection.dispatches` | Counter / `{dispatch}` | Scheduled processing activity. |
| `edfi.dms.document_cache.projection.dispatch.duration` | Histogram / `ms` | Dispatch latency. |
| `edfi.dms.document_cache.projection.dispatch.items` | Histogram / `{item}` | Per-dispatch work volume. |
| `edfi.dms.document_cache.projection.poison_suppressed.documents` | Histogram / `{document}` | Documents suppressed by poison policy in a dispatch. |
| `edfi.dms.document_cache.projection.failure_backoff.documents` | Histogram / `{document}` | Documents under retry backoff in a dispatch. |
| `edfi.dms.document_cache.projection.item.outcomes` | Counter / `{outcome}` | Processing results. |
| `edfi.dms.document_cache.enqueue.successes` / `edfi.dms.document_cache.enqueue.failures` | Counters / `{success}` / `{failure}` | Committed enqueue work versus canonical-write enqueue failures. |

Status observations tag `provider`, opaque hashed `target`, `lifecycle`, `queue_presence`,
`operational_health_status`, `operational_health_reason`, `caught_up_status`, and
`caught_up_reason`. Provider duration uses `provider`, `target`, `outcome`, `reason`;
oldest age uses `provider`, `target`, `lifecycle`. Enqueue counters add
`canonical_operation`, `resource_kind`, and failure `category` or success
`outcome=committed`. Preserve the emitted labels; do not add document IDs, payloads,
connection strings, tenant display names, or diagnostic text as metric dimensions.
Missing age samples cannot establish empty queues, and process-local counters/windows
can reset on restart. Use durable queue observations alongside them.

`CdcTelemetryLabels` defines safe CDC label vocabulary, not an additional emitted lag
meter: provider/readiness/component plus deployment/instance/generation/outcome tokens,
with token labels bounded to 128 characters. CDC lag comes from the reader above.
CDC diagnostics are bounded to 16 entries, with truncation signaled by
`diagnosticsTruncated`; messages are bounded to 512 characters and optional evidence
text to 256. Preserve truncation/eviction indicators and unavailable states when exporting
status. Review incident captures before sharing; raw worker traces, credentials, source
positions, and public document payloads do not belong in dashboards or evidence artifacts.
Sources: [status telemetry](../../src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheStatusTelemetry.cs),
[projection telemetry](../../src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheProjectionTelemetry.cs),
[enqueue telemetry](../../src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheEnqueueTelemetry.cs),
and [CDC labels](../../src/dms/core/EdFi.DataManagementService.Core/DocumentCache/Cdc/CdcTelemetryLabels.cs).
