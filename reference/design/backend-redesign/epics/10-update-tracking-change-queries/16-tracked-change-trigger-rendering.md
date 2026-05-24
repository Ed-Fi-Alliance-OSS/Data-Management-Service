---
jira: Unassigned
---

# Story: Populate Tracked-Change Tables from Stamping Triggers

## Description

Extend the existing `DbTriggerKind.DocumentStamping` render path to insert tracked-change rows when a resource is deleted or when its identifying values change.

The renderers consume `TriggerKindParameters.ChangeTracking` and the associated `TrackedChangeTableInfo`. They must not re-derive tracked-change columns, descriptor joins, person joins, or key-change predicates from SQL text.

Deletes insert tombstones with old values populated and new values null. Identity changes insert key-change rows with old and new values populated. The row's `ChangeVersion` must be the `dms.Document.ContentVersion` produced by the same trigger fire.

## Acceptance Criteria

- PostgreSQL and SQL Server document-stamping trigger renderers consume `TriggerKindParameters.ChangeTracking`.
- Delete branches insert tombstones into the correct `tracked_changes_*` table.
- Update branches insert key-change rows only when the owning `DbTriggerInfo.IdentityProjectionColumns` old/new workset changes.
- Key-change detection uses null-safe value comparisons and key-unification presence-gated canonical expressions where applicable.
- Trigger renderers do not use SQL Server `UPDATE(column)`, PostgreSQL `UPDATE OF`, or equivalent target-list checks for key-change row eligibility.
- Descriptor reference values are materialized through `TrackedChangeDescriptorJoinInfo` as `Namespace` and `CodeValue`.
- Person securable-element values are materialized through `TrackedChangePersonJoinInfo` as person `DocumentId` values.
- Tombstones and key-change rows store `dms.Document.DocumentUuid` in `Id`.
- Tombstones and key-change rows store the stamped `dms.Document.ContentVersion` in `ChangeVersion`.
- Descriptor resources write to `tracked_changes_edfi.Descriptor` with the correct `Discriminator`.
- Concrete abstract resources write to their own tracked-change tables.
- Tests cover deletes, identity changes, cascading key changes, descriptor paths, people securable paths, key-unification paths, and multi-row updates in both dialects.

## Dependencies

- `10-mirror-stamping-triggers.md`.
- `12-tracked-change-inventory.md`.
- `14-tracked-change-table-ddl.md`.

## Out of Scope

- Runtime `/deletes` or `/keyChanges` endpoint queries.
- Delete-by-id ordering changes, which are handled by `17-delete-by-id-tombstone-ordering.md`.
