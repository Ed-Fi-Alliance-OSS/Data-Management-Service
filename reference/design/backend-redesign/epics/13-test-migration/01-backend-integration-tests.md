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

The existing profile E2E suite under `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Profiles` provides useful API-contract smoke coverage for profile header negotiation, basic field filtering, and a few simple profiled merge/creatability cases, but it does not satisfy this story by itself. In particular, the legacy suite does not prove the relational-backend runtime cases that motivated `DMS-1124`: guarded no-op metadata stability for profiled writes, profiled POST-as-update target handling, visible-row delete with hidden-row survival, visible-but-absent non-collection delete-vs-clear behavior, deterministic hidden-gap ordinal recomputation, or the deeper `_ext` / nested-collection / reference / embedded-object profiled write shapes that were previously marked unsupported by the host.

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
  - `ProfileVisibleRowDeleteWithHiddenRowPreservation` is not treated as satisfied by omission-preserves-hidden-item smoke checks alone; runtime assertions must prove that visible-row deletes commit while hidden rows for the same scope instance survive untouched and are reconstituted correctly,
  - `ProfileVisibleButAbsentNonCollectionScope` proves the separate-table delete-vs-inlined-clear split for profiled visible-but-absent scopes rather than only asserting that omitted members disappear from API output,
  - hidden-member preservation assertions cover key-unified canonical storage, synthetic presence flags, and hidden reference/descriptor bindings where those bindings are driven by hidden profiled members,
  - hidden-member preservation coverage is not limited to scalar fields already visible through the legacy suite; it must include inlined/root-row hidden bindings, hidden extension columns, and hidden FK/descriptor-derived bindings on matched visible rows/scopes,
  - `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` is paired with an update-allowed/create-denied case so existing visible scopes/items still update normally, including the three-level parent-create-denied/child-denied chain,
  - `ProfileRootCreateRejectedWhenNonCreatable` also proves final in-session POST target handling so profile root-creatability checks run only for true create-new requests and not for profiled POST-as-update requests that resolve to an existing document,
  - `ProfileUnchangedWriteGuardedNoOp` preserves `_etag` / `_lastModifiedDate` / `ChangeVersion` for unchanged profiled `PUT` and unchanged profiled `POST`-as-update requests, and
  - the previously unsupported legacy profile shapes now run successfully end-to-end against the relational backend where applicable, including nested profiled child-collection writes plus the profiled `_ext`, reference, and embedded-object write cases needed by the shared scenario matrix, and
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
5. Reuse legacy profile E2E assets only as starting points where helpful, but add new relational-runtime assertions for the gaps not covered by the existing suite:
   - profiled POST-as-update create-vs-update target resolution,
   - visible-row delete with hidden-row survival,
   - visible-but-absent non-collection separate-table delete vs inlined clear-only-visible-bindings behavior,
   - deterministic hidden-gap ordinal recomputation for interleaved hidden/visible siblings,
   - hidden `_ext` row and `_ext` child-collection preservation on matched visible writes,
   - guarded no-op metadata stability for unchanged profiled `PUT` and profiled `POST`-as-update requests, and
   - end-to-end coverage for the nested profiled collection / `_ext` / reference / embedded-object write shapes that legacy host-level profile tests previously marked unsupported.
