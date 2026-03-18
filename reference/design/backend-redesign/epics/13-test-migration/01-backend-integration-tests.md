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
- profile-constrained write scenarios for root creatability, hidden-data preservation including hidden inlined members and hidden extension columns on matched rows, deterministic hidden-gap collection ordering across no-previously-visible, delete-all-visible, interleaved update-plus-insert, nested-collection, and extension-child-collection cases, `_ext` preservation, visible-vs-hidden non-collection behavior, and collection/non-collection merge behavior keyed by compiled semantic identity rather than request ordinal

Tests run against provisioned PostgreSQL/SQL Server using docker compose (no Testcontainers).

## Acceptance Criteria

- Integration tests validate:
  - persisted relational state is correct (basic invariants),
  - response JSON is correct after reconstitution,
  - reference validation works (missing refs fail),
  - unchanged PUT / POST-as-update requests are successful no-ops that preserve `_etag` / `ChangeVersion`,
  - delete conflicts are reported correctly,
  - profiled `POST` create requests reject non-creatable root resources,
  - profiled writes distinguish update-of-existing-visible-data from create-of-new-visible-data for non-collection scopes and collection items,
  - profiled updates preserve hidden stored data while applying visible changes,
  - profiled updates preserve hidden inlined parent/root-row values on matched visible scopes,
  - profiled updates preserve hidden extension columns on matched visible `_ext` rows,
  - visible-but-absent profiled non-collection scopes delete separate-table rows or clear inlined parent/root-row values correctly,
  - profile-filtered collections preserve hidden rows/hidden columns while merging visible items, including the rule "start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously",
  - profile-filtered collection coverage includes no previously visible row, delete-all-visible while hidden rows remain, visible updates plus inserts with hidden rows interleaved, nested collection scopes, extension child collections, and semantic-identity-based visible-row matching rather than request ordinal,
  - profiled `_ext` data preserves hidden resource-level rows, collection/common-type extension rows, hidden extension columns on matched visible rows, and extension child collections while visible data merges normally, and
  - profile-based validation/creatability failures return consistent HTTP error semantics.
- Tests can be run locally via documented commands/scripts.

## Tasks

1. Create a set of small fixture schemas + sample payloads for CRUD and profile-constrained scenarios, explicitly including hidden-gap collection-ordering cases for no previously visible row, delete-all-visible while hidden rows remain, visible updates plus inserts with hidden rows interleaved, nested collection scopes, extension child collections, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, hidden-vs-visible-absent non-collection cases, hidden `_ext` rows/child collections, and update-allowed/create-denied creatability pairs for non-collection scopes and collection items.
2. Implement integration test helpers that:
   - provision DB,
   - run DMS with the relational backend,
   - execute HTTP requests with and without profile media types and assert responses/persisted state.
3. Add a test category for integration tests and wire into CI as appropriate.
4. Add fixtures/assertions covering unchanged writes and the required profile scenarios, including root-create denial, hidden-data preservation, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, hidden-vs-visible-absent non-collection behavior, hidden-gap collection ordering for no-previously-visible, delete-all-visible, interleaved update-plus-insert, nested-collection, and extension-child-collection cases, hidden `_ext` row/child-collection preservation, update-allowed/create-denied creatability pairs, and profile-aware collection/non-collection behavior.
