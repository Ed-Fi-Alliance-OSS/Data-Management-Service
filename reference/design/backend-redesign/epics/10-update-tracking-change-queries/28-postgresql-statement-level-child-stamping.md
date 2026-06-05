---
jira: DMS-1208
jira_url: https://edfi.atlassian.net/browse/DMS-1208
---

# Story: Deduplicate PostgreSQL Child and `_ext` Stamping by Affected Document

## Description

Update PostgreSQL `DbTriggerKind.DocumentStamping` trigger rendering for child, nested-child, and `_ext` tables so multi-row DML allocates one `dms.ChangeVersionSequence` value per distinct affected root document, not one value per affected source row.

The current PostgreSQL renderer emits row-level `FOR EACH ROW` stamping triggers. That preserves final mirror equality because the last row-level stamp wins, but it can over-allocate `ContentVersion` values when several changed child or extension rows share the same root `DocumentId`. SQL Server already avoids this shape because its statement-level trigger builds an `affectedDocs` workset from `inserted` / `deleted` and updates each distinct document once.

PostgreSQL should use an equivalent statement-level strategy, such as transition tables with `FOR EACH STATEMENT`, for non-root stamping paths whose `MirrorStampTargetTable` is the owning resource root rather than the source table. Root-table and descriptor stamping can remain row-level if that remains the simplest way to assign root `NEW.ContentVersion` / `NEW.ContentLastModifiedAt` values without regressing existing insert and update behavior.

## Acceptance Criteria

- PostgreSQL child, nested-child, and `_ext` `DocumentStamping` triggers no longer stamp `dms.Document` once per affected source row when multiple rows in the same statement point to the same root `DocumentId`.
- PostgreSQL non-root stamping builds an affected-document workset from statement-level row images, using transition tables or an equivalent deduping strategy.
- The affected-document workset contains one row per distinct affected root `DocumentId`.
- `INSERT`, `UPDATE`, and `DELETE` paths stamp each affected root document once per trigger execution when the source statement changes one or more representation-relevant rows.
- `UPDATE` paths include both old and new owning root `DocumentId` values when a row's root document locator changes.
- No-op updates whose stored-column values do not change still do not allocate a new `ContentVersion`.
- Stamp-only differences in mirror columns still do not cause recursive or redundant stamp activity.
- The mirror update uses the same `ContentVersion` and `ContentLastModifiedAt` returned from the `dms.Document` update and does not allocate a second sequence value.
- Root-table PostgreSQL stamping continues to assign root `ContentVersion` and `ContentLastModifiedAt` mirror values correctly on inserts and updates.
- PostgreSQL delete behavior remains compatible with root tombstone ordering: cascaded child, nested-child, and `_ext` rows must not advance the visible root delete watermark past the root tombstone's `ChangeVersion`.
- Generated PostgreSQL DDL fixture coverage proves the statement-level or deduping shape for:
  - a child collection table,
  - a nested-child collection table,
  - a collection-aligned `_ext` table,
  - a root `_ext` table.
- PostgreSQL integration coverage seeds at least one root document with multiple child rows, runs one multi-row child `UPDATE`, and proves:
  - `dms.GetMaxChangeVersion()` advances by one for that root document,
  - `dms.Document.ContentVersion` equals the root mirror `ContentVersion`,
  - `ContentLastModifiedAt` equals the root mirror `ContentLastModifiedAt`.
- PostgreSQL integration coverage repeats the same allocation-cardinality assertion for a collection-aligned `_ext` table.
- Cross-engine parity coverage proves PostgreSQL and SQL Server advance `ContentVersion` cardinality consistently for the same logical multi-row child or `_ext` update.
