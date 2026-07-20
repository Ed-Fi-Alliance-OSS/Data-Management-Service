---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Kafka Message and Source-Routing Contract Tests

## Description

Add fast tests for the relational CDC v1 key, value, source-operation filtering, topic,
tombstone, and ordering contract without requiring a complete API E2E path for every
case.

## Acceptance Criteria

- Contract tests cover `dms.DocumentCache` create/update/snapshot inputs and assert:
  - key bytes are UTF-8 lowercase `DocumentUuid` text, not JSON,
  - topic matches `<topic-prefix>.instance.<instance-key>.documents.v1`,
  - value is a UTF-8 JSON object without a Connect schema wrapper,
  - `contractVersion = 1`, lower-camel metadata, opaque API `etag`, UTC
    `lastModifiedAt`, and expanded structured `document`,
  - no public `DocumentId` or `ComputedAt`,
  - envelope metadata matches embedded document metadata.
- Contract tests cover `dms.Document` delete input and assert exactly one output:
  - the same UTF-8 `DocumentUuid` key,
  - the same routed topic,
  - Kafka record-level null value,
  - no JSON `null` and no `deleted=true` envelope.
- Contract tests assert no output for:
  - `dms.DocumentCache` delete/truncate and its automatic tombstone if present,
  - `dms.Document` create, update, snapshot/read, and any automatic extra tombstone.
- PostgreSQL fixtures verify a `dms.Document` delete includes the custom key under the
  required replica identity; SQL Server fixtures verify the equivalent CDC key.
- Provider integration coverage proves same-key ordering through the routed topic: a
  cache upsert committed before a canonical document delete is consumed before its
  tombstone.
- Tests cover a canonical delete when no cache row exists and prove it still emits the
  tombstone.
- Tests cover cache truncation/rebuild cleanup and prove it emits no tombstones.
- Tests include a realistic nested Ed-Fi document with reference links and assert absence
  of readable-profile/authorization metadata.
- Tests assert `etag` is derived from `contentVersion` and the Kafka variant key, not a
  cache `Etag` column.

## Tasks

1. Add canonical Debezium fixtures for cache `c/u/r/d` and document `c/u/r/d` records for
   both providers.
2. Add expected retained/dropped Kafka output fixtures.
3. Exercise source-aware filtering, delete-to-tombstone conversion, key simplification,
   value shaping, JSON expansion, and topic routing.
4. Add regression coverage for duplicate tombstone suppression and cache-delete dropping.
5. Add serialized-record tests for schema wrappers, quoted keys, escaped document JSON,
   timestamp format, and Kafka null semantics.
6. Add provider integration tests for missing-cache delete and same-key routed ordering.

## Out of Scope

- Full API-driven E2E scenarios.
- Projector lag/backfill tests.
- Kafka ACL testing.
