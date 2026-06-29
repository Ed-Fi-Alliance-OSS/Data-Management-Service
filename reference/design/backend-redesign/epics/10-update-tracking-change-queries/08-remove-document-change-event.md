---
jira: DMS-1169
jira_url: https://edfi.atlassian.net/browse/DMS-1169
---

# Story: Remove `dms.DocumentChangeEvent`

## Description

The Change Queries redesign in `reference/design/backend-redesign/design-docs/change-queries.md`
retires the unified `dms.DocumentChangeEvent` journal in favor of three purpose-built mechanisms,
all of which are tracked by their own stories elsewhere in this epic:

- **Per-resource `tracked_changes_<schema>.<resource>` tables** (and the shared
  `tracked_changes_edfi.Descriptor`) carry their own `Id`, `ChangeVersion`, and `Old_*`/`New_*`
  identity values. They are populated by the existing `*_Stamp` triggers extended with
  `DocumentStamping.ChangeTracking` and back the `/deletes` and `/keyChanges` endpoints.
- A **`ContentVersion` / `ContentLastModifiedAt` mirror** on every concrete
  `StorageKind = RelationalTables` root and on `dms.Descriptor`, together with
  `IX_<Table>_ContentVersion` and the composite `IX_Descriptor_Discriminator_ContentVersion`,
  serves resource and descriptor `?minChangeVersion=X&maxChangeVersion=Y` filters as a single-table
  range seek (no join to `dms.Document`).
- **`dms.GetMaxChangeVersion()`** reads `dms.ChangeVersionSequence` directly for the
  `/availableChangeVersions` endpoint (DMS-1168).

With those mechanisms in place, `dms.DocumentChangeEvent` carries no remaining responsibility.
This story removes the table, its journaling triggers, and every test/fixture/manifest that
asserts their presence.

**Out of scope (handled by separate stories):**

- Updates to the normative design docs (`data-model.md` §1c, and `update-tracking.md` §"Journaling for
  Change Queries").

## Acceptance Criteria

- The PostgreSQL and SQL Server DDL emission pipeline no longer emits:
  - the `dms.DocumentChangeEvent` table,
  - `PK_DocumentChangeEvent`,
  - `FK_DocumentChangeEvent_Document` and `FK_DocumentChangeEvent_ResourceKey`,
  - `IX_DocumentChangeEvent_DocumentId` and `IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion`,
  - the PostgreSQL journaling trigger function and trigger on `dms.Document`,
  - the SQL Server journaling trigger on `dms.Document`.
- `DocumentChangeEvent` is removed and no code references it.
