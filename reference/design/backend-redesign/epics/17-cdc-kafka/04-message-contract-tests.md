---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Kafka Message Contract Tests

## Description

Add tests that lock down the relational CDC v1 Kafka key, value, topic, and tombstone contract independent of
full API E2E scenarios.

These tests should catch connector/template/transform regressions quickly and should not require a complete DMS
write path for every case.

## Acceptance Criteria

- Contract tests cover create/upsert records with:
  - key bytes as UTF-8 lowercase `DocumentUuid` text, not a JSON string,
  - topic matching `<topic-prefix>.instance.<instance-key>.documents.v1`,
  - value bytes as a UTF-8 JSON object produced without a Kafka Connect `schema` / `payload` wrapper,
  - `contractVersion = 1`,
  - lower-camel metadata envelope,
  - `etag` as an opaque DMS API `_etag` value derived from `contentVersion` and the Kafka
    document-state `variantKey`,
  - `lastModifiedAt` as a UTC RFC 3339 / ISO-8601 string with trailing `Z`,
  - expanded structured `document`,
  - no public `DocumentId`,
  - no public `ComputedAt`.
- Contract tests cover update records and assert consumers can use `contentVersion` as a stale-write guard.
- Contract tests cover delete tombstones:
  - same UTF-8 `DocumentUuid` key bytes,
  - same instance document topic,
  - Kafka record-level null value,
  - no JSON `null` value,
  - no `deleted=true` envelope.
- Source-level or integration contract coverage proves a CDC-mode delete is driven by a `dms.DocumentCache` row
  delete, including the case where the cache row had to be synchronously materialized before delete.
- Tests cover PostgreSQL and SQL Server connector/template differences where they affect keys, tombstones, or
  topic routing.
- Tests include at least one realistic Ed-Fi document with nested arrays and a reference `link` subtree so JSON
  expansion and caller-agnostic projection shape are exercised.
- Tests assert that readable-profile filtering and authorization metadata are absent from the stream contract.
- Tests assert that envelope `documentUuid`, `etag`, and `lastModifiedAt` match embedded `document.id`,
  `document._etag`, and `document._lastModifiedDate`.

## Tasks

1. Add canonical fixture input records representing Debezium `dms.DocumentCache` create, update, snapshot, and
   delete events.
2. Add expected Kafka key/value/topic fixture outputs for the v1 contract.
3. Exercise the transform pipeline with PostgreSQL-shaped input records.
4. Exercise the transform pipeline with SQL Server-shaped input records.
5. Add regression coverage that null tombstone values pass through value-shaping transforms unchanged.
6. Add provider smoke or fixture coverage for the pre-delete materialization path that creates the cache source
   row before `ON DELETE CASCADE` removes it.
7. Add regression coverage that `etag` is derived from `contentVersion` and the Kafka document-state
   `variantKey`, not from a `DocumentCache.Etag` column.
8. Add regression coverage that `DocumentJson` is published as structured JSON, not an escaped string.
9. Add regression coverage that envelope metadata matches embedded `DocumentJson` server metadata.
10. Add regression coverage that schema wrappers, JSON-quoted keys, Debezium envelopes, and Avro/Protobuf-style
   schema-registry payloads are not part of the v1 public topic.

## Out of Scope

- API-level E2E create/update/delete scenarios.
- Full projector lag and backfill testing beyond the source-row delete contract.
- Kafka ACL testing.
