---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1240
---

# Story: Add the Relational `DocumentState` Kafka Connect Transform

## Design References

- [Connector transform pipeline](../../../cdc-streaming.md#connector-transform-pipeline)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Completed generic expand-JSON transform](../../design-docs/expandjsonsmt-replacement.md)

## Outcome

Implement and publish the DMS-specific `DocumentState` transform in
`Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect` as an explicit new contract adapter. This work is
separate from DMS-1240's completed generic `sourceFields` expander.

## Dependencies

- Depends on the DMS-1245 source-operation and topic/message contracts.
- Unblocks runnable connector templates in 19-02 and their fast contract tests in 19-05.
- Does not modify or replace the generic transform delivered by DMS-1240.

## Deliverables

1. Add the public `org.edfi.kafka.connect.transforms.DocumentState` transform to the
   Ed-Fi Kafka Connect plugin with a bytecode target compatible with the pinned runtime.
2. Consume schema-backed raw Debezium records from `dms.DocumentCache`, `dms.Document`,
   and the internal `dms.CdcHeartbeat`, plus Debezium heartbeat records, classifying source
   table and operation before discarding the envelope.
3. Emit exactly one final public upsert, final public tombstone, or no record according to
   the projector/source decision.
4. Normalize the `DocumentUuid` key, parse `DocumentJson` directly, convert provider
   timestamps to the existing DMS whole-second UTC representation by truncating rather
   than rounding fractional seconds, copy opaque `StreamEtag` to `document._etag`, build the
   public envelope, validate embedded metadata, and route the retained record.
5. Expose only `provider` and `target.topic` as contract configuration. Keep source
   tables, operations, source columns, and v1 public fields fixed behavior rather than a
   mapping language.
6. Suppress duplicate automatic Debezium tombstones, intentionally drop every heartbeat
   record without routing it to the public topic, and fail malformed retained document
   records rather than publishing partial state.
7. Publish the transform in the Ed-Fi Kafka Connect image consumed by 19-02; do not remove
   or redefine `ExpandJson$Value`.

## Acceptance Evidence

- JUnit fixtures begin with realistic PostgreSQL and SQL Server Debezium records and cover
  every retained and dropped source operation, including heartbeat-table snapshots and
  updates and connector-generated heartbeat records.
- Serialized-record tests assert final topic, lowercase string key, exact public value,
  structured `document`, opaque ETag copying, internal-field removal, and one Kafka-null
  tombstone per authoritative delete.
- SQL Server fixtures cover `io.debezium.time.NanoTimestamp` through seven fractional
  digits and prove every value within a second truncates to the same whole-second UTC
  string without rounding into the next second. They reject fractional or raw numeric
  public timestamps and embedded timestamp disagreement.
- Invalid JSON, missing required fields, unexpected source schemas or logical types, and
  key/metadata disagreement fail closed.
- Plugin-loading tests pass against the pinned Connect image and a Kafka Connect 4 test
  runtime with the chosen compatible bytecode target.
- Regression tests prove the completed generic `ExpandJson$Value` transform and its
  `sourceFields` contract remain available and unchanged.

## Out of Scope

- Connector JSON generation and registration.
- DMS projector/materializer implementation.
- API-driven Kafka E2E scenarios.
- Changes to DMS-1240 or removal of the generic expand-JSON transform.
