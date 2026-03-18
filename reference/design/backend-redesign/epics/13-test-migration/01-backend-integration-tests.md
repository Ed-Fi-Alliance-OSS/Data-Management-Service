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
- profile-constrained write scenarios from the shared baseline in `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md`, including root creatability, hidden-data preservation, deterministic hidden-gap collection ordering, visible-vs-hidden non-collection behavior, `_ext` preservation, and collection/non-collection merge behavior keyed by compiled semantic identity rather than request ordinal

Tests run against provisioned PostgreSQL/SQL Server using docker compose (no Testcontainers).

This story reuses the shared profile scenario baseline defined in `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md`.

## Shared Profile Scenario Baseline

- `NoProfileWriteBehavior`
- `FullSurfaceCollectionReorder`
- `ProfileVisibleRowUpdateWithHiddenRowPreservation`
- `ProfileVisibleRowDeleteWithHiddenRowPreservation`
- `ProfileVisibleButAbsentNonCollectionScope`
- `ProfileHiddenInlinedColumnPreservation`
- `ProfileRootCreateRejectedWhenNonCreatable`
- `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`
- `ProfileHiddenExtensionRowPreservation`
- `ProfileHiddenExtensionChildCollectionPreservation`
- `ProfileUnchangedWriteGuardedNoOp`

## Acceptance Criteria

- Integration tests validate:
  - persisted relational state is correct (basic invariants),
  - response JSON is correct after reconstitution,
  - reference validation works (missing refs fail),
  - delete conflicts are reported correctly,
  - the shared profile scenario baseline above runs end-to-end,
  - `NoProfileWriteBehavior` includes one `FullSurfaceCollectionReorder` case that proves semantic-identity-based row matching rather than request ordinal,
  - `ProfileVisibleRowUpdateWithHiddenRowPreservation` covers no-previously-visible, interleaved update-plus-insert, nested collection, and extension child-collection variants under the deterministic hidden-gap ordering rule,
  - `ProfileVisibleRowDeleteWithHiddenRowPreservation` covers the delete-all-visible-while-hidden-rows-remain case,
  - `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` is paired with an update-allowed/create-denied case so existing visible scopes/items still update normally,
  - `ProfileUnchangedWriteGuardedNoOp` preserves `_etag` / `ChangeVersion`, and
  - profile-based validation/creatability failures return consistent HTTP error semantics.
- Tests can be run locally via documented commands/scripts.

## Tasks

1. Create a set of small fixture schemas + sample payloads for CRUD and the shared profile scenario baseline above, explicitly carrying the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`.
2. Implement integration test helpers that:
   - provision DB,
   - run DMS with the relational backend,
   - execute HTTP requests with and without profile media types and assert responses/persisted state.
3. Add a test category for integration tests and wire into CI as appropriate.
4. Add fixtures/assertions covering the shared profile scenario baseline above, including semantic-identity-based visible-row matching rather than request ordinal, hidden-data preservation across base and `_ext` scopes, visible-vs-hidden non-collection behavior, update-allowed/create-denied pairings, and unchanged-write guarded no-op behavior.
