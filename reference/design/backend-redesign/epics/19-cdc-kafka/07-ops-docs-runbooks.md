---
jira: DMS-1326
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md
- **V1 deployment-state continuity and adoption deferral**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced documents own the architecture, contracts, constraints, and deferrals. This
story documents the shipped implementation and must link to those owners rather than
restate them.

## Outcome

Publish verified operator guidance for the implemented relational CDC capability.

## Dependencies

- Depends on 18-07 and the completed E19 setup, status, and lifecycle tooling.
- Depends on the shipped downstream-publication-history provider behavior that either
  unlocks or intentionally keeps locked the E18 internal-only DocumentCache administrative
  commands.

## Implementation Scope

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
- Document only the implemented restart, recovery, containment, source-replacement, and
  destructive-retirement commands.
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

## Acceptance Evidence

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
