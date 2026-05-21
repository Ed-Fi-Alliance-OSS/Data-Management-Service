---
jira: DMS-1168
jira_url: https://edfi.atlassian.net/browse/DMS-1168
---

# Story: Emit `dms.GetMaxChangeVersion()` Function (PostgreSQL + SQL Server)

## Description

Emit a `dms.GetMaxChangeVersion()` function in both PostgreSQL and SQL Server that returns the current value of `dms.ChangeVersionSequence`. It is the backing query for the `/changeQueries/v1/availableChangeVersions` endpoint.

See `reference/design/backend-redesign/design-docs/change-queries.md` § "Querying the newest change version" for the function signature and reference implementation (linked to ODS source for both dialects). The DMS version differs from ODS only in schema (`dms` instead of `changes`).

The function is project-independent and hard-coded into the core `dms.*` DDL emission, mirroring the `dms.uuidv5` (DMS-946) pattern. No `DerivedRelationalModelSet` inventory entries are required.

## Acceptance Criteria

- DDL generation emits a `dms.GetMaxChangeVersion()` function for both PostgreSQL and SQL Server, matching the ODS reference implementation in `change-queries.md` § "Querying the newest change version" — the only change being the schema (`dms` instead of `changes`).
- DDL ordering ensures `dms.GetMaxChangeVersion` is created after `dms.ChangeVersionSequence`.
- A direct call (`SELECT dms.GetMaxChangeVersion()`) returns the expected `bigint` value on both engines.
