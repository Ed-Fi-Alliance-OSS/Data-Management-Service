# Backend Redesign: DDL Generation (Tables per Resource)

## Status

Draft.

This document is the DDL Generation deep dive for `overview.md`:

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Extensions: [extensions.md](extensions.md)
- Transactions, concurrency, and cascades: [transactions-and-concurrency.md](transactions-and-concurrency.md)
- Authorization: [auth.md](auth.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

## Purpose

DMS compiles a derived relational resource model from each configured effective `ApiSchema.json` set (core + extensions) (see “Derived Relational Resource Model” in [flattening-reconstitution.md](flattening-reconstitution.md)). DMS treats schema changes as an operational concern outside the server process and validates compatibility per database using the recorded schema fingerprint (see “Schema Validation (EffectiveSchema)” in [transactions-and-concurrency.md](transactions-and-concurrency.md)).

Optionally, the same derived model and dialect-specific plans could be compiled **ahead-of-time** into redistributable mapping packs keyed by `EffectiveSchemaHash` (see [aot-compilation.md](aot-compilation.md)).

This redesign therefore requires a separate utility (“DDL generation utility”) that:

- Builds the same derived relational model as runtime (no separate metadata source).
- Generates deterministic, dialect-specific SQL DDL for PostgreSQL and SQL Server.
- Optionally applies DDL to a target database and records the resulting `EffectiveSchemaHash` in `dms.EffectiveSchema`/`dms.SchemaComponent`.

## Scope

The DDL generation utility is responsible for database objects derived from the effective schema:

- Core `dms.*` tables (e.g., `dms.Document`, `dms.ReferentialIdentity`, `dms.ReferenceEdge`, `dms.IdentityLock`, `dms.DocumentCache`, `dms.EffectiveSchema`).
- Per-project schemas (derived from `ProjectEndpointName`) and per-resource tables (root + child tables).
- Extension project schemas and extension tables derived from `_ext` (see [extensions.md](extensions.md)).
- Abstract union views (e.g., `{schema}.{AbstractResource}_View`) derived from `projectSchema.abstractResources` (see [data-model.md](data-model.md)).

Authorization-specific objects (e.g., `auth.*` views) may be in scope for the DDL generation utility if they are strictly schema-derived and required for DMS operation, but authorization strategy design remains owned by [auth.md](auth.md).

## Inputs and Outputs

**Inputs**
- Core + extension `ApiSchema.json` files (same configuration as DMS).
- Target engine: PostgreSQL or SQL Server.

**Outputs**
- A deterministic SQL script (recommended even when applying directly)
  - All schemas, tables, views
  - Insert statements into `dms.EffectiveSchema`/`dms.SchemaComponent` rows matching the computed `EffectiveSchemaHash`.

## High-level workflow

1. Load the configured core + extension `ApiSchema.json` set.
2. Compute `EffectiveSchemaHash` (as defined in [data-model.md](data-model.md)).
3. Derive the relational resource models and naming (as defined in [flattening-reconstitution.md](flattening-reconstitution.md) and [data-model.md](data-model.md)).
4. Generate “desired state” DDL for all required objects (tables, FKs, unique constraints, indexes, views).
5. Generate the applied schema fingerprint (`dms.EffectiveSchema` and `dms.SchemaComponent`) insert statements.
6. Emit SQL.

## Integration points (implementation-facing)

The DDL generation utility should reuse the same compilation pipeline as runtime:

- Relational model derivation (resource → tables/columns/constraints).
- Dialect-specific DDL generation (`ISqlDialect`-style boundary).
- View generation for abstract resources.
- (Optional) mapping pack output for the same derived models/plans (see [aot-compilation.md](aot-compilation.md)).

DMS runtime should remain “validate-only”:

- Schema creation/update is the DDL generation utility’s responsibility, not the server’s.
- On first use of a given database connection string, DMS reads the database’s recorded `EffectiveSchemaHash` and selects the matching compiled mapping set (or rejects the request if none is available).

## Suggested deliverables

- A CLI entrypoint (e.g., `dms-ddl`) that emits the full SQL script
- (Optional) a mapping pack builder/emit mode that produces redistributable mapping packs keyed by `EffectiveSchemaHash` (see [aot-compilation.md](aot-compilation.md)).
- A test harness that runs the DDL generation utility against empty PostgreSQL/SQL Server instances and verifies:
  - stable naming,
  - DDL success,
  - `EffectiveSchemaHash` recording,
  - basic introspection/diff correctness.
