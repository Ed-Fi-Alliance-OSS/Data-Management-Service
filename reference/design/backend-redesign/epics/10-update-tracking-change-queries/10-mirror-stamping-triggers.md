---
jira: Unassigned
---

# Story: Keep Change-Version Mirrors in Lock-Step from Stamping Triggers

## Description

Extend every `DbTriggerKind.DocumentStamping` trigger renderer so the stamped `dms.Document` values are captured once and copied to the trigger's `MirrorStampTargetTable`.

The trigger must allocate exactly one `dms.ChangeVersionSequence` value per affected document, write that value to `dms.Document.ContentVersion`, and then mirror the same value to the concrete root table or `dms.Descriptor`. It must not call the sequence a second time for the mirror update.

The trigger's affected-document detection must also ignore updates whose only differences are stamp columns. This prevents mirror updates from causing recursive or redundant stamp activity.

## Acceptance Criteria

- PostgreSQL trigger functions capture stamped `DocumentId`, `ContentVersion`, and `ContentLastModifiedAt` values from the `dms.Document` update using `RETURNING`.
- SQL Server triggers capture the same values using `OUTPUT`.
- Trigger renderers update `DbTriggerInfo.MirrorStampTargetTable` with the captured stamp values.
- The mirror update uses the same `ContentVersion` and `ContentLastModifiedAt` written to `dms.Document`.
- The trigger does not allocate a second sequence value for the mirror.
- The affected-document workset excludes rows whose only old/new differences are `ContentVersion`, `ContentLastModifiedAt`, `IdentityVersion`, or `IdentityLastModifiedAt`.
- Inserts, updates, identity changes, child writes, `_ext` writes, FK-cascade updates, extension-project resource writes, and descriptor writes leave the mirror equal to `dms.Document`.
- Successful no-op updates do not change `dms.Document` stamps and do not change mirror stamps.
- Direct stamp-only updates do not insert tracked-change rows and do not allocate an additional change version through nested trigger activity.
- Multi-row updates allocate one distinct `ContentVersion` per affected document and mirror each value correctly.
- PostgreSQL and SQL Server integration tests cover at least a root-only resource, a child-bearing resource, an `_ext`-bearing resource, an extension-project resource, and a descriptor.

## Dependencies

- `09-change-version-mirror-model.md`.
- Existing `00-token-stamping.md`.
- Existing `06-descriptor-stamping.md`.

## Out of Scope

- Adding tracked-change tombstone or key-change inserts.
- Changing read responses to source `_lastModifiedDate` or per-item `ChangeVersion` from the mirror instead of `dms.Document`.
