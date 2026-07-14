---
jira: DMS-1125
jira_url: https://edfi.atlassian.net/browse/DMS-1125
---

# Epic: Close MSSQL Implementation and Parity Gaps

## Description

DMS already supports PostgreSQL and SQL Server. This epic closes the remaining MSSQL deployment,
runtime-validation, persistence-correctness, and operational-workflow gaps needed for equivalent release
confidence across the two supported relational engines.

This is a gap-closure epic, not a new backend implementation. Existing PostgreSQL behavior remains the
regression baseline, and existing cross-engine designs remain authoritative for shared semantics. Work stays
in its owning domain when a specialized epic already exists; this epic owns the MSSQL-specific gaps and the
cross-cutting local and CI workflows that do not have another active home.

## Work Items

- `DMS-873` — `00-gap-analysis-and-follow-up-inventory.md` — Inventory remaining MSSQL gaps and organize
  follow-up work
- `DMS-1270` — `01-local-database-topology-parity.md` — Align local database topology across engines with an
  optional separate CMS database
- `DMS-1271` — `02-database-template-restore-workflow.md` — Add the database-template restore branch to the
  bootstrap workflow
- `DMS-1279` — `03-sql-server-2025-and-native-json.md` — Adopt the SQL Server 2025 runtime and evaluate the
  native `json` storage transition
- `DMS-1284` — `04-mssql-docker-e2e.md` — Run the DMS and Instance Management Docker E2E suites against MSSQL
- `DMS-1285` — `05-mssql-write-path-coverage.md` — Close relational write-path correctness and resilience
  coverage gaps
- `DMS-1286` — `06-mssql-namespace-authorization-coverage.md` — Add real-MSSQL integration coverage for
  NamespaceBased CRUD authorization

## Cross-Work-Item Dependencies

- `DMS-873` owns the gap inventory and scope boundaries. It does not re-own implementation already assigned
  to a follow-up story.
- `DMS-1255` supplies the database-template packages and `-DbOnly` startup slice required by `DMS-1270`,
  `DMS-1271`, and `DMS-1279`. It remains tracked under its existing bootstrap ownership.
- `DMS-1258` retains implementation ownership for the SQL Server foreign-key-pruning design. It may proceed in
  parallel with workflow work, but the MSSQL gap inventory cannot be considered closed while it remains
  pending.
- `DMS-1270` and `DMS-1271` meet at one topology seam: restore always targets a DMS datastore. In shared mode,
  a scratch-validated DMS-only package is restored before CMS initialization touches that database; in
  separate mode, restore must not target the CMS database. `DMS-1271` also owns the narrow package-producer
  extension and consumer validation for an external restore manifest; general template publication remains
  with `DMS-1255`.
- `DMS-1279` separates the required SQL Server 2025 runtime upgrade from a conditional native `json` storage
  change. A recorded defer decision does not block the runtime upgrade. Adoption applies to the optional
  `DocumentCache` column, uses direct provider coverage unless a production cache path is separately assigned,
  and requires a distinct MSSQL physical-schema version, reprovisioned databases/templates, and catalog-type
  validation rather than an implicit migration or a PostgreSQL-affecting relational mapping-version bump.
- `DMS-1284` owns the public HTTP and Docker-stack boundary for both the standard DMS E2E suite and the separate
  multi-datastore Instance Management E2E suite. Backend defects found there should be linked to their owning
  implementation or provider-integration story rather than absorbed into the E2E harness.
- `DMS-1285` reuses the shared fixtures and scenario names owned by `DMS-1023`; it owns real-MSSQL write-path
  execution for the remaining uncovered scenarios.
- `DMS-1286` owns the real-MSSQL NamespaceBased provider boundary. `DMS-1284` owns representative public E2E
  coverage and must not duplicate the full provider matrix.

## Related Cross-Epic Work

- [`DMS-1019` — Benchmark Harness for Read/Write Hot Paths](../12-ops-guardrails/04-performance-benchmarks.md)
- [`DMS-1023` — Cross-Engine Parity Tests and Shared Fixtures](../13-test-migration/02-parity-and-fixtures.md)
- [`DMS-1065` — Further Performance Optimizations](../14-authorization/16-further-performance-optimizations.md)
- `DMS-1127` — SQL Server native-cascade update-tracking validation, governed by
  [`sql-server-pruning.md`](../../design-docs/sql-server-pruning.md) and
  [`update-tracking.md`](../../design-docs/update-tracking.md)
- `DMS-1258` — SQL Server foreign-key-pruning implementation governed by
  [`sql-server-pruning.md`](../../design-docs/sql-server-pruning.md)

## Scope Guardrails

- Do not describe this epic as adding SQL Server support. SQL Server is already a supported backend.
- Preserve PostgreSQL behavior and tests unless a shared defect requires an intentional cross-engine change.
- Run provider-specific claims against a real SQL Server; deterministic SQL-shape tests alone are not enough
  for provider behavior, transactionality, exception decoding, or engine limits.
- Keep CMS persistence changes in CMS-owned code and tests. Cross-service bootstrap and topology work may
  coordinate CMS and DMS without merging their persistence responsibilities.
- Treat dialect differences as explicit, documented contracts. Do not weaken externally visible behavior to
  make an MSSQL test pass.
- Keep performance measurement in `DMS-1019` and speculative optimization in `DMS-1065`.
