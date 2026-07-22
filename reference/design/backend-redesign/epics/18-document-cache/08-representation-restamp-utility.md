---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add an Out-of-Band Representation Restamp Utility

## Design References

- [Byte-changing representation correction](../../../cdc-streaming.md#byte-changing-representation-correction)
- [ETag strong-validator decision](../../../../adr-etag-from-content-version.md#etag-format-and-http-validator-semantics-rfc-9110)
- [Topic and message compatibility](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes)
- [Change Query stamping and mirrors](../../design-docs/change-queries.md#concrete-resource-contentversion--contentlastmodifiedat-mirror)

## Outcome

Provide a supported PostgreSQL and SQL Server utility for the rare case in which corrected
API or CDC representation bytes would otherwise reuse a strong ETag. The utility runs
outside ordinary DMS request processing during a guarded maintenance window, advances the
existing canonical content stamps for an explicit document scope, and lets ordinary
projection and streaming publish corrected higher-version state.

This is a separate implementation story. General DocumentCache and CDC runbooks invoke
the utility but must not approximate it with hand-written database updates.

## Dependencies

- Depends on E10 content-version allocation, root/descriptor stamp mirrors, and Change
  Query selection semantics.
- Depends on 18-00, 18-02, 18-04, and 18-06 for schema, corrected materialization,
  reconciliation, and readiness verification.
- CDC verification consumes 19-00 readiness/barrier behavior but the utility remains
  usable for API-only or cache-only deployments.

## Example Use Scenarios

The utility is necessary when corrected bytes would retain the old ETag, including:

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
   affected documents' stamps.
5. A DMS upgrade fixes a materializer/composer interaction so `DocumentJson` changes while
   the composed `StreamEtag` would remain the same for the fixed stream context.

The utility is not necessary for:

- an ordinary resource or descriptor mutation that already advances `ContentVersion`;
- a schema or profile-definition change whose `schemaEpoch` change gives every changed
  representation a different strong ETag;
- an equal-version compatible correction for which comparison proves every changed public
  representation already has a different corrected `StreamEtag`; or
- an incompatible key, field/type, delete, or document-contract change merely because it
  needs a new topic. The cutover is separate; if it also changes representation bytes that
  would reuse a strong ETag, it invokes this utility as an additional step.

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
5. Integrate with deployment-owned maintenance admission. Before any stamp update, require
   evidence that affected API reads and canonical mutations have stopped admission and
   drained, the CDC target is not ready when present, old application/materializer
   instances are stopped or fenced, projector/direct-fill writers are stopped, and the
   durable cache-ahead latch is clear. A latched target uses its existing explicit recovery
   procedure first; the restamp utility must not clear or bypass the latch. Keep the gate
   closed through corrected projection and final verification.
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
   resumable with API admission closed and readiness false.
10. Add operator documentation showing preview, execute, resume, abort-with-gate-closed,
    and verification workflows. Include all example scenarios above and explain how to
    decide between equal-version repair, this restamp, and an incompatible-contract
    cutover.

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
- Maintenance tests prove the command refuses to mutate when request drain or writer
  fencing is absent or unverifiable or the cache-ahead latch is set, and that timeout,
  controller loss, or partial failure never reopens admission automatically.
- Projection tests prove existing affected cache rows become behind, corrected projector
  output replaces them through the normal monotonic upsert, and an exact zero audit is
  required before completion.
- Strong-validator tests use a fixture whose corrected representation bytes would have
  retained the original ETag. After restamp and corrected materialization, both the API
  ETag and `StreamEtag` differ because `ContentVersion` differs; a conditional GET using
  the old ETag does not return `304` for the corrected representation.
- CDC integration tests retain the binding, topic, partitioning, and connector offsets,
  publish affected documents at higher `contentVersion` values, and prove conforming
  consumers replace the prior state. The operation emits no cache-maintenance tombstones.
- Audit output identifies the operation and outcome without credentials, sensitive tenant
  labels, or raw connection metadata.

## Out of Scope

- Automatically detecting whether two arbitrary application builds emit different bytes.
- A request-path or public administrative API for restamping.
- Ad hoc SQL instructions for direct stamp manipulation.
- A new representation epoch, Kafka ordering field, topic generation, or connector-offset
  reset.
- Ownership of incompatible stream-contract migration; its cutover may still invoke this
  utility when its changed representation bytes would otherwise reuse a strong ETag.
