# CDC Operations Runbook

[Entry point](README.md) · [Evidence index](cdc-inv-evidence.md)

This shared PostgreSQL/SQL Server runbook is under construction. Every procedure
below is **pending** its named documentation task and evidence exercise. The
records reserve stable destinations; they are not instructions to execute an
unfinished workflow. Use the [shipped command reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
for current command details and the linked design owners for support boundaries.

## Procedure Navigation

| Need | Procedure | Documentation task |
| --- | --- | --- |
| PostgreSQL local setup | [postgresql-setup](#postgresql-setup) | T02 — pending |
| SQL Server local setup | [sql-server-setup](#sql-server-setup) | T03 — pending |
| DMS E2E opt-in | [dms-e2e-setup](#dms-e2e-setup) | T02/T03 — pending |
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

The procedure records reserve exact snippet IDs. T01 contains no runnable snippets.
When a later task supplies an example, wrap only its executable fenced block with
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

**Pending T02; exercise T16.** Scope to deliver: Bootstrap variants, initial writer-publication result, InfraOnly and seed ordering.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).

| Record | Value |
| --- | --- |
| Target/generation | Dedicated initial database and CMS-selected target/generation. |
| Authority/offline window | Pending T02: specify authority and applicable fence from the linked owner. |
| Retained inputs | Protected full settings, schema inputs, original state root; host/container endpoints and worker secret sources. Exact paths and substitutions pending T02. |
| Invocation | Reserved IDs: `cdc-pg-settings`, `cdc-pg-bootstrap-local`, `cdc-pg-bootstrap-published`. Commands and fixture substitutions pending T02. |
| JSON/exit status | Pending T02: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T02: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T02: diagnostic-specific retry, containment or escalation and retained evidence. |

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

**Pending T02/T03; exercise T16/T17.** Scope to deliver: Setup/build wrapper wiring and managed teardown handoff; API message scenarios remain DMS-1325.
[Design owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).

| Record | Value |
| --- | --- |
| Target/generation | E2E database, schema and CMS-selected target together. |
| Authority/offline window | Pending T02/T03: specify authority and applicable fence from the linked owner. |
| Retained inputs | Selected environment/overlay, matching test-process settings, original state root. Exact paths and substitutions pending T02/T03. |
| Invocation | Reserved IDs: `cdc-pg-e2e-setup`, `cdc-pg-e2e-build`, `cdc-sqlserver-e2e-setup`, `cdc-sqlserver-e2e-build`. Commands and fixture substitutions pending T02/T03. |
| JSON/exit status | Pending T02/T03: bind operation-specific fields and exit status to shipped fixtures. |
| Postcondition | Pending T02/T03: observable completion criterion; no completion claimed here. |
| Rejection/timeout action | Pending T02/T03: diagnostic-specific retry, containment or escalation and retained evidence. |

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
