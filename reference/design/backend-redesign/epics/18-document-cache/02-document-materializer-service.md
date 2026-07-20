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
the shared DMS served-ETag composer, returning one coherent cache-row result for
reconciliation, optional direct fill, and CDC payload fixtures.

## Dependencies

- Depends on relational read/reconstitution and update-tracking metadata.
- Unblocks 18-03 and 18-05 and supplies realistic data to CDC contract/E2E tests.

## Deliverables

1. Define the materializer interface and result type from the canonical cached-document
   contract.
2. Reuse compiled read plans and reconstitution instead of adding another JSON builder.
3. Compute `StreamEtag` through `IServedEtagComposer` using the row's `ContentVersion`,
   the selected mapping set's `EffectiveSchemaHash`, JSON format, no readable profile,
   the cached document's backend-defined link context, and identity content coding.
   Ordinary resources use the link-bearing context regardless of the API resource-link
   option; descriptors use their links-off context.
4. Load resource identity/version metadata and validate embedded/column/stream-ETag
   consistency before returning a writable result.
5. Report disappearance, reconstitution, and invariant failures without emitting a
   partial cache result.

## Acceptance Evidence

- Unit/integration tests cover every cache result field and metadata invariant, including
  exact `StreamEtag` equality with the existing DMS composer.
- Shape tests cover nested arrays, reference links, and excluded authorization/profile
  data.
- Tests prove API reads ignore `StreamEtag` and continue composing the served `_etag` for
  their request-specific representation context.

## Out of Scope

- Reconciliation scheduling/backoff.
- Kafka envelope shaping.
- Readable-profile response projection.
