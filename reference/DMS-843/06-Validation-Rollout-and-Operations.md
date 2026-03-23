# Validation, Rollout, and Operations

## Rollout Strategy

The feature rollout must keep the public API consistent and avoid windows where the Change Query contract is partially correct.

Recommended rollout order:

1. deploy `dms.ChangeVersionSequence`
2. deploy `dms.ResourceKey`
3. add nullable `dms.Document.ResourceKeyId`, `dms.Document.ChangeVersion`, and `dms.Document.IdentityVersion`
4. add `dms.DocumentChangeEvent`
5. add `dms.DocumentDeleteTracking`
6. add `dms.DocumentKeyChangeTracking`
7. add required indexes
8. seed `dms.ResourceKey` and backfill all existing live rows with deterministic `ResourceKeyId`, `ChangeVersion`, and `IdentityVersion` values
9. backfill `dms.DocumentChangeEvent` from the current live rows
10. make `dms.Document.ResourceKeyId`, `dms.Document.ChangeVersion`, and `dms.Document.IdentityVersion` non-null
11. enable live-row stamp and journal triggers
12. deploy application changes for request validation, query routing, `journal + verify` changed-resource queries, delete queries, key-change queries, and `availableChangeVersions`
13. set `AppSettings.EnableChangeQueries = true` in the deployed configuration
14. expose the feature routes

## Feature Availability Flag

To align with Ed-Fi ODS/API behavior, DMS-843 should add `AppSettings.EnableChangeQueries` as a configurable feature flag.

Rollout rules:

- absent configuration must leave Change Queries disabled
- new code should not rely on implicit defaults during deployment; set the flag explicitly in deployed configuration
- do not turn the flag on until the required Change Queries database artifacts and application changes are deployed together
- turning the flag off is an API-surface decision; database cleanup remains a separate operational action if that path is ever chosen

## Backfill Validation

Required checks after live-row backfill:

- row count is unchanged
- every live row has a non-null `ResourceKeyId`
- every live row has a non-null `ChangeVersion`
- every live row has a non-null `IdentityVersion`
- backfill allocated one `ChangeVersion` value and one `IdentityVersion` value per row rather than one shared value per statement
- live rows resolve to the expected `dms.ResourceKey` entry for their `(ProjectName, ResourceName)`

Required checks after journal backfill:

- journal row count equals current live document row count
- every live row has a matching journal row using the current `ChangeVersion` and `ResourceKeyId`
- candidate queries use the resource-and-window journal index

## Validation Matrix

## Unit tests

Required unit coverage:

- parsing of `minChangeVersion` and `maxChangeVersion`
- `minChangeVersion = 0` is accepted as a bootstrap watermark
- `maxChangeVersion = minChangeVersion` is accepted as a single-version bounded window
- `400 Bad Request` `application/problem+json` behavior for malformed, negative, or otherwise invalid change-query parameters
- if a later retention phase is added, `409 Conflict` `application/problem+json` behavior when `minChangeVersion < oldestChangeVersion`
- route dispatch for `availableChangeVersions`
- route dispatch for `/deletes`
- route dispatch for `/keyChanges`
- changed-resource mode branching in `ApiService`
- changed-resource collection GET continues normal profile resolution and profile response filtering
- `/deletes`, `/keyChanges`, and `availableChangeVersions` bypass profile resolution and profile response filtering
- `keyValues` extraction from `ResourceSchema.IdentityJsonPaths`
- old-key and new-key extraction from `ResourceSchema.IdentityJsonPaths`
- shortest-unique-suffix alias derivation for simple, composite, and repeated-leaf identities
- fail-fast validation for duplicate or otherwise unresolvable identity-path alias sets
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
7. delete inserts one tombstone row with copied natural-key and authorization data
8. delete rollback leaves no tombstone row committed
9. delete query authorization matches live-query authorization semantics
10. identity-changing update inserts one key-change tracking row with copied old-key, new-key, and authorization data
11. non-identity update does not insert a key-change tracking row
12. representation rewrite caused by an upstream identity change does not insert a key-change row when the dependent resource's own identity tuple is unchanged
13. key-change query collapses multiple key changes for one resource within a window correctly
14. key-change query authorization matches live-query authorization semantics
15. `/keyChanges` applies `totalCount`, `offset`, and `limit` after authorization filtering and collapse
16. `availableChangeVersions` returns correct bounds, including bootstrap `0/0` when the synchronization surface is empty
17. if a later retention phase is added, replay-floor enforcement uses `minChangeVersion < oldestChangeVersion`
18. journal trigger inserts exactly one row per committed representation change
19. required `journal + verify` execution filters stale journal candidates correctly
20. required `journal + verify` execution applies `totalCount`, `offset`, and `limit` to the final verified authorized changed-resource result set rather than to raw journal candidates
21. concurrent identity-changing update and delete attempts against the same document serialize under the write-path locking contract and do not capture stale pre-change data

## End-to-end tests

Required E2E coverage:

1. normal collection GET without `minChangeVersion` remains unchanged
2. GET by id remains unchanged
3. changed-resource query returns current resources within the requested window
4. `/deletes` returns tombstones in deterministic order
5. `/keyChanges` returns collapsed key-change rows in deterministic order
6. `minChangeVersion = 0` is accepted when used as a bootstrap watermark
7. `maxChangeVersion = minChangeVersion` returns a valid single-version bounded-window response
8. malformed, negative, or otherwise invalid change-query windows return `400 Bad Request` problem details with the documented `type`, `title`, `detail`, `errors`, and `correlationId`
9. if a later retention phase is added, requests where `minChangeVersion < oldestChangeVersion` return `409 Conflict` problem details with the documented replay-floor fields
10. `availableChangeVersions` returns bootstrap `0/0` before any retained tracking rows exist
11. `/deletes` and `/keyChanges` return the canonical public resource `id` sourced from `DocumentUuid`
12. unauthorized callers do not see unauthorized changed resources
13. unauthorized callers do not see unauthorized deletes
14. unauthorized callers do not see unauthorized key changes
15. insert then delete before sync returns only the delete row
16. delete then reinsert yields a delete row and a later live resource in later windows
17. multiple key changes before sync yield one collapsed key-change row
18. `/keyChanges` for a resource that does not support identity updates returns `200 OK` with an empty array
19. `/keyChanges` paging and `totalCount` semantics apply after authorization filtering and collapse
20. required `journal + verify` changed-resource execution preserves the documented public paging semantics
21. readable profile headers do not alter or block `/deletes`, `/keyChanges`, or `availableChangeVersions`
22. changed-resource mode remains resource-level under readable profiles even when the filtered payload appears unchanged

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
| Repeated identity leaf names | Return deterministic disambiguated key payload fields |
| Key change then delete in one window | Return both the key-change row and the delete tombstone |

## Performance Considerations

The design must be evaluated against:

- high-cardinality resources in live changed-resource queries
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

- invalid or negative `minChangeVersion` returns `400 Bad Request` problem details
- invalid or negative `maxChangeVersion` returns `400 Bad Request` problem details
- `maxChangeVersion < minChangeVersion` returns `400 Bad Request` problem details
- if a later retention phase introduces replay-floor advancement, `minChangeVersion < oldestChangeVersion` returns `409 Conflict` problem details

Problem-detail expectations:

- all responses include `type`, `title`, `status`, `detail`, `errors`, and `correlationId`
- invalid `minChangeVersion` uses type `urn:ed-fi:api:change-queries:validation:min-change-version`
- invalid `maxChangeVersion` uses type `urn:ed-fi:api:change-queries:validation:max-change-version`
- invalid window relationship uses type `urn:ed-fi:api:change-queries:validation:window`
- if a later retention phase is added, replay-floor miss uses type `urn:ed-fi:api:change-queries:sync:window-unavailable`
- if a later retention phase is added, replay-floor miss responses include `requestedMinChangeVersion`, `oldestChangeVersion`, and `newestChangeVersion`

## Reference: Tradeoffs of Not Using Snapshots

DMS-843 v1 intentionally does not expose a client-selectable snapshot or consistent-read mode.

Benefits:

- no snapshot lifecycle or storage-management surface is added to the API
- the feature can be implemented with the canonical `dms.Document` row, tombstones, and key-change tracking only
- the design stays additive and avoids introducing snapshot-specific operational dependencies

Tradeoffs:

- a multi-request synchronization pass does not see one frozen database view
- the same current-state timing tradeoff applies to initial full loads performed without snapshots
- offset paging can drift while requests are in progress
- duplicate delivery above the saved synchronization watermark is expected and must be tolerated by clients
- missing items are still possible under concurrent writes, especially when paging shifts after deletes or updates
- clients that need stronger assurance must use overlap, periodic reinitialization, or a future additive consistency feature if one is introduced later

## Risks and Mitigations

## Snapshot-free paging drift

Risk:

- offset paging may drift if rows move out of the requested window during retrieval

Mitigation:

- document the no-snapshot tradeoffs explicitly
- use the normative open-ended `minChangeVersion` algorithm and persist the pass-start synchronization version only after the full pass succeeds
- require clients to tolerate duplicates and recommend overlap or periodic reinitialization when stronger assurance is required

## False positives from authorization maintenance

Risk:

- authorization projection updates may emit change records if triggers are too broad

Mitigation:

- scope stamp triggers to representation changes on `EdfiDoc`
- emit journal rows only when `ChangeVersion` changes

## Tombstone authorization loss

Risk:

- deletes could become under-authorized or over-authorized if tombstones do not preserve the live authorization projection

Mitigation:

- copy the authorization projection into `dms.DocumentDeleteTracking`
- build delete-query filters against that copied projection

## Key-change collapse or authorization drift

Risk:

- key-change queries could return duplicate rows, the wrong old/new keys, or unauthorized rows if tracking or collapse logic is incomplete

Mitigation:

- capture explicit old and new key values in `dms.DocumentKeyChangeTracking`
- copy the authorization projection into each key-change tracking row
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
- the feature satisfies the Ed-Fi changed-resource semantics of current-state results rather than mutation history
- deletes remain visible after the live row is gone
- key changes remain visible after later key mutations or deletion of the live row
- live and delete authorization behavior stays aligned to current DMS semantics
- live and key-change authorization behavior stays aligned to current DMS semantics
- the key payload contract stays deterministic even when a resource reuses the same leaf name in multiple identity paths
- the design avoids snapshot history tables and explicitly documents the resulting tradeoffs and client obligations
- multi-resource synchronization order is explicit for `keyChanges`, changed resources, and deletes
- the public error contract is normative, including ProblemDetails members and types
- `/deletes` and `/keyChanges` expose the canonical public resource `id`
- the required journal changes only internal execution, not public contract
- the package can be decomposed into implementation stories without reopening major architecture decisions
