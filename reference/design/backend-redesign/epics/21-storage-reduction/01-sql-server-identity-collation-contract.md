---
jira: DMS-1443
jira_url: https://edfi.atlassian.net/browse/DMS-1443
epic: DMS-1402
---

# Story: Pin the SQL Server Identity Collation and Runtime Equality Contract

## Outcome

Make the generated SQL Server storage contract and runtime identity comparison contract agree for
every textual identity value, independent of the database default collation.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- No dependency on another story in this epic.
- This story blocks DMS-1444 and DMS-1448.

## Implementation Scope

- Emit `COLLATE SQL_Latin1_General_CP1_CI_AS` on every generated SQL Server string column that stores
  or copies an identity value, including root natural keys, RefKey copies, abstract identities,
  descriptor identity, tracked-change old/new identity copies, and local collection identity members.
- Preserve purpose-specific explicit collations.
- Introduce the backend identity-equality contract used by DDL and runtime composition. Select
  `OrdinalIgnoreCase` for SQL Server and `Ordinal` for PostgreSQL.
- Add an explicit SQL-free identity-text column role/inventory to the derived model. It is the
  authoritative source for DDL collation emission and runtime comparer selection; consumers must not
  infer coverage from column names, constraint names, or emitted SQL.
- Remove the SQL Server query-side `COLLATE Latin1_General_100_BIN2` override on string equality
  filters in `MssqlPlanDialect` so GET-many `?field=value` predicates render as `t.[Column] = @p`
  and follow the column collation (CI for identity columns under the new contract, database default
  for other string columns), matching ODS and restoring index seeks. Supersedes the DMS-993
  "ordinal/case-sensitive" default recorded in
  [`08-relational-read-path/04-query-execution.md`](../08-relational-read-path/04-query-execution.md)
  and [`05-descriptor-endpoints.md`](../08-relational-read-path/05-descriptor-endpoints.md).
  PostgreSQL query rendering is unchanged.

## Acceptance Criteria

- Golden DDL proves complete identity-column coverage with no inherited-collation gaps.
- The derived inventory contains representative canonical, copied, abstract, descriptor,
  tracked-change, and collection identity columns.
- Provisioning against a `Latin1_General_100_CS_AS_SC_UTF8` database preserves that database default,
  while `sys.columns` reports the pinned CI collation for representative identity columns.
- Under that case-sensitive database default, a regular resource deleted and recreated with only
  identity casing changed is suppressed by Change Query `/deletes` (the tracked-change `Old*` copies
  and live identity columns compare under the DMS CI collation) without a collation-conflict error;
  the descriptor counterpart is pinned by DMS-1455.
- Comparer-provider tests pin the equality behavior for each schema contract, including the
  documented comparer boundary (`ß`/`ss`, `ſ`/`s`, dotless `ı`/`i`, and Kelvin `K` U+212A/`k` are
  *not* equal under `OrdinalIgnoreCase`; `Ǹ`/`ǹ` is) so a .NET casing-table change surfaces as a
  test diff.
- Generated SQL Server GET-many SQL contains no query-side `COLLATE` on string equality predicates;
  a case-variant `?field=` on an identity string column matches on SQL Server and a plan assertion
  proves an index seek on that column; the DMS-993 case-sensitive-filter pins
  (`It_enforces_case_sensitive_string_filtering_for_sql_server` and the descriptor read-filter
  equivalent) are inverted; the ignored E2E mixed-case-value query scenarios run with per-engine
  expectations, using the engine-tagged scenario mechanism introduced here (see below) or, where a
  single scenario cannot express both verdicts, integration-level pins.
- E2E gains `@PostgresqlOnly` and `@MssqlOnly` categories: `run-e2e-tests` excludes `@MssqlOnly`,
  `run-e2e-tests-mssql` includes `@MssqlOnly` alongside `@MssqlRepresentative` and excludes
  `@PostgresqlOnly`; `build-dms.ps1 E2ETest` and the E2E README document the filters. Every E2E
  scenario added by this epic that must gate SQL Server carries `@MssqlRepresentative`; the SQL
  Server lane runs only that cross-section.
- PostgreSQL DDL, comparer, and query-filter behavior remain unchanged.
