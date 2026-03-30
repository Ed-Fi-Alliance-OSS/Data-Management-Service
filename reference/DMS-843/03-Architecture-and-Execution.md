# Architecture and Execution

## Architecture Overview

The feature is implemented across four layers.

The component and file references in this document are informative implementation touchpoints only. They are included to anchor current-behavior analysis and future planning; the design itself is defined by responsibilities, contracts, and behavior rather than by a specific project layout.

## API layer

Responsibilities:

- route Change Query requests
- preserve current route behavior for non-Change-Query requests
- parse the optional `Use-Snapshot` header on synchronization reads
- perform authentication and instance resolution
- dispatch requests into the existing DMS processing pipeline

Informative current touchpoints:

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/CoreEndpointModule.cs`

## Core service layer

Responsibilities:

- parse and validate `minChangeVersion` and `maxChangeVersion`
- parse and validate `Use-Snapshot`
- preserve current GET semantics when change-query parameters are absent
- branch into changed-resource, delete, key-change, or available-change-version handling
- resolve whether the request must execute against the live primary source or the configured snapshot source
- preserve current live-read authorization and resource-schema resolution rules for changed resources
- enforce the documented tracked-change authorization contract for `/deletes` and `/keyChanges`, including the accepted DMS-specific ownership exception

Informative current touchpoints:

- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ParsePathMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateEndpointMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateQueryMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProvideAuthorizationFiltersMiddleware.cs`

## Repository and SQL layer

Responsibilities:

- issue redesign-aligned `journal + verify` changed-resource queries
- issue delete queries
- issue key-change queries
- compute `availableChangeVersions`
- resolve the selected read source for change-query reads and execute authorization plus data selection against that same source
- apply same-window delete re-add suppression on the public `/deletes` surface
- insert tombstones on delete
- insert key-change tracking rows when natural keys change
- preserve tracked-change authorization inputs needed for ownership and DocumentId-based authorization after deletes or later key changes
- provision and resolve resource-key lookups for changed-resource execution

Informative design-target touchpoints:

- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IQueryHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Build/Steps/ExtractInputs/IdentityJsonPathsExtractor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

The transitional `EdFi.DataManagementService.Old.Postgresql` compatibility project may still be consulted as a current-behavior reference, but these touchpoints define the intended implementation seams so the planned replacement backend can absorb the feature without reframing the architecture.

## Storage layer

Responsibilities:

- maintain the global sequence
- maintain resource-key lookup rows
- stamp canonical rows on insert, representation change, and identity change
- retain the live change journal
- retain delete tombstones
- retain key-change tracking rows

Key tables:

- `dms.ResourceKey`
- `dms.Document`
- `dms.DocumentChangeEvent`
- `dms.DocumentDeleteTracking`
- `dms.DocumentKeyChangeTracking`
- `dms.EducationOrganizationHierarchyTermsLookup`
- current authorization companion tables

## Routing Strategy

## Why dedicated routes are required

The current GET path handling supports:

- `/{projectNamespace}/{endpointName}`
- `/{projectNamespace}/{endpointName}/{id}`

It does not support:

- `/{projectNamespace}/{endpointName}/deletes`
- `/{projectNamespace}/{endpointName}/keyChanges`

If `/deletes` is pushed through the current generic route parser, the parser treats `deletes` as an item id candidate.

## Required route model

Keep the generic GET route for:

- normal collection GET
- changed-resource collection GET
- GET by id

Add dedicated routes for:

- `/{routePrefix}changeQueries/v1/availableChangeVersions`
- `/{routePrefix}data/{projectNamespace}/{endpointName}/deletes`
- `/{routePrefix}data/{projectNamespace}/{endpointName}/keyChanges`

The dedicated `/deletes` and `/keyChanges` routes must be registered before the current catch-all data route so ASP.NET Core resolves them first.

Feature-off routing rule:

- when `AppSettings.EnableChangeQueries = false`, the application must still reserve the dedicated Change Query path shapes so `/deletes`, `/keyChanges`, and `availableChangeVersions` return the documented `404 Not Found`
- implementation may do this either by keeping dedicated routes registered and short-circuiting them behind the feature flag, or by an equivalent pre-routing reservation mechanism
- the generic item-by-id path must never interpret `deletes` or `keyChanges` as an item id when evaluating feature-off requests

## Execution Model

Changed-resource execution uses the backend-redesign `journal + verify` model against the selected read source.

Behavior:

1. resolve whether the request targets the live primary source or the configured snapshot source
2. resolve the routed resource to `dms.ResourceKey.ResourceKeyId`
3. read candidate rows from `dms.DocumentChangeEvent`
4. filter candidates by `ResourceKeyId` and the requested `ChangeVersion` window
5. join candidates back to `dms.Document`
6. keep only rows where `dms.Document.ChangeVersion = dms.DocumentChangeEvent.ChangeVersion`
7. apply the existing authorization filters from that same selected source
8. continue candidate processing as needed so public `totalCount`, `offset`, and `limit` are evaluated over the surviving verified authorized rows rather than over raw journal candidates
9. order the final surviving verified authorized rows by `ChangeVersion` plus a stable backend-local document tie-breaker (`DocumentPartitionKey`, `DocumentId` on the current backend; `DocumentId` alone on redesign-aligned backends where `DocumentPartitionKey` is absent)
10. return the current `EdfiDoc` payloads for surviving documents in that selected source

Required paging rule:

- the `journal + verify` path may over-fetch candidates or use an equivalent query shape, but it must preserve the public paging and `totalCount` semantics of the changed-resource contract
- internal candidate rows eliminated by verification or authorization must not consume public page slots
- the public changed-resource order must remain `ChangeVersion` plus the stable backend-local document tie-breaker even when the implementation over-fetches or verifies candidates in batches
- to avoid unbounded single-query scans under high-churn resources, implementations may use bounded internal candidate-read batches per page build (for example, capped batch size with deterministic continuation tokens)
- bounded internal batches must remain transparent to the public contract: continue reading batches until the page is full or the requested window is exhausted, and preserve deterministic ordering and `totalCount` semantics throughout

**Concrete batch-sizing guidance:**

The batch multiplier affects both latency and the number of round-trips needed to fill a page. A conforming starting point is:

- initial candidate batch size: `max(100, limit * 4)` where `limit` is the requested page size (default `25`)
- if the first batch does not fill the page after verification and authorization, continue fetching additional batches of the same size using a deterministic cursor continuation token `(AfterChangeVersion, AfterDocumentId)` until the page is full or the window is exhausted
- cap the total candidates scanned per page build at a configurable upper bound (suggested default: `limit * 100`) and return a partial page rather than scanning indefinitely; surface a server-side warning log when the cap is reached so deployments can investigate pathological churn
- implementations should make the batch multiplier and scan cap configurable at the backend level to allow tuning without redeployment

Example cursor continuation query shape (PostgreSQL):

```sql
WITH candidates AS (
    SELECT e.ChangeVersion, e.DocumentPartitionKey, e.DocumentId
    FROM dms.DocumentChangeEvent e
    WHERE e.ResourceKeyId = @ResourceKeyId
      AND e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
      AND (
        e.ChangeVersion > @AfterChangeVersion OR
        (e.ChangeVersion = @AfterChangeVersion AND e.DocumentPartitionKey > @AfterDocumentPartitionKey) OR
        (e.ChangeVersion = @AfterChangeVersion AND e.DocumentPartitionKey = @AfterDocumentPartitionKey AND e.DocumentId > @AfterDocumentId)
      )
    ORDER BY e.ChangeVersion, e.DocumentPartitionKey, e.DocumentId
    LIMIT @CandidateBatchSize
)
SELECT c.ChangeVersion, c.DocumentPartitionKey, c.DocumentId
FROM candidates c
JOIN dms.Document d
  ON d.DocumentPartitionKey = c.DocumentPartitionKey
 AND d.Id = c.DocumentId
 AND d.ChangeVersion = c.ChangeVersion
ORDER BY c.ChangeVersion, c.DocumentPartitionKey, c.DocumentId;
```

For redesign-aligned backends where `DocumentPartitionKey` is absent, replace the three-column cursor continuation with a two-column `(ChangeVersion, DocumentId)` cursor and join `dms.Document` on `DocumentId` only.

Required architectural rule:

- do not treat direct `dms.Document` range scans as the normative changed-resource execution model for DMS-843
- the required live-query path is the same narrow-journal approach described in the backend-redesign update-tracking docs

## Delete Query Execution

Delete query execution is always tombstone-based and always runs against the selected read source.

Behavior:

- read from `dms.DocumentDeleteTracking`
- filter by `ProjectName`, `ResourceName`, and the requested window
- these shared tracking tables stay resource-scoped by canonical project and resource identity, so the routed-resource filter remains on those columns even though the live change journal uses `ResourceKeyId` as its narrow filter key
- apply the documented tracked-change authorization predicates against the tombstone's preserved authorization data, including ODS-style delete-aware relationship visibility plus the accepted DMS-specific ownership exception and row-local basis values needed for custom-view authorization, using the same selected source for any companion authorization reads
- suppress tombstones whose **natural-key identity** is represented again by a current live row for the same routed resource within the same requested window on that same selected source; natural-key identity is compared by matching the tombstone's stored `keyValues` against the live row's identity values derived from `ResourceSchema.IdentityJsonPaths`; comparison is always natural-key-based, not `DocumentUuid`-based, because delete-then-reinsert always creates a new live row with a new `DocumentUuid` even when the natural key is identical
- materialize `keyValues` in the canonical alias order derived from the routed resource's `IdentityJsonPaths`
- order by `ChangeVersion` plus a stable backend-local document tie-breaker (`DocumentPartitionKey`, `DocumentId` on the current backend; `DocumentId` alone on redesign-aligned backends where `DocumentPartitionKey` is absent)

Delete query execution never reads `dms.DocumentChangeEvent` as a delete source, but it may consult the current live surface to apply the required same-window re-add suppression.

## Key Change Query Execution

Key change query execution is always tracking-row-based and always runs against the selected read source.

Behavior:

- read from `dms.DocumentKeyChangeTracking`
- filter by `ProjectName`, `ResourceName`, and the requested window
- these shared tracking tables stay resource-scoped by canonical project and resource identity, so the routed-resource filter remains on those columns even though the live change journal uses `ResourceKeyId` as its narrow filter key
- apply the documented tracked-change authorization predicates against the copied pre-update tracking-row authorization data, including ODS-style relationship visibility plus the accepted DMS-specific ownership exception and row-local basis values needed for DocumentId-based custom-view authorization, using the same selected source for any companion authorization reads
- materialize `oldKeyValues` and `newKeyValues` in the canonical alias order derived from the routed resource's `IdentityJsonPaths`
- order by `ChangeVersion` plus a stable backend-local document tie-breaker (`DocumentPartitionKey`, `DocumentId` on the current backend; `DocumentId` alone on redesign-aligned backends where `DocumentPartitionKey` is absent)
- apply `totalCount`, `offset`, and `limit` to that authorized event stream rather than to raw pre-authorization tracking rows

Required semantics:

- if the routed resource does not support identity updates, return an empty result set and `totalCount = 0` when requested
- authorization filtering happens before ordering, paging, and `totalCount`
- each surviving key-change tracking row becomes one public key-change event row
- paging and total-count semantics apply after authorization filtering over that event result set
- the copied authorization data on each key-change row represents the pre-update tracked-change authorization state for that transition

## Available Change Versions Execution

The endpoint returns one synchronization surface regardless of the internal execution pattern, but the surface comes from the same selected read source as the later data queries.

This follows the Ed-Fi ODS/API client workflow, which captures one synchronization version from `availableChangeVersions` and reuses it across `keyChanges`, changed-resource queries, and `deletes`; see the [client guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/).

The participating sources are:

- live side from `dms.DocumentChangeEvent.ChangeVersion`
- delete side from `dms.DocumentDeleteTracking.ChangeVersion`
- key-change side from `dms.DocumentKeyChangeTracking.ChangeVersion`

Supporting structures that participate in the same selected source:

- `dms.ChangeVersionSequence` as the source-local allocation ceiling for `newestChangeVersion`
- `dms.ResourceKey` for resource resolution used by changed-resource execution
- source-local authorization companion artifacts required to evaluate tracked-change authorization for `/deletes` and `/keyChanges`

The response remains:

- `oldestChangeVersion`
- `newestChangeVersion`

Required meaning:

- `newestChangeVersion` is the ceiling across the active synchronization surface
- `newestChangeVersion` is derived from the selected source's `dms.ChangeVersionSequence` high-watermark (equivalent to `next value - 1`)
- the endpoint does not derive `newestChangeVersion` from max committed retained row value across participating tracking tables
- when `Use-Snapshot = true`, the active synchronization surface is the snapshot-visible surface rather than the live-primary surface
- for the initial DMS-843 scope, `oldestChangeVersion` is `0`
- when all participating sources are empty and no replay-floor metadata exists, both bounds are `0`; this is ODS-output-compatible behavior (legacy ODS also returns `0/0` on an empty instance) rather than a DMS-specific sentinel
- if a later retention phase introduces purge, `oldestChangeVersion` becomes the replay floor for that same surface and the endpoint derives non-zero floors from persisted replay-floor metadata

## Multi-Resource Synchronization Ordering

For synchronization runs that span multiple resources:

- process `keyChanges` for applicable resources in dependency order
- process changed-resource queries in dependency order
- process `/deletes` in reverse-dependency order

Where available, use Ed-Fi resource dependency metadata or an equivalent dependency graph derived from DMS schemas and references.

## Client-Visible Consistency Mode

The change-tracking artifacts defined by DMS-843 do not require snapshot history tables.

DMS-843 uses a client-selectable `Use-Snapshot` header to choose between the two synchronization flows.

Required interpretation:

- when `Use-Snapshot` is absent or `false`, each request executes against the ordinary current committed state visible to the API at that moment
- when `Use-Snapshot = true`, each request executes against the configured read-only snapshot source for the resolved DMS instance
- this mirrors ODS instance-derivative flow: one resolved instance plus one selected derivative per synchronization request
- the snapshot source must expose the same DMS schema, change-tracking artifacts, and required authorization companion artifacts as the live primary
- the selected derivative must include equivalent synchronization structures: `dms.ChangeVersionSequence`, `dms.ResourceKey`, `dms.DocumentChangeEvent`, `dms.DocumentDeleteTracking`, `dms.DocumentKeyChangeTracking`, `dms.EducationOrganizationHierarchyTermsLookup`, and all authorization companion tables listed in the authorization companion table inventory in `05-Authorization-and-Delete-Semantics.md` that are still used during tracked-change authorization evaluation
- authorization filters, resource-key resolution, and data selection for one request must all run against the same chosen source
- the configured snapshot binding for that instance must remain stable for one bounded synchronization pass; if operations refresh or retire the derivative before the pass completes, later `Use-Snapshot = true` requests fail explicitly rather than switching to a different derivative
- `availableChangeVersions`, changed-resource reads, `/keyChanges`, and `/deletes` for one synchronization pass must reuse the same derivative selection to keep watermarks and data reads coherent
- non-snapshot synchronization under concurrent writes remains best-effort rather than gap-free
- snapshot-backed synchronization uses the same tracking artifacts but changes the completeness guarantee by pinning reads to the snapshot source
- DMS never silently falls back to a live read when `Use-Snapshot = true`
- snapshot lifecycle management is in scope for DMS-843: DMS must validate and preserve a consistent snapshot derivative for the requested snapshot-backed pass while keeping the public API contract unchanged

## Write-Path Locking Contract

Identity-changing updates and deletes must run inside a single transaction that locks the target live `dms.Document` row before reading old keys, tracked-change authorization data, or delete key values.

Required rules:

- acquire a row-level write lock on the target `dms.Document` row before step 1 of either write path
- hold that lock until the tracking-row insert and final commit or rollback are complete
- concurrent update/update, update/delete, and delete/delete operations against the same document must serialize so only one committed path observes and records each pre-change state
- PostgreSQL implementations should use `SELECT ... FOR UPDATE`; MSSQL implementations must use `SELECT ... WITH (UPDLOCK, ROWLOCK)` on the target row; any other engine must use an equivalent row-level serialization mechanism

**Cascade propagation locking rule:**

When an identity-changing update triggers downstream propagation to dependent documents, each dependent document row that will be rewritten in the same transaction must also be row-locked **before** any identity extraction, tombstone capture, or key-change-row construction for that dependent row.

Required rules for cascade propagation locking:

- before reading the pre-change state of any dependent document `D` in the propagation transaction, acquire a row-level write lock on `D` using the same engine-appropriate mechanism as the primary write
- acquire **all** row-level write locks for the transaction — including the primary target row and every dependent row — in a single globally consistent ascending `DocumentId` order pass before reading any pre-change state; the "primary first" rule from the basic write-path locking contract applies only when there are no downstream cascade dependents; in a cascade propagation transaction the primary target participates in the same ascending-`DocumentId` ordering as the dependents
- PostgreSQL: build the full set of `DocumentId` values to be locked (primary + all dependents), sort them ascending, and issue `SELECT ... FOR UPDATE` in that order
- MSSQL: build the full set of `DocumentId` values to be locked (primary + all dependents), sort them ascending, and issue `SELECT ... WITH (UPDLOCK, ROWLOCK)` in that order
- this ordering is deadlock-safe across concurrent cascading transactions because the Ed-Fi reference model is a directed acyclic graph (DAG) that guarantees no circular cascade chains; the only remaining deadlock vector is two concurrent transactions that touch overlapping dependent rows in different orderings, which the global ascending-`DocumentId` sort eliminates
- relying solely on FK cascade write-intent locks is not sufficient; those locks do not prevent a concurrent `DELETE` from reading stale identity values on the about-to-be-cascaded row before the propagating transaction updates it
- hold all locks until the full transaction commits or rolls back

## Downstream Identity-Propagation Execution

An identity-changing update can force representation rewrites on dependent documents whose stored representation embeds the changed identity values.

Required execution model:

1. apply the direct identity-changing write to the target document in one transaction
2. use storage-layer propagation metadata, generated update plans, or equivalent database-driven machinery to identify and rewrite dependent stored identity values in that same transaction; persisted reverse-reference metadata such as the current backend's `dms.Reference` may inform that storage-layer work, but correctness must not depend on ad hoc core-service recursion coverage
3. when a dependent row's served representation changes, allocate a new dependent `ChangeVersion` and let the normal `dms.DocumentChangeEvent` journaling rules emit a dependent live-change row
4. when the propagated rewrite also changes the dependent document's own identity tuple, allocate a new dependent `IdentityVersion` and insert a dependent key-change tracking row using that dependent row's own pre-change tracked-change authorization data
5. when the dependent rewrite does not change the dependent document's own identity tuple, do not insert a dependent key-change row
6. commit only after all propagated dependent rewrites, stamps, and tracking rows are durable

Conforming implementation note:

- the current backend may still use persisted reverse-reference metadata such as `dms.Reference` to determine affected rows, but that remains a storage-layer implementation detail rather than the normative API/core-service contract boundary
- a replacement relational backend may realize the same outcome through stored reference-identity columns, FK/trigger cascades, or other propagation metadata, but it must preserve the same committed-state rewrite, stamping, journaling, and key-change outcomes

## Identity-Change Update Execution Order

Within an update transaction that changes natural-key values for a resource that supports identity updates, the implementation must:

1. lock and load the current document summary, current key values, and current pre-update tracked-change authorization data
2. authorize the update against the live row
3. apply the update so the live row contains the new representation
4. allocate a new live `ChangeVersion`
5. allocate a new live `IdentityVersion`
6. derive the new key values and canonical key aliases from `ResourceSchema.IdentityJsonPaths`
7. allocate a new distinct public key-change token for the tracking row from `dms.ChangeVersionSequence`
8. insert a row into `dms.DocumentKeyChangeTracking` with that distinct public key-change token, plus the old key values, new key values, and copied pre-update tracked-change authorization data
9. allow the storage-layer propagation path to apply any required downstream dependent rewrites, restamps, and key-change inserts in the same transaction
10. commit the transaction

This ordering is mandatory because key changes must preserve both sides of the natural-key transition, and downstream referrers must be restamped and journaled before the transaction commits.

The copied authorization data on the tracking row is intentionally the pre-update tracked-change authorization state. This design does not store a second post-update authorization snapshot for key-change rows.

The key-change row does not expose or store either the live `ChangeVersion` or the internal `IdentityVersion` as its public synchronization token. `IdentityVersion` remains an internal live-row stamp used for alignment and future identity-tracking needs.

## Delete Path Execution Order

Within the delete transaction, the implementation must:

1. lock and load the current document summary and tracked-change authorization data
2. authorize the delete against the live row
3. allocate a new `ChangeVersion`
4. derive `keyValues` and canonical key aliases from `ResourceSchema.IdentityJsonPaths`
5. insert a row into `dms.DocumentDeleteTracking`
6. perform existing hierarchy-specific cleanup where required
7. delete the live `dms.Document` row
8. allow existing FK cascades to remove aliases, references, authorization companion rows, and journal rows

This ordering is mandatory because the tombstone must preserve natural-key and tracked-change authorization data before the live row and companion rows disappear.

## Design Invariants

The architecture must preserve these invariants:

- no existing non-change-query request changes behavior unless the caller explicitly opts into `Use-Snapshot`
- representation-changing writes get a unique `ChangeVersion`
- authorization-only maintenance updates do not emit false change records
- delete visibility survives deletion of the live row
- same-window delete plus re-add churn is suppressed from the public `/deletes` surface
- key-change visibility survives later changes to the live key state
- identity-driven downstream rewrites are restamped and journaled in the same committed write path through storage-layer propagation responsibilities rather than through API/core-layer recursion guarantees
- delete-query authorization targets the documented tracked-change contract, including ODS-style delete-aware relationship visibility plus the accepted DMS-specific ownership exception and redesign custom-view concepts
- key-change-query authorization targets the documented tracked-change contract using the stored pre-update tracked-change authorization state, including the accepted DMS-specific ownership exception
- write paths serialize concurrent update and delete capture on the same live row
- the live journal is never used as a delete store
- snapshot-backed reads reuse the same journal, tombstone, and key-change artifacts; only the read source and resulting completeness guarantees differ from the live mode

## Implementation Surfaces for Planning

The main implementation workstreams implied by this architecture are:

- API routing and request validation
- core service branching and middleware updates
- live-vs-snapshot read-source resolution for synchronization requests
- schema and trigger deployment
- resource-key lookup provisioning and live-row backfill
- tombstone insert logic in delete execution
- key-change tracking insert logic in update execution
- changed-resource `journal + verify` SQL
- delete-query SQL
- key-change-query SQL
- `availableChangeVersions` computation
- `IdentityVersion` capture for identity-changing writes
- unit, integration, and E2E validation
