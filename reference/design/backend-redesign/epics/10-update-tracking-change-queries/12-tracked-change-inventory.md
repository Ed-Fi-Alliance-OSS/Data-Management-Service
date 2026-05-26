---
jira: DMS-1175
jira_url: https://edfi.atlassian.net/browse/DMS-1175
---

# Story: Derive Tracked-Change Inventory

## Description

Derive Change Query database semantics into the shared `DerivedRelationalModelSet` instead of re-deriving them inside DDL emitters or runtime SQL planners.

The inventory describes per-resource tracked-change tables, the shared descriptor tracked-change table, tracked old/new value columns, descriptor and person joins, and the `TriggerKindParameters.ChangeTracking` attachment on document-stamping triggers.

This story owns semantic derivation only. Dialect renderers and endpoint planners consume the derived inventory mechanically.

## Acceptance Criteria

- `TrackedChangeTableInfo` is derived for every concrete resource that needs a tracked-change table.
- A shared descriptor `TrackedChangeTableInfo` is derived for descriptor resources stored in `dms.Descriptor`.
- Concrete abstract resources, such as `School`, `LocalEducationAgency`, and `OrganizationDepartment`, get their own tracked-change table inventory instead of sharing an abstract parent table.
- Each tracked-change table includes system-column metadata for `Id`, `ChangeVersion`, and `CreatedAt`.
- The shared descriptor tracked-change table includes `Discriminator`.
- `TrackedChangeColumnInfo` includes old/new column pairs for every required identity and securable-element path.
- Old and new nullability are represented separately as `IsOldColumnNullable` and `IsNewColumnNullable`.
- Descriptor reference paths materialize `Namespace` and `CodeValue` columns and reference a table-level `TrackedChangeDescriptorJoinInfo`.
- Student, Contact, and Staff securable-element paths materialize person `DocumentId` columns and reference a table-level `TrackedChangePersonJoinInfo`.
- Key-unification paths use canonical storage columns and de-duplicate repeated canonical columns.
- `TriggerKindParameters.ChangeTracking` is attached to the affected `DbTriggerKind.DocumentStamping` entries.
- Key-change row derivation uses the owning `DbTriggerInfo.IdentityProjectionColumns` workset rather than SQL target-list checks.
- Manifest or fixture output exposes tracked-change tables, value columns, descriptor joins, person joins, and trigger attachments.

## Out of Scope

- Rendering tracked-change tables.
- Rendering tracked-change trigger SQL.
- Deriving `ReadChangesAuthorizationViewInfo`, which is handled by `13-readchanges-authorization-inventory.md`.
