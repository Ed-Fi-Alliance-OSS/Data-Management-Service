# Story: Hydrate Relational Rows Using Multi-Result Queries

## Description

Implement the “hydrate root + children” step using a single DB command per resource read (where possible):

- GET by id: load root row and all child/extension tables needed for reconstitution.
- Query paging: load rows for a page keyset and hydrate all tables for those documents in bulk.

Align with the “one command / multiple result sets” approach in `reference/design/backend-redesign/flattening-reconstitution.md`.

## Acceptance Criteria

- GET by id performs a single round-trip (or a minimal bounded number) to hydrate all required tables for reconstitution.
- Query hydration loads all tables for a page in bulk (not N “GET by id” calls).
- Row ordering within result sets is deterministic and supports stable reconstitution (ordering by key + ordinal where required).
- Works for both PostgreSQL and SQL Server.

## Tasks

1. Implement compiled hydration SQL per resource that returns multiple result sets (root + child tables).
2. Implement a multi-result reader that groups rows by `DocumentId`/composite keys for reconstitution.
3. Add integration tests that:
   1. write a document with nested collections,
   2. read it back and assert all tables were hydrated (pgsql + mssql where available).

