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

- issue changed-resource queries
- issue delete queries
- issue key-change queries
- compute `availableChangeVersions`
- insert tombstones on delete
- insert key-change tracking rows when natural keys change
- optionally issue journal-backed candidate queries when `dms.DocumentChangeEvent` is enabled

Informative design-target touchpoints:

- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IQueryHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Build/Steps/ExtractInputs/IdentityJsonPathsExtractor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`

The transitional `EdFi.DataManagementService.Old.Postgresql` compatibility project may still be consulted as a current-behavior reference, but these touchpoints define the intended implementation seams so the planned replacement backend can absorb the feature without reframing the architecture.

## Storage layer

Responsibilities:

- maintain the global sequence
- stamp canonical rows on insert and representation change
- retain delete tombstones
- retain key-change tracking rows
- optionally retain live change journal entries

Key tables:

- `dms.Document`
- `dms.DocumentDeleteTracking`
- `dms.DocumentKeyChangeTracking`
- optional `dms.DocumentChangeEvent`
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

The public feature contract supports two compatible internal execution patterns for changed-resource queries.

## Pattern A: live-row execution

This pattern queries `dms.Document` directly.

Behavior:

- filter by `ProjectName`, `ResourceName`, and the requested `ChangeVersion` window
- apply the existing authorization filters
- order by `ChangeVersion`, `DocumentPartitionKey`, and `Id`
- return the current `EdfiDoc` payloads

Why it exists:

- it is the minimum required implementation for the feature
- it does not require a new live-change journal table
- it satisfies the spike preference for a canonical `ChangeVersion` column model

## Pattern B: journal-assisted execution

This pattern queries `dms.DocumentChangeEvent` first and verifies candidates against `dms.Document`.

Behavior:

1. read candidate rows from `dms.DocumentChangeEvent`
2. filter candidates by resource and `ChangeVersion` window
3. page deterministically by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`
4. join back to `dms.Document`
5. keep only rows where `dms.Document.ChangeVersion = dms.DocumentChangeEvent.ChangeVersion`
6. apply the existing authorization filters
7. return the current `EdfiDoc` payloads for surviving documents

Why it exists:

- it aligns to the backend-redesign `journal + verify` model
- it reduces direct scanning of the hot canonical table for large change windows
- it does not change the public API contract

## Execution model selection

The feature design deliberately separates public contract from internal selection strategy.

Required rule:

- the same API contract must work whether the implementation uses direct live-row scans or the optional journal-assisted path

This allows implementation planning to treat `dms.DocumentChangeEvent` as a conditional story without reopening the feature contract.

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
- apply key-change-query authorization predicates against the copied tracking-row authorization columns
- materialize `oldKeyValues` and `newKeyValues` in the canonical alias order derived from the routed resource's `IdentityJsonPaths`
- collapse multiple rows for the same resource within the window to the earliest `oldKeyValues`, latest `newKeyValues`, and latest `ChangeVersion`
- order final results by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`

## Available Change Versions Execution

The endpoint returns one synchronization surface regardless of the internal execution pattern.

If the journal is not enabled:

- live side derives from `dms.Document.ChangeVersion`
- delete side derives from `dms.DocumentDeleteTracking.ChangeVersion`
- key-change side derives from `dms.DocumentKeyChangeTracking.ChangeVersion`

If the journal is enabled:

- live side derives from `dms.DocumentChangeEvent.ChangeVersion`
- delete side derives from `dms.DocumentDeleteTracking.ChangeVersion`
- key-change side derives from `dms.DocumentKeyChangeTracking.ChangeVersion`

The response remains:

- `oldestChangeVersion`
- `newestChangeVersion`

## Identity-Change Update Execution Order

Within an update transaction that changes natural-key values for a resource that supports identity updates, the implementation must:

1. load the current document summary, current key values, and current authorization projection
2. authorize the update against the live row
3. apply the update so the live row contains the new representation
4. allocate a new live `ChangeVersion`
5. derive the new key values and canonical key aliases from `ResourceSchema.IdentityJsonPaths`
6. insert a row into `dms.DocumentKeyChangeTracking` containing the old key values, new key values, and copied authorization projection
7. commit the transaction

This ordering is mandatory because key changes must preserve both sides of the natural-key transition while staying aligned to the committed live representation.

## Delete Path Execution Order

Within the delete transaction, the implementation must:

1. load the current document summary and authorization projection
2. authorize the delete against the live row
3. allocate a new `ChangeVersion`
4. derive `keyValues` and canonical key aliases from `ResourceSchema.IdentityJsonPaths`
5. insert a row into `dms.DocumentDeleteTracking`
6. perform existing hierarchy-specific cleanup where required
7. delete the live `dms.Document` row
8. allow existing FK cascades to remove aliases, references, authorization companion rows, and optional journal rows

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
- the optional journal is never used as a delete store

## Implementation Surfaces for Planning

The main implementation workstreams implied by this architecture are:

- API routing and request validation
- core service branching and middleware updates
- schema and trigger deployment
- tombstone insert logic in delete execution
- key-change tracking insert logic in update execution
- changed-resource query SQL
- delete-query SQL
- key-change-query SQL
- `availableChangeVersions` computation
- optional journal-assisted execution
- unit, integration, and E2E validation
