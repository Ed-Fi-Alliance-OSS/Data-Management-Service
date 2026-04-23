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

**Snapshot technology decision record (required before CQ-STORY-07):**

The snapshot source must be a read-consistent, freezable view of the live database that DMS can reach through an instance-scoped published binding at request time. The technology that provides this view is engine-specific and must be decided before implementing `Use-Snapshot` infrastructure (CQ-STORY-07). The following options are evaluated per engine:

| Engine | Option | Verdict |
| --- | --- | --- |
| SQL Server | Native database snapshot (`CREATE DATABASE ... AS SNAPSHOT OF`) | **Preferred for on-premises SQL Server and SQL Server on IaaS.** Instantaneous, read-consistent, copy-on-write. Operations publish one named snapshot binding per instance and refresh it through a controlled publish/retire workflow. **Not supported on Azure SQL Database or Azure SQL Managed Instance.** |
| SQL Server | Read-committed standby (log shipping) | Not suitable; not frozen; does not provide pass-stability. |
| SQL Server (Azure SQL Database / Azure SQL Managed Instance) | Azure SQL-compatible snapshot strategy (e.g., Hyperscale named database copy, PIT restore to transient read-only instance) | **Decision required in CQ-STORY-00.** Native `CREATE DATABASE ... AS SNAPSHOT OF` is not available on PaaS SQL Server targets. The correct Azure SQL approach — including connection-string configuration and lifecycle semantics — is a required exit criterion for CQ-STORY-00 before CQ-STORY-07 begins. |
| PostgreSQL | Streaming physical-replication read replica (e.g., AWS RDS read replica) | **Not suitable by itself;** replica continues receiving new writes and cannot be frozen for a pass. |
| PostgreSQL | Point-in-time restore into a transient read-only instance (e.g., RDS PIT restore) | **Viable but slow** (minutes to provision); not suitable for O(seconds) snapshot refresh cycles; suitable for infrequent scheduled snapshots where SLA allows. |
| PostgreSQL | Export/import (pg_dump + restore) | Offline only; not suitable for live pass-stability. |
| PostgreSQL | Logical-replication standby at a fixed LSN | Complex schema-conflict risk; not recommended for initial delivery. |
| PostgreSQL | **Recommended:** A separate PostgreSQL instance provisioned from a base backup or cloud snapshot (e.g., Aurora cluster clone, RDS snapshot restore), treated as a frozen read-only derivative for one snapshots cycle. Operations publish the clone endpoint as the active binding for the instance; the clone is not promoted and receives no new writes. | **Preferred for PostgreSQL.** Acceptable refresh granularity for scheduled snapshot cycles (e.g., nightly or hourly). |

Required implementation contract regardless of technology:

- the snapshot connection string is configured per DMS instance in a `SnapshotConnectionString` (or equivalent) application setting
- DMS never automatically falls back to the live primary when `Use-Snapshot = true` and the snapshot source is unavailable
- DMS validates, at `Use-Snapshot = true` request time, that the currently active snapshot binding responds and exposes all required Change Query artifacts before allowing the request to proceed
- snapshot lifecycle management (creation, publication, refresh, retirement) is an operational responsibility outside the DMS application; DMS validates only whether the currently active binding is usable for the current request, because the public contract does not include a binding id or pass token for cross-request pinning
- a new story **CQ-STORY-00: Snapshot Infrastructure Provisioning and Configuration** must be added (or equivalent sub-task in CQ-STORY-07) before CQ-STORY-07 is implemented, covering: snapshot technology selection, `SnapshotConnectionString` configuration schema, lifecycle validation logic, and engine-specific DDL for snapshot creation/retirement

Operational rules:

- the snapshot source is read-only and instance-scoped
- snapshot configuration is a per-instance binding to one usable snapshot derivative; `Use-Snapshot = true` is invalid for an instance that has no active binding
- the bound derivative must expose the same DMS schema, `dms.Document` rows, journals, tombstones, key-change rows, `dms.EducationOrganizationHierarchyTermsLookup`, and all authorization companion tables listed in `05-Authorization-and-Delete-Semantics.md` that are still used during tracked-change authorization evaluation; omitting any of these tables from the snapshot will produce silently wrong tracked-change authorization results or query-time failures
- the bound derivative must expose enough operational identity or lifecycle metadata for operators to know which published derivative is active and to manage publish/retire workflows safely
- if no snapshot source is configured, ordinary live-flow requests still work exactly as they do today and `Use-Snapshot = true` requests fail explicitly
- snapshot refresh or recreation must occur only after the matching migrations, backfill, and application version are in place
- clients that rely on snapshot-backed synchronization must use `availableChangeVersions` and all later data reads against that same snapshot surface
- a published snapshot binding for an instance should be treated as operationally immutable for the full supported synchronization window; operators should not repoint that binding while snapshot-backed clients may still be using it because DMS-843 does not provide cross-request pinning
- snapshot refresh is a publish/retire operation with runbook discipline, not an invisible in-place substitution that DMS-843 promises to bridge transparently across requests
- if operations retire, refresh, or repoint the bound derivative between requests, DMS guarantees only request-time binding resolution and validation; clients that require one frozen pass must restart after operators confirm a stable published binding

## `AuthorizationBasis` Contract Evolution

Operational rules:

- every retained `AuthorizationBasis` payload must carry a supported positive-integer `contractVersion`
- incompatible changes to a resource's tracked-change authorization contract must bump `contractVersion`
- deployments must validate that retained tombstone and key-change rows use only supported `contractVersion` values before serving tracked-change requests
- if claim-set refresh or deployment rollout removes support for a retained `contractVersion`, DMS must fail with the documented security-configuration behavior until rows are migrated, purged/reinitialized, or backward-compatible evaluation support is restored

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
- direct-write identity-update paths and downstream propagated updates are validated to prove `IdentityVersion` advances for every committed identity-changing write and does not advance for non-identity updates

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
- `Use-Snapshot = true` with no usable snapshot source returns `404 Not Found` `application/problem+json`
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
- fail-fast schema-compilation rejection when an extension-contributed identity path introduces an alias collision against existing base-resource identity-path aliases (i.e., the shortest-unique-suffix algorithm cannot produce distinct field names across the merged base-plus-extension identity-path set)
- fail-fast authorization-metadata validation at initial claim-set load and on claim-set cache refresh for change-query-enabled resources whose tracked-change authorization contract requires `AuthorizationBasis` but lacks a valid supported `contractVersion`, `basisDocumentIds`, and optional `relationshipInputs` mapping
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
11. delete query authorization matches the documented tracked-change authorization contract, including ODS-style delete-aware relationship cases and the accepted DMS-specific ownership exception
12. delete query suppresses a tombstone when the same resource identity is already live again on the selected source at delete-query evaluation time, without applying a requested-window predicate to the surviving live row (live-primary flow)
12a. delete query suppresses a tombstone when the same resource identity is already live again on the **snapshot source** when `Use-Snapshot = true`; suppression must not consult the live primary during a snapshot-backed pass
13. identity-changing update inserts one key-change tracking row with copied old-key, new-key, and tracked-change authorization data
14. identity-changing update records a distinct public key-change token from `dms.ChangeVersionSequence`, not the live resource `ChangeVersion` and not `IdentityVersion`, on `dms.DocumentKeyChangeTracking`
15. non-identity update does not insert a key-change tracking row
16. representation rewrite caused by an upstream identity change does not insert a key-change row when the dependent resource's own identity tuple is unchanged
17. key-change query collapses the authorized tracking rows for one affected resource item within a window into one response row containing the window's initial `oldKeyValues`, final `newKeyValues`, and final tracked `changeVersion`
18. key-change query authorization uses the stored pre-update tracked-change authorization data as documented
19. `/keyChanges` applies `totalCount`, `offset`, and `limit` after authorization filtering and collapse over the final key-change result set
20. `availableChangeVersions` returns correct bounds, including bootstrap `0/0` when the synchronization surface is empty
21. `availableChangeVersions` with `Use-Snapshot = true` returns the snapshot-visible ceiling rather than the live-primary ceiling
22. if a later retention phase is added, replay-floor enforcement uses `minChangeVersion < oldestChangeVersion`
23. journal trigger inserts exactly one row per committed representation change
24. required `journal + verify` execution filters stale journal candidates correctly
25. required `journal + verify` execution applies `totalCount`, `offset`, and `limit` to the final verified authorized changed-resource result set rather than to raw journal candidates
26. required `journal + verify` execution may use bounded internal candidate-read batches per page build, while continuing batch reads until page fill or window exhaustion with deterministic continuation
27. concurrent identity-changing update and delete attempts against the same document serialize under the write-path locking contract and do not capture stale pre-change data
28. tombstones and key-change rows preserve `CreatedByOwnershipTokenId` and the tracked-change authorization basis data needed for redesign authorization concepts
29. write-path capture fails explicitly when required tracked-change authorization inputs cannot be reduced to the declared `AuthorizationBasis` contract for the routed resource, and no tombstone or key-change row is committed in that failure path
30. startup and claim-set refresh both reject unsupported retained `AuthorizationBasis.contractVersion` values before tracked-change routes are served

## End-to-end tests

Required E2E coverage:

1. normal collection GET remains unchanged when both `minChangeVersion` and `maxChangeVersion` are absent and `Use-Snapshot` is not requested
2. GET by id remains unchanged
3. changed-resource query returns current resources within the requested window
4. `/deletes` returns tombstones in deterministic order
5. `/keyChanges` returns one collapsed row per affected resource item in deterministic order
6. `minChangeVersion = 0` is accepted when used as a bootstrap watermark
7. `maxChangeVersion = minChangeVersion` returns a valid single-version bounded-window response
8. max-only windows are accepted on changed-resource, `/deletes`, and `/keyChanges`
9. `Use-Snapshot = false` or an omitted header preserves the current live behavior
10. snapshot-backed changed-resource queries return the snapshot-visible resources in the requested bounded window and do not drift when later live writes occur
11. snapshot-backed `/deletes` and `/keyChanges` return the snapshot-visible tracked rows from the same synchronization surface used by `availableChangeVersions`
12. malformed `Use-Snapshot` values return `400 Bad Request` problem details with the documented `type`, `title`, `detail`, `validationErrors`, `errors`, and `correlationId`
13. `Use-Snapshot = true` with no usable snapshot source returns `404 Not Found` problem details with the documented snapshot-unavailable contract
14. malformed, negative, or otherwise invalid change-query windows return `400 Bad Request` problem details with the documented `type`, `title`, `detail`, `validationErrors`, `errors`, and `correlationId`
15. if a later retention phase is added, requests where `minChangeVersion < oldestChangeVersion` return `409 Conflict` problem details with the documented replay-floor fields
16. `availableChangeVersions` returns bootstrap `0/0` before any retained tracking rows exist
17. delete or identity-changing update requests whose tracked-change authorization capture cannot satisfy the declared `AuthorizationBasis` contract return `500 Internal Server Error` problem details with the documented security-configuration type and do not commit partial tracking rows
18. `/deletes` and `/keyChanges` return the canonical public resource `id` sourced from `DocumentUuid`
19. when Change Queries is disabled, `/deletes`, `/keyChanges`, and `availableChangeVersions` return `404 Not Found` without being interpreted as ordinary resource routes
20. unauthorized callers do not see unauthorized changed resources
21. unauthorized callers do not see unauthorized deletes under the documented tracked-change authorization rules
22. unauthorized callers do not see unauthorized key changes under the documented tracked-change authorization rules
23. insert then delete before sync returns only the delete row
24. delete then reinsert in the same window suppresses the delete row and returns only the later live resource for that window
25. delete then reinsert in a later window suppresses the earlier tombstone whenever the later live row is already visible on the selected source at delete-query evaluation time; otherwise the earlier tombstone may still appear
26. multiple key changes before sync yield one collapsed key-change row per affected resource item with the initial and final key values for the window
27. `/keyChanges` for a resource that does not support identity updates returns `200 OK` with an empty array
28. `/keyChanges` paging and `totalCount` semantics apply after authorization filtering and collapse over the final key-change result set
29. required `journal + verify` changed-resource execution preserves the documented public paging semantics
30. unsupported retained `AuthorizationBasis.contractVersion` values produce the documented security-configuration failure until corrected
31. required `journal + verify` changed-resource execution preserves deterministic continuation and page-build correctness when bounded internal candidate-read batching is used
32. readable profile headers do not alter or block `/deletes`, `/keyChanges`, or `availableChangeVersions`
33. changed-resource mode remains resource-level under readable profiles even when the filtered payload appears unchanged

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
| Delete then reinsert in the same window | Suppress the delete row and expose only the later live state for that window |
| Delete then reinsert in a later window | Suppress the earlier tombstone whenever the later live row is already visible on the selected source at delete-query evaluation time; otherwise the earlier tombstone may still appear |
| Multiple key changes in one window | Return one collapsed key-change row per affected resource item with the initial and final key values for the window |
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
- **`AuthorizationBasis` capture overhead on the delete and identity-changing-update write paths**

Expected success criteria:

- resource-scoped live queries use the journal resource-window index
- tombstone queries use the tombstone resource-window index
- normal resource-scoped Change Queries do not require full-table scans
- live candidate queries use `journal + verify` rather than attempting to materialize historical payloads

**`AuthorizationBasis` write-path overhead acknowledgment:**

Populating `AuthorizationBasis.basisDocumentIds` at delete or identity-changing-update time requires resolving basis-resource `DocumentId` values from the live current-backend resolver graph before the tombstone or key-change row is inserted. For complex relationship-based strategies, this is additional I/O on the hot delete or identity-changing-update path.

Required mitigations:

- the `AuthorizationBasis` resolution query must run inside the same transaction as the tombstone or key-change-row insert, using the row-locked pre-change state; it must not execute after the live row or capture-time resolver rows have been deleted or mutated
- basis-resource `DocumentId` resolution may reuse the same per-request authorization resolution pass that already authorizes the delete or update; no additional round-trips are required if that pass already resolves the same basis-resource identifiers
- on the current PostgreSQL backend, this work is expected to require dedicated capture-time resolver queries against `StudentSecurableDocument`, `ContactSecurableDocument`, `StaffSecurableDocument`, and `EducationOrganizationHierarchy`; the existing `Get*Authorization*Ids` helper methods that aggregate EdOrg ids are not sufficient for `AuthorizationBasis.basisDocumentIds`
- deployments must baseline-measure delete and identity-changing-update latency for high-relationship resources (e.g., `StudentSchoolAssociation`, `StaffEducationOrganizationAssignment`) before and after enabling Change Queries to confirm the additional I/O is within tolerance
- if `AuthorizationBasis` resolution latency is unacceptable for a specific resource, the tracked-change authorization contract for that resource may declare a simplified `basisDocumentIds` mapping that avoids deep relationship joins, provided the simplified mapping preserves the required authorization visibility guarantees

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
- `Use-Snapshot = true` when no usable snapshot source is available returns `404 Not Found` problem details
- invalid or negative `minChangeVersion` returns `400 Bad Request` problem details
- invalid or negative `maxChangeVersion` returns `400 Bad Request` problem details
- `maxChangeVersion < minChangeVersion` returns `400 Bad Request` problem details
- delete and identity-changing update requests that cannot capture the tracked-change authorization data required by the routed resource return `500 Internal Server Error` problem details
- if a later retention phase introduces replay-floor advancement, `minChangeVersion < oldestChangeVersion` returns `409 Conflict` problem details

ODS compatibility note:

- legacy ODS snapshot-missing flows surface `404 Not Found`; DMS-843 keeps that status while still documenting an explicit DMS problem-details body for the case
- invalid `Use-Snapshot` handling is an explicit DMS contract choice; DMS validates the header as a strict boolean even though legacy ODS commonly treats non-true values as snapshot-off

Problem-detail expectations:

- all responses include `type`, `title`, `status`, `detail`, `validationErrors`, `errors`, and `correlationId`
- for the documented change-query parameter-validation and replay-floor failures, `validationErrors` is present as an empty object, `{}`, rather than being omitted
- feature-disabled collection GET requests use type `urn:ed-fi:api:change-queries:feature-disabled`
- invalid `Use-Snapshot` uses type `urn:ed-fi:api:change-queries:validation:use-snapshot`
- unavailable snapshot source uses type `urn:ed-fi:api:change-queries:snapshot:not-found`
- invalid `minChangeVersion` uses type `urn:ed-fi:api:change-queries:validation:min-change-version`
- invalid `maxChangeVersion` uses type `urn:ed-fi:api:change-queries:validation:max-change-version`
- invalid window relationship uses type `urn:ed-fi:api:change-queries:validation:window`
- tracked-change write-side authorization-capture failure uses type `urn:ed-fi:api:system-configuration:security`
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

- the same routes and tracking artifacts can provide one frozen synchronization surface across a multi-request pass when the active snapshot binding remains unchanged
- bounded windows and offset paging are stable for that snapshot-backed pass only while the pass continues to read the same snapshot binding
- the pass sees the state current inside that snapshot binding, not the later state of the live primary
- snapshot lifecycle, refresh discipline, and availability become operational dependencies outside the core tracking schema
- if the snapshot source is unavailable, DMS fails explicitly rather than silently falling back to live reads

## Risks and Mitigations

## Live-mode paging drift

Risk:

- offset paging may drift if rows move out of the requested window during retrieval

Mitigation:

- document the live-mode tradeoffs explicitly
- use the normative bounded-window algorithm with `maxChangeVersion = synchronizationVersion` and persist the pass-start synchronization version only after the full pass succeeds
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

## Key-change event ordering or authorization drift

Risk:

- key-change queries could return missing events, duplicated events, the wrong old/new keys, or unauthorized rows if tracking or event ordering logic is incomplete

Mitigation:

- capture explicit old and new key values in `dms.DocumentKeyChangeTracking`
- copy the tracked-change authorization data into each key-change tracking row
- test multi-event ordering and authorization parity explicitly

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
- delete plus re-add churn is suppressed from the public `/deletes` surface whenever the same natural-key identity is already visible again on the selected source at delete-query evaluation time
- key changes remain visible after later key mutations or deletion of the live row
- live changed-resource authorization remains aligned to current live-read semantics
- delete and key-change authorization target the documented tracked-change contract, including ODS-style delete-aware relationship visibility and the accepted DMS-specific ownership exception
- tracked-change artifacts preserve redesign ownership and DocumentId-based authorization inputs
- the key payload contract stays deterministic even when a resource reuses the same leaf name in multiple identity paths
- the design avoids snapshot history tables, preserves the current live mode, and explicitly documents both the live and snapshot-backed synchronization behaviors and obligations
- `Use-Snapshot` requests fail explicitly when a snapshot source is unavailable and are never silently downgraded to live reads
- multi-resource synchronization order is explicit for `keyChanges`, changed resources, and deletes
- the public error contract is normative, including ProblemDetails members and types
- `/deletes` and `/keyChanges` expose the canonical public resource `id`
- the required journal changes only internal execution, not public contract
- the package can be decomposed into implementation stories without reopening major architecture decisions
