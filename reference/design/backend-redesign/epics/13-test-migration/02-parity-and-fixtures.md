---
jira: DMS-1023
jira_url: https://edfi.atlassian.net/browse/DMS-1023
---

# Story: Cross-Engine Parity Tests and Shared Fixtures

## Description

Ensure the relational redesign behaves consistently across PostgreSQL and SQL Server:

- Same fixtures and test cases run against both dialects, including profile-constrained write scenarios for hidden-gap collection ordering across no-previously-visible, delete-all-visible, interleaved update-plus-insert, nested-collection, and extension-child-collection cases, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, visible-vs-hidden non-collection behavior, and hidden `_ext` preservation.
- Differences are intentional and documented (e.g., error messages where dialect limits differ).

## Acceptance Criteria

- A shared fixture set exists that can run the same CRUD/query and profile-constrained write scenarios on pgsql and mssql, including fixtures for profile-scoped collection ordering with hidden rows across no-previously-visible, delete-all-visible, interleaved update-plus-insert, nested-collection, and extension-child-collection cases, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, visible-vs-hidden non-collection behavior, and hidden `_ext` rows/child collections.
- Parity assertions cover:
  - response bodies (JSON semantics),
  - update-tracking metadata behavior (`_etag/_lastModifiedDate/ChangeVersion` served from stored stamps),
  - paging determinism,
  - hidden-data preservation, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, and delete/clear behavior for profiled non-collection scopes, and
  - the distinction between update-of-existing-visible-data and create-of-new-visible-data for profiled non-collection scopes and collection items, and
  - the deterministic profile-scoped sibling-order rule "start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously", including no-previously-visible, delete-all-visible, interleaved update-plus-insert, nested-collection, and extension-child-collection cases, plus semantic-identity-based visible-row matching and hidden `_ext` row/child-collection preservation, and
  - profile-based validation/creatability failure semantics.
- Any dialect-specific differences are explicitly documented and tested.

## Tasks

1. Define a parity test matrix (features × dialects × fixtures), explicitly including profile-constrained create/update/merge/error scenarios plus hidden-gap collection-ordering fixtures for no previously visible row, delete-all-visible while hidden rows remain, visible updates plus inserts with hidden rows interleaved, nested collection scopes, extension child collections, hidden inlined-member preservation fixtures, hidden extension-column preservation fixtures, visible-vs-hidden non-collection fixtures, hidden `_ext` row/child-collection fixtures, and update-allowed/create-denied creatability pairs.
2. Implement a harness that runs each test case against both engines and compares results.
3. Add documentation for expected/allowed differences and how to add new parity cases, including the profile fixtures for hidden-gap ordering across no-previously-visible, delete-all-visible, interleaved update-plus-insert, nested-collection, and extension-child-collection cases, hidden inlined-member preservation, hidden extension-column preservation, visible-vs-hidden non-collection behavior, and hidden `_ext` preservation.
