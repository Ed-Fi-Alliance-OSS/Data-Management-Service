---
jira: Unassigned
---

# Story: Serve `/deletes` from Tracked-Change Tombstones

## Description

Implement resource and descriptor `/deletes` endpoints backed by tracked-change tombstone rows.

The endpoint returns deleted resource identifying values in the requested change-version window. It must hide tombstones for resources or descriptors that have been recreated with the same identifying values, including resources whose identity references descriptors that were themselves recreated.

The response contract must remain compatible with ODS.

## Acceptance Criteria

- Each regular resource with Change Query support can serve `GET /data/v3/{schema}/{resource}/deletes`.
- Each descriptor with Change Query support can serve `GET /data/v3/{schema}/{descriptor}/deletes`.
- The endpoint filters tracked-change rows to tombstones by requiring an appropriate `New_*` identity column to be null.
- The endpoint filters by `minChangeVersion` and `maxChangeVersion`.
- The endpoint supports `limit`, `offset`, and `totalCount`.
- Regular resource recreated-resource suppression anti-joins against the live table using identifying storage values, not `DocumentId`.
- Descriptor recreated-resource suppression anti-joins against `dms.Descriptor` using `Discriminator`, `Namespace`, and `CodeValue`.
- Resource suppression resolves descriptor identity references by joining current `dms.Descriptor` rows on stored `Namespace` and `CodeValue`, so recreated descriptors do not cause false delete results.
- Response `keyValues` use public field names from `queryFieldMapping`.
- Descriptor `/deletes` responses include public descriptor identity fields only.
- Cascading delete scenarios for abstract-resource families are covered, including a scenario comparable to ODS-4087.
- Tests cover regular resources, descriptors, recreated resources, recreated descriptors, pagination, totalCount, and both dialects.

## Dependencies

- `11-refkey-documentid-ordering.md`.
- `16-tracked-change-trigger-rendering.md`.
- `17-delete-by-id-tombstone-ordering.md`.
- `22-change-query-endpoint-foundation.md`.

## Out of Scope

- `ReadChanges` authorization, handled by `25-readchanges-authorization.md`.
- `/keyChanges`.
- Snapshot support.
