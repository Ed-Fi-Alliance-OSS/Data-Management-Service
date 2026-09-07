# CDC Operations Runbook

This runbook covers the shipped deployment-owned CDC operator surface for PostgreSQL and
SQL Server. Begin with the prerequisites below, then use the provider setup, monitoring,
continuity, adoption, and new-database replacement procedures. Remaining authoring and
live replay are tracked in
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
recreate offsets. Missing state requires [complete-record adoption](#adopt-missing-binding)
with live validation. Binding, incident, and retirement behavior remains owned
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
`--tenant-key` is rejected before dispatch. Every CDC verb reserves this token
case-insensitively; named tenants called `default` are unsupported by the CDC CLI.

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
[continuity route](#route-continuity-incident) or [complete-record adoption](#adopt-missing-binding).

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
session after collecting evidence to release its token/runtime overrides. Follow
[guarded retirement](#retire-binding-generation) for destructive scope, original-source
selection, cleanup-proof inspection, and same-operation retry;
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
fields. Do not restart a worker while containment is unverified. Use the [planned stop/restart exercise](#local-stop-restart); live read-back remains T14/T15.

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
not replace continuity evidence. Use [provider setup](#local-setup) and
[security inspection](#inspect-cdc-security), [provider/broker retention](#retention-and-capacity),
[consumer continuity](#consumer-continuity) and [record-size coordination](#increase-record-size).

<a id="route-binding-incident"></a>
### Binding and physical-source incident routing

First verify [state-root and mount precedence](#deployment-state) and the exact selected
logical target/generation. A missing record is different from an unreadable store;
inspect diagnostics and permissions without recreating state. Compare the deployment's
binding with current source evidence, retaining opaque fingerprints in the incident
record. A backup alone is not authority to restore a controller record or edit its source.
After correcting an invocation/root error, repeat [the same-generation observation](#observe-cdc).
For a truly missing binding with a complete retained artifact set, follow
[complete-record adoption](#adopt-missing-binding). For a planned new-database cutover,
follow [physical-source replacement](#replace-physical-source); a changed fingerprint alone
is not authorization to replace or adopt. Until resolved, readiness and safe restart are
unproved. The [binding owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
owns both boundaries.

<a id="route-continuity-incident"></a>
### Continuity incident routing

Use [the bounded continuity procedure](#continuity-incident): `unknown` requires restored
observations before guarded restart; `lost` requires durable latch and independently
verified fencing of the terminal generation. The procedure separates status, explicit stop,
and eligible restart. Neither adoption nor source replacement clears terminal loss.
Use [guarded retirement](#retire-binding-generation) for destructive removal; baseline-replacing repair is
[deferred](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).

<a id="incident-command-context"></a>
## Select an incident target and preserve evidence

**Scope/effect:** read the operator-selected complete binding record and prepare arguments;
this does not import or repair state. Use this context for continuity, adoption,
replacement, and retirement.
Their authority is the [binding owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
and [continuity owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity).

**Starting directory/prerequisites:** repository root, Bash with `jq`, built CLI, protected
`CDC_SETTINGS`, `CDC_PROVIDER=postgresql` or `sqlserver`, and the exact absolute
`CDC_STATE_ROOT` from [deployment state](#deployment-state). Confirm that CMS resolves the
intended physical database and that the CLI can reach the broker, Connect, and (for status,
restart, and replacement) DMS status endpoint. Use [configured secret references](#prerequisites);
a host process needs host-resolvable addresses, even when setup used a one-shot container.
One controller owns this root; do not overlap operations.

**Target/generation:** set `CDC_RECORD` to the selected existing binding record for a
continuity incident or outgoing replacement generation. For adoption, select the complete
operator-supplied record retained independently of the missing state store. Review its
provenance and all fields against deployment records before using it. A redacted connector
manifest is not a binding record. Do not copy a fixture into a deployment or fill missing
identity from topics, offsets, or guessed fingerprints.

```bash
set -euo pipefail
umask 077
incident_dir=$(mktemp -d)
jq '{version, deploymentKey, tenantKey, dataStoreId, instanceKey, generation,
     provider, physicalSourceFingerprint, connectorName, topicName,
     partitionCount, partitionerAlgorithm, contractVersion}' "$CDC_RECORD"
cdc_tenant=$(jq -er '.tenantKey' "$CDC_RECORD")
if [ "$cdc_tenant" = default ]; then cdc_tenant=''; fi
cdc_generation=$(jq -er '.generation' "$CDC_RECORD")
cdc_target=(--tenant-key "$cdc_tenant"
  --data-store-id "$(jq -er '.dataStoreId' "$CDC_RECORD")"
  --deployment-key "$(jq -er '.deploymentKey' "$CDC_RECORD")"
  --instance-key "$(jq -er '.instanceKey' "$CDC_RECORD")")
cdc_context=(--cdc-binding-state-path "$CDC_STATE_ROOT"
  --settings "$CDC_SETTINGS" --environment Production --datastore "$CDC_PROVIDER" --json)
```

For example, a record for synthetic `cdc-lab`, tenant `default`, data store `42`, instance
`school-year-lab`, generation `1` selects CLI `--tenant-key '' --data-store-id 42
--deployment-key cdc-lab --instance-key school-year-lab --generation 1`. The record keeps
`default`; only the CLI argument becomes empty. Literal `--tenant-key default` is
rejected before dispatch. Use the translation for **every** CDC verb.

**Expected result/verification:** `jq` and shell selection exit `0` and print identity
fields, not a CDC outcome. Stop on a read/parse error. Manually confirm every selected
field, including provider, against configuration, current physical-source evidence, and
state-root/mount provenance. Command flags do not rewrite the supplied adoption record;
its identity governs the import. Preserve protected copies of records, observations,
native exit codes, and sanitized platform evidence with timestamps. Keep the original
records immutable and retain binding, incident, and retirement history.

**Interruption/retry:** these reads mutate no deployment state. Re-establish the same
selection if the shell ends. Each command below captures its native exit independently;
use a new incident directory for a later attempt so earlier evidence is not overwritten.
[T05 evidence and pending live replay](cdc-inv-evidence.md#t05-continuity-review).

<a id="continuity-incident"></a>
## Unknown continuity and terminal history loss

**Scope/effect:** status observes the selected generation and, on proved loss, durably
latches and fences it. An explicit stop fences only its connector; restart requests a start
after the shipped continuity and artifact guards. These operations preserve connector
configuration, committed offsets, topics, provider capture artifacts, and deployment
history. They do not change ordinary DMS API routing. Follow
[continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
and [operational readiness scope](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope).

**Starting directory/prerequisites and selection:** use the repository-root Bash
[incident context](#incident-command-context), with the existing binding and generation.
Verify an incident is not actually a wrong root/mount or physical-source mismatch using
[binding routing](#route-binding-incident). Configure bounded provider/Connect timeouts via
the [catalog](../../docs/CONFIGURATION.md#cdc-timeouts-and-durable-state); collect one
observation per attempt, not an unbounded retry loop.

```bash
if dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  cdc status "${cdc_target[@]}" --generation "$cdc_generation" "${cdc_context[@]}" \
  > "$incident_dir/status.json" 2> "$incident_dir/status.stderr.txt"; then
  status_exit=0
else
  status_exit=$?
fi
```

**Expected result/decision:** `CdcStatus.readiness` may be `ready`, `notReady`, or `unknown`,
all with exit `0` when an answer was produced. Inspect the target identity, all components,
and diagnostics as in [CDC observation](#observe-cdc). Specifically:

- `sourceHistory.continuity=unknown`: retain evidence, correct unavailable provider/Connect
  access or observation inputs, then repeat status for the same generation. Unknown does
  not itself prove loss or latch an incident. Keep failed/stopped connectors stopped; do
  not start/resume until the existing guards can affirm continuity. A running connector,
  small lag, or the expected artifact name alone is insufficient evidence.
- `sourceHistory.continuity=lost`: preserve the terminal generation. Inspect
  `incidentLatched`, `statusSourceHistoryLatchNotDurable`, `localStateUnavailable`, and
  `statusIncidentFenceNotApplied`. Restore store access for a failed latch and Connect
  access for a failed fence, then re-poll the same generation to retry containment. Later
  polls retain the original latch. No restart, adoption, or replacement clears this loss.
- `healthy`: this is one component's evidence. Any binding, provider, policy, offset-store,
  or connector-configuration refusal still blocks a guarded restart.

**Explicit containment command:** if publication must be stopped, run this while Connect
is reachable, with the same binding. This is an operator action, not a prerequisite to
[adoption](#adopt-missing-binding), which requires an already-running artifact set.

```bash
if dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  cdc stop "${cdc_target[@]}" --generation "$cdc_generation" "${cdc_context[@]}" \
  > "$incident_dir/stop.json" 2> "$incident_dir/stop.stderr.txt"; then
  stop_exit=0
else
  stop_exit=$?
fi
```

**Stop verification:** applied stop or an absent connector exits `0`; a fence not attempted
or not applied exits `10`. The returned `CdcStatus` can remain `notReady` or `unknown`.
Inspect `stopNotAttempted` / `stopNotApplied` diagnostics and have the Connect
operator verify the named connector's persisted `STOPPED` target state and tasks. A loss
latch or accepted stop alone does not prove persisted containment. The runtime component
in a status response was collected before its automatic fence; do not read it as fresh
post-fence evidence. Follow [planned worker stop/restart](#local-stop-restart) before
stopping the worker; its startup can restore a running connector before any status poll.

**Guarded restart command — only for a nonterminal generation:** after observations have
been restored and continuity can be proved, use this for the stopped/failed connector.
The controller rechecks continuity and artifacts at execution; an earlier healthy poll
is not authorization to use Connect's direct start/resume surface.

```bash
if dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  cdc restart "${cdc_target[@]}" --generation "$cdc_generation" "${cdc_context[@]}" \
  > "$incident_dir/restart.json" 2> "$incident_dir/restart.stderr.txt"; then
  restart_exit=0
else
  restart_exit=$?
fi
```

**Restart verification:** applied restart exits `0` even if returned readiness is
`notReady`/`unknown`; `restartNotAttempted` or `restartNotApplied` exits `10`. Missing
contracts require stderr and the [CLI exit reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#exit-codes).
Repeat bounded status afterward to observe current component evidence, without treating
restart success as end-to-end readiness. [Captured stop/restart contracts](evidence/t12/cdc-contract-captures.json)
and [loss/fence observations](cdc-inv-evidence.md#t03-monitoring-review) are fixture evidence;
live provider/worker read-back remains T14/T15.

**Interruption/retry:** timeout is not rollback; latching or fencing may have committed.
Keep the same target, generation, and state root and inspect/retry containment after fixing
reachability. Never restart a terminal generation to see whether it clears. A recreated
slot/capture instance, offset reset, or same-topic resnapshot cannot establish continuity.
There is no v1 exact post-admission replacement-baseline recipe. Keep evidence for explicit
[guarded retirement](#retire-binding-generation) or escalation to the
[deferred repair owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).

<a id="adopt-missing-binding"></a>
## Adopt a complete record after missing-state diagnosis

**Scope/effect:** `cdc adopt` validates an existing complete governed artifact set and
atomically imports its operator-supplied binding. It provisions no provider objects,
topics, ACLs, offsets, or connector configuration. It is not initial enablement and
cannot infer missing identity. Authority:
[binding/adoption](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).

**Starting directory/prerequisites and selection:** use the repository-root Bash
[incident context](#incident-command-context) with `CDC_RECORD` naming the full proposed
binding JSON, not a status response, manifest, or proof wrapper. First rule out a wrong
state root/mount, permissions, or inaccessible storage. A trusted backup may supply the
complete proposed record, but has no import authority without live validation. Keep any
surviving incident/retirement history; do not delete state to create a missing-record case.
Review provider, target, both opaque keys, generation, fingerprint, governed names,
partition count/algorithm, and versions against the intended deployment.

The existing connector **and its sole task must already be running**, with its committed
streaming offset under the matching source partition and healthy retained history. A
stopped, paused, failed, absent, or incomplete artifact set is refused. Do not stop first
as a generic adoption step, or start a stopped connector without continuity authority to
make it adoptable. Missing binding plus an unsafe/stopped artifact set has no repair path
through this verb; keep containment and escalate to the binding/repair owner. On a lost
history adoption refuses without importing or latching an incident, so adoption refusal
itself supplies no containment guarantee. The DMS status endpoint is not an adoption
prerequisite; provider, Connect, broker, policy, and durable-state access still are.

```bash
if dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  cdc adopt "${cdc_target[@]}" --generation "$cdc_generation" "${cdc_context[@]}" \
  --binding-json "$CDC_RECORD" \
  > "$incident_dir/adopt.stdout.txt" 2> "$incident_dir/adopt.stderr.txt"; then
  adopt_exit=0
else
  adopt_exit=$?
fi
```

**Expected result:** success exits `0` and stdout is a `CdcAdoptionProof`, with
`contractVersion`, `operationId`, `verifiedAt`, complete `binding`, and
`verificationResults`. It has no `outcome=completed` field. Every issued verification is
`exactMatch`, including physical source, provider artifacts, topics, ACLs, complete Kafka
policy/record budget, shared offset store, connector, configuration, streaming offsets,
and source history. See [actual proof](evidence/t05/adopt-completed.json).
A controller refusal exits `10` (`rejectedNoMutation` internally), emits **no JSON proof
and no stdout contract**, and writes diagnostic code/message lines to stderr even with
`--json`: [stopped-connector capture](evidence/t05/adopt-stopped-refused.stderr.txt).
This also applies when the controller could not persist the import; do not infer a generic
store-error exit from a diagnostic category. Invalid input JSON is `64`; early process
errors follow the [CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#exit-codes).

**Verification:** compare the proof's entire binding with the independently supplied
record and read the durable record back from the intended root. The live checks validate
policy; the proof does not grant permissions or repair drift. Then run bounded
[CDC status](#continuity-incident) with that exact selection when the status endpoint is
available. Import success is not a current readiness or consumer-baseline certificate.

**Interruption/retry:** retain the complete input, surviving state, and raw protected
output. Check whether import committed, then reissue with the identical record and target
after resolving observation/permission failures. The atomic import requires an existing
record to match exactly; a mismatch is not an overwrite opportunity. Never infer offsets
from a backup, recreate slots/capture instances, edit identity, or use enablement to adopt
orphan artifacts. [T13 evidence](cdc-inv-evidence.md#t13-adoption-replacement-review)
records complete-record live adoption, mismatch refusal, and exact-record retry.
Provider runbook replay remains T14/T15; an adoption proof is a point-in-time observation.

<a id="replace-physical-source"></a>
## Replace a source through the new-database workflow

**Scope/effect:** this is a planned publication cutover for a target previously enabled
through the v1 new-database workflow. It fences the explicitly named outgoing connector,
then runs initial enablement for a higher generation on a different physical source.
The old generation is retained, including its connector configuration and offsets, for
later guarded retirement. Every per-generation governed name is new; shared Connect
worker state remains shared. It is not a restore utility, identity-rotation tool,
cache-ahead repair, terminal-history-loss recovery, or same-topic reset. Authority:
[source binding/replacement](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
and [initial readiness](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).

**Starting directory/prerequisites:** repository-root Bash and the
[incident context](#incident-command-context), with `CDC_RECORD` selecting the outgoing
binding. Preserve its original-source secret reference for retirement; once CMS resolves
the replacement, that current connection cannot prove cleanup of the outgoing database.
Arrange external writer admission and configuration handoffs with the deployment owner.
The CLI consumes the explicit provisioning assertions below; it does not install a runtime
writer gate or repoint CMS for the operator.

The replacement database must have been created for this CDC provisioning and have never
admitted a canonical write. Its source identity must **already** differ from the outgoing
fingerprint; rotation is outside this command. A copied/restored identity does not qualify,
and this runbook supplies no manual rotation SQL. Obtain the new-database provisioning
handoff before running the command; if it is absent, the prerequisite is unmet. Configure
CMS to resolve this target to that replacement, the same provider, explicit projection
targets, DMS status access, qualified image, credentials, and durable root. Check the
outgoing generation has no terminal incident and the replacement has a clear cache-ahead
latch. Do not rerun destructive E2E setup over an established deployment as a cutover tool.

**Target/generations:** review outgoing and proposed new identities before mutation.
Use explicit higher `--generation`, never a generation inferred by deleting state. This
synthetic example replaces `1` with `2`; set `cdc_new_generation=2` only after verifying
that selection against all retained bindings/retirements and governed artifacts. Other
live generations of this target, including retained older bindings not named by this
replacement, can refuse the preflight; use their retain/retire disposition with the binding
owner instead of bypassing it. A first attempt requires the new generation's artifact
names to be unused. A retry preserves its already-created exact binding.

```bash
cdc_previous_generation=$cdc_generation
cdc_new_generation=2
if dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  cdc replace-source "${cdc_target[@]}" "${cdc_context[@]}" \
  --previous-generation "$cdc_previous_generation" --generation "$cdc_new_generation" \
  --confirm cdcSourceReplacement \
  --database-creation-mode created-for-initial-cdc-provisioning \
  --write-admission closed-never-opened \
  > "$incident_dir/replace.json" 2> "$incident_dir/replace.stderr.txt"; then
  replace_exit=0
else
  replace_exit=$?
fi
```

**Expected result/verification:** the shared result is `CdcAdmission`, not `CdcStatus` or
a cleanup proof. `admissionState=admitted` exits `0`; `notAdmitted` and `unknown` exit `12`
when returned by the admission controller, including refusals settled before fencing.
Inspect all `steps` and `diagnostics`, not only `primaryBlockingCategory` or the exit code.
Early invocation/configuration failures may produce no admission contract.

| Captured fixture | Outcome / exit | Decision evidence |
| --- | --- | --- |
| [Replacement admitted](evidence/t05/replace-admitted.json) | `admitted` / `0` | New generation's identity, satisfied admission steps, no diagnostics. |
| [Existing bound Tracking retry](evidence/t05/replace-resumed.json) | `admitted` / `0` | Same new generation resumed after binding/activation committed. |
| [Outgoing fence refused](evidence/t05/replace-fence-refused.json) | `unknown` / `12` | `replaceSourceRefused`, `connectorNotRunning`, `retryable=true`; outgoing publication may continue. |
| [Source identity unchanged](evidence/t05/replace-identity-refused.json) | `unknown` / `12` | `replaceSourceRefused`, `sourceMismatch`, `retryable=false`; missing prerequisite, not an automatic retry. |

The refusal captures include additional `missingRequiredField` diagnostics from admission
classification before evidence collection; these are preserved, not omitted to make a
refusal look ready. All captures use synthetic default tenant/data store `1`, old generation
`6`, new `7`, and fake collaborators; they are not results from the example's `1` to `2`
cutover. [Capture provenance](cdc-inv-evidence.md#t05-continuity-review).

[T13 packaged CLI captures](cdc-inv-evidence.md#t13-adoption-replacement-review) additionally
exercise both providers: an unchanged physical identity and a non-advancing generation
return `unknown` / `12`, while missing write-admission evidence returns `64` with no stdout
contract. Each preserves the old record, physical identity, and lifecycle. The packaged
Kafka client can emit connection diagnostics on stderr during construction even when the
controller refuses before broker inspection; keep stderr separately from the stdout JSON.
These refusal checks do not establish a successful live cutover.

After admission, verify the outgoing connector's persisted fence, retained old record and
artifacts, and the new record's different fingerprint and governed names. Observe bounded
`cdc status` with `--generation "$cdc_new_generation"` using the same context. Keep the
old selection for later original-source retirement. Coordinate independent consumer
namespace/bootstrap evidence with the [consumer owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap);
the command does not migrate consumer stores. New status is eventual operational evidence,
not an exact post-admission baseline guarantee.

**Interruption/retry:** read both generations and Connect's actual state before acting.
A timeout after the fence can leave the outgoing connector stopped with no admitted new
generation. Keep external write admission closed. Reissue the same replacement with the
same previous/new generations and provisioning assertions only while those assertions
remain true; the controller revalidates and can resume the new generation's exact binding
in `Disabled` or eligible empty `Tracking`. Do not fall back to `cdc enable`: the retained
outgoing binding requires the replacement's fence context. Do not rotate identity again,
advance generation to evade a refusal, or automatically restart the outgoing connector.
Once writes have been admitted, initial-enable retry is no longer the restart route; use
[guarded continuity procedures](#continuity-incident). Terminal loss stays terminal and
requires the [deferred repair handoff](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).
[T13 assertions and dependency disposition](cdc-inv-evidence.md#t13-adoption-replacement-review)
are recorded. Successful live new-database replacement and API/consumer handoff remain
pending the E19-06 helpers and T14/T15 provider replay; T16 reconciles final evidence.

<a id="retire-binding-generation"></a>
## Destructively retire one binding generation

**Scope/effect:** permanently remove the selected generation's governed CDC artifacts.
Use [planned stop](#local-stop-restart) when artifacts should be retained for restart.
Retirement is a separate operator decision under the
[binding lifecycle owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).
It does not create a replacement baseline or certify deletion from remote broker copies
or independent consumer stores; [disclosure correction](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction)
requires that separate evidence.

**Starting directory/prerequisites:** repository root, Bash/`jq`, and the
[incident command context](#incident-command-context), including protected settings,
the same absolute binding-state root/mount used by setup, and backups of binding,
incident, and retirement records. Close concurrent controller operations. Retain access
to the selected generation's original provider database, Kafka admin interface, and
Connect worker. Retirement does not require the DMS status endpoint or provisioning
assertions for a new database. Shipped settings and step budgets remain in the
[configuration catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc);
command syntax and exit codes remain in the
[CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#commands).

**Target/generation and original-source verification:** select `CDC_RECORD` from the
durable store for the generation being removed. Recheck every displayed identity field
before mutation, especially provider, fingerprint, connector, topic, and generation.
`cdc_generation` selects the retained generation, even if configuration now defaults to
its replacement. Translate record tenant `default` to CLI `--tenant-key ''` using the
shared context; passing literal `default` is rejected before dispatch, including with
`--source-connection-variable`. The retire verb reads
the stored binding; `--binding-json` is an adoption option, not a retirement input.

Have the deployment secret mechanism export `CDC_ORIGINAL_SOURCE_CONNECTION` with the
connection for **this generation's original physical database**. Match that secret's
database identity and protected provisioning/source-fingerprint evidence to
`CDC_RECORD.physicalSourceFingerprint`. Do not print or put its value in arguments.
The command below also verifies the live fingerprint before fencing: the shipped
validate-only provider pass reads the original source and must match the binding.
There is no retirement dry-run or separate source-override status verb. Missing source
access or an unproved match leaves retirement incomplete as an operational objective;
do not certify absence by connecting to the replacement database.

The explicit variable bypasses the CMS connection lookup, which would now return the
replacement database. An unset or empty variable refuses; it never falls back to CMS.
For a current generation, omitting the option uses CMS, but the same live fingerprint
guard still applies. In a one-shot container, inject the named variable through the
deployment's protected environment and use addresses resolvable in that container;
a host environment variable alone is not available inside it.

**Mutation:** after the identity/source review, run once and retain both output streams
and the native exit code. The exact destructive confirmation is `cdcBindingRetirement`.

```bash
test -n "${CDC_ORIGINAL_SOURCE_CONNECTION:-}"
retire_record="$incident_dir/retire-binding-before.json"
cp -- "$CDC_RECORD" "$retire_record"
retire_assertion=()
retire_args=(cdc retire "${cdc_target[@]}" --generation "$cdc_generation"
  --source-connection-variable CDC_ORIGINAL_SOURCE_CONNECTION
  --confirm cdcBindingRetirement "${cdc_context[@]}")
retire_exit=0
dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  "${retire_args[@]}" "${retire_assertion[@]}" \
  >"$incident_dir/retire.stdout" 2>"$incident_dir/retire.stderr" || retire_exit=$?
printf '%s\n' "$retire_exit" >"$incident_dir/retire.exit"
cat "$incident_dir/retire.stderr"
```

**Never-registered/already-removed connector judgement:** the default is no assertion.
If Connect reports the connector absent, it cannot distinguish one that never registered
from one deleted outside guarded retirement. Committed offsets can outlive connector
configuration, so that answer alone does not prove their absence. The operator must
reconcile the selected connector name and retained registration/cleanup evidence before
using `--connector-already-absent`. Examples supporting the judgement are an enablement
that never registered its connector, or an interrupted guarded retirement that deleted
offsets before deleting the connector. An unexplained external deletion needs investigation.

Once that judgement is recorded, set `retire_assertion=(--connector-already-absent)` and
reissue the same invocation in a **new** protected `incident_dir`, preserving earlier
output. This permits the controller to proceed when the worker reports absence; it
neither deletes unobservable offsets nor proves their absence on the worker's behalf.
The proof records `connectSourceOffsets.cleanupState=notFound` and an
`evidenceSummary` beginning `the operator asserted`. It does not bypass source verification
or treat an unavailable worker as an absent connector. See the
[binding owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
for the authority and limits of this assertion.

**Expected outcome:** a successful `--json` invocation emits one `CdcCleanupProof`, exit
`0`, with `cleanupMode=retireBindingGeneration`, complete `bindingIdentity`, `operationId`,
`verifiedAt`, and `governedArtifacts`. It has no `outcome`, readiness, or partial-progress
field. Review the captured [PostgreSQL proof](evidence/t06/retire-postgresql-completed.json),
[SQL Server proof](evidence/t06/retire-sqlserver-completed.json), and
[asserted-absence proof](evidence/t06/retire-absent-asserted.json); these are fixture output,
not deployment cleanup results.

| Captured result | CLI output / disposition |
| --- | --- |
| Complete cleanup | Proof on stdout, exit `0`. Inspect identity, every artifact, evidence summaries, and retained retirement record. |
| Wrong physical source, missing binding, provider mismatch, or unacknowledged absent connector | `retireRefused`, exit `10`, empty stdout; stderr carries code/message. Correct evidence/selection or the operator judgement before retry. No proof. |
| Connector, Kafka, provider, or final state-store step fails | `retireIncomplete`, exit `12`, empty stdout; stderr carries code/message. Preserve surviving state and reconcile what was actually removed. No proof, even if several earlier steps succeeded. |
| Provider cleanup budget expires | Controller diagnostic `retireIncomplete`, component `providerSetup`, observed `timedOut`, retryable `true`; CLI exit `12`, empty stdout. CLI stderr prints the code/message, not the detailed diagnostic object. An initial source-connect/validation failure occurs before cleanup and instead refuses before mutation. |
| Caller interruption, process failure, or lost output | No trustworthy completion receipt. Inspect protected records and component evidence; a timeout or missing stdout does not establish rollback. |

The [captured timeout and refusal evidence](cdc-inv-evidence.md#t06-retirement-review)
distinguishes internal diagnostic observations from the actual CLI streams. Syntax,
configuration, and unexpected failures use the other
[CLI exit codes](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#exit-codes);
do not interpret every nonzero exit as partial cleanup.

**Proof and state verification:** only parse a proof after exit `0`. Compare its full
identity with the protected pre-mutation copy (the live binding is removed on success);
inspect, rather than discard, each artifact's evidence.

```bash
if [ "$retire_exit" -eq 0 ]; then
  jq -e --slurpfile selected "$retire_record" '
    .cleanupMode == "retireBindingGeneration" and
    (.bindingIdentity == ($selected[0] |
      {deploymentKey, tenantKey, dataStoreId, instanceKey, generation, provider,
       physicalSourceFingerprint, connectorName, topicName}))' "$incident_dir/retire.stdout"
  jq '{operationId, verifiedAt, bindingIdentity, cleanupMode, governedArtifacts}' \
    "$incident_dir/retire.stdout"
fi
```

The shipped sequence stops the connector with stopped-state read-back, deletes its
committed source offsets while it still exists, then deletes its configuration. It removes
the binding's public/progress topics and literal grants, plus SQL Server schema-history
topic/grants when applicable, then provider capture artifacts. PostgreSQL owns a slot and
publication; SQL Server owns the three capture instances and gating role named in its
inventory. Successful proofs account for each governed artifact as `deleted` or `notFound`.
Broader Kafka grants and consumer-group grants remain deployment-owned; local
ACL-disabled `notFound` evidence is not production isolation evidence.

The shared Connect offset topic, worker config/status topics, database/source identity,
canonical/projection tables, and other generations are not this cleanup's artifacts.
The filesystem store writes a durable `CdcRetirement` identity record before removing
the selected terminal incident and then the binding. Inspect the matching
`retirements/<deploymentKey>/<instanceKey>/<generation>.json` under the same state root;
the corresponding binding/incident files should be absent after successful cleanup.
The retirement record retains publication history, not the full cleanup proof: archive
the CLI proof separately with the protected before/after evidence. A failure during final
state removal can leave a retirement record alongside a binding or incident; the
retirement record alone is not a completed cleanup receipt. Back up the whole root again
and preserve it through [local teardown](#local-cleanup).

**Interruption/same-operation retry:** keep the same target, generation, original-source
reference, and state root. Resolve the failed provider/broker/worker/permission condition,
then reissue `cdc retire`, adding the absent-connector assertion only after the judgement
above. The CLI allocates a new `operationId` on each invocation; “same operation” means
the same retirement selection, not an operator-supplied ID. A provider timeout can occur
after offsets, connector, and topics were already removed. Its diagnostic reports the
failed step, not an inventory of completed steps; retained binding/incident records name
the work, while component read-back and prior attempt evidence establish what remains.

Do not retry enable/restart, rotate generation, restore the binding from a backup, delete
state files directly, or use broad topic/volume deletion to complete this retirement.
If the binding is already absent after a lost response, another `cdc retire` refuses for
missing binding; reconcile the retained retirement record, archived proof if available,
and component evidence instead of recreating the record. Without source access or proof
of all governed cleanup, keep the outstanding work explicit. Retirement also preserves
the historical CDC restriction on
[offline projection commands](#cache-ahead-containment).

[T06 manual review](cdc-inv-evidence.md#t06-retirement-review) and
[T17 operator assertions and captured results](evidence/t17/README.md) cover registered,
never-registered, timeout/retry, retained state, source-selection and CLI output paths.
T14/T15 own the provider runbook replays; successful fixture cleanup does not prove
platform or independent consumer-store purge.

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
| Sensitive records must be purged | [Sensitive-data containment](#sensitive-data-containment) implements the [disclosure correction handoff](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction). Preserve the incident, fence publication, revoke access through the platform owner, and use guarded retirement. Higher versions, tombstones, or compaction alone do not prove purge. |

All restamp work requires externally stopping affected readers, DMS replicas, canonical
writers, projector/direct-fill writers, bulk/administrative paths, and external writers.
The utility's confirmation does not implement or certify that fence. `Resetting`,
`Rebuilding`, or any set latch is ineligible. Follow the owning utility's mode/manifest/retry
guards when delivered; do not replace it with manual stamp SQL. After projection/publication
correction, higher-version public records and projection/connector status are eventual
observations, not a replacement exact CDC baseline or an API admission gate. Disclosure
re-enablement remains deferred by its owner.

<a id="cdc-security"></a>
## Credentials and isolation

The [security owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations)
defines isolation; the [configuration catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc)
defines settings. Supply each identity in its own authentication boundary:

| Identity / shipped setting | Credential location and operator responsibility |
| --- | --- |
| Provider setup: `SetupPrincipal` | The source connection selected through CMS, or the named original-source connection for retirement, authenticates the setup/validation operation. The setting identifies the expected principal; it is not a password or an impersonation switch. Keep setup authority out of the connector account. |
| Database connector: `ConnectorDatabasePrincipal`, `ProviderConnectionProperties` | Use a distinct least-privilege database role/login, such as `dms_connector`. The rendered connector receives `database.user` and `database.password`; the worker resolves its configured secret reference. Match that user to the database principal whose grants were validated. |
| Kafka connector: `ConnectorKafkaPrincipal`, `KafkaClientSecurityProperties` | The broker principal is typed, for example `User:dms_connector`. Java client credentials authenticate it; this principal label alone does not. The renderer forwards these settings to `producer.override.*` and, on SQL Server, both internal schema-history clients. |
| Kafka worker: `ConnectWorkerPrincipal` | The worker's own deployment configuration supplies its Kafka credentials and internal-topic access. Connector producer overrides do not configure worker authentication. Keep worker internal topics, Connect REST, and its metrics bridge restricted to deployment operators. |
| Consumers: `Consumers[].Principal` and `.GroupId` | Each deployment-supplied principal is paired with its own group. Consumer applications supply their own credentials. Topic-per-instance authorization is the isolation boundary; consumer-side filtering is not a substitute. |
| Control plane: `KafkaAdminClientSecurityProperties` | The CLI's .NET admin client uses deployment-owned librdkafka credentials for topic/configuration/ACL operations and observations. These credentials do not become connector credentials. Maintain separate administrative access during containment. |
| DMS observation: `DmsBearerToken` | Supply a protected bearer token satisfying `DataManagement:DocumentCache:Status:RequiredRole` at `/health/document-cache`. It grants neither Kafka nor database authority. Connect REST reachability/security is a separate deployment responsibility; this token is not sent there. |

`SetupPrincipal` and `ConnectorDatabasePrincipal` remain required with ACLs disabled.
With `AclsEnabled=true`, supply typed Kafka connector/worker/consumer principals and
match them to actual authenticated identities. Rotation changes credentials, not binding
identity; a successful settings validation is not proof of authentication or authorization.
Protect settings, worker configuration, broker administration, and
[deployment-state backups](#deployment-state) independently.

These are supported **configuration fragments**, not complete deployment settings or
commands to register a connector. Merge them into the protected configuration used by the
[shipped enablement workflow](#local-setup). Java secret references are resolved by a
configured worker `env` ConfigProvider, with those named secrets present in the worker:

```json
{
  "DataManagement": {
    "DocumentCache": {
      "Cdc": {
        "ProviderConnectionProperties": {
          "database.user": "dms_connector",
          "database.password": "${env:CDC_DATABASE_PASSWORD}"
        },
        "KafkaClientSecurityProperties": {
          "security.protocol": "SASL_SSL",
          "sasl.mechanism": "SCRAM-SHA-512",
          "sasl.jaas.config": "${env:CDC_KAFKA_JAAS_CONFIG}",
          "ssl.truststore.location": "/run/secrets/kafka.truststore.p12",
          "ssl.truststore.password": "${env:CDC_KAFKA_TRUSTSTORE_PASSWORD}",
          "ssl.truststore.type": "PKCS12"
        }
      }
    }
  }
}
```

The JAAS secret contains the deployment's complete login configuration for the connector
principal. Mount the truststore in the worker. The renderer owns all generated prefixes;
do not supply `producer.override.*` or `schema.history.internal.*` keys in the dictionary.
See the [template allow-list](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcConnectorTemplateInputValidation.cs)
and [SQL Server history owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).

For the .NET dictionary, use the following property names. The deployment's configuration
provider must inject resolved secret values before starting the CLI; the admin client
passes values directly to librdkafka and does **not** resolve `${env:...}` worker references.
Paths here belong to the control-plane process/container, not the worker.

| `KafkaAdminClientSecurityProperties` key | Supported example / value source |
| --- | --- |
| `security.protocol` | `SASL_SSL` |
| `sasl.mechanism` | `SCRAM-SHA-512` |
| `sasl.username` | `cdc_control` (synthetic administrative account) |
| `sasl.password` | Resolved value of named deployment secret `CDC_KAFKA_ADMIN_PASSWORD` |
| `ssl.ca.location` | `/run/secrets/kafka-ca.pem`, mounted for the CLI |

Java `sasl.jaas.config` and `ssl.truststore.location` are rejected in the admin dictionary;
librdkafka `sasl.username` and `sasl.password` are rejected in the connector dictionary.
The actual vocabularies and value checks are owned by
[`CdcControlOptions`](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control/CdcControlOptions.cs).
The CLI loads its documented settings/environment providers; see its
[configuration reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md).
Do not assume that storing a secret in a host user-secret store automatically injects it
into either process.

<a id="inspect-cdc-security"></a>
### Inspect provider grants and effective Kafka policy

**Scope/effect:** inspect retained setup evidence, then observe the selected live binding
with existing validators. `cdc status` does not provision grants or topics; it can latch
proved continuity loss and fence the connector. This is not an ACL-repair command.

**Starting directory/prerequisites and target:** repository root, Bash and `jq`, the built
CLI and [incident command context](#incident-command-context). Use the exact existing
record/generation, default-tenant translation, state root, and network context. Have the
provider and Kafka operators retain current authorized inspection evidence for this
selection; raw Connect configuration and internal source records stay in protected storage.

Inspect any retained generated provider manifest (`cdc-provider.pgsql.manifest.json` or
`cdc-provider.mssql.manifest.json`) for `outcome`, `observed_source_fingerprint`,
`source_table_inventory`, `provider_artifacts`, `grant_inventory`, and `validation_diagnostics`.
Compare the safe principal, object, privilege, and column observations with
[PostgreSQL setup](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql) or
[SQL Server setup](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).
Verify the intended three captured source tables and exclusion of
`dms.DocumentProjectionWork` from both capture and effective connector access. Check
role membership, inherited/public permissions, forbidden document writes, and heartbeat
column permissions as well as direct grants. A short direct-GRANT listing is insufficient.
Provider validators report unsafe access/capture without silently removing it.

Use the generated redacted connector manifest, when retained, to correlate provider,
connector/topic names, `configSha256`, and `redactedConfig` selectors with provider evidence.
Its schema-history identity is absent for PostgreSQL. These are optional artifact outputs
of the [provider manifest emitter](../../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CdcProviderManifest.cs)
and [connector renderer](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcConnectorTemplateRenderer.cs).
The packaged controller requests no provider manifest and exposes no manifest-export verb;
do not expect these files under the binding root after a CLI invocation. If the setup
owner did not retain them, record that absence and use current validator observations plus
provider-owner evidence. Do not rerun initial setup or invent provider SQL to manufacture
historical evidence. A manifest is neither a binding record nor a fresh policy verdict.

```bash
security_exit=0
dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- \
  cdc status "${cdc_target[@]}" --generation "$cdc_generation" "${cdc_context[@]}" \
  > "$incident_dir/security-status.json" 2> "$incident_dir/security-status.stderr" || security_exit=$?
printf '%s\n' "$security_exit" > "$incident_dir/security-status.exit"
if [ "$security_exit" -eq 0 ]; then
  jq '{observedAt, readiness, primaryBlockingCategory,
       targets: [.targets[] | {targetIdentity, providerSetup, kafkaPolicy,
         connectOffsetStore, connectorConfig, diagnostics}]}' "$incident_dir/security-status.json"
fi
```

**Expected outcome and verification:** a produced `CdcStatus` exits `0`, including
`notReady` or `unknown`; verify selected identity and component evidence using
[status interpretation](#observe-cdc). Captured [ready](evidence/t03/ready.json) and
[offset-store-invalid](evidence/t03/offset-store-invalid.json) examples establish the
JSON shape, not this deployment's isolation. The status components summarize validation;
they do not contain a full grant inventory or an individual ACL-state array. Retain the
platform's effective-policy observations alongside them:

| Inspection scope | Compare using the owning policy |
| --- | --- |
| Selected generation's public topic | Derived name and explicit topic settings against the [topic contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic); configured producer/consumer grants and consumer-group pairing against [security](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations). Inspect effective wildcard/prefixed grants as well as literal entries. |
| Binding progress topic | Generated inventory and validator evidence; instance consumers must not read internal source metadata. Follow [security](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations). |
| Shared Connect offset topic | Configured worker identity, explicit effective topic settings and worker-only grants against the [offset-store owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#kafka-connect-offset-store). Broker defaults alone do not establish an explicit topic policy. Other worker internal topics and REST access also need deployment inspection. |
| SQL Server schema-history topic | Generated identity, explicit policy and connector history-client access against [SQL Server history](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server). Consumer access is forbidden; `include.schema.changes=false` does not remove this internal topic. PostgreSQL has no corresponding topic. |

The [Kafka adapter](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control/CdcKafkaAdminAdapter.cs)
checks effective matching grants and configured consumer isolation. Its permitted retained
public topics belong to older generations of the **same target**; generations are not
separate consumer isolation boundaries. Ask the platform owner to establish the effective
authentication, superuser, unmanaged-principal, and network boundaries outside these
configured grants. Do not claim a complete security audit from the component verdict.

With `AclsEnabled=false`, adapter ACL evidence is **not applicable**, even when aggregate
local status is ready; it cannot qualify production isolation. The existing authorizer
fixture checks real broker ACL metadata but connects as its configured anonymous superuser;
it does not prove separate production principals authenticate and are denied end to end.
[Evidence and pending replay](cdc-inv-evidence.md#t07-security-review) distinguish these layers.
Consumers still need the [bootstrap protocol](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap)
and [consumer conformance owner](../design/backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md).

**Interruption/retry:** preserve diagnostics and observed timestamps. Restore the same
context and repeat after the responsible operator corrects access or configuration.
Do not delete state, recreate capture, or rerun enablement to conceal drift. During a
disclosure incident, keep admission closed and use the containment procedure below;
restoring consumer grants to satisfy routine readiness is not incident resolution.

<a id="sensitive-data-containment"></a>
## Contain a sensitive-data disclosure

**Scope/effect:** stop publication, remove consumer access through the deployment owner,
and destructively retire the affected generation. The
[disclosure correction owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction)
defines completion and deferred re-enablement. This procedure leaves CDC unavailable for
the affected target; it does not promise an old-topic restart or a replacement baseline.

**Starting directory/prerequisites and selection:** repository root and the
[incident command context](#incident-command-context), including a protected immutable
copy of each affected binding record, default-tenant translation, original-source evidence,
and a single controller. Select every affected generation explicitly, including retained
ones; do not infer affected scope solely from the current configuration generation.
Engage the deployment admission, Connect, broker/platform, and consumer-store owners.
Establish deployment-controlled offline/write-admission containment and mark the target
not ready in that deployment's orchestration. DMS supplies no CLI flag to set incident
readiness and no runtime API writer gate; projection status does not stop ordinary writes.

1. **Fence publication and revoke access.** For each affected connector, run the
   [explicit `cdc stop` command and verification](#continuity-incident), retaining native
   exit and status JSON. Exit `0` means stop applied or connector absent; `10` indicates
   not attempted/not applied. Require separate persisted `STOPPED` target-state and
   all-task fencing evidence from the Connect operator. Missing state, unreachable worker,
   or a successful stop request without read-back leaves fencing unverified; maintain
   external containment and escalate. Keep the fence across worker restarts.

   Have the Kafka/platform operator revoke consumer access to each affected public topic
   and verify effective denial, including access through broader grants or administrative
   identities. Record revocation time and platform evidence. **ACL revocation is an
   operator-owned action, not a shipped `cdc` verb.** The retirement command later removes
   governed literal topic grants; that is not a substitute for immediate revocation.
   Preserve the control plane's ability to inspect and clean up. Do not publish a
   corrective record, restamp, or rebuild while consumer access/publication is uncontained.

2. **Coordinate offline correction.** Follow the
   [representation-correction handoff](#representation-correction-handoff) while the data
   store remains offline. The dedicated E18-S08 restamp utility/evidence handoff is unmet
   in this checkout; record it if needed for this incident. The packaged E18 offline
   commands remain rejected under CDC history. Neither a rebuild nor a higher-version
   upsert, delete, tombstone, or compaction is a purge certificate. Do not redirect this
   incident into the same-topic correction path.

3. **Retire explicitly.** Follow [guarded retirement](#retire-binding-generation) for
   each affected generation, with `--confirm cdcBindingRetirement` and, for a retained
   source, `--source-connection-variable CDC_ORIGINAL_SOURCE_CONNECTION` containing that
   generation's original database connection. Use `--connector-already-absent` only after
   the documented operator judgement; absence does not itself prove offset cleanup.
   This is the destructive step: it removes the governed connector/offsets, per-binding
   topics/grants and provider capture artifacts, then live binding state, retaining
   retirement history. Shared worker topics and consumer-group grants survive.

   Exit `0` returns a `CdcTeardownProof`; compare its identity with the pre-mutation record
   and inspect every `governedArtifacts` entry. Refusal `10` or incomplete cleanup `12`
   provides no proof on stdout. Follow the retirement procedure's same-selection retry
   after resolving prerequisites and reconciling surviving artifacts. A new invocation
   has a new operation ID; retain all attempts under the incident identity. Do not delete
   record files or broader topics/volumes as a shortcut.

4. **Obtain platform and downstream deletion evidence.** Record the public-topic deletion
   request and its completion separately. Obtain the broker/managed-platform confirmation
   required by its deletion guarantee for the public topic and any governed remote/tiered
   copies. A transient metadata lookup failure, successful delete request, or component
   cleanup proof is insufficient. Track independently operated consumer stores, replicas,
   exports, and backups with their own owners: broker retirement cannot erase those copies.
   If required platform confirmation is unavailable, the incident **remains open**, even
   with a complete teardown proof. Do not describe fixture cleanup as platform purge.

Maintain a protected incident record with sanitized shareable evidence. This is an
operator record, not another CDC JSON contract:

| Record | Required observation or explicit absence |
| --- | --- |
| Identity | Incident ID; operation IDs for all attempts; correction/restamp ID if applicable; affected target, generation, connector and public topic; opaque source fingerprint, never the raw source UUID. |
| Containment | UTC detection/admission-closure time, stop request/result/exit, persisted fence/all-task read-back time and evidence, consumer revocation/effective-denial time and evidence. Record each as unverified if missing. |
| Deletion | UTC request and observed deletion times, exact artifact selection, full sanitized cleanup proof with `operationId`/`verifiedAt`, retained retirement-history reference, and any incomplete attempts. Proof `verifiedAt` is not remote purge time. |
| Platform and consumers | Platform request/reference, guarantee and covered copies, confirmation/time or explicitly absent confirmation; independent consumer-store/export owners and outstanding deletion evidence. |
| Disposition | Open conditions and their owners; CDC stays unavailable. Completion requires the disclosure owner's evidence, not a status exit code or local test pass. |

Keep raw payloads, document/student identifiers, raw source positions/UUIDs, connection
strings, credentials, bearer tokens, JAAS content, and unredacted Connect responses/task
traces out of shareable evidence. Preserve original captures in protected storage and
sanitize copies without dropping outcome, generation, operation, artifact-kind,
cleanup-state, and timestamp fields needed to assess the result. The
[redaction tests](cdc-inv-evidence.md#t07-security-review) cover shipped diagnostic
boundaries; raw platform tools may expose data those boundaries would redact.

**Interruption/retry and final disposition:** preserve the incident, pre-mutation records,
retirement history, and all partial evidence. Re-establish external offline containment,
verify fences and revoked access, then resume only the documented same-generation
retirement/purge-evidence work. Even complete retirement does not authorize recreation
or restart of the old binding/topic. Re-enablement remains the
[deferred new-topic cutover](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deferred-new-topic-cutover),
including new consumer namespace, snapshot, and barrier requirements. The
[manual absent-purge walkthrough](cdc-inv-evidence.md#t07-security-review) demonstrates
an open incident using existing fixture output; deployment replay remains T14/T15 and
platform purge confirmation remains the deployment owner's evidence.

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

<a id="retention-and-capacity"></a>
## Inspect retention and pipeline capacity

**Scope/effect:** bounded metadata inspection for one selected binding; no source-row,
cache, work-table, or public-topic scan. These observations supplement
[CDC status](#observe-cdc), which can latch history loss and fence publication. Provider
history, broker retention, and consumer checkpoint continuity are separate obligations;
the [continuity owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
and [topic owner](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic)
define their policies. Do not shorten retention, drop slots/capture instances, reset
offsets, or enable segment deletion to relieve pressure.

**Starting directory/prerequisites and selection:** repository root; establish the
[incident context](#incident-command-context), including the same record, generation,
physical source and default-tenant translation. The DBA uses a protected connection
profile for that physical database, not a password in SQL or shell arguments. Use a
statement/query timeout appropriate to the maintenance budget (for example, 10 seconds
per metadata query); this is an inspection bound, not a readiness threshold. Obtain
catalog/Agent access and the provider's monitoring permission separately from connector
credentials. Retain UTC sample time, provider version, identity, native errors and units.
Take one sample, then a second at a recorded interval if a rate is needed; do not loop
unboundedly. The following provider subsections share this context and disposition.

<a id="inspect-postgresql-retention"></a>
### PostgreSQL retained WAL and slot pressure

In a `psql` session connected through the approved profile to the selected writable
primary, set `cdc_slot` to the **exact generated slot name** from the retained artifact
inventory. The synthetic value below is a selection placeholder, not a naming recipe.
The adapter inspects the same slot/database/plugin/LSN metadata in
[CdcPostgresqlHeartbeatPublicationProvider](../../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CdcPostgresqlHeartbeatPublicationProvider.cs).

```sql
\set ON_ERROR_STOP on
\set cdc_slot 'selected_generated_slot'
SET statement_timeout = '10s';
SELECT clock_timestamp() AS observed_at, current_database() AS database_name,
       slot.slot_name, slot.plugin, slot.slot_type, slot.database, slot.active,
       slot.restart_lsn, slot.confirmed_flush_lsn,
       to_jsonb(slot)->>'wal_status' AS wal_status,
       to_jsonb(slot)->>'invalidation_reason' AS invalidation_reason,
       pg_wal_lsn_diff(pg_current_wal_lsn(), slot.restart_lsn) AS retained_wal_span_bytes,
       to_jsonb(slot)->>'safe_wal_size' AS safe_wal_size_bytes
FROM pg_catalog.pg_replication_slots AS slot
WHERE slot.slot_name = :'cdc_slot';
SHOW max_slot_wal_keep_size;
RESET statement_timeout;
```

**Expected result/verification:** one metadata row for the selected slot, text settings,
no CDC JSON; a successful noninteractive `psql` run exits `0`. Zero rows, denied access,
null/unsupported observations or an error are unavailable evidence, not zero pressure.
The LSN difference is a WAL-address span in bytes, not the exact on-disk allocation or a
Kafka consumer lag. Correlate it with the platform's `pg_wal` filesystem free bytes,
WAL generation rate, and the planned outage duration. `safe_wal_size` can be null for an
unlimited retention setting or a lost slot; it is not a promise of unlimited disk.
See [PostgreSQL slot metadata](https://www.postgresql.org/docs/16/view-pg-replication-slots.html).
Do not equate `active=true` or `confirmed_flush_lsn` with the committed Kafka Connect
source offset. Use [continuity observation](#continuity-incident) for that comparison;
retain invalidation/loss evidence and verify containment separately. Escalate a shrinking
storage/retention margin to the DBA before the planned stop consumes it.

**Interruption/retry:** these queries change only session timeout. Reconnect with the
same profile/slot and take a fresh timestamped sample. No slot recreation or WAL deletion
is a retry. Live query output and pressure observations remain pending T14 in
[T08 evidence](cdc-inv-evidence.md#t08-capacity-review).

<a id="inspect-sqlserver-retention"></a>
### SQL Server capture, cleanup and row-version storage

Use the approved SQL query client in the selected database with query timeout enabled.
The DBA needs access to CDC metadata, `msdb` job activity/history and, for SQL Server
2025 monitoring DMVs, `VIEW SERVER PERFORMANCE STATE`. The shipped
[CdcSqlServerHeartbeatDatabaseProvider](../../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CdcSqlServerHeartbeatDatabaseProvider.cs)
uses `sp_cdc_help_jobs`, latest Agent activity/history, and each capture instance's retained
LSNs. Substitute all three exact generated capture names; never guess from a table name.

```sql
EXEC sys.sp_cdc_help_jobs;

SELECT capture_instance,
       sys.fn_varbintohexstr(sys.fn_cdc_get_min_lsn(capture_instance)) AS retained_min_lsn,
       sys.fn_varbintohexstr(sys.fn_cdc_get_max_lsn()) AS captured_max_lsn
FROM cdc.change_tables
WHERE capture_instance IN
    (N'selected_document_capture', N'selected_cache_capture', N'selected_heartbeat_capture');

SELECT job.name, job.enabled,
       activity.start_execution_date, activity.stop_execution_date,
       history.run_status, history.run_date, history.run_time
FROM msdb.dbo.sysjobs AS job
OUTER APPLY (
    SELECT TOP (1) start_execution_date, stop_execution_date
    FROM msdb.dbo.sysjobactivity
    WHERE job_id = job.job_id ORDER BY session_id DESC
) AS activity
OUTER APPLY (
    SELECT TOP (1) run_status, run_date, run_time
    FROM msdb.dbo.sysjobhistory
    WHERE job_id = job.job_id AND step_id = 0 ORDER BY instance_id DESC
) AS history
WHERE job.name IN (N'cdc.' + DB_NAME() + N'_capture', N'cdc.' + DB_NAME() + N'_cleanup');

SELECT database_id, reserved_page_count, reserved_space_kb
FROM sys.dm_tran_version_store_space_usage WHERE database_id = DB_ID();

SELECT database_id, persistent_version_store_size_kb,
       oldest_active_transaction_id, min_transaction_timestamp
FROM sys.dm_tran_persistent_version_store_stats WHERE database_id = DB_ID();
```

**Expected result/verification:** native result sets, not CDC JSON. A successful batch
reports no SQL errors (with command-line `sqlcmd`, use `-b` so SQL errors produce failure
exit status; success exits `0`). Save errors as well as rows. Expect the selected three
capture instances and both jobs; missing/inaccessible rows are unproved. `sp_cdc_help_jobs`
reports cleanup `retention` in **minutes**, capture `pollinginterval` in **seconds**, and
`maxtrans`/`maxscans` as counts. Read actual values; the
[continuity owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
explains the provider default and retained-range requirement. LSNs are positions, not
elapsed-time or byte estimates. Compare every retained range with committed-source
evidence through `cdc status`; a failed job is independently not ready while covered
history may still be healthy. A scheduled cleanup job need not be running at every sample:
inspect its enabled schedule, latest result and next run with the DBA, rather than
requiring continuous activity or inferring health from an enabled flag alone.

The aggregate [tempdb version-store DMV](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-tran-version-store-space-usage?view=sql-server-ver17)
returns pages and KB without scanning individual versions. When ADR is enabled, also
inspect the [persistent version store](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-tran-persistent-version-store-stats?view=sql-server-ver17);
its size is off-row KB and excludes in-row versions. Ask the DBA to correlate storage
free space, growth and long-lived transactions/snapshots with the sampled usage. Missing
DMV access is unavailable evidence; tempdb alone does not account for an ADR deployment.
Reuse [E18 prerequisite correction](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction)
for RCSI/nested triggers; do not toggle them to relieve pressure in an admitted lifecycle.

**Interruption/retry:** cancel a timed-out read, retain diagnostics, then take a new
bounded sample for the same source. Do not invoke capture/cleanup setup to repair missing
metadata or manually purge change tables. Route unknown/lost history to
[continuity handling](#continuity-incident). Live job/range/version-store observations
remain pending T15 in [T08 evidence](cdc-inv-evidence.md#t08-capacity-review).

<a id="inspect-broker-capacity"></a>
### Broker retained log and cleaner health

**Context:** repository root, Bash, GNU `timeout`, an installed Kafka tool distribution
matching the qualified deployment (`CDC_KAFKA_HOME`), and protected Java admin-client
properties at `CDC_KAFKA_ADMIN_PROPERTIES`. This file is separate from the .NET admin
settings. Set `CDC_KAFKA_BOOTSTRAP` to reachable broker addresses; obtain Describe/DescribeConfigs
and required log-directory inspection authority from the Kafka owner. Use the incident
record to select the exact topic. These are metadata reads, not a public consumer.

```bash
cdc_topic=$(jq -er '.topicName' "$CDC_RECORD")
timeout 30s "$CDC_KAFKA_HOME/bin/kafka-configs.sh" \
  --bootstrap-server "$CDC_KAFKA_BOOTSTRAP" --command-config "$CDC_KAFKA_ADMIN_PROPERTIES" \
  --entity-type topics --entity-name "$cdc_topic" --describe --all
timeout 30s "$CDC_KAFKA_HOME/bin/kafka-get-offsets.sh" \
  --bootstrap-server "$CDC_KAFKA_BOOTSTRAP" --command-config "$CDC_KAFKA_ADMIN_PROPERTIES" \
  --topic "\Q${cdc_topic}\E" --time earliest
timeout 30s "$CDC_KAFKA_HOME/bin/kafka-get-offsets.sh" \
  --bootstrap-server "$CDC_KAFKA_BOOTSTRAP" --command-config "$CDC_KAFKA_ADMIN_PROPERTIES" \
  --topic "\Q${cdc_topic}\E" --time latest
timeout 30s "$CDC_KAFKA_HOME/bin/kafka-log-dirs.sh" \
  --bootstrap-server "$CDC_KAFKA_BOOTSTRAP" --command-config "$CDC_KAFKA_ADMIN_PROPERTIES" \
  --describe --topic-list "$cdc_topic"
```

**Expected result/verification:** configs and partition offsets are text;
`kafka-log-dirs` emits its native JSON with per-replica sizes/errors, not a CDC contract.
Success exits `0`; timeout exits `124`. Capture each native result/exit independently;
stop on failure. The offset tool treats `--topic` as a regex, so the example quotes
the literal name with `\Q`/`\E`. It can skip failed partitions while exiting `0`; inspect
stderr and require every expected partition. No live output is claimed here. Check all
hosting brokers. Earliest-to-end differences are offset spans, not record counts or
bytes: compaction leaves gaps. Log-directory replica `size` is bytes; distinguish each
replica from logical topic volume, and request any remote/tiered allocation separately.
Compare effective public-topic policy and explicit overrides using
[security/policy inspection](#inspect-cdc-security). Shared offsets, binding progress and
SQL Server schema history have different retention owners; do not copy public-topic
settings to them or delete them to reclaim space.

For the same bounded observation window, request broker monitoring for cleaner activity,
uncleanable partitions/bytes, compaction backlog, log bytes per partition, disk free bytes,
and I/O utilization. Correlate [Kafka broker metrics](https://kafka.apache.org/40/operations/monitoring/)
with cleaner error logs and effective `log.cleaner.*` settings; unavailable exporter or
managed-platform metrics stay explicitly unavailable. A quiet producer or low live-key
count does not show that dirty historical records have been compacted. Capacity must
include the largest **retained earliest-to-end log**, dirty records, tombstones, partition
skew, and concurrent writes, as required by the
[consumer owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap).
Do not convert one sample into a universal cleaner threshold or a production capacity
claim. Request platform capacity remediation without weakening the topic contract.

**Interruption/retry:** metadata reads are repeatable for the same topic; sample times
will differ and do not form an atomic bootstrap barrier. Preserve earlier outputs, then
retry the failed observation once access is restored. T14/T15 own live tool/version/output
capture; the consumer must capture its own barriers as described next.

<a id="consumer-continuity"></a>
## Verify consumer bootstrap and checkpoint continuity

**Scope/context:** diagnostic evidence handoff to each independently operated consumer
owner. From the repository root and selected incident record, identify that consumer's
public topic/generation, state namespace and configured principal/group. Obtain its
protected operational report; the CDC CLI has no consumer-bootstrap or checkpoint-repair
verb. No generic shell consumer is substituted for the owned implementation.

Follow the design-owned [bootstrap deadline and renewal rules](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap)
and [explicit tombstone-retention minimum](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic).
The deadline covers the whole bootstrap from first partition scan through durable state
persistence, including stalls/rebalances. Request the first-scan UTC time, earliest
positions, complete partition assignment, each captured end-offset barrier, durably
applied positions and next-offset checkpoints, completion time, and latest renewal proof.
An end offset is the next offset, so evidence must establish application through the
records preceding that barrier. Include empty/idle partitions; an unchanged end can
renew proof only after durable checkpoint completion. Kafka group commits or low lag
alone do not prove application into the consumer's durable state.

**Expected result/verification:** the consumer's own report format and native result,
not `CdcStatus` JSON or a CDC exit code. Missing partitions/checkpoints, corruption,
unexpected assignments, an expired bootstrap deadline or uncertain/expired incremental
renewal invalidate the **entire reconstructed state**. The owner must stop advertising
it, discard it and perform the consumer-owned full bootstrap from earliest offsets.
Resuming an uncertain incremental checkpoint is not a recovery option, even if no offset
error occurred. A repeated missed deadline requires capacity correction before production
use; extending a local timeout does not extend the topic contract.

**Interruption/retry:** let the consumer owner classify any interrupted scan or renewal
against its durable evidence. If proof cannot be recovered, require full bootstrap.
The [E19-05 consumer fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractConsumerBrokerTests.cs)
checks durable barriers, empty partitions, idle renewal and checkpoint-loss/corruption
reconstruction. It is a small test consumer, not certification of another product.
Consumer capacity evidence must include dirty retained log, skew, maximum records, durable
state writes and concurrent mutation load. [T08 evidence](cdc-inv-evidence.md#t08-capacity-review)
separates reviewed sibling assertions from pending live/deployment-owned results.

<a id="increase-record-size"></a>
## Coordinate an in-place record-size increase

**Scope/effect:** a maintenance change across independently operated consumers, broker
replication, the public topic and connector producer. It can restart publication and
increase memory/storage use. The
[record contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size)
and [ordered increase owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase)
own the operation. Budget the fully materialized public envelope, key and one-record
Kafka framing after real transforms/converters; an HTTP request-body limit or compressed
sample is insufficient. Over-budget production fails the task without partial publication
under the fixed `errors.tolerance=none` and uncompressed producer contract.

**Starting directory/prerequisites and selection:** repository root and the same incident
context, record/generation, state root and physical source. Securely retain the existing
full connector configuration and deployment policy; a redacted manifest cannot be sent
back as configuration. Have consumer, Kafka, Connect and deployment operators available,
plus fresh healthy continuity and sufficient source-retention margin. Unknown/lost history
goes to [continuity handling](#continuity-incident) before any configuration action that
could start a task. This procedure is not initial enablement or a source-replacement path.

1. Deployment automation marks the target unavailable for CDC admission and retains that
   maintenance disposition throughout the rollout. Use the shipped
   [stop/read-back procedure](#local-stop-restart) to fence publication and preserve
   offsets. This does not install a runtime API writer gate; the deployment owner must
   separately coordinate admission/mutation limits and the resulting queue/history growth.
2. Each consumer owner raises `max.partition.fetch.bytes` and `fetch.max.bytes` to carry
   the new byte budget, provisions receive/deserialization memory, and supplies effective
   read-back plus capacity evidence. A configured `Consumers` list is not that evidence.
3. Kafka operators raise and read back every broker's `socket.request.max.bytes`,
   `replica.fetch.max.bytes` and `replica.fetch.response.max.bytes`, and the effective
   `message.max.bytes`/topic override. Use the platform's broker configuration mechanism
   and required rolling restarts; `kafka-configs.sh --describe --all --entity-type brokers
   --entity-name <broker-id>` provides inspection with the same connection arguments as
   [broker inspection](#inspect-broker-capacity). An inaccessible broker makes verification
   incomplete. Change this public topic's `max.message.bytes` using Kafka Admin
   `IncrementalAlterConfigs` or `kafka-configs.sh --alter --entity-type topics --entity-name
   <selected-topic> --add-config max.message.bytes=<new-budget>`, then describe it again.
   Preserve its other explicit retention/durability settings and partition count.
4. Update persisted `DataManagement:DocumentCache:Cdc:MaxRecordBytes` and, if needed,
   `ProducerBufferBytes` through the deployment configuration owner. The
   [catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc) and
   [renderer](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcConnectorTemplateRenderer.cs)
   own defaults/validation. The shipped buffer validator requires at least
   `max(33554432, MaxRecordBytes)`, stronger than merely accepting the new record. Raise
   Connect heap with additional headroom; producer buffer size is not a total memory cap.
   Clear stale environment/command-line overrides according to catalog precedence.
5. The Connect owner applies the corresponding generated producer configuration through
   the authenticated Connect REST `PUT /connectors/{name}/config`, retaining the exact
   registered identity and all unrelated governed settings/secret references. Increase
   `producer.override.buffer.memory` before allowing the larger
   `producer.override.max.request.size`; increase request size last. Verify the worker
   permits the overrides. This is a deployment integration action: the packaged CLI has
   no resize/reconfigure/render-export verb, and `--max-record-bytes` only selects the
   policy to validate. `cdc restart` does **not** update the retained configuration.
   Do not hand-author a replacement connector or rerun fresh-database setup. A REST
   update may start/reconfigure tasks: the deployment must preserve containment and
   healthy continuity through that boundary. If it cannot control that transition,
   leave the change pending with the Connect owner; the sibling boundary test is not
   a delivered deployment orchestrator.
6. Read back `GET /connectors/{name}/config`, broker/topic limits and consumer settings
   into protected evidence. Keep compression, ordering, partitioner, task topology and
   error tolerance unchanged. Use [guarded restart](#local-stop-restart) once its
   prerequisites pass, then [CDC status](#observe-cdc) with the new persisted policy.
   Inspect returned components and continuity independently of exit status; retain
   consumer confirmation before the deployment restores its readiness disposition.

**Expected outcome/verification:** Kafka tools exit `0` for successful native operations;
Connect returns an HTTP success response with its own configuration representation, not a
CDC cleanup/admission proof. `cdc restart` success exits `0` even if eventual readiness
is not ready, and a produced `cdc status` also exits `0` for `notReady`/`unknown`.
Reuse [captured CLI shapes](evidence/t12/cdc-contract-captures.json), not invented resize
JSON. Verify unchanged binding/generation/topic/keying, all effective limits, sufficient
heap, successful task progress and retained source offsets. Once aligned, an earlier
rejected record resumes from its uncommitted source position; do not reset offsets or
skip it. [E19-05 boundary assertions](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRecordSizeTests.cs)
exercise this with a synthetic envelope on each provider; they do not establish the
largest production record or this deployment's rollout.

**Interruption/retry:** a timeout may leave a subset applied; retain maintenance/not-ready
disposition, inspect every layer and finish in the same order for the same generation.
Leave already-raised downstream limits in place while resolving missing upstream changes.
Do not blindly lower limits after larger records may have been published. A partial,
out-of-order or unverifiable rollout stays unavailable; a terminal loss during the window
requires containment, not a size retry. T14/T15 own live observations; platform and consumer
owners supply their own read-backs. See [T08 review](cdc-inv-evidence.md#t08-capacity-review).

<a id="pipeline-overhead"></a>
## Separate write overhead, queue drain and Kafka lag

The [projector owner](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#durable-work-and-lifecycle)
places coalesced enqueue work in the canonical write transaction. PostgreSQL's emitted
`ON CONFLICT` update and SQL Server's update/insert locking path both add per-write work;
same-document enqueue/acknowledgement contention is distinct from unrelated-document
throughput. Inspect provider lock waits/deadlocks and enqueue latency alongside canonical
request latency. PostgreSQL also needs WAL, vacuum and bloat observations; SQL Server
needs transaction-log, ghost/index and [row-version-store observations](#inspect-sqlserver-retention).
The [emitted queue routines](../../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs)
and [E18 component evidence](../document-cache-documentation/cdc-inv-evidence.md)
are the existing implementation/evidence owners.

Projector downtime permits queued canonical writes; an enqueue failure rejects the whole
canonical transaction. Queue drain later adds materialization, cache write and work
acknowledgement cost; Connect/Kafka lag occurs farther downstream and is not an enqueue
latency measurement. Compare timestamped [projection/queue observations](#observe-projection),
[shipped telemetry](#telemetry), [connector lag](#inspect-lag) and provider/storage metrics
for the same interval. Coalescing means work-row count is not the number of writes waiting
to be replayed. Measure drain while concurrent writes continue, not only after traffic
stops. SQL Server capture-job polling and connector `SqlServerPollInterval` are different
stages and settings.

Use shipped [projection settings](../../docs/CONFIGURATION.md#datamanagementdocumentcache)
`Projector:PageSize`, `PollInterval`, `MaxConcurrentTargets`, `FailureBackoff` and
`BaselineHighWaterMark` for selected targets. Page size bounds a page, not elapsed drain
or rebuild time; target concurrency is per process, not per-document parallelism within
one target. Use [CDC settings](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc)
for heartbeat/poll intervals, producer buffer and operation timeouts. A larger lag
threshold changes admission policy rather than improving throughput. Change one measured
constraint at a time with the responsible owner and preserve before/after observations;
do not add connector tasks or partitions as an in-place tuning shortcut.

The provider writer tests' `DocumentCacheWriterPerformanceEvidence_it_compares_projector_and_direct_fill_workload_modes`
measure elapsed milliseconds, per-operation timing and contention/retry cases in small
component workloads. The E18 index links both implementations; it carries no representative
production timing artifacts. E19-05's consumer/boundary results and these local exercises
likewise do not certify independent consumer capacity. Follow the still-unassigned
[Projection Performance Qualification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
for provider-specific write/lifecycle, outage drain, baseline/reset/restart cost, log
amplification and large-source query-plan qualification. This task adds neither a
performance harness nor universal thresholds. Record unmeasured capacity as unqualified.
