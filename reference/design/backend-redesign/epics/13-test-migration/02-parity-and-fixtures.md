---
jira: DMS-1023
jira_url: https://edfi.atlassian.net/browse/DMS-1023
---

# Story: Cross-Engine Parity Tests and Shared Fixtures

## Description

Ensure the relational redesign behaves consistently across PostgreSQL and SQL Server:

- Same fixtures and test cases run against both dialects, including the shared profile scenario baseline from `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md`.
- Differences are intentional and documented (e.g., error messages where dialect limits differ).
- This story owns the compact shared profile scenario matrix that keeps `DMS-1106`, `DMS-1105`, `DMS-984`, `DMS-1104`, `DMS-1022`, and `DMS-1023` aligned on fixture names and coverage expectations.

## Shared Profile Scenario Matrix

Use these scenario names verbatim in fixtures, helper APIs, acceptance criteria, and parity assertions.

| Scenario | Semantic merge and ordering | Non-collection visibility | Hidden-member overlay | `_ext` preservation | Creatability and failure path | Guarded no-op |
| --- | --- | --- | --- | --- | --- | --- |
| `NoProfileWriteBehavior` | Control path for full-surface writes |  |  |  |  |  |
| `FullSurfaceCollectionReorder` | Primary |  |  |  |  |  |
| `ProfileVisibleRowUpdateWithHiddenRowPreservation` | Primary |  | Primary | Variant coverage |  |  |
| `ProfileVisibleRowDeleteWithHiddenRowPreservation` | Primary |  | Primary | Variant coverage when `_ext` collections participate |  |  |
| `ProfileVisibleButAbsentNonCollectionScope` |  | Primary |  |  |  |  |
| `ProfileHiddenInlinedColumnPreservation` |  | Primary | Primary |  |  |  |
| `ProfileRootCreateRejectedWhenNonCreatable` |  |  |  |  | Primary |  |
| `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` |  | Variant coverage for new visible scopes |  | Variant coverage for extension scopes/items | Primary |  |
| `ProfileHiddenExtensionRowPreservation` |  |  | Primary | Primary |  |  |
| `ProfileHiddenExtensionChildCollectionPreservation` | Primary |  | Primary | Primary |  |  |
| `ProfileUnchangedWriteGuardedNoOp` | Compare/post-merge shape reused | Compare/post-merge shape reused | Hidden preservation must survive compare | Extension rows participate under the same compare rules |  | Primary |

Variant families carried under the shared scenario names:

- `ProfileVisibleRowUpdateWithHiddenRowPreservation`: no-previously-visible rows, interleaved update-plus-insert, nested collection scope, root-level extension child collection, and collection-aligned extension child collection.
- `ProfileVisibleRowDeleteWithHiddenRowPreservation`: delete-all-visible-while-hidden-rows-remain.
- `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`: new visible 1:1 scope, nested/common-type scope, collection/common-type item, extension scope, extension collection item, and a three-level chain where an existing visible middle-level parent still allows descendant update/create while a new visible middle-level parent is rejected because a required member is hidden and therefore blocks descendant extension-child creation.

Story alignment:

- `DMS-1106` consumes the contract-heavy scenarios: `ProfileVisibleRowUpdateWithHiddenRowPreservation`, `ProfileVisibleButAbsentNonCollectionScope`, `ProfileHiddenInlinedColumnPreservation`, `ProfileRootCreateRejectedWhenNonCreatable`, and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`.
- `DMS-1105` reuses nested and `_ext` fixtures from `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileHiddenExtensionChildCollectionPreservation`.
- `DMS-984` owns runtime execution for the full scenario set.
- `DMS-1104` owns the failure semantics for `ProfileRootCreateRejectedWhenNonCreatable` and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, plus invalid usage/forbidden-data cases that are not separate matrix scenarios.
- `DMS-1022` and `DMS-1023` execute the full matrix end-to-end on both engines.

## Acceptance Criteria

- A shared fixture set exists that can run the same CRUD/query and shared profile scenario matrix above on pgsql and mssql.
- Parity assertions cover:
  - response bodies (JSON semantics),
  - update-tracking metadata behavior (`_etag/_lastModifiedDate/ChangeVersion` served from stored stamps),
  - paging determinism,
  - `NoProfileWriteBehavior`, including `FullSurfaceCollectionReorder` with semantic-identity-based visible-row matching rather than request ordinal,
  - hidden-data preservation, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, key-unified canonical storage preservation, synthetic presence-flag preservation, hidden reference/descriptor FK preservation, and delete/clear behavior for profiled non-collection scopes across `ProfileVisibleRowUpdateWithHiddenRowPreservation`, `ProfileVisibleRowDeleteWithHiddenRowPreservation`, `ProfileVisibleButAbsentNonCollectionScope`, `ProfileHiddenInlinedColumnPreservation`, `ProfileHiddenExtensionRowPreservation`, and `ProfileHiddenExtensionChildCollectionPreservation`, and
  - the distinction between update-of-existing-visible-data and create-of-new-visible-data for profiled non-collection scopes and collection items, including `ProfileRootCreateRejectedWhenNonCreatable` and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, plus the three-level parent-create-denied/child-denied chain, and
  - the deterministic profile-scoped sibling-order rule "start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously", including the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`, and
  - `ProfileUnchangedWriteGuardedNoOp`, and
  - profile-based validation/creatability failure semantics.
- The matrix above is the source of truth for shared fixture identifiers and scenario naming reused by `DMS-1106`, `DMS-1105`, `DMS-984`, `DMS-1104`, and `DMS-1022`.
- Any dialect-specific differences are explicitly documented and tested.

## Tasks

1. Define and maintain the shared profile scenario matrix above as the canonical feature-by-scenario fixture map, including the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`, plus the creatability variants nested under `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`.
2. Implement a harness that runs each test case against both engines and compares results.
3. Add documentation for expected/allowed differences and how to add new parity cases using the shared scenario names, including which ordering variants belong under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`, and which creatability variants belong under `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`.
