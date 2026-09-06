# DocumentCache Operations Runbook

This runbook covers operational workflows for the durable DocumentCache projection inside a
single DMS relational data store. It uses the shipped `dms-document-cache` CLI and links to
the design sections that own the behavioral contracts.

For command syntax and package installation, see the
[DocumentCacheAdmin README](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md).
For runtime settings, see [CONFIGURATION.md](../../docs/CONFIGURATION.md#datamanagementdocumentcache).
For relational provisioning context, see
[RELATIONAL-BACKEND.md](../../docs/RELATIONAL-BACKEND.md#always-provisioned-documentcache-inventory).

## Scope

This runbook covers these DocumentCache operations:

- status interpretation for one configured `(tenantKey, dataStoreId)` target;
- projection failure, poison, queue, lifecycle, and cache-ahead remediation;
- new-empty activation, online rebuild, explicit integrity scrub, and packaged rejection
  of offline activation/deactivation and internal-only cache-ahead recovery;
- SQL Server projection prerequisite correction when lifecycle is `Disabled`; and
- the required explicit scrub after suspected restore or unsupported direct mutation.

Kafka connector setup, connector status, topic operations, binding retirement, source
replacement, downstream publication containment, and consumer-state recovery are Kafka/CDC
runbook concerns. Use this runbook only up to the DMS projection boundary, then follow
[Kafka/CDC operations](../cdc-documentation/operations-runbook.md#projection-repair-handoff)
when connector or downstream state may be affected.

Representation restamp belongs to the independently owned offline byte-changing
representation correction workflow and is outside this runbook. This runbook does not
replace that workflow with manual SQL.

Owning design sections:

- [Durable Work and Lifecycle](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#durable-work-and-lifecycle)
- [Freshness and Reconciliation](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation)
- [Projection Operational Health, Caught-Up Status, and CDC Admission](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#projection-operational-health-caught-up-status-and-cdc-admission)
- [Baseline, Rebuild, Deactivation, and Scrub](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#baseline-rebuild-deactivation-and-scrub)
- [Cache-Ahead Invariant Recovery](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-ahead-invariant-recovery)
- [Cache-Backed Reads and Domain Lifecycle](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-backed-reads-and-domain-lifecycle)
- [Projection Administration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration)
- [Contract Change and Repair Operations](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations)

## Command Safety

Use `--json` for automation. Stdout is the shared contract document; logs and sanitized
diagnostics go to stderr or configured sinks. Do not put connection strings, credentials,
client secrets, or protected CMS values on the command line. Load those through the normal
CLI JSON configuration or environment. Resolve user-secrets or deployment secret providers
into those supported inputs before invoking the CLI; see its configuration reference.

Every command targets exactly one data store:

```bash
dms-document-cache status --data-store-id 1 --tenant-key district-a --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

The `--datastore` value must match the deployed DMS/CMS database platform and the selected
CMS data store's `Provider` metadata. Mixed-provider CMS-backed DocumentCache targets are
not a supported production topology.

Omit `--tenant-key` for the normalized default tenant:

```bash
dms-document-cache status --data-store-id 1 --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

Use `--expected-physical-source-fingerprint` on mutating workflows when the operator
has a current status observation and wants to reject mutation if the physical source
changed between diagnosis and execution.

Mutating commands and required tokens:

| Workflow | Command | Confirmation | Offline writer admission |
| --- | --- | --- | --- |
| New empty target activation | `activate-new-empty` | `newEmptyActivation` | None |
| Existing target offline activation | `activate-offline` | `offlineActivation` | `closedAndDrained` |
| Offline deactivation | `deactivate-offline` | `offlineDeactivation` | `closedAndDrained` |
| Online rebuild | `rebuild-online` | `onlineCacheRebuild` | None |
| Explicit integrity scrub | `scrub` | `integrityScrub` | None |
| Internal-only cache-ahead recovery | `recover-cache-ahead` | `internalCacheAheadRecovery` | `closedAndDrained` |

<a id="packaged-downstream-history"></a>
### Packaged downstream-history gate

Packaged v1 rejects `activate-offline`, `deactivate-offline`, and `recover-cache-ahead`.
The shipped CDC-backed downstream-publication-history provider reads durable binding and
retirement records. A matching target/current-source binding reports `active`; a binding
for the target's earlier source or a retirement naming the target reports `historical`.
All other production evidence shapes are `unknown`, including absent deployment
configuration, an empty or unavailable store, unreadable records, and records naming only
other targets. No shipped provider reports `internalOnly` for the same target and
physical-source fingerprint. See the
[binding/history owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
and [projection administration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration).

Current or historical CDC binding/consumer state disqualifies the simple read-acceleration
activation/deactivation toggle. Stopping a connector, removing a runtime target, retiring
a binding, or mounting an empty state root cannot establish internal-only authority.
Test-only `internalOnly` fixtures exercise guarded implementation branches; their success
does not unlock production recovery.

When earlier target, lifecycle, confirmation, source, and provider guards pass, the history
gate returns `status=rejectedNoMutation`, `classification=downstreamHistoryPresentOrUnknown`,
`mutated=false`, exit `10`. Earlier failures retain their own classification; an unresolved
or mismatched source is not proof of internal-only history. Use the
[CDC cache-ahead/repair handoff](../cdc-documentation/operations-runbook.md#projection-repair-handoff)
and [reviewed evidence](../cdc-documentation/cdc-inv-evidence.md#t04-projection-handoff-review).

Command result statuses are:

- `completed`: workflow finished according to the shared result DTO.
- `rejectedNoMutation`: a known guard rejected the command before lifecycle, cache, work,
  latch, or provider-setting mutation.
- `failedNoMutation`: the workflow failed before mutation.
- `incompleteRetryable`: the workflow may have mutated state and must be retried as the
  same command with the same target and guard values.

Exit code `12` maps to `incompleteRetryable`. Reissue the same command; do not select a
different recovery based only on `Resetting`.

## Status Interpretation

The CLI `status` command and `GET /health/document-cache` expose the same projection
status contract. Select the target object whose `targetKey.tenantKey` and
`targetKey.dataStoreId` match the target being investigated, then inspect:

- `resolution.status` and `resolution.reason`: whether the target was found and resolved.
- `eligibility.status` and `eligibility.reason`: process-level projection eligibility.
- `inventory.*`: state, work, cache, data-store identity, and enqueue-trigger inventory.
- `providerPrerequisites.*`: SQL Server RCSI and `nested triggers` status when applicable.
- `lifecycle.state`: `disabled`, `resetting`, `rebuilding`, `tracking`, `invalid`, or
  `unknown`.
- `cacheAhead.state` and `cacheAhead.recoveryRequired`: cache-ahead latch state.
- `operationalHealth.status` and `operationalHealth.reason`: whether projection can
  process ordinary work.
- `caughtUp.status` and `caughtUp.reason`: whether one provider statement observed
  `Tracking`, clear latch, and empty `dms.DocumentProjectionWork`.
- `queueSummary.presence`, `queueSummary.oldestWorkFirstEnqueuedAt`, and
  `queueSummary.oldestWorkAgeSeconds`: indexed queue observation.
- `executionState.status`: process-local target execution state.
- `targetDiagnostics`, `documentDiagnostics`, `poisonTraversalDiagnostics`, and
  `enqueueFailures`: bounded diagnostics for current failure analysis.

Lifecycle interpretation:

| Lifecycle | Meaning | Operator posture |
| --- | --- | --- |
| `disabled` | Enqueue, projection writes, acknowledgements, and cache-backed reads are disabled. | Canonical API remains available. New-empty activation requires initial provisioning authority; packaged offline activation rejects at the history gate. |
| `resetting` | Administrative clearing is in progress or was interrupted. Enqueue remains enabled; cache writes and acknowledgements are fenced. | Reissue the same known interrupted command. If the intended command is unknown, treat as unsupported incident. |
| `rebuilding` | Work seeding/drain is in progress. Cache reads and caught-up success are fenced. | Let the active command or replacement owner finish baseline and drain work. |
| `tracking` | Enqueue and ordinary work processing are enabled. | Cache reads still require clear latch and version-equal cache rows. Queue presence only makes caught-up false. |

Status reasons to triage first:

| Reason or component | Meaning | Action |
| --- | --- | --- |
| `queueNotEmpty` | Durable work exists. | Watch `oldestWorkAgeSeconds` and projector diagnostics. Canonical API routing remains available. |
| `targetBackoff`, `runtimeCancelled`, `runtimeNotObserved` | Projection process is not currently draining normally. | Restart or correct the projector host, then recheck status. |
| `inventoryInvalid`, `enqueueTriggerUnavailable`, non-empty `enqueueFailures` | Enqueue or fixed inventory is broken. | Treat canonical writes as at risk of rollback until corrected. Fix inventory/prerequisites; do not clear work manually. |
| `lifecycleDisabled`, `lifecycleResetting`, `lifecycleRebuilding` | Durable lifecycle fences projection success. | Use the lifecycle workflow for the intended operation. |
| `cacheAheadRecoveryRequired` | A current cache row was observed ahead of canonical source. | Follow the [cache-ahead containment handoff](#cache-ahead-recovery); packaged internal-only recovery rejects. |
| `sqlServerPrerequisiteFailed` | SQL Server RCSI or `nested triggers` is disabled while lifecycle is `Disabled`. | Correct prerequisites during maintenance, restart the target context, then retry activation. |
| `unsupportedPrerequisiteIncident` | SQL Server prerequisite failure was observed outside supported initialization scope. | Preserve evidence and escalate; v1 defines no recovery or renewed readiness guarantee. |

Projection status is not ordinary DMS API health. Processing backlog, poison rows,
`Disabled`, `Resetting`, `Rebuilding`, or a projector failure causes cache-backed reads
to fall back to relational reconstruction, but does not remove an otherwise healthy DMS
replica from canonical API routing.

## Enqueue vs Processing Availability

Supported canonical writes commit source data and `dms.DocumentProjectionWork` atomically
when lifecycle is enqueue-enabled. If enqueue persistence fails, the whole canonical
write rolls back. This is an API availability incident, not only a projection lag issue.
Look at `inventory.enqueueTrigger`, `inventory.work`, `enqueueFailures`, and sanitized
logs at the canonical write/provider boundary.

Projection processing failures are different. If writes enqueue successfully but work is
not draining, `queueSummary.presence` becomes `notEmpty`, `caughtUp.reason` becomes
`queueNotEmpty`, and the API still serves relational responses. Fix projection workers,
materializer failures, provider failures, or poison documents without editing
`dms.DocumentProjectionWork` directly.

## Activation

New empty target activation is only for a newly provisioned database before the first
canonical write, with write admission closed and in-flight writers drained:

```bash
dms-document-cache activate-new-empty --data-store-id 1 --confirm newEmptyActivation --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

Existing `Disabled` target activation through `activate-offline` is rejected in packaged
v1 by the [downstream-history gate](#packaged-downstream-history). Its syntax and
confirmation remain in the [CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#examples);
there is no production internal-only proof to obtain by stopping writers or CDC.
For fresh CDC enablement, use the [CDC provisioning handoff](../cdc-documentation/operations-runbook.md#prerequisites)
so initial admission stays with its owning setup workflow.

The accepted new-empty activation revalidates provider prerequisites and enters `Tracking`
without an existing-source baseline. Offline activation's reset/baseline branches are
exercised with test-only internal-only proof; they are not a packaged operator option.
After an admitted operation, inspect projection status; projection health or caught-up
status alone is not Kafka CDC admission.

## Online Rebuild

Use online rebuild when lifecycle is `Tracking` or `Rebuilding` and the cache-ahead latch
is clear. Canonical writes may remain online:

```bash
dms-document-cache rebuild-online --data-store-id 1 --confirm onlineCacheRebuild --expected-physical-source-fingerprint sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --command-timeout-seconds 86400 --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

The workflow enters `Resetting`, clears cache while preserving transactional work
recording, enters `Rebuilding`, seeds baseline work, drains, and returns to `Tracking`.
A set cache-ahead latch rejects online rebuild before mutation; follow the
[cache-ahead handoff](#cache-ahead-recovery). Rebuild does not change canonical representation
stamps, clear published-state risk, or certify a replacement CDC baseline. Bounded database
batches do not bound total completion time; see the performance owner below.

If the command returns `incompleteRetryable`, rerun the same `rebuild-online` command
with the same target and fingerprint guard. A crash in `Resetting` or `Rebuilding` is
restart-safe only for an explicitly reissued operation.

## Deactivation

Packaged v1 rejects `deactivate-offline` at the
[downstream-history gate](#packaged-downstream-history). The command and its confirmations
remain in the [CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#examples),
but its accepted `Resetting`/bounded cache-and-work clearing/`Disabled` path is test-only
coverage with injected `internalOnly` proof.

Removing a runtime `DocumentCache:Targets` entry pauses processing; it does not deactivate
the durable lifecycle or authorize clearing cache/work. A stopped connector retains
publication history. Route CDC shutdown/containment through the
[CDC incident handoff](../cdc-documentation/operations-runbook.md#projection-repair-handoff).

## Explicit Integrity Scrub

Run an explicit integrity scrub when status or incident analysis indicates missing or
mismatched work, after suspected restore, or after unsupported direct mutation. Scrub is
admitted only in lifecycle `Tracking` with a clear cache-ahead latch:

```bash
dms-document-cache scrub --data-store-id 1 --confirm integrityScrub --expected-physical-source-fingerprint sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

Scrub performs the explicit O(N) source/cache/work relationship scan; it is not a routine
health probe or a scan-free restart step. It conditionally
inserts missing work, repairs mismatched work to the current canonical version, and sets
the cache-ahead latch if it observes current cache-ahead state. It never clears a set
latch and it is not caught-up proof. After scrub completes, wait for projection to drain
and then re-run status. It does not restamp corrected bytes, erase published-state risk,
or establish a replacement CDC baseline.

Direct `DocumentProjectionWork` DML, cache truncation, cache clearing, lifecycle edits,
or latch edits outside supported runtime writers and serialized administrative commands
are unsupported.

## Cache-Ahead Recovery

`cacheAhead.state = recoveryRequired` means a current cache version was observed ahead of
canonical source. Cache-backed reads and cache/direct-fill writes are fenced until an
explicit workflow resolves the latch.

Packaged v1 rejects `recover-cache-ahead` at the
[downstream-history gate](#packaged-downstream-history); the CLI reference retains its
syntax and confirmation contract. `internalOnly` acceptance in an isolated fixture is not
production clearing authority. Preserve status, cache/work/latch evidence, physical-source
fingerprint, binding generation, and the rejected result. Do not retry with a removed
target, empty store, or altered identity to try to clear the latch.

Follow the [CDC cache-ahead containment handoff](../cdc-documentation/operations-runbook.md#cache-ahead-containment).
Stopping publication does not clear the latch or erase downstream observation. V1 does not
publish a lower canonical version as an in-place correction; the required new downstream
namespace/recovery workflow remains deferred by the design. No online rebuild, scrub, or
restamp procedure bypasses that boundary.

## Persistent Projection Failure and Poison Remediation

Use status diagnostics to distinguish the failure:

- `documentDiagnostics` with `materializationFailed` or `writerFailed` usually points to
  materializer, provider, or target-invariant defects for a specific document.
- `poisonTraversalDiagnostics` shows retry scheduling and whether poison rows are being
  skipped so later work can progress.
- `targetDiagnostics` with `runtimeFault`, `targetInvariant`, or `providerObservationFailed`
  points to target-level projection failure.
- `enqueueFailures` points to canonical write enqueue failures, not projection processing.

Remediation sequence:

1. Preserve status JSON, sanitized logs, lifecycle, latch, queue, and oldest-work evidence.
2. Fix the materializer/provider/configuration defect or the unsupported source mutation.
3. Restart or resume the projector host when needed.
4. Run an admitted `scrub` only in clear-latch `Tracking` if restore, unsupported direct
   mutation, missing work, or mismatched work is suspected; otherwise follow the lifecycle
   or cache-ahead handoff.
5. Recheck status until `operationalHealth.status = operational`; require
   `caughtUp.status = caughtUp` only when queue-empty proof is needed.

Do not delete poison work manually to make status green. Work remains visible so projection
can retry after the underlying defect is corrected. If poison rows exhaust baseline
capacity, fix the poison condition first; repeatedly rebuilding without correction only
recreates the same queue pressure.

## Lifecycle Mismatch and Resetting

When a mutating command returns `rejectedNoMutation` with `classification =
lifecycleMismatch`, the durable lifecycle did not match the command's precondition. Re-run
status, confirm the intended workflow, and issue the command that matches that workflow.

`Resetting` by itself is not enough to infer recovery. If `activeCommand` or
`lastEndedDiagnostic` identifies the interrupted command, reissue that same command with
the same target and guard values. If the intended command cannot be proven, preserve
evidence and treat the target as an unsupported incident. Do not manually set lifecycle,
clear the latch, clear cache, or clear work.

`Rebuilding` after interruption restarts baseline from the beginning because v1 has no
durable baseline cursor. These operator workflows do not qualify production-scale
completion time, database load, or repeated queue-DML limits; the owning
[Projection Performance Qualification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
design section still defines the requirement.

## SQL Server Prerequisite Failure Correction

SQL Server projection targets require database Read Committed Snapshot Isolation and
server-level `nested triggers`. They are validated at target initialization and again at
activation preflight. These are projection-target prerequisites, not global DMS
relational-only requirements.

Inspect with DBA tooling connected to the target database:

```sql
SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();
SELECT value_in_use FROM sys.configurations WHERE name = 'nested triggers';
```

If status reports `sqlServerPrerequisiteFailed` while lifecycle is `Disabled`, correct the
settings during a maintenance step, restart the DMS target context, and retry activation.
Example correction statements, executed by an authorized DBA in the intended environment:

```sql
ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
EXEC sp_configure 'show advanced options', 1;
RECONFIGURE;
EXEC sp_configure 'nested triggers', 1;
RECONFIGURE;
```

Activation-preflight failure changes no lifecycle state, cache rows, work rows, latch, or
provider setting, so it may be retried after correction. If a prerequisite failure is
observed in `Tracking`, `Resetting`, or `Rebuilding`, v1 treats it as
`unsupportedPrerequisiteIncident`; it defines no correction-and-restart workflow and no
renewed projection-health or CDC-readiness guarantee. Changing RCSI or `nested triggers`
after successful validation while a target is active is outside the supported v1 contract.

## Suspected Restore or Direct Mutation

After suspected database restore, point-in-time reset, out-of-band data copy, unsupported
direct mutation, trigger disablement, or direct edits to cache/work/lifecycle tables, do
not rely on queue-empty caught-up status. Run an admitted explicit integrity scrub first:

```bash
dms-document-cache scrub --data-store-id 1 --confirm integrityScrub --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

If scrub sets the cache-ahead latch, follow the [CDC containment handoff](#cache-ahead-recovery). If
scrub repairs work, let projection drain and verify status again. If scrub is rejected
because lifecycle is not `Tracking` or the latch is already set, resolve that lifecycle or
latch condition through the supported workflow before claiming caught-up.

## Restamp Scope Boundary

Representation restamp belongs to the independently owned
[offline byte-changing correction design](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction)
and [E18-S08 utility work package](../design/backend-redesign/epics/18-document-cache/08-representation-restamp-utility.md).
This checkout has no dedicated utility, runnable restamp procedure, or restamp test evidence;
that handoff remains unmet in the [CDC evidence index](../cdc-documentation/cdc-inv-evidence.md#t04-projection-handoff-review).
Do not substitute manual `ContentVersion`, resource-mirror, cache, or tracked-change SQL.

Use the [CDC repair decision](../cdc-documentation/operations-runbook.md#representation-correction-handoff)
to distinguish compatible byte correction, cache-ahead recovery, and purge-required
disclosure. Changed public bytes require fresh canonical `ContentVersion` values; rebuilding
or scrubbing at an unchanged version is not correction. The design requires external offline
fencing and a clear latch: `Tracking` for projection/publication mode, `Disabled` for
canonical-only mode with no Kafka publication claim. Same-topic correction is eligible only
when prior records require no purge. Eventual status after correction cannot certify a
replacement exact CDC baseline. These are handoff guards, not a delivered restamp command.

## Kafka/CDC Boundary

Stop at this runbook when the incident is limited to DMS projection status, cache rows,
work rows, lifecycle, latch, and provider prerequisites. Move to Kafka/CDC procedures when
an incident involves connector registration, Connect offsets, PostgreSQL
slots/publications, SQL Server CDC capture artifacts, public or progress topics, source
binding history, consumer state, possibly published cache-ahead values, sensitive-data
containment, or destructive topic/binding cleanup.

Use [CDC observation](../cdc-documentation/operations-runbook.md#observe-cdc),
[continuity routing](../cdc-documentation/operations-runbook.md#route-continuity-incident),
[binding routing](../cdc-documentation/operations-runbook.md#route-binding-incident), and the
[repair handoff](../cdc-documentation/operations-runbook.md#projection-repair-handoff).
The [CDC evidence index](../cdc-documentation/cdc-inv-evidence.md) owns downstream verification;
the [E18 matrix](cdc-inv-evidence.md) remains projection-scoped.
