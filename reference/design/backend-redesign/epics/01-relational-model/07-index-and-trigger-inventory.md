---
jira: DMS-945
jira_url: https://edfi.atlassian.net/browse/DMS-945
---

# Story: Derive Index + Trigger Inventory (DDL Intent)

## Description

Derive deterministic index and trigger inventories from the dialect-aware `DerivedRelationalModelSet`, per:

- `reference/design/backend-redesign/design-docs/ddl-generation.md` (FK index policy + required triggers)
- `reference/design/backend-redesign/design-docs/data-model.md` (stable naming + identifier shortening)
- `reference/design/backend-redesign/design-docs/update-tracking.md` (stamping semantics)

This story produces “SQL-free DDL intent” lists (`DbIndexInfo[]`, `DbTriggerInfo[]`) that are:
- embedded in `DerivedRelationalModelSet` (`reference/design/backend-redesign/design-docs/compiled-mapping-set.md`),
- emitted into `relational-model.manifest.json`, and
- consumed by DDL emission so indexes/triggers cannot drift between manifest vs SQL output.

## Scope (What This Story Is Talking About)

This story derives inventories for **schema-derived project objects**, i.e., objects that come from the effective `ApiSchema.json` set:

- Resource tables in project schemas:
  - root tables,
  - child/collection tables (including nested),
  - `_ext` extension tables (in extension project schemas),
  - abstract identity tables (`{schema}.{AbstractResource}Identity`).

This story does **not** derive inventories for **core** `dms.*` objects (tables/indexes/triggers/sequences). Core inventory is owned by DDL emission (`epics/02-ddl-emission/01-core-dms-ddl.md`).

Descriptor resources stored in shared `dms.Descriptor` (no per-descriptor tables) therefore contribute **no** project-schema table indexes/triggers here; they are covered by core DDL (`epics/02-ddl-emission/05-descriptor-ddl.md`).

## Acceptance Criteria

### Index inventory

- Inventory includes (for schema-derived tables only):
  - **PK-implied indexes** (`DbIndexKind.PrimaryKey`):
    - one per table primary key, named the same as the PK constraint (`PK_{TableName}`).
  - **UNIQUE-implied indexes** (`DbIndexKind.UniqueConstraint`):
    - one per UNIQUE constraint, named the same as the constraint (`UX_{TableName}_{Column1}_...`).
    - includes identity/uniqueness constraints derived from `identityJsonPaths` / `arrayUniquenessConstraints`,
    - includes “referenced-key” UNIQUE constraints that exist purely to make composite FKs legal (as described in `ddl-generation.md`).
  - **FK-supporting indexes** (`DbIndexKind.ForeignKeySupport`) required by the FK index policy in `ddl-generation.md`:
    - one candidate per FK on the referencing columns, named `IX_{TableName}_{Column1}_{Column2}_...`.
  - **Explicit (non-query) indexes** (`DbIndexKind.Explicit`) only when required by the design for schema-derived tables.
    - v1 expectation: most schema-derived tables only need PK/UK and FK-support indexes; do not add query indexes derived from `queryFieldMapping`.

- FK-support indexes are derived deterministically:
  - one candidate per FK (referencing columns in FK key order),
  - suppressed when an existing PK/UK/index already has the FK columns as a leftmost prefix.
- Index names follow `data-model.md` rules and are collision-checked after dialect shortening.

### Trigger inventory

- Inventory includes required trigger intents for schema-derived tables (non-descriptor resources):

1. **Representation/identity stamping triggers** (`DbTriggerKind.DocumentStamping`)
   - One per schema-derived table that can change a resource representation:
     - resource root tables,
     - resource child/collection tables,
     - `_ext` extension tables.
   - Trigger events: `INSERT`, `UPDATE`, `DELETE` on the table.
   - Semantic purpose: update `dms.Document` stamps per `update-tracking.md`:
     - always bump `ContentVersion`/`ContentLastModifiedAt` for any representation-affecting change,
     - bump `IdentityVersion`/`IdentityLastModifiedAt` when the change affects identity projection columns for the document.
   - The trigger intent must carry (at minimum) the column(s) needed to identify the affected `DocumentId`:
     - root tables: `DocumentId`,
     - child/extension tables: the root document id parent-key-part column (e.g., `{RootBaseName}_DocumentId`).

2. **Referential identity maintenance triggers** (`DbTriggerKind.ReferentialIdentityMaintenance`)
   - One per concrete resource root table.
   - Trigger events: `INSERT` and `UPDATE` when identity-projection columns change.
   - Semantic purpose:
     - recompute and upsert the resource’s `dms.ReferentialIdentity` primary row, and
     - (when applicable) recompute and upsert the superclass/abstract alias row,
     - using the engine UUIDv5 helper (`epics/02-ddl-emission/06-uuidv5-function.md`) for DB-side computation.

3. **Abstract identity table maintenance triggers** (`DbTriggerKind.AbstractIdentityMaintenance`)
   - One per participating concrete resource root table **per abstract identity table** it contributes to.
   - Trigger events: `INSERT` and `UPDATE` when the concrete identity projection changes.
   - Semantic purpose: upsert/update the `{schema}.{AbstractResource}Identity` row so polymorphic reference enforcement and cascades work.

- SQL Server-only trigger-based propagation fallbacks (when used) appear as explicit trigger intents with stable naming.
  - These are `DbTriggerKind.IdentityPropagationFallback` triggers emitted only when `ON UPDATE CASCADE` cannot be used due to SQL Server cascade-path restrictions (as described in `ddl-generation.md` and `strengths-risks.md`).
- Trigger names follow `data-model.md` rules and are collision-checked after dialect shortening.
  - Trigger naming should use `TR_{TableName}_{Purpose}` with purpose tokens aligned to `data-model.md`:
    - `Stamp`, `ReferentialIdentity`, `AbstractIdentity`, `PropagateIdentity`.

Out of scope for this story:
- Core `dms.Document` journal triggers (owned by core DDL emission).
- Any authorization-related triggers.

### Determinism and testing

- Inventories are stable across input ordering and dictionary iteration order.
- Unit tests cover:
  - FK index policy leftmost-prefix suppression,
  - trigger set composition for a fixture with nested collections + identity-component references + at least one abstract resource,
  - dialect identifier shortening impact on names is deterministic.

## Tasks

1. Implement deterministic index derivation from:
   - table keys + unique constraints (implied indexes),
   - FK constraints (FK-support indexes per policy),
   - any explicit index intents required by the design.
2. Implement deterministic trigger intent derivation per `ddl-generation.md`:
   - table stamping triggers,
   - referential identity maintenance triggers,
   - abstract identity maintenance triggers,
   - (dialect-conditional) propagation fallback triggers.
3. Ensure both the DDL emitter and the manifest emitter consume the same derived inventories.
4. Add unit tests for ordering, suppression, and naming determinism.
