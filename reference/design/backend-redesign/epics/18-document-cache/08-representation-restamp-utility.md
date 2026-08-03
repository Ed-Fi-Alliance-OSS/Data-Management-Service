---
jira: DMS-1318
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add an Out-of-Band Representation Restamp Utility

## Design References

- **Offline byte-changing representation correction**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction
- **ETag strong-validator decision**: reference/adr-etag-from-content-version.md#etag-format-and-http-validator-semantics-rfc-9110
- **Topic and message compatibility**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes
- **Change Query stamping and mirrors**: reference/design/backend-redesign/design-docs/change-queries.md#concrete-resource-contentversion--contentlastmodifiedat-mirror

The referenced design sections define when and how representation restamping is safe. This
story is only the work package for implementing that utility.

## Outcome

Deliver the supported PostgreSQL and SQL Server administrative utility and its operator
workflow for the correction cases owned by the design.

## Dependencies

- Depends on E10 content-version allocation and mirror behavior.
- Depends on 18-00, 18-01, 18-02, 18-04, and 18-06 for schema, lifecycle validation,
  administrative serialization, materialization, reconciliation, and status verification.

## Implementation Scope

- Add the non-interactive administrative command and provider adapters.
- Add scope and explicit execution-mode selection, preview, confirmation, operation
  manifests, resumable execution, progress reporting, and final reports.
- Acquire the database-scoped projection administrative mutex through 18-04's shared
  exact-identity provider adapter for the complete utility run, validate the requested mode
  against durable state, persist it in the manifest, and require the durable state to
  remain eligible for that same mode on resume.
- Integrate canonical stamp and mirror updates with existing Change Query behavior.
- With a clear cache-ahead latch, use lifecycle `Tracking` as projection/publication mode:
  every restamp transaction automatically enqueues the new `ContentVersion` in the same
  transaction, and a failed enqueue rolls back the complete restamp batch.
- With a clear cache-ahead latch, allow lifecycle `Disabled` only as explicit canonical-only
  mode. It records no projection work, performs no queue-drain follow-up, and makes no cache
  or Kafka publication claim.
- Reject `Resetting`, `Rebuilding`, or any set `CacheAheadRecoveryRequired` latch before
  changing a stamp. A correction that requires cache or Kafka publication must select
  projection/publication mode.
- Add operator documentation and cross-links to E18 and E19 recovery procedures.

## Acceptance Evidence

- PostgreSQL and SQL Server integration tests cover mutex serialization and session loss;
  selection, stamping, mirrors, same-mode resumability, and reporting; `Tracking`
  transactional enqueue and queue-drain projection follow-up; `Disabled` canonical-only
  behavior; and rejection of transitional, mode-mismatched, or latched state from the
  referenced design sections.
- API, Change Query, strong-validator, and CDC integration fixtures cover the observable
  effects assigned to this utility.
- Documentation tests exercise preview, execution, resume, failure, and verification
  flows against the shipped command.

## Not Assigned to This Story

- Kafka containment, destructive retirement, and replacement-namespace work are assigned
  to E19 or remain deferred by the owning design.
- The utility does not own stream-contract migration.
