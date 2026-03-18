---
jira: DMS-1023
jira_url: https://edfi.atlassian.net/browse/DMS-1023
---

# Story: Cross-Engine Parity Tests and Shared Fixtures

## Description

Ensure the relational redesign behaves consistently across PostgreSQL and SQL Server:

- Same fixtures and test cases run against both dialects, including profile-constrained write scenarios for hidden-gap collection ordering, hidden inlined-member preservation, visible-vs-hidden non-collection behavior, and hidden `_ext` preservation.
- Differences are intentional and documented (e.g., error messages where dialect limits differ).

## Acceptance Criteria

- A shared fixture set exists that can run the same CRUD/query and profile-constrained write scenarios on pgsql and mssql, including fixtures for profile-scoped collection ordering with hidden rows, hidden inlined-member preservation, visible-vs-hidden non-collection behavior, and hidden `_ext` rows/child collections.
- Parity assertions cover:
  - response bodies (JSON semantics),
  - update-tracking metadata behavior (`_etag/_lastModifiedDate/ChangeVersion` served from stored stamps),
  - paging determinism,
  - hidden-data preservation, hidden inlined-member preservation, and delete/clear behavior for profiled non-collection scopes, and
  - profile-scoped hidden-gap sibling ordering, semantic-identity-based visible-row matching, and hidden `_ext` row/child-collection preservation, and
  - profile-based validation/creatability failure semantics.
- Any dialect-specific differences are explicitly documented and tested.

## Tasks

1. Define a parity test matrix (features × dialects × fixtures), explicitly including profile-constrained create/update/merge/error scenarios plus hidden-gap collection-ordering fixtures, hidden inlined-member preservation fixtures, visible-vs-hidden non-collection fixtures, and hidden `_ext` row/child-collection fixtures.
2. Implement a harness that runs each test case against both engines and compares results.
3. Add documentation for expected/allowed differences and how to add new parity cases, including the profile fixtures for hidden-gap ordering, hidden inlined-member preservation, visible-vs-hidden non-collection behavior, and hidden `_ext` preservation.
