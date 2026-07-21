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
- The broker-backed poison-record readiness scenario also depends on the status and
  registration behavior from 17-00 and 17-03.

## Deliverables

1. Add canonical PostgreSQL and SQL Server Debezium fixtures for every source operation.
2. Exercise classification, tombstone conversion/suppression, key and value shaping,
   JSON expansion, opaque `StreamEtag` copying, and topic routing.
3. Include SQL Server `datetime2(7)` fixtures represented as
   `io.debezium.time.NanoTimestamp` and exercise the Ed-Fi shaping SMT's UTC ISO string
   conversion at zero through seven significant fractional digits, with every fraction
   deliberately truncated to the existing DMS whole-second representation.
4. Add realistic nested/reference-link payload fixtures from the shared materializer.
5. Add real-provider delete-key and routed-topic ordering coverage.
6. Pin the v1 key, public fields/types, stream selectors, tombstone behavior, document
   semantics, and served-ETag source/copy relationship without independently freezing the
   opaque ETag bytes.
7. Exercise consumer ordering for higher, lower, and equal `contentVersion` records and
   verify that neither the topic partition count nor the binding's
   `partitionerAlgorithm` token can change within a binding generation.
8. Pin the delivery and monotonic-lag contract: raw at-least-once delivery may contain
   duplicates or a lower-version replay after a higher version, while conforming
   consumer-applied non-null upsert state remains monotonic. A consumer that has not yet
   seen a newer canonical version may temporarily retain an older projection, then
   converges when the newer projection arrives. Across a tombstone, an older replayed
   upsert may temporarily restore state until the replayed tombstone arrives.
9. Add a broker-backed producer retry scenario using the registered v1 connector and its
   pinned idempotence, acknowledgement, retry, and maximum-in-flight settings. Inject a
   retriable send failure after a cache upsert is submitted and before the later canonical
   delete is sent.
10. Add one broker-backed poison-record scenario using a registered connector with the
    required `errors.tolerance=none`. Supply a malformed retained record that reaches the
    `DocumentState` transform, rather than an operation the transform intentionally
    drops.
11. Add one maximum-record fixture sourced from the shared DMS materializer. It must use
    the maximum supported link-bearing `DocumentJson`, add the public envelope, and run
    through the real transform, converters, partitioner, and pinned producer. Publish,
    replicate, and consume it with producer, topic, broker, replica-fetch, and consumer
    limits set to the binding's exact `maxRecordBytes`; add an over-budget variant.
12. Add provider heartbeat fixtures and a broker-backed idle-source scenario. Prove the
    heartbeat table and Debezium heartbeat records emit no public document record, while
    their committed source offsets advance only after earlier retained records complete
    and expose the fields required by the provider barrier adapter.

## Acceptance Evidence

- Every retained fixture produces exactly the record required by the topic/message ADR.
- Every dropped fixture produces no public record, including automatic extra tombstones.
- Regression tests catch wrapper, quoting, escaped-JSON, timestamp, metadata, internal
  field, missing or incorrect `document._etag`, unexpected envelope `etag`, and Kafka-null
  contract violations.
- SQL Server regression tests reject raw numeric or fractional public timestamps, rounding
  into the next second, a missing trailing `Z`, unexpected temporal logical types, and any
  difference between `lastModifiedAt` and `document._lastModifiedDate`.
- Materializer fixtures use ETags produced by the shared DMS composer; connector tests
  prove `document._etag` is an exact copy rather than a Java-derived value.
- Fixtures obtain `StreamEtag` from the current DMS composer and fail if the transform
  alters it, publishes it outside `document._etag`, or publishes metadata inconsistent
  with it; they do not fail merely because a conforming composer correction changes the
  opaque value.
- Ordering tests prove a higher `contentVersion` replaces, a lower version is ignored,
  and the later partition offset replaces an equal version without a public projection
  generation field. They prove both that a lower replay received after a higher version
  does not regress applied state and that an older projection received before the newer
  projection is accepted temporarily and then replaced, as ordinary monotonic lag.
- Delete-boundary replay tests prove a previously emitted upsert received after a tombstone
  may temporarily restore state and that the subsequent replayed tombstone deletes it
  again; they do not assert monotonic applied state across the tombstone.
- Repair tests prove a cache clear/rebuild republishes corrected equal-version values into
  the same topic, while incompatible key/field/type/delete-contract changes use a new
  topic suffix and matching `contractVersion`.
- Provider tests cover canonical deletion without a cache row, cache rebuild cleanup,
  and same-key routed ordering.
- The producer retry test proves the routed partition contains the committed cache upsert
  before its canonical tombstone despite the retriable send failure, and that connector
  catch-up leaves the document deleted rather than resurrected.
- The poison-record test proves no public record is emitted, the connector task enters a
  failed state instead of skipping the malformed retained record, and deployment-owned
  combined readiness remains false even if offset or lag observations would otherwise
  appear caught up.
- The maximum-record test proves the supported fully materialized link-bearing envelope
  fits within the one-record byte budget and reaches a consumer without relying on
  compression. The over-budget variant emits no partial record, fails the connector task,
  and keeps combined readiness false.
- The idle-source test captures a barrier after a zero audit and proves `RUNNING` plus
  acceptable lag remains not ready below it, then observes the action-query heartbeat and
  passes only when the committed PostgreSQL `lsn_proc` or SQL Server
  commit/change/event-serial position reaches it. No heartbeat appears in the public
  topic.

## Out of Scope

- Full API-driven E2E scenarios.
- Projector reconciliation/completeness testing.
- Kafka ACL testing.
