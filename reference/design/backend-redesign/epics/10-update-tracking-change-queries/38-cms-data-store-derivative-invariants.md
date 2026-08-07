---
jira: DMS-1366
jira_url: https://edfi.atlassian.net/browse/DMS-1366
---

# Story: Enforce CMS Data Store Derivative Invariants

## Description

Implement the CMS administrative-database changes required by the DMS-1190 snapshot and read-replica design.

CMS already stores `Snapshot` and `ReadReplica` rows in `dmscs.DataStoreDerivative`, but the database does not enforce a single derivative of each type per data store or reject invalid type values. Add those invariants for PostgreSQL and SQL Server, make upgrades fail with actionable diagnostics when legacy data violates them, and map insert and update conflicts through the CMS API.

## Acceptance Criteria

- PostgreSQL and SQL Server add a named unique constraint on `(DataStoreId, DerivativeType)`. In the same new upgrade script, both engines drop the existing `IX_DataStoreDerivative_DataStoreId` index because the unique constraint's backing index has the same leading key. Retaining the existing index instead requires documented measured performance or query-plan justification.
- PostgreSQL `DatabaseShapeTests` move `IX_DataStoreDerivative_DataStoreId` from the expected-index inventory to the established `RemovedRedundantIndexNames` expectation and verify the new unique constraint and its backing index. SQL Server receives equivalent database-shape coverage proving the old index is absent and the new constraint and backing index are present.
- PostgreSQL and SQL Server add a named check constraint that accepts exactly `Snapshot` and `ReadReplica`.
- The check constraint requires ordinal equality including length in both engines, so equality is exact rather than merely case-sensitive. On SQL Server, the comparison expression explicitly uses a binary/ordinal comparison, such as a `BIN2` collation or an equivalent expression, without requiring one particular collation name. Case variants such as `SNAPSHOT` and `readreplica` are rejected even under a case-insensitive database collation, and whitespace variants such as `Snapshot ` are rejected even though SQL Server's SQL-92 string padding makes them compare equal to `Snapshot` under `=` and `IN` at any collation. `LEN` is not a usable length test on SQL Server because it ignores trailing spaces.
- A preflight runs before either constraint is added and hard-stops the upgrade when duplicate `(DataStoreId, DerivativeType)` rows or invalid derivative types exist.
- Duplicate diagnostics identify the offending `(DataStoreId, DerivativeType, Id)` tuples.
- Invalid-type diagnostics identify the offending `(Id, DataStoreId, DerivativeType)` tuples, including case and whitespace variants. On SQL Server, the invalid-type preflight uses the same explicitly binary/ordinal comparison semantics as the check constraint, such as a `BIN2` collation or an equivalent expression, together with an exact length test. The preflight is therefore case- and padding-exact, so a value such as `Snapshot ` is reported rather than passing an `IN` scan silently on SQL Server and surviving into a runtime where DMS's ordinal type matching ignores it.
- Upgrade guidance explains how to correct an invalid type with `PUT /v3/dataStoreDerivatives/{id}` or remove an unwanted row with `DELETE /v3/dataStoreDerivatives/{id}` before retrying.
- The migration never deletes derivative rows, rewrites type values, or arbitrarily chooses among duplicates.
- Insert and update repository result types in both backends include an explicit duplicate/conflict result identified by the named unique constraint.
- The CMS frontend maps insert and update duplicates to the established conflict response — `FailureResponse.ForConflict` with HTTP `409` — instead of the unknown-error response.
- Updating either `DataStoreId` or `DerivativeType` exercises the same conflict behavior as inserting a duplicate.
- The existing foreign key, cascade-delete behavior, nullable connection-string contract, encryption, tenant scoping, and auditing remain unchanged.
- PostgreSQL and SQL Server integration tests cover clean upgrades, duplicate-row preflight failures, invalid-type preflight failures, case-variant rejection, trailing-whitespace rejection, insert conflicts, update conflicts, tenant isolation, and derivative inclusion in data-store responses.
- Frontend coverage proves the HTTP mapping that repository-level integration tests cannot: `DataStoreDerivativeModule` unit tests assert `409` for both the insert duplicate result and the update duplicate result, or `DataStoreDerivatives.feature` E2E scenarios assert `409` for both. A repository conflict result on its own does not satisfy this criterion, because the module's fall-through arm would still return the unknown-error response.

## Out of Scope

- DMS runtime deserialization or routing.
- Snapshot or read-replica creation, refresh, promotion, and teardown.
- Changing the CMS API's accepted derivative types or connection-string representation.
