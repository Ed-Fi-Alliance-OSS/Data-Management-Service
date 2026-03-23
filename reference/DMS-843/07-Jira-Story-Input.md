# Jira Story Input

## Purpose

This document decomposes the consolidated Change Queries feature design into story-sized implementation slices that can be used as direct input for Jira story creation.

The intent is not to prescribe sprint sequencing rigidly. The intent is to provide a technically coherent breakdown with dependencies, scope boundaries, and acceptance themes.

The story slices are normative planning inputs. The project and file references listed under "Likely implementation areas" are informative planning aids only.

Likely implementation areas below point to the design-target seams and shared or dialect-specific relational backend modules intended for the replacement path. The transitional `EdFi.DataManagementService.Old.Postgresql` project may still be consulted where current-behavior parity must be confirmed, but the stories are not anchored to that project structure and must account for both PostgreSQL and MSSQL backend targets.

## Proposed Epic Structure

## Epic A: Core Change Queries feature

Covers the public API contract, redesign-aligned update-tracking artifacts, delete tracking, key-change tracking, query execution, and test coverage needed for the feature to exist.

## Epic B: Later retention phase

Covers purge policy, replay-floor metadata, and `409 Conflict` behavior if production retention is introduced after the core feature is shipped.

This epic is explicitly deferred and is not required for the initial DMS-843 delivery.

## Story Candidates

## CQ-STORY-01: Add redesign-aligned live-row schema and deterministic backfill

Objective:

- add `dms.ResourceKey`
- add `dms.Document.ResourceKeyId`
- add `dms.Document.ChangeVersion`
- add `dms.Document.IdentityVersion`
- create required supporting indexes
- backfill existing rows deterministically
- enable live-row stamping for inserts and representation changes

Key acceptance themes:

- every live row has non-null `ResourceKeyId`, `ChangeVersion`, and `IdentityVersion`
- inserts allocate unique `ChangeVersion` and `IdentityVersion` values
- representation-changing updates allocate new `ChangeVersion` values
- identity-changing updates allocate new `IdentityVersion` values
- authorization-only maintenance updates do not allocate new `ChangeVersion` or `IdentityVersion` values
- the rollout sequence is safe for existing data

Likely implementation areas:

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

Dependencies:

- none

## CQ-STORY-02: Add required live journal with redesign-aligned `ResourceKeyId` filtering

Objective:

- create `dms.DocumentChangeEvent`
- backfill one journal row per current live row
- emit journal rows whenever `dms.Document.ChangeVersion` changes
- key candidate selection by `ResourceKeyId`

Key acceptance themes:

- one journal row exists per committed live representation change
- journal rows contain `ChangeVersion`, `DocumentPartitionKey`, `DocumentId`, and `ResourceKeyId`
- stale journal candidates are filtered by verification against `dms.Document.ChangeVersion`
- delete operations remove live journal rows by cascade and still preserve tombstones
- the journal remains a live-change artifact only and is never used as a delete store

Likely implementation areas:

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

Dependencies:

- `CQ-STORY-01`

## CQ-STORY-03: Add delete tombstones with natural-key and authorization projection capture

Objective:

- create `dms.DocumentDeleteTracking`
- insert tombstones during delete execution in the same transaction
- derive `keyValues` from `ResourceSchema.IdentityJsonPaths`
- apply the canonical shortest-unique-suffix alias contract when key leaf names repeat
- preserve the live authorization projection on the tombstone

Key acceptance themes:

- delete commits produce exactly one tombstone row
- delete rollbacks produce no tombstone row
- tombstones contain `id`, `changeVersion`, and `keyValues`
- tombstone `keyValues` stay deterministic even for composite reference-based identities with repeated leaf names
- tombstones contain the authorization projection required for delete-query filtering

Likely implementation areas:

- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

Dependencies:

- `CQ-STORY-01`

## CQ-STORY-04: Add key-change tracking with old-key, new-key, and authorization projection capture

Objective:

- create `dms.DocumentKeyChangeTracking`
- insert key-change tracking rows during identity-changing updates in the same transaction
- derive `oldKeyValues` and `newKeyValues` from `ResourceSchema.IdentityJsonPaths`
- apply the canonical shortest-unique-suffix alias contract when key leaf names repeat
- preserve the live authorization projection on the key-change tracking row

Key acceptance themes:

- identity-changing update commits produce exactly one key-change tracking row
- non-identity updates produce no key-change tracking row
- tracking rows contain `id`, `changeVersion`, `oldKeyValues`, and `newKeyValues`
- key payload aliases stay deterministic even when `IdentityJsonPaths` reuse the same leaf names
- tracking rows contain the authorization projection required for key-change-query filtering

Likely implementation areas:

- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateCascadeHandler.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Build/Steps/ExtractInputs/IdentityJsonPathsExtractor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

Dependencies:

- `CQ-STORY-01`

## CQ-STORY-05: Add API routing and request validation for Change Queries

Objective:

- add `AppSettings.EnableChangeQueries`
- add `availableChangeVersions` route
- add `/deletes` route
- add `/keyChanges` route
- accept and validate `minChangeVersion` and `maxChangeVersion`
- define profile-bypass behavior for the new non-resource Change Query GET endpoints
- define exact ProblemDetails contracts for invalid windows, while keeping replay-floor miss behavior deferred to a later retention phase
- preserve current collection GET and item-by-id behavior when change-query parameters are absent

Key acceptance themes:

- Change Queries can be enabled or disabled through `AppSettings.EnableChangeQueries`
- if `AppSettings.EnableChangeQueries` is absent, Change Queries remain disabled
- when the flag is off, the application does not silently fall back to ordinary non-Change-Query behavior for Change Query requests
- `/deletes` resolves as a dedicated route rather than being treated as an item id
- `/keyChanges` resolves as a dedicated route rather than being treated as an item id
- invalid or inconsistent change-query windows return the documented `400 Bad Request` problem details
- replay-floor `409 Conflict` behavior remains deferred until a later retention phase is approved
- changed-resource collection GET continues normal profile behavior, while `/deletes`, `/keyChanges`, and `availableChangeVersions` bypass profile resolution and profile filtering
- changed-resource eligibility remains resource-level even when a readable profile filters the returned representation
- existing non-change-query GET behavior remains unchanged

Likely implementation areas:

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/CoreEndpointModule.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Configuration/AppSettings.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Configuration/AppSettings.cs`
- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateQueryMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ParsePathMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateEndpointMiddleware.cs`

Dependencies:

- none

## CQ-STORY-06: Implement changed-resource queries using required `journal + verify`

Objective:

- add changed-resource candidate selection against `dms.DocumentChangeEvent`
- resolve the routed resource to `ResourceKeyId`
- verify candidates against the current `dms.Document.ChangeVersion`
- preserve current authorization logic
- keep the normal collection GET path unchanged when `minChangeVersion` is absent

Key acceptance themes:

- changed-resource mode returns current live resource payloads only once per resource
- authorization behavior matches current collection GET semantics
- the package documents the non-snapshot tradeoffs and the recommended open-ended `minChangeVersion` synchronization algorithm explicitly
- paging and `totalCount` are evaluated over the verified authorized result set rather than raw journal candidates
- candidate selection is narrow-index-based rather than a direct live-row range scan

Likely implementation areas:

- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IQueryHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

Dependencies:

- `CQ-STORY-01`
- `CQ-STORY-02`
- `CQ-STORY-05`

## CQ-STORY-07: Implement delete queries, key-change queries, and `availableChangeVersions`

Objective:

- implement `/deletes` query execution against tombstones
- implement `/keyChanges` query execution against key-change tracking rows
- compute `oldestChangeVersion` as the effective replay floor and `newestChangeVersion` as the synchronization ceiling
- serialize identity-changing update and delete capture with row-level locking or an engine-equivalent mechanism
- preserve delete-query and key-change-query authorization parity

Key acceptance themes:

- `/deletes` returns tombstones ordered deterministically
- `/keyChanges` returns collapsed rows ordered deterministically
- `/keyChanges` remains valid for resources that do not support identity updates and returns `200 OK` with an empty array for them
- `/deletes` and `/keyChanges` return the canonical public resource `id` sourced from `DocumentUuid`
- `/keyChanges` applies `totalCount`, `offset`, and `limit` after authorization filtering and collapse
- delete-query authorization matches live-query semantics
- key-change-query authorization matches live-query semantics
- `availableChangeVersions` returns one ODS-aligned synchronization surface with correct bootstrap and retained-data bounds for the active tracking artifacts
- multi-resource synchronization guidance is explicit: `keyChanges` and changed resources in dependency order, deletes in reverse-dependency order
- concurrent updates and deletes on the same document do not capture stale old keys or tombstones

Likely implementation areas:

- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IQueryHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

Dependencies:

- `CQ-STORY-01`
- `CQ-STORY-03`
- `CQ-STORY-04`
- `CQ-STORY-05`
- `CQ-STORY-06`

## CQ-STORY-08: Add unit, integration, and E2E coverage plus rollout validation

Objective:

- add test coverage for request validation, changed-resource behavior, delete behavior, authorization parity, and paging semantics
- cover unsupported-resource `/keyChanges` behavior and required `journal + verify` paging parity
- cover canonical key alias derivation for simple, composite, and repeated-leaf identities
- validate resource-key backfill, trigger behavior, and non-breaking route behavior

Key acceptance themes:

- the test matrix in the design package is covered
- existing non-change-query behavior remains unchanged
- crash-replay and edge-case scenarios are exercised

Likely implementation areas:

- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/*`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/*`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/*`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/*`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/*`

Dependencies:

- `CQ-STORY-01`
- `CQ-STORY-02`
- `CQ-STORY-03`
- `CQ-STORY-04`
- `CQ-STORY-05`
- `CQ-STORY-06`
- `CQ-STORY-07`

## Suggested Delivery Order

Recommended technical order:

1. `CQ-STORY-01`
2. `CQ-STORY-05`
3. `CQ-STORY-02`
4. `CQ-STORY-03`
5. `CQ-STORY-04`
6. `CQ-STORY-06`
7. `CQ-STORY-07`
8. `CQ-STORY-08`

## Story Slicing Notes

- `CQ-STORY-01` and `CQ-STORY-05` can proceed mostly in parallel because one is schema-focused and the other is route and validation focused.
- `CQ-STORY-02` stays separate from `CQ-STORY-01` so the live journal can be reviewed and implemented as its own redesign-aligned artifact rather than as an afterthought inside generic backfill work.
- `CQ-STORY-03` should not be merged into `CQ-STORY-01` because the tombstone behavior depends on delete-path application logic and authorization projection copying, not just DDL.
- `CQ-STORY-04` should not be merged into `CQ-STORY-01` because key-change behavior depends on update-path application logic and explicit old/new key capture, not just DDL.
- `CQ-STORY-06` and `CQ-STORY-07` should stay separate because live-query execution and delete/key-change execution are technically different read paths.
- later-phase retention work should stay outside the initial story set so reviewers can approve the core feature without reopening purge policy.

## Story Definition of Ready

Each story should include:

- explicit reference to the relevant numbered-package document sections in `reference/DMS-843`
- a clear statement of public behavior changes or non-changes
- listed data-migration or deployment prerequisites when applicable
- explicit test expectations
- confirmation that the story does not reintroduce snapshot requirements

## Story Definition of Done Themes

A story should be considered done only if:

- code changes and database changes are implemented or explicitly not needed
- unit and integration coverage exists where appropriate
- E2E impact is covered for route or behavior changes
- no existing API behavior regresses
- the resulting artifact still matches the documented change-query synchronization model, including the no-snapshot tradeoffs and saved-watermark rules

## Deferred Later-Phase Work

If the project later approves retention and purge behavior, create a separate story set for:

- `dms.ChangeQueryRetentionFloor`
- replay-floor advancement during purge
- `409 Conflict` replay-floor enforcement
- operational retention windows and purge-job rollout
