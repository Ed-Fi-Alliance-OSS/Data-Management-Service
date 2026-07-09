---
jira: DMS-1001
jira_url: https://edfi.atlassian.net/browse/DMS-1001
---


# Epic: Update Tracking (`_etag/_lastModifiedDate`) + Change Queries (`ChangeVersion`)

## Description

Implement the representation-sensitive update tracking design in:

- `reference/design/backend-redesign/design-docs/update-tracking.md` (normative)
- `reference/design/backend-redesign/design-docs/change-queries.md` (normative)

Deliverables include:
- write-side stamping of `ContentVersion/IdentityVersion` (global monotonic stamps),
- journal emission via triggers,
- serving `_etag`, `_lastModifiedDate`, and per-item `ChangeVersion` from stored stamps,
- ensuring successful no-op updates leave stored stamps and journal rows unchanged,
- `If-Match` enforcement using stored representation stamps,
- ChangeQueries feature does not introduce any breaking changes to its API interface
- Ideally, being able to support the feature without requiring DB snapshots

## Stories

- `DMS-1002` — `00-token-stamping.md` — Allocate stamps and update token columns only for representation changes
- `DMS-1003` — `01-journaling-contract.md` — _Retired_ (superseded by DMS-1169 + DMS-1179)
- `DMS-1004` — `02-derived-metadata.md` — Serve `_etag/_lastModifiedDate/ChangeVersion` from stored stamps
- `DMS-1005` — `03-if-match.md` — Enforce optimistic concurrency using stored `_etag`
- `DMS-1006` — `04-change-query-selection.md` — _Retired_ (superseded by DMS-1182 + DMS-1186 + DMS-1187)
- `DMS-1007` — `05-change-query-api.md` — _Retired_ (superseded by DMS-1184, DMS-1186, DMS-1187, DMS-1188, and the split non-relationship `ReadChanges` strategy story)
- `DMS-1008` — `06-descriptor-stamping.md` — Ensure descriptor writes stamp/journal correctly (triggers on `dms.Descriptor`)
- `DMS-1168` — `07-get-max-change-version-function.md` — Emit `dms.GetMaxChangeVersion()` function for `/availableChangeVersions`
- `DMS-1169` — `08-remove-document-change-event.md` — Remove `dms.DocumentChangeEvent`; superseded by the per-resource `tracked_changes_*` tables and the `ContentVersion` mirror
- `DMS-1172` — `09-change-version-mirror-model.md` — Derive concrete-table `ContentVersion` / `ContentLastModifiedAt` mirrors and indexes
- `DMS-1173` — `10-mirror-stamping-triggers.md` — Keep concrete-table mirrors in lock-step from document-stamping triggers
- `DMS-1174` — `11-refkey-documentid-ordering.md` — Emit `*_RefKey` indexes with public identity first, intrinsic
  lineage anchors next, and `DocumentId` last for recreated-resource probes
- `DMS-1175` — `12-tracked-change-inventory.md` — Derive tracked-change table, column, join, and trigger inventory
- `DMS-1176` — `13-readchanges-authorization-inventory.md` — Derive `ReadChangesAuthorizationViewInfo` inventory for `*IncludingDeletes` views
- `DMS-1177` — `14-tracked-change-table-ddl.md` — Emit `tracked_changes_<schema>` tables from derived inventory
- `DMS-1178` — `15-readchanges-authorization-view-ddl.md` — Emit `ReadChanges` `*IncludingDeletes` authorization views
- `DMS-1179` — `16-tracked-change-trigger-rendering.md` — Populate tracked-change tombstones and key-change rows from stamping triggers
- `DMS-1180` — `17-delete-by-id-tombstone-ordering.md` — Delete concrete rows before `dms.Document` so tombstone triggers can read document stamps
- `DMS-1181` — `18-change-version-parameter-validation.md` — Validate `minChangeVersion` / `maxChangeVersion` consistently
- `DMS-1182` — `19-live-change-version-filters.md` — Filter live resource and descriptor GET-many endpoints by mirrored `ContentVersion`
- `DMS-1183` — `20-openapi-change-query-surface.md` — Extend MetaEd and DMS OpenAPI metadata for Change Queries
- `DMS-1184` — `21-available-change-versions-endpoint.md` — Serve `/changeQueries/v1/availableChangeVersions`
- `DMS-1186` — `23-deletes-endpoint.md` — Serve `/deletes` from tracked-change tombstones and establish the shared Change Query endpoint foundation
- `DMS-1187` — `24-keychanges-endpoint.md` — Serve `/keyChanges` from tracked-change key-change rows
- `DMS-1188` — `25-readchanges-authorization.md` — Apply relationship-based `ReadChanges` authorization to `/deletes` and `/keyChanges`
- `DMS-1197` — `27-no-further-and-namespace-readchanges-authorization.md` — Apply `NoFurtherAuthorizationRequired` and `NamespaceBased` `ReadChanges` authorization to Change Query endpoints
- `DMS-1208` — `28-postgresql-statement-level-child-stamping.md` — Deduplicate PostgreSQL child and `_ext` stamping by affected document
- `DMS-1194` — `32-document-v1-release-note-deferrals.md` — Document DMS v1.0 Change Queries deferred features in release notes

## Deferred Stories (post-v1.0)

These spikes investigate features explicitly deferred in `change-queries.md`. Each spike's deliverable is a design proposal plus the implementation tickets it spawns.

- `DMS-1185` — `22-auth-check-indexes-on-tracked-changes.md` — Spike: auth-check indexes on `tracked_changes_*` tables
- `DMS-1190` — `29-snapshot-support.md` — Spike: snapshot support (`Use-Snapshot` header) for Change Queries
- `DMS-1191` — `30-disable-change-queries-feature.md` — Spike: runtime feature flag to disable Change Queries
- `DMS-1193` — `31-custom-view-based-readchanges-authorization.md` — Spike: custom view-based authorization for `ReadChanges`
