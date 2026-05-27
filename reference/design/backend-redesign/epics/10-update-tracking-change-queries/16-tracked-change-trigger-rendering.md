---
jira: DMS-1179
jira_url: https://edfi.atlassian.net/browse/DMS-1179
---

# Story: Populate Tracked-Change Tables from Stamping Triggers

## Description

Extend the existing `DbTriggerKind.DocumentStamping` render path to insert tracked-change rows when a resource is deleted or when its identifying values change.

The renderers consume `TriggerKindParameters.ChangeTracking` and the associated `TrackedChangeTableInfo`. They must not re-derive tracked-change columns, descriptor joins, person joins, or key-change predicates from SQL text.

Deletes insert tombstones with old values populated and new values null. Identity changes insert key-change rows with old and new values populated. The row's `ChangeVersion` must be the `dms.Document.ContentVersion` produced by the same trigger fire.

Concrete abstract resources (e.g., `School`, `LocalEducationAgency`, `OrganizationDepartment`) participate in tombstone emission but do not emit key-change rows: their inherited identity is immutable in practice, matching legacy ODS behavior for derived resources.

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
- Key-change rows inserted by a trigger fire satisfy `tracked_changes.ChangeVersion == dms.Document.ContentVersion == persisted mirror ContentVersion` for that same fire.
- Delete tombstones inserted by a trigger fire satisfy `tracked_changes.ChangeVersion == dms.Document.ContentVersion` stamped by the delete trigger before `dms.Document` deletion.
- Root resource deletes that cascade child, nested-child, or `_ext` rows produce exactly one visible root tombstone in the appropriate `tracked_changes_*` table.
- The root tombstone's `ChangeVersion` is the final delete ChangeVersion exposed to Change Queries; cascaded child or `_ext` trigger activity must not leave a later visible root stamp or tracked-change row that can advance an extraction watermark past the tombstone.
- PostgreSQL and SQL Server tests assert the full three-way linkage for key-change rows and tracked row to document stamp linkage for delete tombstones before document deletion.
- Descriptor resources write to `tracked_changes_edfi.Descriptor` with the correct `Discriminator`.
- Concrete abstract resources write tombstones to their own tracked-change tables on delete but do not emit key-change rows (their inherited identity is immutable in practice).
- Tests cover deletes, identity changes, cascading key changes, descriptor paths, people securable paths, key-unification paths, multi-row updates, and root deletes with cascaded child / `_ext` rows in both dialects. The root-delete cascade tests must include at least one child-bearing resource and one extension-bearing resource on PostgreSQL and SQL Server.

## Out of Scope

- Runtime `/deletes` or `/keyChanges` endpoint queries.
- Delete-by-id ordering changes, which are handled by `17-delete-by-id-tombstone-ordering.md`.
