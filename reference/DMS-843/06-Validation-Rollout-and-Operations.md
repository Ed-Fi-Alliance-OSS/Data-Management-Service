# Validation, Rollout, and Operations

## Rollout Strategy

The feature rollout must keep the public API consistent and avoid windows where the Change Query contract is partially correct.

Recommended rollout order:

1. deploy `dms.ChangeVersionSequence`
2. deploy `dms.ResourceKey`
3. add nullable `dms.Document.ResourceKeyId`, `dms.Document.CreatedByOwnershipTokenId` if not already present, `dms.Document.ChangeVersion`, and `dms.Document.IdentityVersion`
4. add `dms.DocumentChangeEvent`
5. add `dms.DocumentDeleteTracking`
6. add `dms.DocumentKeyChangeTracking`
7. add required indexes
8. seed `dms.ResourceKey` and backfill all existing live rows with deterministic `ResourceKeyId`, `ChangeVersion`, and `IdentityVersion` values
9. backfill `dms.DocumentChangeEvent` from the current live rows
10. make `dms.Document.ResourceKeyId`, `dms.Document.ChangeVersion`, and `dms.Document.IdentityVersion` non-null
11. enable live-row stamp and journal triggers
12. deploy application changes for request validation, query routing, `journal + verify` changed-resource queries, delete queries, key-change queries, and `availableChangeVersions` while `AppSettings.EnableChangeQueries` remains `false`
13. set `AppSettings.EnableChangeQueries = true` in the deployed configuration
14. expose the feature routes
15. if snapshot-backed reads are required, provision or refresh the read-only snapshot source only after the live instance schema, backfill, and application rollout are complete, then validate `Use-Snapshot` against that same snapshot surface

Operational precondition:

- steps 8 through 11 must run in one controlled deployment window with representation-changing DMS writes paused or drained so no committed write can occur after backfill and before trigger enablement
- any snapshot source exposed to clients must be structurally aligned to the same deployed schema version and must include the change-query artifacts before `Use-Snapshot` is advertised as available

## Feature Availability Flag

DMS-843 should add `AppSettings.EnableChangeQueries` as a configurable feature flag.

When the setting is absent, DMS should follow the same default-on posture documented by Ed-Fi ODS/API for Change Queries.

Rollout rules:

- absent configuration leaves Change Queries enabled
- new code should not rely on the implicit default during deployment; set the flag explicitly in deployed configuration and keep it `false` until the database artifacts, backfill, and trigger enablement are complete
- do not turn the flag on until the required Change Queries database artifacts and application changes are deployed together
- when the flag is off, collection GET requests that include `minChangeVersion`, `maxChangeVersion`, or `Use-Snapshot = true` fail explicitly rather than silently ignoring the requested synchronization mode
- turning the flag off is an API-surface decision; database cleanup remains a separate operational action if that path is ever chosen

## Optional Snapshot Source

Snapshot-backed reads are one of the two synchronization flows in the design. `Use-Snapshot` absent or `false` selects the live flow, and `Use-Snapshot = true` selects the snapshot-backed flow.

Operational rules:

- the snapshot source is read-only and instance-scoped
- the snapshot source must expose the same DMS schema, `dms.Document` rows, journals, tombstones, key-change rows, and required authorization companion artifacts as the live instance
- if no snapshot source is configured, ordinary live-flow requests still work exactly as they do today and `Use-Snapshot = true` requests fail explicitly
- snapshot refresh or recreation must occur only after the matching migrations, backfill, and application version are in place
- clients that rely on snapshot-backed synchronization must use `availableChangeVersions` and all later data reads against that same snapshot surface

## Backfill Validation

Required checks after live-row backfill:

- `dms.ResourceKey` count and contents match the deployed effective schema seed
- resources with zero current live rows still have seeded `dms.ResourceKey` rows
- row count is unchanged
- if `CreatedByOwnershipTokenId` is part of the deployed bridge schema, the column exists and is readable for tracked-change capture
- every live row has a non-null `ResourceKeyId`
- every live row has a non-null `ChangeVersion`
- every live row has a non-null `IdentityVersion`
- backfill allocated one `ChangeVersion` value and one `IdentityVersion` value per row rather than one shared value per statement
- live rows resolve to the expected `dms.ResourceKey` entry for their `(ProjectName, ResourceName)`
- application-managed identity-update paths are validated to prove `IdentityVersion` advances for every committed identity-changing write and does not advance for non-identity updates

Required checks after journal backfill:

- journal row count equals current live document row count
- every live row has a matching journal row using the current `ChangeVersion` and `ResourceKeyId`
- candidate queries use the resource-and-window journal index
- `availableChangeVersions.newestChangeVersion` resolves to the selected source sequence ceiling (`next value - 1`) rather than to max retained committed tracking-row value

## Validation Matrix

## Unit tests

Required unit coverage:

- parsing of `minChangeVersion` and `maxChangeVersion`
- parsing of `Use-Snapshot`
- feature-disabled collection GET behavior when `minChangeVersion`, `maxChangeVersion`, or `Use-Snapshot = true` is supplied while `AppSettings.EnableChangeQueries = false`
- `minChangeVersion = 0` is accepted as a bootstrap watermark
- max-only windows are accepted on changed-resource, `/deletes`, and `/keyChanges`
- `/deletes` and `/keyChanges` accept omitted bounds and return the retained tracked rows for the routed resource
- `maxChangeVersion = minChangeVersion` is accepted as a single-version bounded window
- `Use-Snapshot = false` or an omitted header preserves the current live-read behavior
- invalid `Use-Snapshot` returns `400 Bad Request` `application/problem+json`
- `Use-Snapshot = true` with no usable snapshot source returns `409 Conflict` `application/problem+json`
- `400 Bad Request` `application/problem+json` behavior for malformed, negative, or otherwise invalid change-query parameters
- if a later retention phase is added, `409 Conflict` `application/problem+json` behavior when `minChangeVersion < oldestChangeVersion`
- route dispatch for `availableChangeVersions`
- route dispatch for `/deletes`
- route dispatch for `/keyChanges`
- when Change Queries is disabled, `/deletes`, `/keyChanges`, and `availableChangeVersions` return the documented `404 Not Found` behavior without falling through to generic item-route parsing
- changed-resource mode branching in `ApiService`
- changed-resource collection GET continues normal profile resolution and profile response filtering
- `/deletes`, `/keyChanges`, and `availableChangeVersions` bypass profile resolution and profile response filtering
- `keyValues` extraction from `ResourceSchema.IdentityJsonPaths`
- old-key and new-key extraction from `ResourceSchema.IdentityJsonPaths`
- shortest-unique-suffix alias derivation for simple, composite, and repeated-leaf identities
- fail-fast validation for duplicate or otherwise unresolvable identity-path alias sets
- fail-fast startup validation for change-query-enabled resources whose tracked-change authorization contract requires `AuthorizationBasis` but lacks a valid `basisDocumentIds` and optional `relationshipInputs` mapping
- tombstone DTO or response mapping
- key-change DTO or response mapping
- `/deletes` and `/keyChanges` map `id` from stored `DocumentUuid`

## Integration tests

Required backend integration coverage:

1. insert stamps a new `ChangeVersion` and a new `IdentityVersion`
2. `UPDATE OF EdfiDoc` bumps `ChangeVersion`
3. identity-changing update bumps `IdentityVersion`
4. authorization-only maintenance update does not bump `ChangeVersion` or `IdentityVersion`
5. changed-resource query filters correctly by `ResourceKeyId` and window
6. changed-resource query preserves authorization filtering
7. `Use-Snapshot = true` causes changed-resource queries to read the snapshot-visible journal and live rows rather than the live primary rows
8. snapshot-backed changed-resource paging remains stable when later live writes occur after the snapshot point
9. delete inserts one tombstone row with copied natural-key and authorization data
10. delete rollback leaves no tombstone row committed
11. delete query authorization matches the documented ODS-style tracked-change authorization criteria, including delete-aware relationship cases
12. identity-changing update inserts one key-change tracking row with copied old-key, new-key, and tracked-change authorization data
13. identity-changing update records the new live `ChangeVersion`, not `IdentityVersion`, on `dms.DocumentKeyChangeTracking`
14. non-identity update does not insert a key-change tracking row
15. representation rewrite caused by an upstream identity change does not insert a key-change row when the dependent resource's own identity tuple is unchanged
16. key-change query collapses multiple key changes for one resource within a window correctly
17. key-change query authorization uses the stored pre-update tracked-change authorization data as documented
18. `/keyChanges` applies `totalCount`, `offset`, and `limit` after authorization filtering and collapse
19. `availableChangeVersions` returns correct bounds, including bootstrap `0/0` when the synchronization surface is empty
20. `availableChangeVersions` with `Use-Snapshot = true` returns the snapshot-visible ceiling rather than the live-primary ceiling
21. if a later retention phase is added, replay-floor enforcement uses `minChangeVersion < oldestChangeVersion`
22. journal trigger inserts exactly one row per committed representation change
23. required `journal + verify` execution filters stale journal candidates correctly
24. required `journal + verify` execution applies `totalCount`, `offset`, and `limit` to the final verified authorized changed-resource result set rather than to raw journal candidates
25. required `journal + verify` execution may use bounded internal candidate-read batches per page build, while continuing batch reads until page fill or window exhaustion with deterministic continuation
26. concurrent identity-changing update and delete attempts against the same document serialize under the write-path locking contract and do not capture stale pre-change data
27. tombstones and key-change rows preserve `CreatedByOwnershipTokenId` and the tracked-change authorization basis data needed for redesign authorization concepts
28. write-path capture fails explicitly when required tracked-change authorization inputs cannot be reduced to the declared `AuthorizationBasis` contract for the routed resource, and no tombstone or key-change row is committed in that failure path

## End-to-end tests

Required E2E coverage:

1. normal collection GET remains unchanged when both `minChangeVersion` and `maxChangeVersion` are absent and `Use-Snapshot` is not requested
2. GET by id remains unchanged
3. changed-resource query returns current resources within the requested window
4. `/deletes` returns tombstones in deterministic order
5. `/keyChanges` returns collapsed key-change rows in deterministic order
6. `minChangeVersion = 0` is accepted when used as a bootstrap watermark
7. `maxChangeVersion = minChangeVersion` returns a valid single-version bounded-window response
8. max-only windows are accepted on changed-resource, `/deletes`, and `/keyChanges`
9. `Use-Snapshot = false` or an omitted header preserves the current live behavior
10. snapshot-backed changed-resource queries return the snapshot-visible resources in the requested bounded window and do not drift when later live writes occur
11. snapshot-backed `/deletes` and `/keyChanges` return the snapshot-visible tracked rows from the same synchronization surface used by `availableChangeVersions`
12. malformed `Use-Snapshot` values return `400 Bad Request` problem details with the documented `type`, `title`, `detail`, `validationErrors`, `errors`, and `correlationId`
13. `Use-Snapshot = true` with no usable snapshot source returns `409 Conflict` problem details with the documented snapshot-unavailable contract
14. malformed, negative, or otherwise invalid change-query windows return `400 Bad Request` problem details with the documented `type`, `title`, `detail`, `validationErrors`, `errors`, and `correlationId`
15. if a later retention phase is added, requests where `minChangeVersion < oldestChangeVersion` return `409 Conflict` problem details with the documented replay-floor fields
16. `availableChangeVersions` returns bootstrap `0/0` before any retained tracking rows exist
17. `/deletes` and `/keyChanges` return the canonical public resource `id` sourced from `DocumentUuid`
18. when Change Queries is disabled, `/deletes`, `/keyChanges`, and `availableChangeVersions` return `404 Not Found` without being interpreted as ordinary resource routes
19. unauthorized callers do not see unauthorized changed resources
20. unauthorized callers do not see unauthorized deletes under the documented tracked-change authorization rules
21. unauthorized callers do not see unauthorized key changes under the documented tracked-change authorization rules
22. insert then delete before sync returns only the delete row
23. delete then reinsert yields a delete row and a later live resource in later windows
24. multiple key changes before sync yield one collapsed key-change row
25. `/keyChanges` for a resource that does not support identity updates returns `200 OK` with an empty array
26. `/keyChanges` paging and `totalCount` semantics apply after authorization filtering and collapse
27. required `journal + verify` changed-resource execution preserves the documented public paging semantics
28. required `journal + verify` changed-resource execution preserves deterministic continuation and page-build correctness when bounded internal candidate-read batching is used
29. readable profile headers do not alter or block `/deletes`, `/keyChanges`, or `availableChangeVersions`
30. changed-resource mode remains resource-level under readable profiles even when the filtered payload appears unchanged

## Scenario Expectations

| Scenario | Expected behavior |
| --- | --- |
| Multiple updates before sync | Return the current resource once |
| ChangeVersion gaps from rollback | Preserve ordering; gaps are acceptable |
| Large change window | Deterministic ordering with pagination |
| Client crash mid-window | Persist the pass-start synchronization version only after success; re-delivery above that watermark is expected in later passes |
| Request older than replay floor | Later retention phase only: return `409 Conflict` problem details when `minChangeVersion < oldestChangeVersion` and instruct the client to restart from the advertised replay floor |
| All participating sources empty after purge | Later retention phase only: return `oldestChangeVersion = newestChangeVersion =` current replay floor |
| Auth-only maintenance updates | No new changed-resource signal |
| Delete transaction rollback | No committed tombstone |
| Multiple key changes in one window | Return one collapsed key-change row |
| Snapshot-backed bounded synchronization | Return the snapshot-visible surface for the bounded window without paging drift from later live writes |
| Snapshot source unavailable | Return explicit snapshot-unavailable problem details; do not silently fall back to live reads |
| Repeated identity leaf names | Return deterministic disambiguated key payload fields |
| Key change then delete in one window | Return both the key-change row and the delete tombstone |

## Performance Considerations

The design must be evaluated against:

- high-cardinality resources in live changed-resource queries
- high-cardinality resources in snapshot-backed changed-resource queries
- large tombstone counts in `/deletes`
- large key-change counts in `/keyChanges`
- cost of `availableChangeVersions` under steady write load
- journal growth and candidate verification costs under the required live-query path

Expected success criteria:

- resource-scoped live queries use the journal resource-window index
- tombstone queries use the tombstone resource-window index
- normal resource-scoped Change Queries do not require full-table scans
- live candidate queries use `journal + verify` rather than attempting to materialize historical payloads

## Future Retention Phase

Retention and purge are not required for the initial DMS-843 delivery. They can be considered in a later phase for delete tombstones, key-change tracking rows, and the required live journal.

If a later retention phase is pursued:

- do not purge immediately at first rollout
- define the production retention period before enabling purge jobs
- expect a 30-90 day range unless product or operations decide otherwise

If purge is eventually enabled:

- deploy `dms.ChangeQueryRetentionFloor` before any purge job is allowed to run
- each participating surface contributes replay floor `0` until purge advances it
- `oldestChangeVersion` is the greatest replay floor among the participating live, delete, and key-change tracking surfaces
- each purge job must delete obsolete rows and advance that surface's `dms.ChangeQueryRetentionFloor` value in the same transaction
- if all rows in a tracked source are purged, the metadata row remains authoritative; emptiness alone must not lower the replay floor

## Change-Query Error Contract

Required external-contract behavior:

- collection GET requests that supply `minChangeVersion`, `maxChangeVersion`, or `Use-Snapshot = true` while Change Queries is disabled return `400 Bad Request` problem details
- invalid `Use-Snapshot` returns `400 Bad Request` problem details
- `Use-Snapshot = true` when no usable snapshot source is available returns `409 Conflict` problem details
- invalid or negative `minChangeVersion` returns `400 Bad Request` problem details
- invalid or negative `maxChangeVersion` returns `400 Bad Request` problem details
- `maxChangeVersion < minChangeVersion` returns `400 Bad Request` problem details
- if a later retention phase introduces replay-floor advancement, `minChangeVersion < oldestChangeVersion` returns `409 Conflict` problem details

ODS compatibility note:

- DMS-843 intentionally keeps `409 Conflict` for snapshot-unavailable requests to preserve ODS-compatible change-query client behavior

Problem-detail expectations:

- all responses include `type`, `title`, `status`, `detail`, `validationErrors`, `errors`, and `correlationId`
- for the documented change-query parameter-validation and replay-floor failures, `validationErrors` is present as an empty object, `{}`, rather than being omitted
- feature-disabled collection GET requests use type `urn:ed-fi:api:change-queries:feature-disabled`
- invalid `Use-Snapshot` uses type `urn:ed-fi:api:change-queries:validation:use-snapshot`
- unavailable snapshot source uses type `urn:ed-fi:api:change-queries:snapshot:unavailable`
- invalid `minChangeVersion` uses type `urn:ed-fi:api:change-queries:validation:min-change-version`
- invalid `maxChangeVersion` uses type `urn:ed-fi:api:change-queries:validation:max-change-version`
- invalid window relationship uses type `urn:ed-fi:api:change-queries:validation:window`
- if a later retention phase is added, replay-floor miss uses type `urn:ed-fi:api:change-queries:sync:window-unavailable`
- if a later retention phase is added, replay-floor miss responses include `requestedMinChangeVersion`, `oldestChangeVersion`, and `newestChangeVersion`

## Reference: `Use-Snapshot` Consistency Flows

DMS-843 supports both the live flow and the snapshot-backed flow while still avoiding snapshot history tables.

Without snapshots:

- no snapshot operational dependency is required
- the feature runs with the canonical `dms.Document` row, tombstones, key-change tracking, and the live database only
- a multi-request synchronization pass does not see one frozen database view
- offset paging can drift while requests are in progress
- duplicate delivery above the saved synchronization watermark is expected and must be tolerated by clients
- missing items are still possible under concurrent writes, especially when paging shifts after deletes or updates

With snapshots:

- the same routes and tracking artifacts can provide one frozen synchronization surface for the pass
- bounded windows and offset paging are stable for that snapshot-backed pass
- the pass sees the state current inside the snapshot, not the later state of the live primary
- snapshot lifecycle, refresh discipline, and availability become operational dependencies outside the core tracking schema
- if the snapshot source is unavailable, DMS fails explicitly rather than silently falling back to live reads

## Risks and Mitigations

## Live-mode paging drift

Risk:

- offset paging may drift if rows move out of the requested window during retrieval

Mitigation:

- document the live-mode tradeoffs explicitly
- use the normative open-ended `minChangeVersion` algorithm and persist the pass-start synchronization version only after the full pass succeeds
- require clients to tolerate duplicates and recommend overlap or periodic reinitialization when stronger assurance is required

## Snapshot lifecycle or staleness drift

Risk:

- the snapshot source may be unavailable, out of date, or structurally misaligned with the live deployment when clients request `Use-Snapshot`

Mitigation:

- fail explicitly when `Use-Snapshot = true` and no usable snapshot source is available
- require snapshot refresh and publication only after matching migrations, backfill, and application rollout are complete
- compute `availableChangeVersions` and all later synchronization reads from the same snapshot source so the advertised ceiling matches the returned rows
- document that later live commits are intentionally outside the snapshot-backed pass until a later snapshot refresh or a later live run

## False positives from authorization maintenance

Risk:

- authorization projection updates may emit change records if triggers are too broad

Mitigation:

- scope stamp triggers to representation changes on `EdfiDoc`
- emit journal rows only when `ChangeVersion` changes

## Tombstone authorization loss

Risk:

- deletes could become under-authorized or over-authorized if tombstones do not preserve the tracked-change authorization data needed for ODS-style visibility

Mitigation:

- copy the tracked-change authorization data into `dms.DocumentDeleteTracking`, including ownership and row-local authorization basis values
- build delete-query filters against that preserved tracked-change state

## Key-change collapse or authorization drift

Risk:

- key-change queries could return duplicate rows, the wrong old/new keys, or unauthorized rows if tracking or collapse logic is incomplete

Mitigation:

- capture explicit old and new key values in `dms.DocumentKeyChangeTracking`
- copy the tracked-change authorization data into each key-change tracking row
- test multi-change collapse and authorization parity explicitly

## Journal misuse for deletes

Risk:

- implementers could try to use `dms.DocumentChangeEvent` as a delete store

Mitigation:

- document that the journal is a live-change artifact only
- keep deletes exclusively in tombstones

## Review Checklist

The feature design is ready for review if reviewers can answer yes to the following:

- the public API remains non-breaking
- feature-off behavior is explicit, with no silent downgrade of Change Query requests
- the feature satisfies the Ed-Fi changed-resource semantics of current-state results rather than mutation history
- deletes remain visible after the live row is gone
- key changes remain visible after later key mutations or deletion of the live row
- live changed-resource authorization remains aligned to current live-read semantics
- delete and key-change authorization target the documented ODS-style tracked-change criteria
- tracked-change artifacts preserve redesign ownership and DocumentId-based authorization inputs
- the key payload contract stays deterministic even when a resource reuses the same leaf name in multiple identity paths
- the design avoids snapshot history tables, preserves the current live mode, and explicitly documents both the live and snapshot-backed synchronization behaviors and obligations
- `Use-Snapshot` requests fail explicitly when a snapshot source is unavailable and are never silently downgraded to live reads
- multi-resource synchronization order is explicit for `keyChanges`, changed resources, and deletes
- the public error contract is normative, including ProblemDetails members and types
- `/deletes` and `/keyChanges` expose the canonical public resource `id`
- the required journal changes only internal execution, not public contract
- the package can be decomposed into implementation stories without reopening major architecture decisions
