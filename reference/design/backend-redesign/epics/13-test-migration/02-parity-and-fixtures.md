---
jira: DMS-1023
jira_url: https://edfi.atlassian.net/browse/DMS-1023
---

# Story: Cross-Engine Parity Tests and Shared Fixtures

## Description

Ensure the relational redesign behaves consistently across PostgreSQL and SQL Server:

- Same fixtures and test cases run against both dialects, including the shared profile scenario baseline from `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md`.
- Differences are intentional and documented (e.g., error messages where dialect limits differ).

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

- A shared fixture set exists that can run the same CRUD/query and shared profile scenario baseline on pgsql and mssql.
- Parity assertions cover:
  - response bodies (JSON semantics),
  - update-tracking metadata behavior (`_etag/_lastModifiedDate/ChangeVersion` served from stored stamps),
  - paging determinism,
  - `NoProfileWriteBehavior`, including `FullSurfaceCollectionReorder` with semantic-identity-based visible-row matching rather than request ordinal,
  - hidden-data preservation, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, and delete/clear behavior for profiled non-collection scopes across `ProfileVisibleRowUpdateWithHiddenRowPreservation`, `ProfileVisibleRowDeleteWithHiddenRowPreservation`, `ProfileVisibleButAbsentNonCollectionScope`, `ProfileHiddenInlinedColumnPreservation`, `ProfileHiddenExtensionRowPreservation`, and `ProfileHiddenExtensionChildCollectionPreservation`, and
  - the distinction between update-of-existing-visible-data and create-of-new-visible-data for profiled non-collection scopes and collection items, including `ProfileRootCreateRejectedWhenNonCreatable` and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, and
  - the deterministic profile-scoped sibling-order rule "start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously", including the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`, and
  - `ProfileUnchangedWriteGuardedNoOp`, and
  - profile-based validation/creatability failure semantics.
- Any dialect-specific differences are explicitly documented and tested.

## Tasks

1. Define a parity test matrix (features × dialects × fixtures) keyed by the shared profile scenario baseline above, including the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`.
2. Implement a harness that runs each test case against both engines and compares results.
3. Add documentation for expected/allowed differences and how to add new parity cases using the shared scenario names, including which ordering variants belong under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`.
