---
jira: DMS-1022
jira_url: https://edfi.atlassian.net/browse/DMS-1022
---

# Story: Runtime Integration Tests for Relational Backend (CRUD + Query)

## Description

Add runtime integration tests that exercise the relational backend end-to-end:

- POST upsert
- GET by id
- PUT by id
- DELETE by id
- GET by query paging
- no-profile write scenarios from the shared baseline in `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md`, and
- profile-constrained write scenarios from the shared baseline in `reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor.md`, including root creatability, hidden-data preservation, deterministic hidden-gap collection ordering, visible-vs-hidden non-collection behavior, `_ext` preservation, and collection/non-collection merge behavior keyed by compiled semantic identity rather than request ordinal
- profile-constrained hidden-member coverage includes key-unified canonical storage, synthetic presence flags, and hidden reference/descriptor bindings on matched profiled rows/scopes
- profile-constrained creatability coverage includes the three-level parent-create-denied/child-denied chain from the profile design doc

Tests run against provisioned PostgreSQL/SQL Server using docker compose (no Testcontainers).

This story runs the shared profile scenario matrix defined in `reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md` and reuses the scenario definitions from `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md` and `reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor.md`.

Fixture names and helper APIs in this story should use the shared scenario names from the matrix verbatim.

## Implementation Clarification

This story owns creating an API-level integration harness. The harness should run the DMS HTTP pipeline against real provisioned PostgreSQL and SQL Server relational databases, while using controlled test doubles for nonessential external concerns such as auth/config-service dependencies where appropriate. This is not intended to be a full docker-stack E2E suite.

The backend/unit/database integration layers remain the exhaustive coverage point for unusual apiSchema/profile shapes and deep merge edge cases. The API-level harness should cover representative no-profile and profiled scenarios through HTTP, using the shared scenario names where applicable, and assert both response JSON/error semantics and enough persisted relational state to prove the public pipeline is correctly wired to the relational backend.

Scope boundaries for implementation and review:

- The primary deliverable is the reusable API-level integration harness plus representative coverage through that harness on both PostgreSQL and SQL Server.
- Do not require every shared profile matrix variant to be exercised through HTTP. The shared matrix must remain covered across the test strategy, with exhaustive edge-case coverage allowed to stay in backend/unit/database integration tests.
- Do not require this story to port every existing PostgreSQL-only backend relational-write integration test to SQL Server. Add only the SQL Server coverage needed for the representative API-level scenarios owned by this story.
- Do not expand this story into a full E2E suite or require real CMS/auth service wiring when controlled test doubles can prove the DMS HTTP-to-relational-backend boundary.
- Reviewers should evaluate whether the public DMS pipeline is proven for representative no-profile success, profiled success, profiled failure/error semantics, CRUD/query behavior, and persisted relational state, rather than expecting exhaustive HTTP coverage for every rare schema/profile shape.

## Acceptance Criteria

- Integration tests validate:
  - persisted relational state is correct (basic invariants),
  - response JSON is correct after reconstitution,
  - reference validation works (missing refs fail),
  - delete conflicts are reported correctly,
  - the shared profile scenario matrix from `02-parity-and-fixtures.md` runs end-to-end,
  - `NoProfileWriteBehavior` includes one omitted non-collection scope case, one no-profile `_ext` case, and one `FullSurfaceCollectionReorder` case that proves semantic-identity-based row matching rather than request ordinal,
  - `ProfileVisibleRowUpdateWithHiddenRowPreservation` covers no-previously-visible, interleaved update-plus-insert, nested collection, and extension child-collection variants under the deterministic hidden-gap ordering rule,
  - `ProfileVisibleRowDeleteWithHiddenRowPreservation` covers the delete-all-visible-while-hidden-rows-remain case,
  - hidden-member preservation assertions cover key-unified canonical storage, synthetic presence flags, and hidden reference/descriptor bindings where those bindings are driven by hidden profiled members,
  - `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` is paired with an update-allowed/create-denied case so existing visible scopes/items still update normally, including the three-level parent-create-denied/child-denied chain,
  - `ProfileUnchangedWriteGuardedNoOp` preserves `_etag` / `ChangeVersion`, and
  - profile-based validation/creatability failures return consistent HTTP error semantics.
- Tests can be run locally via documented commands/scripts.

## Tasks

1. Create a set of small fixture schemas + sample payloads for CRUD and the shared profile scenario matrix from `02-parity-and-fixtures.md`, explicitly carrying the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`.
2. Implement integration test helpers that:
   - provision DB,
   - run DMS with the relational backend,
   - execute HTTP requests with and without profile media types and assert responses/persisted state.
3. Add a test category for integration tests and wire into CI as appropriate.
4. Add fixtures/assertions covering the shared profile scenario matrix from `02-parity-and-fixtures.md`, including no-profile omitted-scope and `_ext` coverage, semantic-identity-based visible-row matching rather than request ordinal, hidden-data preservation across base and `_ext` scopes plus key-unified/presence/FK/descriptor bindings, visible-vs-hidden non-collection behavior, update-allowed/create-denied pairings including the three-level chain, and unchanged-write guarded no-op behavior.

## Implementation

The harness implementation lives at `src/dms/tests/EdFi.DataManagementService.Tests.Integration/`. See its README for run commands, fixture map, and the runtime-compatibility materialization details.
