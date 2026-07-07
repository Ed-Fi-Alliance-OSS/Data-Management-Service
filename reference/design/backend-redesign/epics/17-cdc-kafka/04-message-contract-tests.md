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
  - key as lowercase `DocumentUuid`,
  - topic matching `<topic-prefix>.instance.<instance-key>.documents.v1`,
  - `contractVersion = 1`,
  - lower-camel metadata envelope,
  - `etag` as a 44-character base64-encoded `SHA-256` value, not a 64-character hex digest,
  - expanded structured `document`,
  - no public `DocumentId`,
  - no public `ComputedAt`.
- Contract tests cover update records and assert consumers can use `contentVersion` as a stale-write guard.
- Contract tests cover delete tombstones:
  - same `DocumentUuid` key,
  - same instance document topic,
  - null value,
  - no `deleted=true` envelope.
- Source-level or integration contract coverage proves a CDC-mode delete is driven by a `dms.DocumentCache` row
  delete, including the case where the cache row had to be synchronously materialized before delete.
- Tests cover PostgreSQL and SQL Server connector/template differences where they affect keys, tombstones, or
  topic routing.
- Tests include at least one realistic Ed-Fi document with nested arrays and a reference `link` subtree so JSON
  expansion and caller-agnostic projection shape are exercised.
- Tests assert that readable-profile filtering and authorization metadata are absent from the stream contract.

## Tasks

1. Add canonical fixture input records representing Debezium `dms.DocumentCache` create, update, snapshot, and
   delete events.
2. Add expected Kafka key/value/topic fixture outputs for the v1 contract.
3. Exercise the transform pipeline with PostgreSQL-shaped input records.
4. Exercise the transform pipeline with SQL Server-shaped input records.
5. Add regression coverage that null tombstone values pass through value-shaping transforms unchanged.
6. Add provider smoke or fixture coverage for the pre-delete materialization path that creates the cache source
   row before `ON DELETE CASCADE` removes it.
7. Add regression coverage that `etag` preserves the DMS API base64 `_etag` string from `DocumentCache.Etag`.
8. Add regression coverage that `DocumentJson` is published as structured JSON, not an escaped string.

## Out of Scope

- API-level E2E create/update/delete scenarios.
- Full projector lag and backfill testing beyond the source-row delete contract.
- Kafka ACL testing.
