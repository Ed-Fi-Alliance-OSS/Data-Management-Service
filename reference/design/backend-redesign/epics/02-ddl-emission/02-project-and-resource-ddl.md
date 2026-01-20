# Story: Emit Project Schemas + Resource/Extension Tables + Abstract Views

## Description

Generate deterministic DDL for per-project schemas and per-resource objects derived from the relational model:

- Create one physical schema per project (`ProjectEndpointName` normalization).
- For each non-descriptor concrete resource:
  - root table `{schema}.{Resource}`
  - child tables for arrays (including nested collections)
  - `_ext` extension tables in extension project schemas
- For each abstract resource:
  - union view `{schema}.{AbstractResource}_View`
- Apply FK index policy (supporting indexes for all FKs).

## Acceptance Criteria

- DDL includes `CREATE SCHEMA` for each project schema, deterministic ordering.
- For each resource, DDL includes:
  - tables + PK/UK/CHECK constraints,
  - FKs emitted in the FK phase (after tables),
  - FK-supporting indexes emitted per policy.
- Abstract views are emitted deterministically:
  - stable `UNION ALL` arm ordering,
  - stable select-list ordering/casting.
- `_ext` tables are created in the correct extension schemas with aligned keys.
- DDL output is deterministic and snapshot-testable for small fixtures that include nested collections, polymorphism, and `_ext`.

## Tasks

1. Implement DDL emission for project schemas (schema normalization + ordering).
2. Implement table DDL emission for resource root + child tables (PK/UK/CHECK first).
3. Implement FK emission phase using derived model FK relationships.
4. Implement FK index policy and index emission phase (avoid duplicates via leftmost-prefix detection).
5. Implement abstract union view emission (dialect-specific `CREATE OR REPLACE` vs `CREATE OR ALTER`).
6. Add snapshot tests for small fixtures covering:
   1. nested collections,
   2. polymorphic abstract views,
   3. `_ext` mapping.
