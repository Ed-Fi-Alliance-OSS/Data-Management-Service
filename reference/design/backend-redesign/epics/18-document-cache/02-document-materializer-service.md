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

- Depends on 18-00 plus relational read/reconstitution and update-tracking metadata.
- Unblocks 18-03 through 18-05 and supplies realistic data to CDC contract/E2E tests.

## Deliverables

1. Define the materializer interface and result type from the canonical cached-document
   contract.
2. Reuse compiled read plans and reconstitution instead of adding another JSON builder.
3. Compute `StreamEtag` through `IServedEtagComposer` using the row's `ContentVersion`,
   the selected mapping set's `EffectiveSchemaHash`, JSON format, no readable profile,
   the cached document's backend-defined link context, and identity content coding.
   Ordinary resources use the link-bearing context regardless of the API resource-link
   option; descriptors use their links-off context.
4. Load `DocumentUuid` and resource identity/version metadata from the canonical
   `dms.Document` row; callers do not supply an independent cache UUID.
5. After every hydration/result-set read completes, re-read the
   current source `(DocumentId, ContentVersion)` in a new current-visibility statement that
   does not reuse a repeatable/snapshot view fixed before hydration. Request no update/write
   lock and carry no lock acquired by the check into the cache transaction. Return a stale
   result with no writable cache row when the source disappeared or no longer matches the
   captured version. This optimistic check prevents mixed-version reconstitution but
   deliberately retains no commit-order fence after it succeeds; ordinary provider read
   locking may still block briefly when row-versioned reads are unavailable.
6. After the optimistic check succeeds, validate embedded/column/stream-ETag consistency
   and compose the writable result.
7. Retain the canonical provider-precision `LastModifiedAt` in the cache-row result while
   reusing the existing DMS whole-second UTC formatter for
   `DocumentJson._lastModifiedDate`; do not introduce a cache-specific timestamp format.
8. Report disappearance, stale, reconstitution, and invariant failures without emitting a
   partial cache result.
9. Supply a reusable CDC sizing fixture that runs a schema-valid maximum supported body
   through the real link-bearing materializer. The fixture exposes the resulting
   `DocumentJson` to story 19-05 so its final envelope and Kafka framing, rather than the
   incoming HTTP body length, establish and verify `maxRecordBytes`.

## Acceptance Evidence

- Unit/integration tests cover every cache result field and metadata invariant, including
  equality between the canonical, cache-column, and embedded document UUIDs and exact
  `StreamEtag` equality with the existing DMS composer.
- Fractional provider timestamp fixtures prove the cache-row result retains the canonical
  value, `DocumentJson._lastModifiedDate` discards the fraction without rounding, and
  formatting the former exactly reproduces the latter.
- Contract fixtures prove `StreamEtag` is produced by the current shared composer for the
  fixed stream context and remains coherent with `ContentVersion`, effective schema, and
  document link context. They treat the resulting bytes as opaque rather than freezing
  them independently of the composer.
- Shape tests cover nested arrays, reference links, and excluded authorization/profile
  data.
- The sizing fixture fills a test resource to the configured maximum supported DMS body,
  exercises worst-case reference-link injection for that resource, and proves the
  resulting `DocumentJson` is the input used by CDC's maximum-record boundary test. It
  does not claim that the HTTP request-body byte count is the final Kafka record size.
- Deterministically synchronized provider tests commit a source update during
  multi-result-set hydration and prove the final optimistic check returns a stale result,
  never a mixed document labeled with the captured version or an invariant failure. A
  companion test commits the update after the check and proves the coherent older result
  remains eligible for the monotonic upsert.
- Provider tests prove the final check observes current committed state rather than a
  hydration snapshot and requests no update/write source-row lock.
- Tests prove API reads ignore `StreamEtag` and continue composing the served `_etag` for
  their request-specific representation context.

## Out of Scope

- Reconciliation scheduling/backoff.
- Kafka envelope shaping.
- Readable-profile response projection.
