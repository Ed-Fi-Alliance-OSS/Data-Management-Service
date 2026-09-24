---
jira: DMS-1326
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md
- **V1 deployment-state continuity and adoption deferral**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral
- **V1 physical-source replacement deferral**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced documents own the architecture, contracts, constraints, and deferrals. This
story documents the shipped implementation and must link to those owners rather than
restate them.

## Outcome

Publish verified operator guidance for the implemented relational CDC capability and
complete the SQL Server initial connector-user mapping needed by the public setup paths.

## Dependencies

- Depends on 18-07 and the completed E19 setup, status, and lifecycle tooling.
- Depends on the shipped downstream-publication-history provider behavior that either
  unlocks or intentionally keeps locked the E18 internal-only DocumentCache administrative
  commands.

## Implementation Scope

- Implement and qualify SQL Server initial connector-user mapping through the shared
  provider setup path, as specified below. DMS-1326 owns this integration fix.
- Document local opt-in, production-like prerequisites, setup, observation, and
  troubleshooting for both providers.
- Document the shipped topic, connector, consumer, binding-state, security, retention,
  sizing, and telemetry operations.
- Document queue backlog/oldest work, poison failure remediation, lifecycle/configuration
  mismatch, activation/deactivation, `Resetting`, bounded rebuild, clear-latch `Tracking`
  admission for the explicit O(N) scrub, enqueue-failure diagnosis, and provider-specific
  per-write/queue-drain overhead.
- Document that current or historical CDC binding/consumer state disqualifies the simple
  read-acceleration activation/deactivation toggle in v1, and that stopping a connector or
  removing a runtime target is not clearing authority.
- Document the shipped availability of `activate-offline`, `deactivate-offline`, and
  `recover-cache-ahead`. These commands are usable only when the production
  downstream-history provider reports `internalOnly` for the same target and
  physical-source fingerprint; current or historical CDC binding/consumer state,
  `unknown`, missing, or mismatched evidence routes operators to CDC containment/recovery
  instead of simple read-acceleration toggles.
- State that projector downtime permits canonical writes to queue work, while enqueue
  failure rejects the complete canonical transaction. Projection status never gates
  ordinary API routing.
- Document only the implemented restart, recovery, containment, and
  destructive-retirement commands.
- Link to the design's physical-source replacement deferral and document DMS-1323's
  source-mismatch rejection diagnostics. Do not present an operator-prepared replacement,
  retirement, or independent new-database provisioning as a supported migration or
  continuity-preserving replacement procedure.
- Document managed shutdown/startup and native worker/task recovery using the design's
  [recovery boundary](../../design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
  and DMS-1323's shipped commands and diagnostics, including incomplete shutdown and the
  limits of post-recovery readiness observations.
- Document preservation of deployment state, intact-state validation/restart, interrupted
  initial-setup retry, and independently guarded retirement. Link state-loss diagnostics and
  backup/rollback limitations to the owning adoption deferral; do not present adoption,
  replacement-binding JSON, artifact recreation, or state deletion/restoration as a recovery
  procedure for lost provenance or a terminal generation.
- Cross-link E18 projection/restamp guidance and the design-owned deferred workflows.
- Add documentation checks against command help, templates, status output, and test
  fixtures.
- Qualify the real SQL Server projection observation path. Correlate fresh standalone
  reads with the controller clock under the owning readiness contract; database-clock
  granularity/skew must not reject fresh reads. Keep stale/missing evidence and elapsed
  call bounds fail-closed. T17 owns this runtime integration fix and its regression
  coverage; it changes no relational mapping or schema hash.

- Qualify standalone restart/resume with an initially unstarted invocation-owned
  projector. After eligible established preflight, start that executor once for fresh
  readiness, preserving already running executors and rejecting invalid provenance before
  processing. T18 owns this integration fix and its regression coverage under the
  [managed lifecycle contract](../../design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).

## Resolved Runbook Delivery and Verification Scope

These choices define the documentation artifacts, implementation handoffs, and evidence
owned by DMS-1326. The linked design sections remain the owners of behavior; this story
turns the shipped surfaces into operator procedures without adding a second controller or
redefining a recovery contract.

### SQL Server Initial Connector-User Mapping

The public wrappers create a new database and proceed directly to CDC enablement. The
previous provider required an already existing database user. DMS-1326 resolves this
gap through the amended [SQL Server provider contract](../../design-docs/cdc/cdc-streaming.md#sql-server).
The deployment supplies the restricted login; initial provider setup maps its database
user and performs the existing grant validation. The qualified examples use the same
login/user name through `Cdc:DatabaseConnectorPrincipal` and the matching connector
`database.user` identity.

- Use the existing managed provider setup, retained completion, and retry boundaries.
  Preserve the authoritative creation receipt, selected CMS target, physical-source
  validation, writer exclusion, and original state root across local, published, and
  DMS E2E paths. No public callback, pause/resume command, or separate provisioning phase
  is required.
- Reject missing logins, conflicting user/login SIDs, unsupported principal types, and
  elevated connector permissions. Keep login/credential management with the deployment.
  Completed provider setup and established workflows remain validation-only; they must
  not recreate missing users or repair mappings.
- Add focused provider/controller regression coverage and wrapper ordering checks, then
  exercise the public SQL Server setup paths from an existing restricted login with no
  target database/user. A fixture that manually creates the target user cannot qualify
  this workflow. Reuse existing fixtures and qualification lanes.
- T29 implements this prerequisite before T03 writes the SQL Server examples. T17 owns the
  exact runbook-snippet qualification after the examples exist. Neither task is complete
  merely because the design or task plan has been revised. This CDC access setup does
  not change relational mappings or require a `RelationalMappingVersion` bump.

### Documentation Home and Integration with Existing Guidance

- Add `reference/cdc-documentation/README.md` as the operator entry point,
  `operations-runbook.md` for the procedures, and `cdc-inv-evidence.md` for procedure-to-test
  references and qualification results. Keep provider differences within those documents;
  separate PostgreSQL and SQL Server runbook trees are unnecessary.
- Keep the [SchemaTools README](../../../../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands)
  as the command/configuration reference. Reuse and correct its shipped examples; the new
  runbook supplies prerequisites, ordered invocations, expected observations, failure
  branches, and completion criteria. Link to configuration and generated connector
  contracts instead of maintaining another full settings or connector-property catalog.
- Keep projection administration and restamp instructions in the
  [DocumentCache reference set](../../../../document-cache-documentation/README.md) and
  [DocumentCacheAdmin README](../../../../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md).
  Add the CDC decision and handoff at that boundary, including the production downstream
  history gate. Update their links that currently point to this story to point to the
  delivered operator guidance; preserve useful existing anchors.
- Audit the entry points named by the design's
  [documentation disposition](../../design-docs/cdc/cdc-streaming.md#documentation-audit-and-disposition):
  `eng/docker-compose/README.md`, the RestClient local setup instructions, and the Instance
  Management E2E README, as well as `docs/CONFIGURATION.md` and
  `docs/RELATIONAL-BACKEND.md`. Replace stale global claims that relational registration
  has not landed with links to the supported opt-in. A setup path without CDC integration
  must still say so; the DMS E2E path does not confer CDC support on Instance Management.
  Update the design's implementation-status disposition if needed, without changing its
  normative contracts or reviving legacy KafkaMessaging instructions.
- Consolidate duplicated operational prose when editing those entry points. Documentation
  corrections, example extraction, small test-helper reuse, and the initial-user mapping
  fix above are in scope. DMS-1326 is the remaining non-test-centric ticket: required
  runtime integration work must be planned and completed here, with contract amendments
  made in the owning design documents. Do not route that work to completed sibling
  stories or hide it behind manual SQL, raw Connect mutations, or runbook-only automation.
  Existing design deferrals remain out of scope.

### Supported Deployment and Command Examples

- Start with the shipped `api-schema-tools cdc` group, `bootstrap-local-dms.ps1`,
  `bootstrap-published-dms.ps1`, and DMS E2E wrappers. Use `CdcCommandHost`,
  `CdcCommandConfiguration`, and the shared `bootstrap-cdc.psm1`, `cdc-lifecycle.psm1`, and
  `e2e-cdc.psm1` as implementation inputs. Procedures invoke these surfaces; they do not
  reconstruct binding names, render connector JSON, or implement readiness in shell code.
- Provide one complete local setup example per provider, runnable from the repository
  root, with explicit prerequisites, settings preparation, dedicated database selection,
  original state-root selection, first enablement, observation, and managed stop/start.
  Explain the `mssql` wrapper/application token versus `sqlserver` CDC token, host versus
  container endpoints, CMS-selected target IDs, schema inputs, and worker-resolved secret
  references. Identify placeholders and their source; do not leave unexplained IDs or
  pretend that a partial `Cdc` settings fragment is a complete DMS configuration.
- Describe the supported CLI deployment as the qualified local single-worker,
  single-broker profile, including its reported `aclIsolationProven: false`. Present
  production-like durability, security, persistent-state, and deployment-authority
  prerequisites as requirements for deployment-supplied adapters. Changing a profile token
  in the local example is not a secured production installation. Link the
  [topology](../../design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup)
  and [security](../../design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations)
  owners and the separate authorization-enabled qualification evidence.
- Document the actual state inventory: original controller root, provisioning/source-history
  evidence, binding/journal/incident records, `.cdc-deployments` inventory, retained
  `.bootstrap/cdc-runtime` settings, and broker-size override. Distinguish prepared inputs
  from retained CDC settings/state; do not describe the entire `.bootstrap` tree as
  disposable during CDC operation.
  Use wrapper-managed shutdown/startup for the shared worker; explain the narrower
  `start-worker` building block and `status/watch` containment side effects using the
  [managed recovery boundary](../../design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).
  Include custom state roots and interrupted operations, not only the default happy path.
- For every procedure, state the target/generation, required authority and offline window
  when applicable, inputs to retain, command, relevant JSON fields and exit code, expected
  postcondition, and action on rejection or timeout. Take names, casing, output envelopes,
  and exit codes from the shipped host and fixtures. A successful `stop`, `retire`, or
  observation must be interpreted in that operation's scope. Do not equate command success
  with initial writer admission, purge evidence, or consumer correctness.

### Troubleshooting and Recovery Handoffs

- Organize troubleshooting by observed symptom and diagnostic category. Each entry names
  the status/metric or provider observation to inspect, the owning procedure, and the
  observation that ends the procedure. Include unavailable evidence and failure to persist
  an incident or stop a connector, so an unsuccessful containment attempt cannot read as
  completed containment. Consume sanitized controller results rather than inventing new
  status fields or asking operators to interpret raw offsets as continuity proof.
- For projection backlog, oldest work, poison failures, enqueue failure, lifecycle mismatch,
  `Resetting`, rebuild, and scrub, link to the specific E18 runbook procedure. Explain the
  CDC handoff using [projection administration](../../design-docs/cdc/cdc-streaming.md#projection-administration)
  and the shipped `CdcDownstreamPublicationHistoryProvider`. Show both an admitted
  `internalOnly` example and a rejected downstream-history example from the packaged
  DocumentCacheAdmin fixtures. Binding absence or successful retirement is not evidence
  to substitute into the gate.
- For SQL Server prerequisite failures, reuse E18's lifecycle-specific correction guidance
  and distinguish projection RCSI/nested-trigger validation from CDC Agent, capture-job,
  snapshot-isolation, and schema-history diagnostics. Link the
  [provider setup](../../design-docs/cdc/cdc-streaming.md#sql-server) owner. An ordinary
  database connection or successful HTTP health check is insufficient evidence for either
  prerequisite set.
- Cover intact initial-setup retry, established validation/restart, verified managed
  shutdown/startup, and native recovery as distinct procedures backed by DMS-1323 results.
  Route unavailable history, terminal incidents, missing provenance, and source mismatch
  through the owning [continuity](../../design-docs/cdc/cdc-streaming.md#source-history-continuity),
  [adoption](../../design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral),
  and [replacement](../../design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral)
  boundaries. Unsupported recovery ends with preserved evidence and containment/escalation;
  do not turn a deferred new-generation workflow into an executable next step.
- Give guarded retirement its own destructive procedure, with explicit generation and
  cleanup intent, infrastructure prerequisites, partial-cleanup retry, retained evidence,
  and verification of the controller's result. Distinguish per-binding retirement from
  shared-volume teardown and document what survives each. The
  [binding lifecycle owner](../../design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
  governs cleanup authority and order. Incorporate the sensitive-data procedure below,
  including the platform purge evidence that controller cleanup alone cannot supply.

### Monitoring, Retention, Security, and Sizing

- Map the shipped projection status, CDC command status, and Connect exporter observations
  to operator checks under the [operations](../../design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations)
  and [telemetry](../../design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry)
  owners. Identify metric names, units, scope, freshness, and which fields may be absent.
  Use `CdcControllerStatusDetails`, the telemetry adapter, and the qualified exporter
  fixtures as sources. Explain current lag separately from optional percentiles, queue
  backlog, provider retention, and consumer progress. Missing telemetry is not zero lag.
- Include PostgreSQL slot/WAL retention and disk pressure, SQL Server capture/cleanup jobs,
  retained LSN range and row-version-store health, shared offset-store policy, progress and
  schema-history diagnostics, and public-topic cleaner health. Supply bounded inspection
  examples and follow-up actions; reuse existing provider observations where available.
  Label deployment-chosen alert thresholds and sampling intervals as such instead of
  inventing DMS defaults or a new monitoring stack.
- Use a compact role/artifact access checklist linked to the security owner: setup and
  connector database principals, worker, controller, and instance consumers. Cover private
  REST/metrics endpoints, externalized credentials, retained state permissions, and
  sanitized incident artifacts. Show verification of effective access through existing
  tooling; local authorization-disabled exercises cannot satisfy ACL evidence.
- Link the [public consumer bootstrap](../../design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap)
  and [topic retention](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic)
  contracts and turn them into an operator/consumer-owner evidence checklist: configured
  retention, partition barriers, durable checkpoints, deadline/renewal evidence, and
  capacity for the retained log. Reuse the DMS-1324 consumer-conformance evidence as an
  example, not a supported consumer implementation or certification of third-party stores.
- Qualify the standalone size-increase runtime boundary: after acknowledged capacity
  alignment and fresh eligibility, start the invocation-owned projection executor
  before connector resume, preserving any running executor and caller-owned disposal.
  A start failure retains pending intent without resume. The owning
  [size-increase contract](../../design-docs/cdc/cdc-streaming.md#in-place-record-size-increase)
  governs this DMS-1326 integration fix; status/watch cannot complete the operation.
- Document `increase-record-size` using the shipped acknowledgement file and renewed
  confirmation flow, including an explicit no-consumers example and interrupted-rollout
  retry. Link [record sizing](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size)
  and [coordinated increases](../../design-docs/cdc/cdc-streaming.md#in-place-record-size-increase);
  qualify claims using the existing size/rollout fixtures rather than payload length alone.
  For projector tuning and provider overhead, report existing evidence with its workload
  limits. The deferred [production-scale performance qualification](../../design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
  is not reassigned to this documentation story; no new benchmark harness or invented
  capacity thresholds are required.

### Documentation Checks and Exercised Evidence

- Add focused documentation checks to the existing SchemaTools CDC tests and wrapper
  Pester suites. Mark the runnable command/configuration examples with stable snippet IDs
  and read those exact snippets in the checks. Substitute only declared fixture values;
  parse commands through the shipped command host and load settings through the production
  configuration path. Check operation/options against command help without snapshotting
  entire help text. Do not execute every Markdown code block or build a general-purpose
  documentation framework.
- Check documented JSON examples against serialization of representative production result
  fixtures, including success, rejection, not-ready/unavailable, and optional fields.
  Reuse template-rendering fixtures for configuration examples and the packaged-command
  harness for stdout/stderr and exit-code examples. Check relative links/anchors in the
  touched operator documents. Human review owns explanatory prose and correct design
  attribution; tests should fail for a broken example, not an editorial rewording.
- Keep these checks in the existing Contract/PR path via
  `eng/ci/Invoke-CdcQualification.ps1` and existing Pester integration. DMS-1323's exclusion
  of documentation tests assigns that work here; E18's manually reviewed prose does not
  need wholesale conversion to executable documentation.
- Exercise the documented invocations on disposable PostgreSQL and SQL Server deployments
  using the existing controller, provider, packaged CLI, and DMS E2E fixtures. Reuse
  `CdcManagedLifecycleTests`, `CdcNativeRecoveryTests`, `CdcRecordSizeIncreaseTests`,
  `CdcArtifactCleanupProviderTests`, and DocumentCacheAdmin `CdcPublicationHistoryTests`
  where they supply the needed evidence. Add only missing procedure/command wiring cases;
  a parser check or mocked controller result is not a live runbook exercise. Faults and
  destructive invocations are explicitly selected by fixtures that own their artifacts.
- Record each procedure's design link, stable test identifiers, provider, command/example
  ID, qualification profile/image, and sanitized result-artifact reference in the evidence
  index. Map this story's evidence to `CDC-INV-14` and `CDC-INV-15`, linking sibling-owned
  evidence for other invariants. Reuse the shared provider/nightly and secured Kafka lanes;
  required missing prerequisites and skipped cases cannot count as passing acceptance.
  Keep unsupported procedures explicitly unsupported even when a lower-level fixture can
  perform the mutation. Do not claim completed DMS-1325 API scenarios from controller-only
  evidence.

## Acceptance Evidence

- SQL Server initial setup creates the missing user for the pre-existing restricted login
  and succeeds through the public local, published, and DMS E2E setup paths. Focused tests
  cover exact-match retry, conflicting mappings, unsupported/elevated principals, missing
  login, setup-authority failure, and validation-only rejection of a missing user after
  provider completion. Failure prevents connector/writer admission and seed continuation.
- Runbook commands are exercised against the supported PostgreSQL and SQL Server workflows.
- Provider exercises cover RCSI/nested-trigger target and activation validation, guidance
  that post-validation changes to either setting are outside the supported v1 contract,
  `Disabled`-only initialization correction-and-restart, unsupported-incident
  classification without a renewed-readiness guarantee for any other lifecycle,
  activation correction-and-retry, restart without source scan, reset/rebuild crash
  recovery, and work-table capture exclusion.
- Documentation tests detect drift from the shipped configuration, status, and lifecycle
  surfaces.
- Documentation checks or exercised scenarios cover the deployment-state continuity
  boundary, unsupported missing-state adoption, terminal-incident rejection, and retirement
  limitations using DMS-1323's shipped diagnostics and rejection fixtures.
- Documentation checks cover the physical-source replacement deferral and source-mismatch
  rejection using DMS-1323's command help and provider rejection fixtures; no replacement
  command or successful replacement runbook is documented.
- Recovery guidance is checked against DMS-1323's managed lifecycle and native recovery
  qualification evidence; it does not describe eventual containment as pre-consumption
  fencing or later healthy observations as retrospective continuity certification.
- Documentation checks or exercised runbook scenarios cover both admitted `internalOnly`
  paths and rejected active, historical, possible, unknown, missing, or mismatched
  downstream-history evidence for the E18 command gate.
- Every behavioral, security, recovery, or compatibility statement links to its owning
  design section instead of reproducing its normative algorithm or value table.
- Destructive procedures are verified against the implemented guarded operations.

## Representation Restamp and Sensitive-Data Containment

The E18 [representation-restamp utility](../18-document-cache/08-representation-restamp-utility.md)
corrects canonical representation stamps. Its operational procedure is documented in the
[DocumentCache runbook](../18-document-cache/07-documentcache-integration-tests-and-runbooks.md#representation-restamp-operations-runbook).
The normative Kafka boundaries and containment sequence remain owned by
[Contract Change and Repair Operations](../../design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).

For a compatible representation correction in lifecycle `Tracking`, a completed restamp
means canonical work is complete and projection work is queued. After only corrected
DMS/projector instances start, ordinary projection and connector processing may eventually
produce a higher-`contentVersion` Kafka v1 state replacement with the same key, topic,
fields, types, and ordering semantics. The restamp command itself does not drain the queue,
publish records, verify connector or broker delivery, reset offsets, purge older records,
or certify an exact replacement baseline. Lifecycle `Disabled` has no Kafka publication
expectation.

When prior Kafka values contain sensitive information that should never have been
published and must be removed, same-topic restamp is not a containment procedure. A
higher-version upsert, tombstone, compaction request, or successful restamp establishes at
most eventual current state; none proves that superseded bytes were destroyed. Follow the
[Sensitive-data disclosure correction](../../design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction)
procedure:

1. Mark the target not ready, fence every affected connector task, and revoke consumer
   access to the public topic before corrected state can be published.
2. Correct the materializer and use the offline E18 restamp procedure when needed, while
   preserving the full writer fence.
3. Retire the affected binding generation in the governed cleanup order, including the
   public topic, connector, offsets, ACLs, progress topic, and SQL Server schema-history
   artifacts where applicable, before removing binding state.
4. Record the restamp operation ID, binding generation, topic, containment time, deletion
   request, and broker or managed-platform purge confirmation. A successful delete request,
   missing metadata, corrective record, tombstone, or compaction request is not purge
   evidence. Keep the incident open when the deployment cannot obtain the evidence its
   platform requires.
5. Do not recreate or restart the old binding or topic. Re-enablement requires the deferred
   new-generation topic, consumer namespace, fresh snapshot, and publication-barrier
   workflow. Independently operated consumer stores and exports remain in the deployment's
   disclosure-response scope.

Treat the restamp manifest's scope, reason, operation identity, and affected UUIDs as
operational audit data. Do not place them in metric labels or unsanitized logs. Do not put
credentials, connection strings, document bodies, or response JSON in the manifest,
reason, diagnostics, examples, or telemetry. The utility's bounded result claims are not
authorization to restore Kafka access or close a disclosure incident.

## Not Assigned to This Story

- Cloud-provider-specific instructions and consumer product implementation guidance are
  separate work.
- Design changes must be made in the owning documents, not in the runbook.
