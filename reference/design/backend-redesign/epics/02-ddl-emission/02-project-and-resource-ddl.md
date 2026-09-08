---
jira: DMS-938
jira_url: https://edfi.atlassian.net/browse/DMS-938
---

# Story: Emit Project Schemas + Resource/Extension Tables + Abstract Identity Tables/Views

## Description

Generate deterministic DDL for per-project schemas and schema-derived objects from the relational model set (`DerivedRelationalModelSet`; see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`):

- Create one physical schema per project (`ProjectEndpointName` normalization).
- For each non-descriptor concrete resource:
  - root table `{schema}.{Resource}`
  - child tables for arrays (including nested collections)
  - `_ext` extension tables in extension project schemas
- For each abstract resource:
  - identity table `{schema}.{AbstractResource}Identity`
  - union view `{schema}.{AbstractResource}_View`
- Emit required per-table triggers derived from the model set (see `reference/design/backend-redesign/design-docs/ddl-generation.md` “Triggers (required)”):
  - representation/identity stamping triggers,
  - `dms.ReferentialIdentity` maintenance triggers, and
  - `{schema}.{AbstractResource}Identity` maintenance triggers.
- Apply FK index policy (supporting indexes for all FKs).

## Acceptance Criteria

- DDL includes `CREATE SCHEMA` for each project schema, deterministic ordering.
- For each resource, DDL includes:
  - tables + PK/UK/CHECK constraints,
  - FKs emitted in the FK phase (after tables),
  - FK-supporting indexes emitted per policy.
- Required triggers are emitted deterministically (stable naming + stable statement ordering).
- Abstract views are emitted deterministically:
  - stable `UNION ALL` arm ordering,
  - stable select-list ordering/casting.
- Abstract identity tables are emitted deterministically:
  - stable column ordering from `identityJsonPaths` order,
  - stable constraint/index naming.
- `_ext` tables are created in the correct extension schemas with aligned keys.
- DDL output is deterministic and snapshot-testable for small fixtures that include nested collections, polymorphism, and `_ext`.

## Tasks

1. Implement DDL emission for project schemas (schema normalization + ordering).
2. Implement table DDL emission for resource root + child tables (PK/UK/CHECK first).
3. Implement FK emission phase using derived model FK relationships.
4. Implement FK index policy and index emission phase (avoid duplicates via leftmost-prefix detection).
5. Implement trigger emission using the derived trigger inventory (dialect-specific idempotency patterns).
6. Implement abstract identity table emission.
7. Implement abstract union view emission (dialect-specific `CREATE OR REPLACE` vs `CREATE OR ALTER`).
8. Add snapshot tests for small fixtures covering:
   1. nested collections,
   2. polymorphic abstract views,
   3. `_ext` mapping.
9. Emit abstract identity tables from `DerivedRelationalModelSet.AbstractIdentityTablesInNameOrder`:
   - Ensure one `CREATE TABLE` per abstract identity table (including PK/UK/CHECK constraints) is emitted deterministically.
   - Ensure abstract identity tables are emitted before abstract union views (views may depend on the tables for diagnostics and DDL ordering).
   - Add focused unit tests that assert the emitted SQL contains the expected abstract identity-table `CREATE TABLE` statements.
10. Fix SQL Server DDL emission for unbounded string scalar types:
    - When `RelationalScalarType.Kind=String` and `MaxLength` is omitted, emit `nvarchar(max)` (not bare `nvarchar`) for SQL Server.
    - Apply the same rule consistently for both table column definitions and abstract union view `CAST(... AS ...)` expressions.
    - Add focused unit tests covering at least one unbounded string column in a table and in a union view output column under the SQL Server dialect.
