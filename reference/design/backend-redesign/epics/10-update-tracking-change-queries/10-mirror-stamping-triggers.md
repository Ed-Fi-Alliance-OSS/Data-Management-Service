---
jira: DMS-1173
jira_url: https://edfi.atlassian.net/browse/DMS-1173
---

# Story: Keep Change-Version Mirrors in Lock-Step from Stamping Triggers

> **Superseded by the `dms.Document` / `dms.ReferentialIdentity` removal:** the whole of this story. Its
> subject is the dual write this phase retired — stamping `dms.Document`, capturing those values with
> `RETURNING` / `OUTPUT`, and copying them to a mirror row. There is no `dms.Document` row to stamp or
> capture from. A root-table or `dms.Descriptor` `DocumentStamping` trigger now writes `ContentVersion`
> and `ContentLastModifiedAt` — plus `IdentityVersion` and `IdentityLastModifiedAt` on insert and on an
> identity change — directly onto its own row; a child / collection / `_ext` trigger still `UPDATE`s the
> resource root row named by its `MirrorStampTargetTable`, and that row is now the only copy of the stamp
> rather than a mirror of one. What survives on those triggers: allocation from
> `dms.ChangeVersionSequence`, and the suppression of updates whose authoritative columns did not change
> (rendered as a positive `IS DISTINCT FROM` / `inserted`-vs-`deleted` comparison over the authoritative
> column list). What goes: every acceptance criterion phrased as reading, writing or matching
> `dms.Document` values. Retained as a historical work record. See
> [`docs/RELATIONAL-BACKEND.md` §4](../../../../../docs/RELATIONAL-BACKEND.md#4-debugging-the-writeread-paths-and-update-tracking-stored-stamps).

## Description

Extend every `TriggerKindParameters.DocumentStamping` trigger renderer so the stamped `dms.Document` values are captured once and copied to the trigger's `MirrorStampTargetTable`.

For representation-changing updates and deletes, the trigger must allocate exactly one `dms.ChangeVersionSequence` value per affected document, write that value to `dms.Document.ContentVersion`, and then mirror the same value to the concrete root table or `dms.Descriptor`. Root-resource and descriptor inserts must copy the existing `dms.Document.ContentVersion` initialized by `dms.Document` defaults instead of allocating another content version. No mirror update may call the sequence a second time for the same document.

The trigger's affected-document detection must also ignore updates whose only differences are stamp columns. This prevents mirror updates from causing recursive or redundant stamp activity.

Known PostgreSQL follow-up: this story keeps PostgreSQL child / `_ext` `DocumentStamping` triggers on their current row-level shape. When one PostgreSQL statement changes multiple child or `_ext` rows that share the same root `DocumentId`, the final mirror still equals `dms.Document`, but the trigger can allocate more than one `ContentVersion` for that one affected document. Dedupe-by-document PostgreSQL statement-level stamping is tracked separately in `28-postgresql-statement-level-child-stamping.md`.

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
