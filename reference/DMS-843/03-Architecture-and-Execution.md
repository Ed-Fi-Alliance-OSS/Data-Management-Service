# Architecture and Execution

## Architecture Overview

The feature is implemented across four layers.

The component and file references in this document are informative implementation touchpoints only. They are included to anchor current-behavior analysis and future planning; the design itself is defined by responsibilities, contracts, and behavior rather than by a specific project layout.

## API layer

Responsibilities:

- route Change Query requests
- preserve current route behavior for non-Change-Query requests
- perform authentication and instance resolution
- dispatch requests into the existing DMS processing pipeline

Informative current touchpoints:

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/CoreEndpointModule.cs`

## Core service layer

Responsibilities:

- parse and validate `minChangeVersion` and `maxChangeVersion`
- preserve current GET semantics when change-query parameters are absent
- branch into changed-resource, delete, key-change, or available-change-version handling
- preserve current authorization and resource-schema resolution rules

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
- insert tombstones on delete
- insert key-change tracking rows when natural keys change
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

## Execution Model

Changed-resource execution uses the backend-redesign `journal + verify` model.

Behavior:

1. resolve the routed resource to `dms.ResourceKey.ResourceKeyId`
2. read candidate rows from `dms.DocumentChangeEvent`
3. filter candidates by `ResourceKeyId` and the requested `ChangeVersion` window
4. join candidates back to `dms.Document`
5. keep only rows where `dms.Document.ChangeVersion = dms.DocumentChangeEvent.ChangeVersion`
6. apply the existing authorization filters
7. continue candidate processing as needed so public `totalCount`, `offset`, and `limit` are evaluated over the surviving verified authorized rows rather than over raw journal candidates
8. return the current `EdfiDoc` payloads for surviving documents

Required paging rule:

- the `journal + verify` path may over-fetch candidates or use an equivalent query shape, but it must preserve the public paging and `totalCount` semantics of the changed-resource contract
- internal candidate rows eliminated by verification or authorization must not consume public page slots

Required architectural rule:

- do not treat direct `dms.Document` range scans as the normative changed-resource execution model for DMS-843
- the required live-query path is the same narrow-journal approach described in the backend-redesign update-tracking docs

## Delete Query Execution

Delete query execution is always tombstone-based.

Behavior:

- read from `dms.DocumentDeleteTracking`
- filter by `ProjectName`, `ResourceName`, and the requested window
- apply delete-query authorization predicates against the copied tombstone authorization columns
- materialize `keyValues` in the canonical alias order derived from the routed resource's `IdentityJsonPaths`
- order by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`

Delete query execution never reads `dms.DocumentChangeEvent`.

## Key Change Query Execution

Key change query execution is always tracking-row-based.

Behavior:

- read from `dms.DocumentKeyChangeTracking`
- filter by `ProjectName`, `ResourceName`, and the requested window
- apply key-change-query authorization predicates against the copied pre-update tracking-row authorization columns
- materialize `oldKeyValues` and `newKeyValues` in the canonical alias order derived from the routed resource's `IdentityJsonPaths`
- collapse multiple authorized rows for the same resource within the window to the earliest `oldKeyValues`, latest `newKeyValues`, and latest `ChangeVersion`
- apply `totalCount`, `offset`, and `limit` to that collapsed authorized result set rather than to raw tracking rows
- order final results by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`

Required semantics:

- if the routed resource does not support identity updates, return an empty result set and `totalCount = 0` when requested
- authorization filtering happens before collapse
- only rows that survive authorization filtering participate in collapse
- paging and total-count semantics apply after authorization filtering and collapse
- the copied authorization projection on each key-change row represents pre-update visibility for that transition

## Available Change Versions Execution

The endpoint returns one synchronization surface regardless of the internal execution pattern.

This follows the Ed-Fi ODS/API client workflow, which captures one synchronization version from `availableChangeVersions` and reuses it across `keyChanges`, changed-resource queries, and `deletes`; see the [client guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/).

The participating sources are:

- live side from `dms.DocumentChangeEvent.ChangeVersion`
- delete side from `dms.DocumentDeleteTracking.ChangeVersion`
- key-change side from `dms.DocumentKeyChangeTracking.ChangeVersion`

The response remains:

- `oldestChangeVersion`
- `newestChangeVersion`

Required meaning:

- `newestChangeVersion` is the ceiling across the active synchronization surface
- if retained participating rows exist, `newestChangeVersion` is the greatest retained `ChangeVersion` across them
- for the initial DMS-843 scope, `oldestChangeVersion` is `0`
- when all participating sources are empty and no replay-floor metadata exists, both bounds are `0`; that bootstrap value is a starting watermark sentinel rather than a retained change row
- if a later retention phase introduces purge, `oldestChangeVersion` becomes the replay floor for that same surface and the endpoint derives non-zero floors from persisted replay-floor metadata rather than from the current `dms.ChangeVersionSequence` value

## Multi-Resource Synchronization Ordering

For synchronization runs that span multiple resources:

- process `keyChanges` for applicable resources in dependency order
- process changed-resource queries in dependency order
- process `/deletes` in reverse-dependency order

Where available, use Ed-Fi resource dependency metadata or an equivalent dependency graph derived from DMS schemas and references.

## Client-Visible Consistency Mode

The change-tracking artifacts defined by DMS-843 do not require snapshot history tables.

DMS-843 v1 does not define a client-selectable snapshot header, query parameter, or other request mechanism for obtaining one consistent read view across a synchronization pass.

Required interpretation:

- each request executes against the ordinary current committed state visible to the API at that moment
- synchronization under concurrent writes is therefore best-effort rather than gap-free
- a future additive feature could introduce a client-visible consistent-read mode without changing the tracking artifacts defined by DMS-843

## Write-Path Locking Contract

Identity-changing updates and deletes must run inside a single transaction that locks the target live `dms.Document` row before reading old keys, current authorization projection, or delete key values.

Required rules:

- acquire a row-level write lock on the target `dms.Document` row before step 1 of either write path
- hold that lock until the tracking-row insert and final commit or rollback are complete
- concurrent update/update, update/delete, and delete/delete operations against the same document must serialize so only one committed path observes and records each pre-change state
- PostgreSQL implementations should use `SELECT ... FOR UPDATE`; MSSQL implementations must use an equivalent row-level update-locking pattern on the target row; any other engine must use an equivalent row-level serialization mechanism

## Identity-Change Update Execution Order

Within an update transaction that changes natural-key values for a resource that supports identity updates, the implementation must:

1. lock and load the current document summary, current key values, and current pre-update authorization projection
2. authorize the update against the live row
3. apply the update so the live row contains the new representation
4. allocate a new live `ChangeVersion`
5. allocate a new live `IdentityVersion`
6. derive the new key values and canonical key aliases from `ResourceSchema.IdentityJsonPaths`
7. insert a row into `dms.DocumentKeyChangeTracking` containing the old key values, new key values, and copied pre-update authorization projection
8. commit the transaction

This ordering is mandatory because key changes must preserve both sides of the natural-key transition while staying aligned to the committed live representation.

The copied authorization projection on the tracking row is intentionally the pre-update authorization state. This design does not store a second post-update authorization snapshot for key-change rows.

## Delete Path Execution Order

Within the delete transaction, the implementation must:

1. lock and load the current document summary and authorization projection
2. authorize the delete against the live row
3. allocate a new `ChangeVersion`
4. derive `keyValues` and canonical key aliases from `ResourceSchema.IdentityJsonPaths`
5. insert a row into `dms.DocumentDeleteTracking`
6. perform existing hierarchy-specific cleanup where required
7. delete the live `dms.Document` row
8. allow existing FK cascades to remove aliases, references, authorization companion rows, and journal rows

This ordering is mandatory because the tombstone must preserve natural-key and authorization data before the live row and companion rows disappear.

## Design Invariants

The architecture must preserve these invariants:

- no non-change-query request changes behavior
- representation-changing writes get a unique `ChangeVersion`
- authorization-only maintenance updates do not emit false change records
- delete visibility survives deletion of the live row
- key-change visibility survives later changes to the live key state
- delete-query authorization stays logically equivalent to live collection GET authorization
- key-change-query authorization stays logically equivalent to live collection GET authorization
- write paths serialize concurrent update and delete capture on the same live row
- the live journal is never used as a delete store

## Implementation Surfaces for Planning

The main implementation workstreams implied by this architecture are:

- API routing and request validation
- core service branching and middleware updates
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
