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

The linked design sections define when and how representation restamping is safe. This
story is only the work package for implementing that utility.

## Outcome

Deliver the supported PostgreSQL and SQL Server administrative utility and its operator
workflow for the correction cases owned by the design.

## Dependencies

- Depends on E10 content-version allocation and mirror behavior.
- Depends on 18-00, 18-02, 18-04, and 18-06 for schema, materialization,
  reconciliation, and status verification.

## Implementation Scope

- Add the non-interactive administrative command and provider adapters.
- Add scope selection, preview, confirmation, operation manifests, resumable execution,
  progress reporting, and final reports.
- Integrate canonical stamp and mirror updates with existing Change Query behavior.
- Add operator documentation and cross-links to E18 and E19 recovery procedures.

## Acceptance Evidence

- PostgreSQL and SQL Server integration tests cover selection, stamping, mirrors,
  resumability, safety checks, reporting, and projection follow-up from the referenced
  design sections.
- API, Change Query, strong-validator, and CDC integration fixtures cover the observable
  effects assigned to this utility.
- Documentation tests exercise preview, execution, resume, failure, and verification
  flows against the shipped command.

## Not Assigned to This Story

- Kafka containment, destructive retirement, and replacement-namespace work are assigned
  to E19 or remain deferred by the owning design.
- The utility does not own stream-contract migration.
