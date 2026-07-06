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
  - expanded structured `document`,
  - no public `DocumentId`,
  - no public `ComputedAt`.
- Contract tests cover update records and assert consumers can use `contentVersion` as a stale-write guard.
- Contract tests cover delete tombstones:
  - same `DocumentUuid` key,
  - same instance document topic,
  - null value,
  - no `deleted=true` envelope.
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
6. Add regression coverage that `DocumentJson` is published as structured JSON, not an escaped string.

## Out of Scope

- API-level E2E create/update/delete scenarios.
- Projector lag and backfill testing.
- Kafka ACL testing.
