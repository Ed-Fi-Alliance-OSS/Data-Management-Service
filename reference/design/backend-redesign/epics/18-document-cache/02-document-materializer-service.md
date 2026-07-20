---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Reusable Caller-Agnostic Document Materialization

## Design References

- [Cached document contract](../../../cdc-streaming.md#cached-document-contract)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Add a cache-projection materializer that reuses relational response reconstitution and
returns one coherent cache-row result for reconciliation, optional direct fill, and CDC
payload fixtures.

## Dependencies

- Depends on relational read/reconstitution and update-tracking metadata.
- Unblocks 18-03 and 18-05 and supplies realistic data to CDC contract/E2E tests.

## Deliverables

1. Define the materializer interface and result type from the canonical cached-document
   contract.
2. Reuse compiled read plans and reconstitution instead of adding another JSON builder.
3. Load resource identity/version metadata and validate embedded/column consistency
   before returning a writable result.
4. Report disappearance, reconstitution, and invariant failures without emitting a
   partial cache result.

## Acceptance Evidence

- Unit/integration tests cover every cache result field and metadata invariant.
- Shape tests cover nested arrays, reference links, and excluded authorization/profile
  data.
- Tests prove the materializer leaves served/stream `_etag` composition to its designated
  boundary.

## Out of Scope

- Reconciliation scheduling/backoff.
- Kafka envelope shaping.
- Readable-profile response projection.
