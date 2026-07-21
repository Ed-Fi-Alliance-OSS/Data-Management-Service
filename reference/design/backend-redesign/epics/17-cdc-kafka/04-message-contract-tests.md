---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Kafka Message and Source-Routing Contract Tests

## Design References

- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Connector transform pipeline](../../../cdc-streaming.md#connector-transform-pipeline)
- [Verification](../../../cdc-streaming.md#verification)

## Outcome

Add fast serialized-record and provider integration tests that pin the v1 public contract
without requiring an API E2E path for every source operation.

## Dependencies

- Depends on the transform artifact and unit-fixture contract from 17-02a and the provider
  source setup from 17-01.

## Deliverables

1. Add canonical PostgreSQL and SQL Server Debezium fixtures for every source operation.
2. Exercise classification, tombstone conversion/suppression, key and value shaping,
   JSON expansion, opaque `StreamEtag` copying, and topic routing.
3. Include SQL Server `datetime2(7)` fixtures represented as
   `io.debezium.time.NanoTimestamp` and exercise the Ed-Fi shaping SMT's UTC ISO string
   conversion at zero through seven significant fractional digits.
4. Add realistic nested/reference-link payload fixtures from the shared materializer.
5. Add real-provider delete-key and routed-topic ordering coverage.
6. Pin the v1 key, public fields/types, stream selectors, tombstone behavior, document
   semantics, and served-ETag source/copy relationship without independently freezing the
   opaque ETag bytes.
7. Exercise consumer ordering for higher, lower, and equal `contentVersion` records and
   verify that the topic partition count cannot change within a binding generation.
8. Pin the monotonic-lag contract: a consumer that has not yet seen a newer canonical
   version may temporarily retain an older projection, then converges when the newer
   projection arrives.

## Acceptance Evidence

- Every retained fixture produces exactly the record required by the topic/message ADR.
- Every dropped fixture produces no public record, including automatic extra tombstones.
- Regression tests catch wrapper, quoting, escaped-JSON, timestamp, metadata, internal
  field, missing or incorrect `document._etag`, unexpected envelope `etag`, and Kafka-null
  contract violations.
- SQL Server regression tests reject raw numeric timestamps, precision loss, a missing
  trailing `Z`, more than seven fractional digits, unexpected temporal logical types,
  and any difference between `lastModifiedAt` and `document._lastModifiedDate`.
- Materializer fixtures use ETags produced by the shared DMS composer; connector tests
  prove `document._etag` is an exact copy rather than a Java-derived value.
- Fixtures obtain `StreamEtag` from the current DMS composer and fail if the transform
  alters it, publishes it outside `document._etag`, or publishes metadata inconsistent
  with it; they do not fail merely because a conforming composer correction changes the
  opaque value.
- Ordering tests prove a higher `contentVersion` replaces, a lower version is ignored,
  and the later partition offset replaces an equal version without a public projection
  generation field. They also prove that an older projection received before the newer
  projection is accepted temporarily and then replaced, as ordinary monotonic lag.
- Repair tests prove a cache clear/rebuild republishes corrected equal-version values into
  the same topic, while incompatible key/field/type/delete-contract changes use a new
  topic suffix and matching `contractVersion`.
- Provider tests cover canonical deletion without a cache row, cache rebuild cleanup,
  and same-key routed ordering.

## Out of Scope

- Full API-driven E2E scenarios.
- Projector reconciliation/completeness testing.
- Kafka ACL testing.
