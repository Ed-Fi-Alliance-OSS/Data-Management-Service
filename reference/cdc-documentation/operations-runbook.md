# CDC Operations Runbook

This runbook covers the shipped deployment-owned CDC operator surface for PostgreSQL and
SQL Server. Begin with the prerequisites below. Monitoring and incident routing are available below;
provider setup exercises and recovery procedures are still pending in
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
queue/poison, enqueue failure, lifecycle, rebuild, and scrub; CDC handoff reconciliation
is pending T04. Current/historical CDC state disqualifies simple internal-only toggles;
stopping a connector does not clear downstream publication history.

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
| Lifecycle or cache-ahead incident | Projection `lifecycle.state`, `cacheAhead.recoveryRequired`; CDC `projection.category=projectionNonOperational` | [E18 lifecycle triage](../document-cache-documentation/operations-runbook.md#lifecycle-mismatch-and-resetting), [CDC repair boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations); stopping CDC does not authorize an internal-only reset. | [Lifecycle](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#durable-work-and-lifecycle) |

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
