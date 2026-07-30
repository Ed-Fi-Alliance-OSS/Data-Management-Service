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
- composing `_etag` from `ContentVersion` plus the active `variantKey`, and serving
  `_lastModifiedDate` and per-item `ChangeVersion` from stored stamps,
- ensuring successful no-op updates leave stored stamps and journal rows unchanged,
- `If-Match` enforcement using stored representation stamps,
- ChangeQueries feature does not introduce any breaking changes to its API interface
- Ideally, being able to support the feature without requiring DB snapshots

## Stories

- `DMS-1002` — `00-token-stamping.md` — Allocate stamps and update token columns only for representation changes
- `DMS-1003` — `01-journaling-contract.md` — _Retired_ (superseded by DMS-1169 + DMS-1179)
- `DMS-1004` — `02-derived-metadata.md` — Compose `_etag`; serve `_lastModifiedDate/ChangeVersion` from stored stamps
- `DMS-1005` — `03-if-match.md` — Enforce optimistic concurrency using stored representation stamps
- `DMS-1006` — `04-change-query-selection.md` — _Retired_ (superseded by DMS-1182 + DMS-1186 + DMS-1187)
- `DMS-1007` — `05-change-query-api.md` — _Retired_ (superseded by DMS-1184, DMS-1186, DMS-1187, DMS-1188, and the split non-relationship `ReadChanges` strategy story)
- `DMS-1008` — `06-descriptor-stamping.md` — Ensure descriptor writes stamp/journal correctly (triggers on `dms.Descriptor`)
- `DMS-1168` — `07-get-max-change-version-function.md` — Emit `dms.GetMaxChangeVersion()` function for `/availableChangeVersions`
- `DMS-1169` — `08-remove-document-change-event.md` — Remove `dms.DocumentChangeEvent`; superseded by the per-resource `tracked_changes_*` tables and the `ContentVersion` mirror
- `DMS-1172` — `09-change-version-mirror-model.md` — Derive concrete-table `ContentVersion` / `ContentLastModifiedAt` mirrors and indexes
- `DMS-1173` — `10-mirror-stamping-triggers.md` — Keep concrete-table mirrors in lock-step from document-stamping triggers
- `DMS-1174` — `11-refkey-documentid-ordering.md` — Emit `*_RefKey` indexes with `DocumentId` last for recreated-resource probes
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

- `DMS-1185` — `22-auth-check-indexes-on-tracked-changes.md` — Spike: auth-check indexes on `tracked_changes_*` tables (findings and proposed design: `22-spike-findings.md`)
- `DMS-1190` — `29-snapshot-support.md` — Spike: snapshot (`Use-Snapshot` header) and read-replica support for Change Queries
- `DMS-1191` — `30-disable-change-queries-feature.md` — Spike: runtime feature flag to disable Change Queries
- `DMS-1193` — `31-custom-view-based-readchanges-authorization.md` — Spike: custom view-based authorization for `ReadChanges`

## Follow-on Stories (spawned by DMS-1185)

Release disposition: none of the follow-ons is must-have for 8.1. Stories 33 and 35 are stretch candidates for 8.1: each ships in 8.1 only if its evidence gates pass before the release cut, and missing the cut moves it to the next release with no API impact, since both are additive index emission only. Stories 34, 36, and 37, and the cross-epic Authorization prerequisite, are post-8.1.

- `DMS-1357` - `33-tracked-change-index-emission.md` - Evaluate each Tier-1 category with pinned candidate overlays on both providers, emit only the finally selected categories, and rerun the gates against exact generated DDL (8.1 stretch)
- `DMS-1358` - `34-readchanges-subject-cardinality.md` - After Story 33's candidate-evaluation phase, select and implement provider-appropriate `ReadChanges` relationship subject-cardinality shapes; if the PA category remains blocked, its candidate-overlay rerun triggers the dedicated PA re-evaluation ticket recorded in Story 33's closure rule (post-8.1)
- `DMS-1359` - `35-mssql-descriptor-identity-index.md` - Emit the SQL Server live descriptor identity index selected by DMS-1185 (8.1 stretch)
- `DMS-1360` - `36-per-resource-edorg-person-index-emission.md` - After Stories 33 and 34, emit per-resource EdOrg/person tracked-change indexes (post-8.1)
- `DMS-1361` - `37-tracked-namespace-index-emission.md` - After Story 33 and Authorization Story 22, adapt tracked Namespace predicates and emit tracked namespace indexes (post-8.1)

## Follow-on Stories (spawned by DMS-1190)

The `TBD` keys below are intentional and are not an incomplete edit. `29-snapshot-support.md` § Follow-on Ticket Plan gates Jira creation on approval of that spike, so the story files ship with placeholder front matter and the keys are filled in — here and in each story's `jira` / `jira_url` front matter — once the tickets exist. Do not treat these rows as traceable work items until they carry real keys.

**Authoritative release disposition:** Story dependencies and the snapshot release gate are deliberately different. Story 42 may be scheduled and closed after Stories 38–40 and the external Publisher build are available; it does not depend on Story 41 or on the upstream MetaEd/ApiSchema publication, so Publisher validation, operator guidance, and release-note preparation can finish independently. That independence does **not** authorize runtime rollout. The runtime changes delivered by Stories 39 and 40 must not ship in any release until Story 41 is complete in that release using the published upstream packages and the served OpenAPI documents advertise the matching `Use-Snapshot`, `404`, and `405` contract. If Story 41 or its upstream publication misses the release cut, hold the Stories 39/40 runtime changes from that release; Story 42 may remain closed and its validation and documentation artifacts carry forward.

- `TBD` — `38-cms-data-store-derivative-invariants.md` — Enforce CMS data-store derivative cardinality and type invariants
- `TBD` — `39-snapshot-read-replica-runtime-routing.md` — Route eligible DMS reads to snapshots and read replicas
- `TBD` — `40-snapshot-problem-details.md` — Implement snapshot ProblemDetails and connection-unavailable translation
- `TBD` — `41-snapshot-openapi-surface.md` — Add the snapshot contract to served OpenAPI documents (DMS half; depends on Story 40 and on the upstream MetaEd ticket below)
- `TBD` — `42-api-publisher-snapshot-interoperability.md` — Validate API Publisher snapshot interoperability and document operator workflow (depends on Stories 38-40 only; deliberately not on Story 41, so validation and release-note preparation are not blocked on upstream package publication; runtime release remains subject to the gate above)

One prerequisite is not a DMS ticket and has no story file in this epic:

- `TBD` — *upstream MetaEd/ApiSchema* — Author the `Use-Snapshot` parameter and snapshot response components in the served base documents and publish the ApiSchema packages. `41-snapshot-openapi-surface.md` consumes the published packages and cannot be scheduled before this is created and linked.

## Cross-Epic Prerequisite

- Authorization epic `DMS-1362` - `../14-authorization/22-namespace-auth-index-prefix-like.md` - Select and implement the live PostgreSQL Namespace predicate/index mechanism before Story 37 adapts tracked Namespace authorization (post-8.1)
