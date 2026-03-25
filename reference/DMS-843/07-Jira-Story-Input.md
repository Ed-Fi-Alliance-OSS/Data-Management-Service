# Jira Story Input

## Purpose

This document decomposes the consolidated Change Queries feature design into ticket-ready implementation stories for `DMS-843`.

The goal is not just to name workstreams. The goal is to make each story self-contained enough that an implementer can:

- understand the intended outcome
- see the behavioral contract that must hold
- identify the concrete work items that belong in the story
- understand which other stories must land first
- find the canonical design references without reopening the entire package repeatedly

The numbered design package remains the canonical source of truth. This document is a planning and execution aid built from that package.

## How To Read Each Story

Each story uses the same structure:

- `Description`: the outcome, scope boundary, and non-goals for the story
- `Acceptance Criteria`: the externally verifiable behavior or invariant that must be true when the story is complete
- `Tasks`: the concrete implementation work expected in the story
- `Dependencies`: stories that should land first
- `Design References`: the canonical design documents that govern the story
- `Likely Implementation Areas`: informative codebase touchpoints only

Implementation areas are planning aids, not design constraints. The stories are intended for the replacement relational-backend path and must account for both PostgreSQL and MSSQL targets.

## Proposed Epic Structure

## Epic A: Core Change Queries feature

Covers the public API contract, redesign-aligned update-tracking artifacts, delete tracking, key-change tracking, query execution, and test coverage needed for the feature to exist.

## Epic B: Later retention phase

Covers purge policy, replay-floor metadata, and `409 Conflict` behavior if production retention is introduced after the core feature is shipped.

This epic is explicitly deferred and is not required for the initial DMS-843 delivery.

## Story Candidates

## CQ-STORY-01: Add redesign-aligned live-row schema and deterministic backfill

### Description

This story introduces the live-row schema changes required for Change Queries and prepares existing data safely before any Change Query route is exposed.

The story owns:

- creation and seeding of `dms.ResourceKey`
- addition of `dms.Document.ResourceKeyId`
- addition of `dms.Document.CreatedByOwnershipTokenId` if it is not already present through redesign-auth work
- addition of `dms.Document.ChangeVersion`
- addition of `dms.Document.IdentityVersion`
- supporting indexes on the live row
- deterministic backfill for current live rows
- insert and representation-change stamping on the live row

This story does not own the live journal, delete tombstones, key-change tracking rows, or the public API routes.

### Acceptance Criteria

- `dms.ResourceKey` is seeded from the deployed effective schema inventory rather than from currently populated rows.
- Resources with zero current live rows still receive `dms.ResourceKey` seed rows.
- Every live row has non-null `ResourceKeyId`, `ChangeVersion`, and `IdentityVersion` after backfill.
- If redesign-auth work has not already added it, `dms.Document.CreatedByOwnershipTokenId` exists and is available for tracked-change capture.
- Inserted documents allocate unique `ChangeVersion` and `IdentityVersion` values.
- Representation-changing writes allocate a new `ChangeVersion`.
- Identity-changing writes allocate a new `IdentityVersion`.
- Authorization-only maintenance updates do not allocate a new `ChangeVersion` or `IdentityVersion`.
- Backfill order is deterministic and does not depend on undefined set-based sequence allocation order.
- The rollout sequence does not expose partially backfilled Change Query behavior.

### Tasks

- Add `dms.ResourceKey` with stable seeded `ResourceKeyId` values derived from the deployed effective schema manifest.
- Add nullable `ResourceKeyId`, `CreatedByOwnershipTokenId` if required, `ChangeVersion`, and `IdentityVersion` columns to `dms.Document`.
- Add the required live-row support index for redesign-aligned joins and diagnostics.
- Add the required ownership support index when `CreatedByOwnershipTokenId` is present on `dms.Document`.
- Implement insert-time defaulting or trigger behavior for `ChangeVersion` and `IdentityVersion`.
- Implement representation-change stamping for updates that change `EdfiDoc`.
- Ensure authorization-only maintenance updates do not change the live-row stamps.
- Backfill `ResourceKeyId`, `ChangeVersion`, and `IdentityVersion` in deterministic `DocumentPartitionKey ASC, Id ASC` order.
- Enforce non-null constraints after backfill validation succeeds.
- Add validation checks that prove one sequence allocation per row rather than one shared statement-level value.

### Dependencies

- none

### Design References

- `01-Feature-Summary-and-Decisions.md`
- `04-Data-Model-and-DDL.md`
- `06-Validation-Rollout-and-Operations.md`

### Likely Implementation Areas

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

## CQ-STORY-02: Add required live journal with redesign-aligned `ResourceKeyId` filtering

### Description

This story introduces the required narrow live-change journal used by changed-resource queries.

The story owns:

- creation of `dms.DocumentChangeEvent`
- journal indexes
- journal backfill after live-row backfill
- journal emission whenever `dms.Document.ChangeVersion` changes
- live candidate selection keyed by `ResourceKeyId`

This story does not own the public changed-resource endpoint behavior. It provides the journal artifact and low-level execution foundation required by that endpoint.

### Acceptance Criteria

- One journal row exists for each committed live representation change.
- Journal rows contain `ChangeVersion`, `DocumentPartitionKey`, `DocumentId`, and `ResourceKeyId`.
- Journal rows are emitted only when the live `ChangeVersion` changes.
- Stale journal candidates can be filtered by verification against the current `dms.Document.ChangeVersion`.
- Delete operations remove live journal rows by cascade while preserving delete tombstones in their separate artifact.
- The journal remains a live-change artifact only and is never treated as a delete store.

### Tasks

- Create `dms.DocumentChangeEvent` and its required indexes.
- Backfill one journal row per current live document after live-row backfill is complete.
- Add trigger or equivalent database behavior to emit a journal row when `ChangeVersion` changes.
- Ensure journal emission does not occur for authorization-only maintenance updates that leave `ChangeVersion` unchanged.
- Add repository or SQL support for reading journal candidates by `ResourceKeyId` and window.
- Preserve delete-time cascade semantics so the journal does not become a historical delete artifact.

### Dependencies

- `CQ-STORY-01`

### Design References

- `01-Feature-Summary-and-Decisions.md`
- `03-Architecture-and-Execution.md`
- `04-Data-Model-and-DDL.md`
- `06-Validation-Rollout-and-Operations.md`

### Likely Implementation Areas

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

## CQ-STORY-03: Add delete tombstones with natural-key and tracked-change authorization capture

### Description

This story introduces the delete-tracking artifact and the delete-path write behavior required to preserve delete visibility after the live row is gone.

The story owns:

- creation of `dms.DocumentDeleteTracking`
- tombstone insertion in the delete transaction
- extraction of `keyValues` from `ResourceSchema.IdentityJsonPaths`
- shortest-unique-suffix aliasing for repeated identity leaf names
- copied tracked-change authorization data on the tombstone, including ownership and authorization basis values

This story is about write-path capture. It does not own the public `/deletes` query execution route.

### Acceptance Criteria

- Each committed delete produces exactly one tombstone row.
- Rolled-back deletes produce no committed tombstone row.
- Tombstones contain `id`, `changeVersion`, and `keyValues`.
- `id` is sourced from the canonical public `DocumentUuid`.
- `keyValues` remain deterministic for simple, composite, and repeated-leaf identity shapes.
- Tombstones preserve the tracked-change authorization data required for delete-query filtering after the live row and related relationship rows are removed.
- Tombstone insertion occurs before the live row and companion authorization rows disappear.

### Tasks

- Create `dms.DocumentDeleteTracking` and its resource-window index.
- Capture tombstones in the delete transaction before deleting the live `dms.Document` row.
- Extract `keyValues` from the pre-delete `EdfiDoc` using the canonical identity-path and key-alias rules.
- Copy the current live tracked-change authorization data into the tombstone row, including `CreatedByOwnershipTokenId` and authorization basis values.
- Preserve existing delete authorization behavior so unauthorized callers cannot create tombstone side effects.
- Keep education-organization cleanup ordering compatible with tombstone capture.

### Dependencies

- `CQ-STORY-01`

### Design References

- `02-API-Contract-and-Synchronization.md`
- `03-Architecture-and-Execution.md`
- `04-Data-Model-and-DDL.md`
- `05-Authorization-and-Delete-Semantics.md`

### Likely Implementation Areas

- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

## CQ-STORY-04: Add key-change tracking with old-key, new-key, and tracked-change authorization capture

### Description

This story introduces the identity-transition tracking artifact and the update-path behavior required for `/keyChanges`.

The story owns:

- creation of `dms.DocumentKeyChangeTracking`
- capture of one tracking row per committed identity-changing update
- extraction of `oldKeyValues` and `newKeyValues` from `ResourceSchema.IdentityJsonPaths`
- canonical key-alias derivation for repeated identity leaf names
- copied pre-update tracked-change authorization data on the tracking row, including ownership and authorization basis values
- use of the new live `dms.Document.ChangeVersion` as the public synchronization token on the tracking row

This story does not own the public `/keyChanges` endpoint behavior or collapse semantics. It owns the write-path capture artifact that endpoint depends on.

### Acceptance Criteria

- Each committed identity-changing update produces exactly one key-change tracking row.
- Non-identity updates produce no key-change tracking row.
- Tracking rows contain `id`, `changeVersion`, `oldKeyValues`, and `newKeyValues`.
- Tracking-row `changeVersion` is the new live `dms.Document.ChangeVersion`, not the internal `IdentityVersion`.
- `oldKeyValues` and `newKeyValues` remain deterministic for simple, composite, and repeated-leaf identity shapes.
- The tracking row preserves the pre-update tracked-change authorization data required for transition visibility.
- No key-change row is created for authorization-only maintenance updates.

### Tasks

- Create `dms.DocumentKeyChangeTracking` and its resource-window index.
- Detect identity-changing updates by comparing the pre-update and post-update identity tuples derived from `IdentityJsonPaths`.
- Capture the pre-update identity tuple and tracked-change authorization data before mutating the live row.
- Capture the post-update identity tuple from the updated `EdfiDoc`.
- Insert one tracking row with `ChangeVersion = dms.Document.ChangeVersion` for the committed identity-changing update.
- Ensure dependent representation rewrites do not create key-change rows unless the dependent document's own identity tuple changed.

### Dependencies

- `CQ-STORY-01`

### Design References

- `02-API-Contract-and-Synchronization.md`
- `03-Architecture-and-Execution.md`
- `04-Data-Model-and-DDL.md`
- `05-Authorization-and-Delete-Semantics.md`

### Likely Implementation Areas

- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateCascadeHandler.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Build/Steps/ExtractInputs/IdentityJsonPathsExtractor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

## CQ-STORY-05: Add API routing and request validation for Change Queries

### Description

This story introduces the public API surface and request-validation behavior for Change Queries without yet implementing all query execution logic.

The story owns:

- `AppSettings.EnableChangeQueries`
- route registration for `availableChangeVersions`, `/deletes`, and `/keyChanges`
- change-query parameter parsing and validation
- `Use-Snapshot` parsing and validation
- feature-off behavior
- profile behavior for change-query routes
- exact error-contract behavior for supported validation failures

This story must preserve the existing collection GET and GET-by-id behavior when change-query parameters are absent.

### Acceptance Criteria

- Change Queries can be enabled or disabled through `AppSettings.EnableChangeQueries`.
- If the flag is absent, Change Queries are enabled by default.
- When the flag is off, collection GET requests that supply change-query parameters or `Use-Snapshot = true` return the documented feature-disabled `400 Bad Request` behavior.
- When the flag is off, `/deletes`, `/keyChanges`, and `availableChangeVersions` resolve to `404 Not Found` and do not fall through to generic item-by-id parsing.
- The dedicated `/deletes` and `/keyChanges` paths resolve before the generic catch-all data route.
- Changed-resource mode activates when either `minChangeVersion` or `maxChangeVersion` is supplied.
- Max-only windows are accepted on changed-resource, `/deletes`, and `/keyChanges`.
- `/deletes` and `/keyChanges` accept omitted bounds and return the retained tracked rows for the routed resource.
- `Use-Snapshot = false` or an omitted header preserves the current live behavior.
- `Use-Snapshot = true` is accepted on synchronization reads, invalid values fail with the documented `400 Bad Request` contract, and unavailable snapshot sources fail with the documented `409 Conflict` contract.
- Invalid or inconsistent change-query windows return the documented `400 Bad Request` ProblemDetails contract.
- Replay-floor `409 Conflict` behavior remains deferred until the later retention phase is approved.
- Changed-resource collection GET continues normal profile behavior.
- `/deletes`, `/keyChanges`, and `availableChangeVersions` bypass profile resolution and profile filtering.
- Existing non-change-query GET behavior remains unchanged.

### Tasks

- Add `AppSettings.EnableChangeQueries` to the relevant configuration surfaces.
- Default the setting to enabled when it is absent so DMS aligns with Ed-Fi ODS/API behavior.
- Register `availableChangeVersions`, `/deletes`, and `/keyChanges` as dedicated routes ahead of the generic data route.
- Reserve dedicated route shapes even when the feature flag is off so those paths return the documented `404 Not Found` behavior.
- Parse and validate `minChangeVersion` and `maxChangeVersion` consistently across changed-resource, delete, and key-change requests.
- Parse and validate `Use-Snapshot` consistently across synchronization reads.
- Implement the documented `400 Bad Request` and `409 Conflict` ProblemDetails contracts for feature-disabled collection GET requests, invalid values, invalid window relationships, and unavailable snapshot sources.
- Keep replay-floor `409 Conflict` behavior explicitly deferred to the later retention phase.
- Route changed-resource collection GET through the normal profile pipeline.
- Route `/deletes`, `/keyChanges`, and `availableChangeVersions` through a pipeline that bypasses profile resolution and filtering.

### Dependencies

- none

### Design References

- `01-Feature-Summary-and-Decisions.md`
- `02-API-Contract-and-Synchronization.md`
- `03-Architecture-and-Execution.md`
- `05-Authorization-and-Delete-Semantics.md`
- `06-Validation-Rollout-and-Operations.md`

### Likely Implementation Areas

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Modules/CoreEndpointModule.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Configuration/AppSettings.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Configuration/AppSettings.cs`
- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateQueryMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ParsePathMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateEndpointMiddleware.cs`

## CQ-STORY-06: Implement changed-resource queries using required `journal + verify`

### Description

This story implements changed-resource mode on the existing collection GET route using the required redesign-aligned `journal + verify` execution model.

The story owns:

- candidate selection against `dms.DocumentChangeEvent`
- resource resolution to `ResourceKeyId`
- verification against the current `dms.Document.ChangeVersion`
- authorization filtering on surviving live rows
- execution against either the live primary source or the resolved snapshot source
- final paging, ordering, and `totalCount` semantics

This story must not redefine changed-resource execution as a direct `dms.Document` range scan.

### Acceptance Criteria

- Changed-resource mode returns the latest current resource payload from the selected read source only once per qualifying resource.
- Authorization behavior matches the current collection GET semantics for live reads.
- Candidate selection is journal-driven and keyed by `ResourceKeyId`.
- Stale journal candidates are filtered by verification against the current `ChangeVersion` in the selected read source.
- `Use-Snapshot = true` executes the same `journal + verify` logic against the resolved snapshot source rather than against the live primary.
- Paging and `totalCount` are evaluated over the final verified authorized result set rather than raw journal candidates.
- Final changed-resource results are ordered by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`.
- Ordinary collection GET behavior remains unchanged when both `minChangeVersion` and `maxChangeVersion` are absent unless the caller explicitly opts into `Use-Snapshot`.
- The implementation does not depend on snapshot history tables or historical payload reconstruction.

### Tasks

- Add query execution that reads live candidates from `dms.DocumentChangeEvent` by `ResourceKeyId` and requested window.
- Join candidates back to `dms.Document` and keep only rows whose current `ChangeVersion` still matches the journal row.
- Apply the existing authorization predicates after verification.
- Resolve the live-vs-snapshot read source before query execution and ensure verification plus authorization use that same source.
- Over-fetch or batch candidates as needed so public paging semantics apply to surviving verified authorized rows.
- Materialize the final result set in deterministic `ChangeVersion`, `DocumentPartitionKey`, `DocumentId` order.
- Preserve the normal collection GET path for requests that do not activate changed-resource mode.

### Dependencies

- `CQ-STORY-01`
- `CQ-STORY-02`
- `CQ-STORY-05`

### Design References

- `02-API-Contract-and-Synchronization.md`
- `03-Architecture-and-Execution.md`
- `04-Data-Model-and-DDL.md`
- `05-Authorization-and-Delete-Semantics.md`
- `06-Validation-Rollout-and-Operations.md`

### Likely Implementation Areas

- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IQueryHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

## CQ-STORY-07: Implement delete queries, key-change queries, and `availableChangeVersions`

### Description

This story completes the remaining public synchronization surface beyond changed-resource collection queries.

The story owns:

- `/deletes` execution against tombstones
- `/keyChanges` execution against key-change tracking rows
- `availableChangeVersions` computation across the participating artifacts
- live-vs-snapshot source selection for those synchronization reads
- key-change collapse rules
- delete and key-change authorization semantics
- row-level locking or engine-equivalent serialization on identity-changing updates and deletes

This story does not introduce retention purge or replay-floor metadata. Later-phase retention behavior remains out of scope.

### Acceptance Criteria

- `/deletes` returns tombstones ordered deterministically.
- `/keyChanges` returns collapsed rows ordered deterministically.
- `/keyChanges` remains valid for resources that do not support identity updates and returns `200 OK` with an empty array for them.
- `/deletes` and `/keyChanges` return the canonical public resource `id` sourced from `DocumentUuid`.
- `/keyChanges` applies `totalCount`, `offset`, and `limit` after authorization filtering and collapse.
- Delete-query authorization matches the documented ODS-style tracked-change semantics.
- Key-change-query authorization uses the documented stored pre-update tracked-change authorization data for transition visibility.
- `availableChangeVersions` returns one synchronization surface with correct bootstrap and retained-data bounds for the participating artifacts.
- `Use-Snapshot = true` causes `availableChangeVersions`, `/deletes`, and `/keyChanges` to read the snapshot-visible synchronization surface rather than the live primary surface.
- Multi-resource synchronization guidance remains explicit: `keyChanges` and changed resources in dependency order, deletes in reverse-dependency order.
- Concurrent updates and deletes on the same document do not capture stale old keys or tombstones.

### Tasks

- Implement `/deletes` query execution over `dms.DocumentDeleteTracking`.
- Implement `/keyChanges` query execution over `dms.DocumentKeyChangeTracking`.
- Apply delete-query authorization against the stored tracked-change authorization data on tombstones.
- Apply key-change authorization against the stored pre-update tracked-change authorization data before collapse.
- Resolve the live-vs-snapshot read source before computing `availableChangeVersions`, `/deletes`, and `/keyChanges`, and keep each request on that chosen source throughout execution.
- Implement key-change collapse semantics to preserve earliest `oldKeyValues`, latest `newKeyValues`, and latest surviving `changeVersion`.
- Compute `availableChangeVersions` from the live journal, delete tombstones, and key-change tracking rows rather than from the raw sequence value.
- Implement row-level locking or engine-equivalent serialization for identity-changing updates and deletes so pre-change capture is consistent.
- Preserve the documented empty-array behavior for resources that do not support identity updates.

### Dependencies

- `CQ-STORY-01`
- `CQ-STORY-03`
- `CQ-STORY-04`
- `CQ-STORY-05`
- `CQ-STORY-06`

### Design References

- `01-Feature-Summary-and-Decisions.md`
- `02-API-Contract-and-Synchronization.md`
- `03-Architecture-and-Execution.md`
- `04-Data-Model-and-DDL.md`
- `05-Authorization-and-Delete-Semantics.md`
- `06-Validation-Rollout-and-Operations.md`

### Likely Implementation Areas

- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IQueryHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

## CQ-STORY-08: Add unit, integration, and E2E coverage plus rollout validation

### Description

This story adds the validation depth required to prove the feature behaves as designed and can be rolled out safely.

The story owns:

- unit coverage for routing, parameter validation, alias derivation, and response mapping
- integration coverage for stamping, journaling, tombstones, key-change tracking, authorization, and locking
- E2E coverage for public API behavior, both live and snapshot-backed synchronization modes, and no-regression behavior
- rollout and backfill validation

This story is not a generic testing bucket. Its purpose is to prove the documented change-query contract, especially the tricky concurrency, routing, live-mode, and snapshot-backed behaviors.

### Acceptance Criteria

- The validation matrix from the numbered design package is covered.
- Existing non-change-query behavior remains unchanged.
- Route behavior, feature-off behavior, and invalid-window behavior are exercised.
- Backfill validation proves seeded resource keys, non-null live-row stamps, and journal backfill correctness.
- Crash-replay, duplicate-delivery, snapshot-backed bounded-window, and edge-case scenarios are exercised where the package documents them as expected behavior.
- The test suite proves the required `journal + verify` paging semantics rather than only happy-path retrieval.

### Tasks

- Add unit tests for parameter parsing, feature-off behavior, route dispatch, profile behavior, key alias derivation, and response mapping.
- Add unit tests for parameter parsing, feature-off behavior, `Use-Snapshot`, route dispatch, profile behavior, key alias derivation, and response mapping.
- Add integration tests for live-row stamping, journal emission, delete capture, key-change capture, authorization behavior, `availableChangeVersions`, snapshot-backed reads, and row-level locking semantics.
- Add E2E tests for changed-resource queries, `/deletes`, `/keyChanges`, profile interactions, deterministic ordering, invalid windows, snapshot-backed bounded windows, and unsupported-resource behavior.
- Add rollout validation checks for seeded `dms.ResourceKey` rows, deterministic backfill, and journal backfill completeness.
- Add explicit tests for repeated identity leaf names and for preserving `IdentityJsonPaths` order in key payloads.

### Dependencies

- `CQ-STORY-01`
- `CQ-STORY-02`
- `CQ-STORY-03`
- `CQ-STORY-04`
- `CQ-STORY-05`
- `CQ-STORY-06`
- `CQ-STORY-07`

### Design References

- `02-API-Contract-and-Synchronization.md`
- `04-Data-Model-and-DDL.md`
- `05-Authorization-and-Delete-Semantics.md`
- `06-Validation-Rollout-and-Operations.md`

### Likely Implementation Areas

- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/*`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/*`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/*`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/*`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/*`

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
- `CQ-STORY-03` should not be merged into `CQ-STORY-01` because the tombstone behavior depends on delete-path application logic and tracked-change authorization capture, not just DDL.
- `CQ-STORY-04` should not be merged into `CQ-STORY-01` because key-change behavior depends on update-path application logic and explicit old/new key plus tracked-change authorization capture, not just DDL.
- `CQ-STORY-06` and `CQ-STORY-07` should stay separate because live-query execution and delete/key-change execution are technically different read paths.
- Later-phase retention work should stay outside the initial story set so reviewers can approve the core feature without reopening purge policy.

## Story Definition of Ready

Each story should include:

- a clear `Description` that states the outcome, scope boundary, and major non-goals
- explicit `Acceptance Criteria` tied back to the numbered-package design
- concrete `Tasks` that describe the implementation work expected in the story
- listed dependencies, data-migration prerequisites, and rollout prerequisites when applicable
- explicit test expectations
- confirmation that the story does not reintroduce snapshot history tables or historical payload storage

## Story Definition of Done Themes

A story should be considered done only if:

- code changes and database changes are implemented or explicitly not needed
- unit and integration coverage exists where appropriate
- E2E impact is covered for route or behavior changes
- no existing API behavior regresses
- the resulting artifact still matches the documented change-query synchronization model, including the live-mode tradeoffs, snapshot-backed rules, and saved-watermark rules
- the story can be understood without requiring unstated assumptions outside the numbered package

## Deferred Later-Phase Work

If the project later approves retention and purge behavior, create a separate story set for:

- `dms.ChangeQueryRetentionFloor`
- replay-floor advancement during purge
- `409 Conflict` replay-floor enforcement
- operational retention windows and purge-job rollout
