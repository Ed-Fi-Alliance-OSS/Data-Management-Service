---
jira: DMS-1173
jira_url: https://edfi.atlassian.net/browse/DMS-1173
---

# Story: Keep Change-Version Mirrors in Lock-Step from Stamping Triggers

## Description

Extend every `TriggerKindParameters.DocumentStamping` trigger renderer so the stamped `dms.Document` values are captured once and copied to the trigger's `MirrorStampTargetTable`.

For representation-changing updates and deletes, the trigger must allocate exactly one `dms.ChangeVersionSequence` value per affected document, write that value to `dms.Document.ContentVersion`, and then mirror the same value to the concrete root table or `dms.Descriptor`. Root-resource and descriptor inserts must copy the existing `dms.Document.ContentVersion` initialized by `dms.Document` defaults instead of allocating another content version. No mirror update may call the sequence a second time for the same document.

The trigger's affected-document detection must also ignore updates whose only differences are stamp columns. This prevents mirror updates from causing recursive or redundant stamp activity.

Known PostgreSQL follow-up: this story keeps PostgreSQL child / `_ext` `DocumentStamping` triggers on their row-level shape as delivered here. When one PostgreSQL statement changes multiple child or `_ext` rows that share the same root `DocumentId`, the final mirror still equals `dms.Document`, but the trigger can allocate more than one `ContentVersion` for that one affected document. Dedupe-by-document PostgreSQL statement-level stamping is tracked separately in `28-postgresql-statement-level-child-stamping.md`. (Delivered in DMS-1208: that dedupe-by-affected-root, statement-level shape is now implemented for PostgreSQL child, nested-child, and `_ext` stamping.)

## Acceptance Criteria

- PostgreSQL trigger functions capture update/delete `DocumentId`, `ContentVersion`, and `ContentLastModifiedAt` values from the `dms.Document` update using `RETURNING`; root/descriptor insert trigger paths read the existing `dms.Document` stamp values.
- SQL Server triggers capture update/delete values using `OUTPUT`; root/descriptor insert trigger paths insert the existing `dms.Document` stamp values into the same stamped workset.
- Trigger renderers update `DbTriggerInfo.MirrorStampTargetTable` with the captured stamp values.
- The mirror update uses the same `ContentVersion` and `ContentLastModifiedAt` stored on `dms.Document`.
- The trigger does not allocate a second sequence value for the mirror.
- The affected-document workset excludes rows whose only old/new differences are `ContentVersion`, `ContentLastModifiedAt`, `IdentityVersion`, or `IdentityLastModifiedAt`.
- Inserts, updates, identity changes, child writes, `_ext` writes, FK-cascade updates, extension-project resource writes, and descriptor writes leave the mirror equal to `dms.Document`.
- Successful no-op updates do not change `dms.Document` stamps and do not change mirror stamps.
- Direct stamp-only updates do not insert tracked-change rows and do not allocate an additional change version through nested trigger activity.
- Multi-row updates allocate one distinct `ContentVersion` per affected document and mirror each value correctly for SQL Server statement-level stamping and PostgreSQL root / descriptor stamping. PostgreSQL child / `_ext` multi-row statements where multiple changed rows share one root `DocumentId` are deferred to `28-postgresql-statement-level-child-stamping.md`.
- PostgreSQL and SQL Server integration tests cover at least a root-only resource, a child-bearing resource, an `_ext`-bearing resource, an extension-project resource, and a descriptor.

## Out of Scope

- Adding tracked-change tombstone or key-change inserts.
- Changing read responses to source `_lastModifiedDate` or per-item `ChangeVersion` from the mirror instead of `dms.Document`.
- Reworking PostgreSQL child / `_ext` `DocumentStamping` triggers from row-level stamping to statement-level or otherwise deduped stamping.
