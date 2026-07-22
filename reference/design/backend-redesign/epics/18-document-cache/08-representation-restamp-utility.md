---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add an Out-of-Band Representation Restamp Utility

## Design References

- [Offline byte-changing representation correction](../../../cdc-streaming.md#offline-byte-changing-representation-correction)
- [ETag strong-validator decision](../../../../adr-etag-from-content-version.md#etag-format-and-http-validator-semantics-rfc-9110)
- [Topic and message compatibility](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes)
- [Change Query stamping and mirrors](../../design-docs/change-queries.md#concrete-resource-contentversion--contentlastmodifiedat-mirror)

## Outcome

Provide a supported PostgreSQL and SQL Server utility for every rare correction that changes
API or CDC representation bytes without an ordinary domain write. The utility runs outside
ordinary DMS request processing while the selected data store is explicitly offline,
advances the existing canonical content stamps for an explicit document scope, and lets
ordinary projection and streaming publish corrected higher-version state eventually when
prior Kafka records do not require purging. When a correction removes sensitive bytes that
should never have been published, the utility is only the database/API correction step in
E19's destructive disclosure-response workflow; same-topic republication is prohibited.
The utility does not implement or certify a cross-replica/external-writer gate or Kafka
purge.

This is a separate implementation story. General DocumentCache and CDC runbooks invoke
the utility but must not approximate it with hand-written database updates.

## Dependencies

- Depends on E10 content-version allocation, root/descriptor stamp mirrors, and Change
  Query selection semantics.
- Depends on 18-00, 18-02, 18-04, and 18-06 for schema, corrected materialization,
  reconciliation, and readiness verification.
- CDC may observe the resulting higher-version records through ordinary eventual recovery,
  but the utility does not restore or certify another exact CDC baseline.

## Example Use Scenarios

The utility is necessary for every byte-changing correction without an ordinary domain
write, including:

1. A reconstitution or serialization defect omitted a field, emitted an incorrect value,
   changed stable JSON ordering, or formatted a timestamp/number incorrectly for existing
   documents, and the correction does not change `EffectiveSchemaHash`.
2. A reference-link materialization defect produced an incorrect `link.href` or link
   subtree for existing resources without changing their canonical relational data.
3. A descriptor, readable-profile, or final response-shaping defect changes bytes served
   for existing documents while `ContentVersion` and all affected `variantKey` selectors
   remain unchanged.
4. A security or privacy correction removes or masks bytes that should not have appeared
   in an API or stream representation, but no ordinary domain write exists to advance the
   affected documents' stamps. If those bytes were published to Kafka and require purging,
   the connector must be fenced and the affected binding generation destructively retired
   through the E19 runbook; restamping and same-topic republication alone are insufficient.
5. A DMS upgrade fixes a materializer/composer interaction so `DocumentJson`,
   `StreamEtag`, or both change for the fixed stream context.

The utility is not necessary for:

- an ordinary resource or descriptor mutation that already advances `ContentVersion`;
- a schema or profile-definition change whose `schemaEpoch` change gives every changed
  representation a different strong ETag;
- an incompatible key, field/type, delete, or document-contract change merely because it
  needs a new topic. That cutover is deferred; a future implementation may invoke this
  utility as an additional offline step if it also includes a byte-changing representation
  correction for existing documents.

## Deliverables

1. Add a non-interactive administrative command that connects to one explicitly selected
   DMS data store and supports PostgreSQL and SQL Server. It is not an HTTP endpoint,
   background DMS task, projector mode, or client-accessible capability.
2. Require an explicit affected-document scope. At minimum support all current documents
   and project/resource selection; support an explicit UUID input set when it can retain
   the same validation and resume guarantees. Preview the resolved scope and count before
   mutation, and require an explicit confirmation flag for execution.
3. Create and persist an operation manifest containing an operation identifier, opaque
   physical-source fingerprint, provider, normalized scope, operator-supplied reason,
   corrected deployment identity, captured pre-restamp content-version boundary, start
   time, counts, and completion state. Do not store credentials or human-readable tenant
   data in the manifest.
4. Make the operation safely resumable. On first execution, capture a boundary at least as
   high as every selected current `ContentVersion`. On execution or resume, stamp only
   selected current documents at or below that boundary. Fresh values allocated from the
   normal provider sequence are above the boundary, so a completed row is not stamped
   again. Refuse resume when the source fingerprint, provider, scope, or corrected
   deployment identity differs from the manifest.
5. Require an explicit non-interactive confirmation that the operator has taken the
   selected data store offline by stopping every affected DMS replica, API reader, cache
   writer, bulk/seed loader, administrative mutation path, and external writer. Refuse to
   run without that confirmation and record it in the manifest, while stating that the
   flag is not a fence or proof. Require the durable cache-ahead latch to be clear; a
   latched target uses its existing explicit recovery procedure first, and the utility must
   not clear or bypass the latch. The operator remains responsible for keeping the data
   store offline through corrected deployment and verification.
6. In bounded provider transactions, allocate one fresh unique `ContentVersion` per
   selected current document from the normal change-version sequence. Advance
   `dms.Document.ContentVersion` and `ContentLastModifiedAt`, and atomically write the same
   content stamps to the concrete resource-root mirror or `dms.Descriptor`. Preserve
   `DocumentUuid`, identity stamps, domain columns, keys, authorization data, and deletion
   history.
7. Do not synthesize delete or key-change history. The advanced root/descriptor content
   mirror intentionally makes the live document visible as an update to Change Queries;
   descriptors and resources follow the same logical contract.
8. Leave existing cache rows in place. Their lower `ContentVersion` makes them ordinary
   cache-behind work after only the corrected projector writers start. Missing rows remain
   ordinary missing-row work. Do not directly modify `DocumentCache`, clear the
   cache-ahead latch, reset connector offsets, or create Kafka topics.
9. Emit machine-readable progress and a final report with selected, stamped, skipped-
   already-complete, missing/deleted, and failed counts. A partial failure remains
   resumable and requires the operator to keep the data store offline.
10. Add operator documentation showing preview, execute, resume, abort-while-offline,
    and verification workflows. Include all example scenarios above, distinguish this
    offline utility from ordinary eventual recovery, state that byte-changing equal-version
    publication is prohibited, and identify exact baseline certification and incompatible-
    contract cutover as deferred. Cross-link E19's sensitive-data disclosure response and
    state that the ordinary same-topic workflow must not be used when previously published
    bytes require purging.

## Acceptance Evidence

- PostgreSQL and SQL Server integration tests prove each selected current document gets a
  distinct new sequence-backed `ContentVersion`, an advanced `ContentLastModifiedAt`, and
  an exactly matching resource-root or descriptor mirror without domain or identity
  changes.
- Resource and descriptor tests prove the restamped rows appear through live Change Query
  update selection without false tombstone or key-change records.
- Scope tests cover all-documents, project/resource, an empty scope, documents deleted
  before their batch, and explicit UUID scope when implemented. A preview performs no
  writes.
- Resume tests interrupt after at least one committed batch and prove a resume with the
  original manifest does not allocate another version for completed rows. Mismatched source
  fingerprint, provider, scope, deployment identity, or manifest fails closed.
- Safety tests prove the command refuses to mutate without the explicit offline
  confirmation or while the cache-ahead latch is set, records the confirmation without
  presenting it as proof, and never starts or reopens application admission after timeout,
  process loss, or partial failure.
- Projection tests prove existing affected cache rows become behind, corrected projector
  output replaces them through the normal monotonic upsert, and an exact zero audit is
  required before completion.
- Strong-validator tests use fixtures whose corrected representation bytes would retain the
  original ETag and whose corrected composer would independently produce a different ETag.
  Both are restamped. After corrected materialization, the API ETag and `StreamEtag` differ
  because `ContentVersion` differs; a conditional GET using the old ETag does not return
  `304` for the corrected representation.
- CDC integration tests for corrections that do not require prior-record purging retain the
  binding, topic, partitioning, and connector offsets, publish affected documents at higher
  `contentVersion` values, and prove conforming consumers replace the prior state. The
  operation emits no cache-maintenance tombstones. Sensitive-data disclosure guidance
  prohibits this same-topic path and instead requires connector fencing, consumer ACL
  revocation, verified destructive retirement, and continued not-ready status.
- Audit output identifies the operation and outcome without credentials, sensitive tenant
  labels, or raw connection metadata.

## Out of Scope

- Automatically detecting whether two arbitrary application builds emit different bytes.
- A request-path or public administrative API for restamping.
- Ad hoc SQL instructions for direct stamp manipulation.
- A new representation epoch, Kafka ordering field, topic generation, or connector-offset
  reset.
- Kafka containment, topic destruction, or replacement-namespace bootstrap. E19 owns the
  destructive disclosure-response runbook; the replacement workflow remains deferred.
- A production cross-replica/external-writer admission gate or transaction drain.
- Certification of an exact CDC baseline after first-write admission.
- Ownership of incompatible stream-contract migration; a future cutover may still invoke
  this utility offline when it also includes a byte-changing representation correction for
  existing documents.
