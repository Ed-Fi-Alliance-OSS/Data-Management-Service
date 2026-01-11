# Backend Redesign: DDL Generation (Tables per Resource)

## Status

Draft.

This document is the DDL Generation deep dive for `overview.md`:

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Extensions: [extensions.md](extensions.md)
- Transactions, concurrency, and cascades: [transactions-and-concurrency.md](transactions-and-concurrency.md)
- DDL generator verification harness: [ddl-generator-testing.md](ddl-generator-testing.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

## Purpose

DMS compiles a derived relational resource model from each configured effective `ApiSchema.json` set (core + extensions) (see “Derived Relational Resource Model” in [flattening-reconstitution.md](flattening-reconstitution.md)). DMS treats schema changes as an operational concern outside the server process and validates compatibility per database using the recorded schema fingerprint (see “Schema Validation (EffectiveSchema)” in [transactions-and-concurrency.md](transactions-and-concurrency.md)).

Optionally, the same derived model and dialect-specific plans could be compiled **ahead-of-time** into redistributable mapping packs keyed by `EffectiveSchemaHash` (see [aot-compilation.md](aot-compilation.md)).

This redesign therefore requires a separate utility (“DDL generation utility”) that:

- Builds the same derived relational model as runtime (no separate metadata source).
- Generates deterministic, dialect-specific SQL DDL for PostgreSQL and SQL Server.
- Optionally creates the target database (when configured), applies DDL, and records the resulting schema fingerprint in the singleton `dms.EffectiveSchema` row plus `dms.SchemaComponent` rows keyed by `EffectiveSchemaHash`.

## Scope

The DDL generation utility is responsible for database objects derived from the effective schema:

- Core `dms.*` objects required for correctness and update tracking:
  - `dms.ResourceKey`, `dms.Document`, `dms.IdentityLock`, `dms.ReferentialIdentity`, `dms.Descriptor`, `dms.ReferenceEdge`
  - update tracking / Change Queries: `dms.ChangeVersionSequence`, `dms.DocumentChangeEvent`, `dms.IdentityChangeEvent`
  - schema fingerprinting: `dms.EffectiveSchema`, `dms.SchemaComponent`
  - required triggers for journal emission (see [update-tracking.md](update-tracking.md))
- Per-project schemas (derived from `ProjectEndpointName`) and per-resource tables (root + child tables).
- Extension project schemas and extension tables derived from `_ext` (see [extensions.md](extensions.md)).
- Abstract union views (e.g., `{schema}.{AbstractResource}_View`) derived from `projectSchema.abstractResources` (see [data-model.md](data-model.md)).

Explicitly out of scope for this redesign phase:
- `dms.DocumentCache` (materialized JSON projection)
- any authorization objects (`auth.*`, `dms.DocumentSubject`, etc.)

## Inputs and Outputs

**Inputs**
- Core + extension `ApiSchema.json` files (same configuration as DMS).
- Target engine: PostgreSQL or SQL Server.
  - Target platforms: the latest generally-available (GA) non-cloud releases of PostgreSQL and SQL Server.

**Outputs**
- A deterministic SQL script (recommended even when applying directly)
  - All schemas, tables, views, sequences, triggers
  - Deterministic seed inserts for `dms.ResourceKey` (`ResourceKeyId ↔ (ProjectName, ResourceName, ResourceVersion)`)
  - Deterministic `ResourceKeySeedHash`/`ResourceKeyCount` recorded alongside `EffectiveSchemaHash` in `dms.EffectiveSchema` (fast runtime validation)
  - Insert-if-missing statements for the singleton `dms.EffectiveSchema` row and the corresponding `dms.SchemaComponent` rows (keyed by `EffectiveSchemaHash`).
  - Indexes explicitly called out in the design docs plus supporting indexes for all foreign keys (no query indexes)

## SQL Text Canonicalization (Determinism Contract)

This redesign requires deterministic SQL text output for:
- repeatable schema provisioning (golden-file DDL diffs),
- stable AOT mapping pack contents (compiled SQL plans are serialized as strings), and
- stable plan-cache keys and diagnostics.

Therefore, the DDL generator (and the plan compiler used for AOT packs) MUST apply **SQL text canonicalization**:

- **Stable whitespace/formatting**: emit `\n` line endings, avoid tabs, avoid trailing whitespace, and use stable indentation and keyword casing conventions per dialect.
- **Stable naming**: all generated identifiers (schemas/tables/columns/constraints/indexes/views/triggers) and all generated aliases must be derived deterministically from the compiled model (no random suffixes, no hash-map iteration order).
- **Stable parameter naming**: any parameterized SQL emitted as an artifact (e.g., compiled read/write/projection SQL in mapping packs) must use parameter names derived deterministically from the model/bindings.
- **Stable ordering**: emit objects and clauses in a deterministic order (see “Deterministic output ordering (DDL + packs)” below).

Acceptance: for a fixed `(ApiSchema.json set, dialect, relational mapping version)`, emitted SQL text artifacts are byte-for-byte stable.

## Determinism Scope (Artifacts)

Determinism requirements differ by artifact:

- **`EffectiveSchemaHash`**: byte-for-byte stable lowercase hex output for a fixed effective schema set and relational mapping version (see [data-model.md](data-model.md)).
- **DDL scripts**: byte-for-byte stable SQL text output for a fixed `(ApiSchema.json set, dialect, relational mapping version)` (see “SQL Text Canonicalization” above).
- **AOT mapping packs (`.mpack`)**: semantic equivalence is required (not byte-for-byte file identity).
  - Definition: two packs are equivalent if their keying fields match (effective schema hash, dialect, relational mapping version, pack format version) and their decoded payloads (models/plans/resource keys) are semantically identical after decompression and protobuf parse.
  - Rationale: pack envelopes may include producer metadata and compression may not be byte-for-byte stable even when payload semantics are unchanged.

## DDL Object Inventory (v1)

This inventory is the explicit “what exists in the database” contract that the DDL generator produces for a given `EffectiveSchemaHash`.

### 1) Schemas

- `dms` (core tables shared across all projects/resources)
- One physical schema per project, derived from `projectSchema.projectEndpointName`:
  - e.g., `ed-fi` → `edfi`
  - extension projects (e.g., `tpdm`, `sample`) each have their own physical schema

### 2) Core objects (`dms` schema)

**Tables**
- `dms.ResourceKey`
- `dms.Document`
- `dms.IdentityLock`
- `dms.ReferentialIdentity`
- `dms.Descriptor`
- `dms.ReferenceEdge`
- `dms.EffectiveSchema` (singleton current state)
- `dms.SchemaComponent` (keyed by `EffectiveSchemaHash`)
- Update tracking / Change Queries:
  - `dms.DocumentChangeEvent`
  - `dms.IdentityChangeEvent`

**Sequence**
- `dms.ChangeVersionSequence`

**Triggers (required)**
- Journal emission triggers on `dms.Document`:
  - PostgreSQL: trigger function + trigger (as defined in [update-tracking.md](update-tracking.md))
  - SQL Server: `AFTER INSERT, UPDATE` trigger (as defined in [update-tracking.md](update-tracking.md))

**Indexes**
- All PK/UK indexes implied by constraints
- Additional explicit indexes called out in the design docs (e.g., `IX_Document_ResourceKeyId_DocumentId`, `IX_ReferenceEdge_ChildDocumentId`, etc.)
- Supporting indexes for all FKs (see “FK index policy” below)

### 3) Project objects (per project schema)

For each concrete resource in the effective schema (core + extensions):

- Root table `{schema}.{Resource}` (one row per `DocumentId`)
- Child tables for each JSON array scope under the resource (including nested collections)
- Tables for `_ext` sites (in extension project schemas), aligned to the base scope keys (see [extensions.md](extensions.md))

For each abstract resource in `projectSchema.abstractResources`:
- Union view `{schema}.{AbstractResource}_View` over participating concrete root tables (see [data-model.md](data-model.md))

**Indexes**
- All PK/UK indexes implied by constraints
- Supporting indexes for all FKs (no query indexes derived from `queryFieldMapping`)

### 4) Seed data (required deterministic inserts)

The emitted SQL must include deterministic DML that establishes the runtime contract:

1. `dms.ResourceKey` seed inserts with explicit `ResourceKeyId` values (deterministic ordering).
2. Upsert/replace of the singleton `dms.EffectiveSchema` row (`EffectiveSchemaSingletonId=1`) including:
   - `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, `ResourceKeyCount`, `ResourceKeySeedHash`, `AppliedAt`
3. Replace of `dms.SchemaComponent` rows for the current `EffectiveSchemaHash`.

## FK index policy (v1)

In addition to the explicit indexes called out in the design docs, the DDL generator must ensure every foreign key has a supporting index on the referencing columns (to avoid accidental table scans on deletes, joins, and existence checks).

Rule:
- For each FK `(A.Col1, A.Col2, ...) → B(...)`, create a non-unique index on `(Col1, Col2, ...)` unless an existing PK/UK/index already has `(Col1, Col2, ...)` as a **leftmost prefix**.

This policy applies to:
- parent/child table FKs (including composite key FKs),
- document-reference FKs (`..._DocumentId`),
- descriptor-reference FKs (`..._DescriptorId`),
- core-table FKs (e.g., `dms.Document(ResourceKeyId) → dms.ResourceKey`).

## High-level workflow

1. Load the configured core + extension `ApiSchema.json` set.
2. Compute `EffectiveSchemaHash` (as defined in [data-model.md](data-model.md)).
3. Derive the relational resource models and naming (as defined in [flattening-reconstitution.md](flattening-reconstitution.md) and [data-model.md](data-model.md)).
4. Generate “desired state” DDL for all required objects (schemas, tables, sequences, FKs, unique constraints, indexes, views, triggers).
   - Derive the `dms.ResourceKey` seed set from the effective schema and emit deterministic `INSERT` statements with explicit `ResourceKeyId` values.
5. Generate the applied schema fingerprint statements (`dms.EffectiveSchema` singleton row and `dms.SchemaComponent` keyed by `EffectiveSchemaHash`).
6. Emit SQL and (optionally) apply it.

## Apply semantics (create-only, no migrations)

The DDL generation utility is a **provisioning** tool, not a schema migration engine. Apply behavior is defined as follows:

### Create-only (no re-run support, no upgrades)

- The utility targets **new/empty** databases only.
- There is **no upgrade/migration** capability and no support for evolving an already-provisioned database from one `EffectiveSchemaHash` to another.
- The utility does **not** define “re-run” support as a product feature (it does not attempt to compute diffs, reconcile drift, or preserve data).

### Existence-check patterns everywhere

Even though the tool is create-only, the generated SQL uses existence-check patterns for *all* database objects to make provisioning robust against partial/failed runs:

- PostgreSQL: `CREATE SCHEMA IF NOT EXISTS`, `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS` (where supported), and `CREATE OR REPLACE VIEW`.
- SQL Server: `IF NOT EXISTS (...) CREATE ...` patterns for schemas/tables/indexes/constraints where needed, and `CREATE OR ALTER VIEW`.

This is not a migration story; it is a guardrail to avoid brittle provisioning scripts.

### Seed data semantics

- Deterministic seed data (notably `dms.ResourceKey`) uses **TRUNCATE + reinsert** semantics.
- Inserts always provide explicit deterministic ids (e.g., `ResourceKeyId`) so the mapping remains stable for a given `EffectiveSchemaHash`.

### `dms.EffectiveSchema` / `dms.SchemaComponent` immutability

- `dms.EffectiveSchema` is a **singleton current-state** record of the database’s provisioned schema fingerprint.
  - Provisioning writes the singleton row once (no append-only “applied history”).
  - Generated SQL uses insert-if-missing semantics (no “new applied row per run” behavior).
- `dms.SchemaComponent` rows are keyed by `EffectiveSchemaHash` and are treated as immutable for that fingerprint.

### Transaction boundary

- Apply runs in a **single transaction**:
  - all schemas/tables/views/sequences/triggers,
  - all deterministic seeds (`dms.ResourceKey`, schema fingerprint rows),
  - all required supporting indexes.
- Any failure rolls back the transaction and the database is left unprovisioned.

### Optional database creation

- Apply can optionally create the target database if it does not exist.
- Database creation is treated as a **pre-step** (performed before the main transaction) because PostgreSQL `CREATE DATABASE` cannot run inside a transaction block.
- After the database exists, the utility connects to the target database and runs the main apply transaction as described above.

### Concurrency

- Apply runs are assumed to be operationally serialized (no explicit apply-locking is required by this design).

## Deterministic `dms.ResourceKey` seeding

Because `ResourceKeyId` is persisted in core tables and indexes, `ResourceKeyId` assignments must be deterministic for a given `EffectiveSchemaHash`.

Recommended derivation:
- Build the set of `(ProjectName, ResourceName)` pairs from the effective schema (core + extensions):
  - include all concrete `resourceSchemas[*].resourceName` (including descriptors),
  - include all `abstractResources[*]` names (used for polymorphic/superclass alias rows in `dms.ReferentialIdentity`).
- Sort pairs by `(ProjectName, ResourceName)` using **ordinal** (culture-invariant) string ordering.
- Assign `ResourceKeyId` sequentially from 1..N and emit seed inserts (deriving `ResourceVersion` from the owning `projectSchema.projectVersion`):
  - `INSERT INTO dms.ResourceKey(ResourceKeyId, ProjectName, ResourceName, ResourceVersion) VALUES ...`
- Fail fast if `N` exceeds the maximum representable `ResourceKeyId` (`smallint`).

Recommended additional fingerprinting:
- Compute `ResourceKeySeedHash` as `SHA-256` over a canonical UTF-8 manifest derived from the same ordered seed list (include a version header like `resource-key-seed-hash:v1` and one line per row as `ResourceKeyId|ProjectName|ResourceName|ResourceVersion`).
- Record `ResourceKeyCount=N` and `ResourceKeySeedHash` alongside `EffectiveSchemaHash` in `dms.EffectiveSchema` so DMS can validate the `ResourceKeyId` mapping with a single-row read (full table diff only on mismatch).

DMS runtime should validate and cache this mapping per database (fail fast on mismatch) as part of the schema fingerprint check.

## Deterministic output ordering (DDL + packs)

To make DDL generation (and optional mapping pack compilation) reproducible, the derived relational model and all emitted artifacts must be ordered deterministically and must not depend on JSON file ordering, JSON property ordering, or dictionary iteration order.

Rules:

- All ordering comparisons use `StringComparer.Ordinal` semantics (culture-invariant, case-sensitive).
- When emitting a multi-phase DDL script, use a stable phase order to avoid dependency/topological sorting differences across dialects:
  1. Create schemas
  2. Create tables (PK/UNIQUE/CHECK only; omit cross-table FKs)
  3. Add foreign keys (all `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY ...`)
  4. Create indexes
  5. Create/alter views
  6. Create triggers (optional features only)
  7. Seed deterministic data (`dms.ResourceKey`, `dms.EffectiveSchema`, `dms.SchemaComponent`, etc.)

Within each phase:

- **Schemas**: order by schema name (ordinal).
- **Projects**: order by `ProjectEndpointName` (ordinal).
- **Resources within a project**: order by `ResourceName` (ordinal).
- **Tables within a resource**:
  - order by `JsonScope` depth (`$` root first, then child arrays, then nested arrays),
  - then by `JsonScope` string (ordinal),
  - then by physical table name as a final tie-breaker.
- **Columns within a table**:
  1. key columns in key order (`DocumentId` / parent key parts in order, then `Ordinal`)
  2. document reference FKs (`..._DocumentId`) by column name
  3. descriptor FKs (`..._DescriptorId`) by column name
  4. scalar columns by column name
- **Constraints**: group by kind in fixed order `PK → UNIQUE → FK → CHECK`, then order by constraint name (ordinal).
- **Indexes**: order by table name, then index name (ordinal).
- **Views**: order by view name (ordinal).
  - For abstract union views (`{schema}.{AbstractResource}_View`), order `UNION ALL` arms by concrete `ResourceName` (ordinal), and use a fixed select-list order: `DocumentId`, abstract identity fields in `identityPathOrder`, then optional `Discriminator`.
- **Triggers**: order by table name, then trigger name (ordinal).
- **Seed data**:
  - `dms.ResourceKey` inserts ordered by `ResourceKeyId` ascending (where ids are assigned from the seed list sorted by `(ProjectName, ResourceName)` ordinal).
  - `dms.SchemaComponent` rows ordered by `ProjectEndpointName` ordinal.

## Integration points (implementation-facing)

The DDL generation utility should reuse the same compilation pipeline as runtime:

- Relational model derivation (resource → tables/columns/constraints).
- Dialect-specific DDL generation (`ISqlDialect`-style boundary).
- View generation for abstract resources.
- Identifier rules (schema/table/column naming, quoting, truncation, and constraint/index naming): see `data-model.md` (“Naming Rules (Deterministic, Cross-DB Safe)”).
- Scalar type mapping rules (ApiSchema → SQL): see `data-model.md` (“Type Mapping Defaults (Deterministic, Cross-DB Safe)”) and `flattening-reconstitution.md` (“Scalar type mapping (dialect defaults)”).
- (Optional) mapping pack output for the same derived models/plans (see [aot-compilation.md](aot-compilation.md)).

DMS runtime should remain “validate-only”:

- Schema creation/update is the DDL generation utility’s responsibility, not the server’s.
- On first use of a given database connection string, DMS reads the database’s recorded `EffectiveSchemaHash` and selects the matching compiled mapping set (or rejects the request if none is available).

## Suggested deliverables

- A CLI entrypoint (e.g., `dms-ddl`) that emits the full SQL script
- (Optional) a mapping pack builder/emit mode that produces redistributable mapping packs keyed by `EffectiveSchemaHash` (see [aot-compilation.md](aot-compilation.md)).
- A test harness that runs the DDL generation utility against empty PostgreSQL and SQL Server instances and verifies:
  - stable naming,
  - DDL success,
  - `EffectiveSchemaHash` recording,
  - basic introspection/diff correctness.
