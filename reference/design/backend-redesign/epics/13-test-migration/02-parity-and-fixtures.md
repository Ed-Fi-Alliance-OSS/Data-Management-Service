# Story: Cross-Engine Parity Tests and Shared Fixtures

## Description

Ensure the relational redesign behaves consistently across PostgreSQL and SQL Server:

- Same fixtures and test cases run against both dialects.
- Differences are intentional and documented (e.g., error messages where dialect limits differ).

## Acceptance Criteria

- A shared fixture set exists that can run the same CRUD/query scenarios on pgsql and mssql.
- Parity assertions cover:
  - response bodies (JSON semantics),
  - update-tracking metadata behavior (`_etag/_lastModifiedDate/ChangeVersion` served from stored stamps),
  - paging determinism.
- Any dialect-specific differences are explicitly documented and tested.

## Tasks

1. Define a parity test matrix (features × dialects × fixtures).
2. Implement a harness that runs each test case against both engines and compares results.
3. Add documentation for expected/allowed differences and how to add new parity cases.
