# Validation, Rollout, and Operations

## Rollout Strategy

The feature rollout must keep the public API consistent and avoid windows where the Change Query contract is partially correct.

Recommended rollout order:

1. deploy `dms.ChangeVersionSequence`
2. add nullable `dms.Document.ChangeVersion`
3. add `dms.DocumentDeleteTracking`
4. add `dms.DocumentKeyChangeTracking`
5. add required indexes
6. backfill all existing live rows with deterministic `ChangeVersion` values
7. make `dms.Document.ChangeVersion` non-null
8. enable live-row stamp triggers
9. deploy application changes for request validation, query routing, live changed-resource queries, delete queries, key-change queries, and `availableChangeVersions`
10. expose the feature routes
11. if the optional journal is included, create and backfill `dms.DocumentChangeEvent`, enable the journal trigger, then switch live query execution to `journal + verify`

## Backfill Validation

Required checks after live-row backfill:

- row count is unchanged
- every live row has a non-null `ChangeVersion`
- backfill allocated one value per row rather than one shared value per statement
- changed-resource queries can order deterministically by `ChangeVersion`, `DocumentPartitionKey`, and `Id`

Required checks after optional journal backfill:

- journal row count equals current live document row count
- every live row has a matching journal row using the current `ChangeVersion`
- candidate queries use the resource-and-window journal index

## Validation Matrix

## Unit tests

Required unit coverage:

- parsing of `minChangeVersion` and `maxChangeVersion`
- ODS-compatible `200 OK` behavior for malformed or inconsistent change-query parameters
- route dispatch for `availableChangeVersions`
- route dispatch for `/deletes`
- route dispatch for `/keyChanges`
- changed-resource mode branching in `ApiService`
- `keyValues` extraction from `ResourceSchema.IdentityJsonPaths`
- old-key and new-key extraction from `ResourceSchema.IdentityJsonPaths`
- shortest-unique-suffix alias derivation for simple, composite, and repeated-leaf identities
- fail-fast validation for duplicate or otherwise unresolvable identity-path alias sets
- tombstone DTO or response mapping
- key-change DTO or response mapping

## Integration tests

Required backend integration coverage:

1. insert stamps a new `ChangeVersion`
2. `UPDATE OF EdfiDoc` bumps `ChangeVersion`
3. authorization-only maintenance update does not bump `ChangeVersion`
4. changed-resource query filters correctly by `ProjectName`, `ResourceName`, and window
5. changed-resource query preserves authorization filtering
6. delete inserts one tombstone row with copied natural-key and authorization data
7. delete rollback leaves no tombstone row committed
8. delete query authorization matches live-query authorization semantics
9. identity-changing update inserts one key-change tracking row with copied old-key, new-key, and authorization data
10. non-identity update does not insert a key-change tracking row
11. representation rewrite caused by an upstream identity change does not insert a key-change row when the dependent resource's own identity tuple is unchanged
12. key-change query collapses multiple key changes for one resource within a window correctly
13. key-change query authorization matches live-query authorization semantics
14. `availableChangeVersions` returns correct bounds
15. optional journal trigger inserts exactly one row per committed representation change
16. optional `journal + verify` execution filters stale journal candidates correctly

## End-to-end tests

Required E2E coverage:

1. normal collection GET without `minChangeVersion` remains unchanged
2. GET by id remains unchanged
3. changed-resource query returns current resources within the requested window
4. `/deletes` returns tombstones in deterministic order
5. `/keyChanges` returns collapsed key-change rows in deterministic order
6. malformed or inconsistent change-query windows preserve ODS-compatible `200 OK` behavior
7. unauthorized callers do not see unauthorized changed resources
8. unauthorized callers do not see unauthorized deletes
9. unauthorized callers do not see unauthorized key changes
10. insert then delete before sync returns only the delete row
11. delete then reinsert yields a delete row and a later live resource in later windows
12. multiple key changes before sync yield one collapsed key-change row
13. replaying the same window is safe after client crash recovery

## Scenario Expectations

| Scenario | Expected behavior |
| --- | --- |
| Multiple updates before sync | Return the current resource once |
| ChangeVersion gaps from rollback | Preserve ordering; gaps are acceptable |
| Large change window | Deterministic ordering with pagination |
| Client crash mid-window | Replay the same bounded window safely |
| Request older than retained window | Preserve ODS-compatible `200 OK` behavior; exact body must match ODS parity |
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
- journal growth and candidate verification costs if the optional journal is enabled

Expected success criteria:

- resource-scoped live queries use the new live-row index or the journal index when enabled
- tombstone queries use the tombstone resource-window index
- normal resource-scoped Change Queries do not require full-table scans
- optional journal candidate queries use `journal + verify` rather than attempting to materialize historical payloads

## Retention

The feature needs a retention policy for delete tombstones, key-change tracking rows, and, if enabled, the optional journal.

Recommended operational starting point:

- do not purge immediately at first rollout
- define the production retention period before enabling purge jobs
- expect a 30-90 day range unless product or operations decide otherwise

When purge is eventually enabled:

- `oldestChangeVersion` must reflect the oldest retained value across all tracking artifacts the feature depends on
- if all rows in a tracked source are purged, the implementation should not infer an incorrect lower bound from emptiness alone
- a persisted retention floor should be introduced at that point

## Monitoring

Recommended metrics:

- current maximum `ChangeVersion`
- minimum retained `ChangeVersion`
- tombstone row count
- key-change tracking row count
- optional journal row count
- changed-resource query latency
- delete-query latency
- key-change-query latency
- `availableChangeVersions` latency
- query plans for live scans or journal candidate scans

## Risks and Mitigations

## Snapshot-free paging drift

Risk:

- offset paging may drift if rows move out of the requested window during retrieval

Mitigation:

- require bounded windows
- document the behavior
- ensure clients only advance their watermark after the full window succeeds

## False positives from authorization maintenance

Risk:

- authorization projection updates may emit change records if triggers are too broad

Mitigation:

- scope stamp triggers to representation changes on `EdfiDoc`
- emit optional journal rows only when `ChangeVersion` changes

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
- the design avoids snapshots while documenting the resulting paging tradeoffs
- the optional journal changes only internal execution, not public contract
- the package can be decomposed into implementation stories without reopening major architecture decisions
