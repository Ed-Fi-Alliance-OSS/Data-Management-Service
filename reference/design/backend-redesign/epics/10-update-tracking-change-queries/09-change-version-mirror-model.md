---
jira: Unassigned
---

# Story: Derive `ContentVersion` and `ContentLastModifiedAt` Mirrors

## Description

Derive the concrete-table mirror required by `reference/design/backend-redesign/design-docs/change-queries.md`.

Every `ConcreteResourceModel` whose `StorageKind = RelationalTables` gets two synthesized root-table columns:

- `ContentVersion`, with `ColumnKind.MirroredContentVersion`
- `ContentLastModifiedAt`, with `ColumnKind.MirroredContentLastModifiedAt`

The shared `dms.Descriptor` table gets the same two columns through the core DDL pass because descriptor resources use `StorageKind = SharedDescriptorTable`.

The mirror supports live resource and descriptor `minChangeVersion` / `maxChangeVersion` filters without joining `dms.Document`, and gives SQL-side integrators the same ODS-shaped row-local watermark that many existing tools expect.

## Acceptance Criteria

- Every `StorageKind = RelationalTables` concrete resource root table in every project schema has synthesized `ContentVersion` and `ContentLastModifiedAt` columns in its `DbTableModel`.
- The synthesized root columns use `ColumnKind.MirroredContentVersion` and `ColumnKind.MirroredContentLastModifiedAt`, with no `SourceJsonPath` and no `TargetResource`.
- Child tables, collection tables, `_ext` tables, and abstract-resource union views do not get independent mirror columns.
- The `dms.Descriptor` core DDL model includes `ContentVersion` and `ContentLastModifiedAt` for descriptor resources.
- `DeriveIndexInventoryPass` adds one `IX_<Table>_ContentVersion` single-column index per mirrored concrete resource root table.
- The `dms.Descriptor` index inventory includes `IX_Descriptor_Discriminator_ContentVersion` with key columns ordered as `Discriminator`, then `ContentVersion`.
- `DbTriggerInfo` for every `Kind = DocumentStamping` entry has a non-null `MirrorStampTargetTable`.
- `MirrorStampTargetTable` points to:
  - the source table for root-table triggers,
  - the owning resource root table for child and `_ext` triggers,
  - `dms.Descriptor` for the descriptor trigger.
- `TableWritePlan.ColumnBindings` excludes the mirror column kinds from client-writable projections.
- Fixture or manifest coverage proves mirrors and indexes are emitted for core resources, extension-project resources, and descriptors.

## Dependencies

- Existing `00-token-stamping.md` for the document-stamping trigger inventory.
- Existing `06-descriptor-stamping.md` for the descriptor trigger entry.
- `10-mirror-stamping-triggers.md` consumes `MirrorStampTargetTable`.

## Out of Scope

- Runtime `minChangeVersion` / `maxChangeVersion` query planning.
- Populating mirror values from triggers.
- Tracking deletes or key changes.
