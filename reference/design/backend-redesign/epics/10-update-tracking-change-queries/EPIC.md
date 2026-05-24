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
- `DMS-1003` — `01-journaling-contract.md` — Treat journals as derived artifacts; validate trigger behavior
- `DMS-1004` — `02-derived-metadata.md` — Serve `_etag/_lastModifiedDate/ChangeVersion` from stored stamps
- `DMS-1005` — `03-if-match.md` — Enforce optimistic concurrency using stored `_etag`
- `DMS-1006` — `04-change-query-selection.md` — Implement Change Query candidate selection (journal + verify)
- `DMS-1007` — `05-change-query-api.md` — Implement Change Query endpoints (optional/future-facing)
- `DMS-1008` — `06-descriptor-stamping.md` — Ensure descriptor writes stamp/journal correctly (triggers on `dms.Descriptor`)
- `DMS-1168` — `07-get-max-change-version-function.md` — Emit `dms.GetMaxChangeVersion()` function for `/availableChangeVersions`
- `DMS-1169` — `08-remove-document-change-event.md` — Remove `dms.DocumentChangeEvent`; superseded by the per-resource `tracked_changes_*` tables and the `ContentVersion` mirror
- Unassigned — `09-change-version-mirror-model.md` — Derive concrete-table `ContentVersion` / `ContentLastModifiedAt` mirrors and indexes
- Unassigned — `10-mirror-stamping-triggers.md` — Keep concrete-table mirrors in lock-step from document-stamping triggers
- Unassigned — `11-refkey-documentid-ordering.md` — Emit `*_RefKey` indexes with `DocumentId` last for recreated-resource probes
- Unassigned — `12-tracked-change-inventory.md` — Derive tracked-change table, column, join, and trigger inventory
- Unassigned — `13-readchanges-authorization-inventory.md` — Derive `ReadChangesAuthorizationViewInfo` inventory for `*IncludingDeletes` views
- Unassigned — `14-tracked-change-table-ddl.md` — Emit `tracked_changes_<schema>` tables from derived inventory
- Unassigned — `15-readchanges-authorization-view-ddl.md` — Emit `ReadChanges` `*IncludingDeletes` authorization views
- Unassigned — `16-tracked-change-trigger-rendering.md` — Populate tracked-change tombstones and key-change rows from stamping triggers
- Unassigned — `17-delete-by-id-tombstone-ordering.md` — Delete concrete rows before `dms.Document` so tombstone triggers can read document stamps
- Unassigned — `18-change-version-parameter-validation.md` — Validate `minChangeVersion` / `maxChangeVersion` consistently
- Unassigned — `19-live-change-version-filters.md` — Filter live resource and descriptor GET-many endpoints by mirrored `ContentVersion`
- Unassigned — `20-openapi-change-query-surface.md` — Extend MetaEd and DMS OpenAPI metadata for Change Queries
- Unassigned — `21-available-change-versions-endpoint.md` — Serve `/changeQueries/v1/availableChangeVersions`
- Unassigned — `22-change-query-endpoint-foundation.md` — Add shared route, paging, total-count, and response foundation for `/deletes` and `/keyChanges`
- Unassigned — `23-deletes-endpoint.md` — Serve `/deletes` from tracked-change tombstones
- Unassigned — `24-keychanges-endpoint.md` — Serve `/keyChanges` from tracked-change key-change rows
- Unassigned — `25-readchanges-authorization.md` — Apply `ReadChanges` authorization to `/deletes` and `/keyChanges`
- Unassigned — `26-school-year-type-readchanges-forbidden.md` — Return `403 Forbidden` for crafted `SchoolYearType` Change Query requests
