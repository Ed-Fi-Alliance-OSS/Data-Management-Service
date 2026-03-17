---
jira: DMS-1023
jira_url: https://edfi.atlassian.net/browse/DMS-1023
---

# Story: Cross-Engine Parity Tests and Shared Fixtures

## Description

Ensure the relational redesign behaves consistently across PostgreSQL and SQL Server:

- Same fixtures and test cases run against both dialects, including profile-constrained write scenarios for hidden-gap collection ordering and hidden `_ext` preservation.
- Differences are intentional and documented (e.g., error messages where dialect limits differ).

## Acceptance Criteria

- A shared fixture set exists that can run the same CRUD/query and profile-constrained write scenarios on pgsql and mssql, including fixtures for profile-scoped collection ordering with hidden rows and for hidden `_ext` rows/child collections.
- Parity assertions cover:
  - response bodies (JSON semantics),
  - update-tracking metadata behavior (`_etag/_lastModifiedDate/ChangeVersion` served from stored stamps),
  - paging determinism,
  - hidden-data preservation and delete/clear behavior for profiled non-collection scopes, and
  - profile-scoped hidden-gap sibling ordering and hidden `_ext` row/child-collection preservation, and
  - profile-based validation/creatability failure semantics.
- Any dialect-specific differences are explicitly documented and tested.

## Tasks

1. Define a parity test matrix (features × dialects × fixtures), explicitly including profile-constrained create/update/merge/error scenarios plus hidden-gap collection-ordering fixtures and hidden `_ext` row/child-collection fixtures.
2. Implement a harness that runs each test case against both engines and compares results.
3. Add documentation for expected/allowed differences and how to add new parity cases, including the profile fixtures for hidden-gap ordering and hidden `_ext` preservation.
