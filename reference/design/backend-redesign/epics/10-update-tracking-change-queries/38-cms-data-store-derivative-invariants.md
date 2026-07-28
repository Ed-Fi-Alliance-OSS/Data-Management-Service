---
jira: TBD
jira_url: TBD
---

# Story: Enforce CMS Data Store Derivative Invariants

## Description

Implement the CMS administrative-database changes required by the DMS-1190 snapshot and read-replica design.

CMS already stores `Snapshot` and `ReadReplica` rows in `dmscs.DataStoreDerivative`, but the database does not enforce a single derivative of each type per data store or reject invalid type values. Add those invariants for PostgreSQL and SQL Server, make upgrades fail with actionable diagnostics when legacy data violates them, and map insert and update conflicts through the CMS API.

## Acceptance Criteria

- PostgreSQL and SQL Server add a named unique constraint on `(DataStoreId, DerivativeType)`.
- PostgreSQL and SQL Server add a named check constraint that accepts exactly `Snapshot` and `ReadReplica`.
- The check constraint has explicit case semantics in both engines. Case variants such as `SNAPSHOT` and `readreplica` are rejected even under a case-insensitive SQL Server collation.
- A preflight runs before either constraint is added and hard-stops the upgrade when duplicate `(DataStoreId, DerivativeType)` rows or invalid derivative types exist.
- Duplicate diagnostics identify the offending `(DataStoreId, DerivativeType, Id)` tuples.
- Invalid-type diagnostics identify the offending `(Id, DataStoreId, DerivativeType)` tuples, including case variants.
- Upgrade guidance explains how to correct an invalid type with `PUT /v3/dataStoreDerivatives/{id}` or remove an unwanted row with `DELETE /v3/dataStoreDerivatives/{id}` before retrying.
- The migration never deletes derivative rows, rewrites type values, or arbitrarily chooses among duplicates.
- Insert and update repository result types in both backends include an explicit duplicate/conflict result identified by the named unique constraint.
- The CMS frontend maps insert and update duplicates to the established conflict response instead of the unknown-error response.
- Updating either `DataStoreId` or `DerivativeType` exercises the same conflict behavior as inserting a duplicate.
- The existing foreign key, cascade-delete behavior, nullable connection-string contract, encryption, tenant scoping, and auditing remain unchanged.
- PostgreSQL and SQL Server integration tests cover clean upgrades, duplicate-row preflight failures, invalid-type preflight failures, case-variant rejection, insert conflicts, update conflicts, tenant isolation, and derivative inclusion in data-store responses.

## Out of Scope

- DMS runtime deserialization or routing.
- Snapshot or read-replica creation, refresh, promotion, and teardown.
- Changing the CMS API's accepted derivative types or connection-string representation.
