# Design Change Proposal: Cross-Scope Key Unification (Parent ↔ Child) via Document-Local Equality Propagation

## Status

Draft (proposal).

## Background

The backend redesign’s key unification design (`reference/design/backend-redesign/design-docs/key-unification.md`)
implements **row-local** unification: when `resourceSchema.equalityConstraints` indicate duplicated identity/scalar values
stored on the **same physical table row**, DMS stores a single canonical column and exposes per-path/per-site columns as
generated/persisted aliases.

That scope boundary is explicit: cross-table constraints (root ↔ child collections, base ↔ extension, child ↔ child) are
currently ignored by key unification (see “Scope of DB-Level Unification” in `reference/design/backend-redesign/design-docs/key-unification.md`),
and the current implementation enforces that behavior (see
`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/KeyUnificationPass.cs` where cross-table
constraints are recorded as `KeyUnificationIgnoredReason.CrossTable`).

The redesign’s relational model also differs structurally from legacy ODS/API:

- Child collection tables are keyed by **parent `DocumentId` + `Ordinal`** (see “Child tables for collections” in
  `reference/design/backend-redesign/design-docs/data-model.md`), not by parent natural keys.
- Identity propagation is defined over **reference edges** (composite reference FKs / SQL Server fallback triggers),
  not over `equalityConstraints`.

These choices are intentional, but they introduce a parity gap described in `key-unification-children-problem.md`: after an
upstream identity/key change propagates into a document’s root row, the same logical key parts duplicated into child rows
may not update, causing document-level `equalityConstraints` to become false **after commit**.

## Legacy ODS/API semantics (parity target)

ODS/API’s behavior can be summarized as:

1. **Schema-level unified keys**: a property is “unified” if it has multiple upstream sources (local definition plus one
   or more incoming associations, or multiple incoming associations).
2. **Write-time unification**:
   - Generated resource classes copy unified key parts from the parent context into child references when the parent is
     set (so nested references can omit redundant key parts).
   - Validation treats the parent as an upstream source and requires all supplied values across the document to match.
3. **Database-level structural unification**:
   - A single physical column is reused across relationships in the same table row (e.g., the same `SchoolId` column
     participates in multiple FKs).
   - Parent key changes cascade into children via `ON UPDATE CASCADE`, keeping unified key parts consistent across parent
     and child tables; triggers separately record the key-change event.

In the redesign, we cannot rely on “shared physical columns across relationships” for parent ↔ child because child tables
do not include parent natural key columns in their PK/FK shape. We need an explicit derived maintenance mechanism to
achieve equivalent “after commit” consistency.

### ODS/API reference points (traceability)

The parity target summary above is grounded in these ODS/API implementation points (paths from the prompt):

- Unified key property definition: `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Common/Models/Domain/EntityProperty.cs:65`.
- Generated resources copy unified key parts from parent context into child references:
  `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen/Mustache/Resources.mustache:288`.
- Validation treats the parent as an upstream source and requires supplied values to match:
  `/home/brad/work/ods/Ed-Fi-ODS/Utilities/CodeGeneration/EdFi.Ods.CodeGen/Mustache/Resources.mustache:1236`.
- DB structural unification example:
  - `edfi.SectionClassPeriod` has a single `SchoolId` column:
    `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-ods-sqlserver/test/integration/artifact/v7_3/0020-Tables-authoritative.sql:6481`.
  - That `SchoolId` participates in both FK→`ClassPeriod` and FK→parent `Section`:
    `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-ods-sqlserver/test/integration/artifact/v7_3/0030-ForeignKeys-authoritative.sql:4395`.
- Key-change-supported parents rely on `ON UPDATE CASCADE` to keep unified references consistent, and triggers record the
  key change event:
  - `SectionClassPeriod`→`Section` cascade example:
    `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-ods-sqlserver/test/integration/artifact/v7_3/0030-ForeignKeys-authoritative.sql:4404`.
  - SSA children cascade examples:
    `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-ods-sqlserver/test/integration/artifact/v7_3/0030-ForeignKeys-authoritative.sql:7032`.
  - `Section` key change trigger example:
    `/home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/4.0.0/Artifacts/MsSql/Structure/Ods/Changes/0210-CreateTriggersForChangeVersionAndKeyChanges.sql:896`.

## Problem Statement

We need parity for a specific class of document-level equality constraints:

- A key part value is duplicated across **root scope** and one or more **child collection scopes**.
- An upstream identity/key update propagates into the root row (via composite FK cascades or SQL Server propagation
  fallback).
- The duplicated child values must update to preserve document-level `equalityConstraints`, even when the child table does
  not reference the upstream updated entity directly.

See `key-unification-children-problem.md` for the motivating scenario and the failure mode.

## Goals

1. Preserve document-level `equalityConstraints` **after commit** for the supported cross-scope shapes, even when updates
   arrive indirectly via identity propagation.
2. Match ODS/API’s “unified key parts follow the parent” behavior for parent ↔ child collection scenarios.
3. Implement deterministically from ApiSchema and the derived relational model (no hand-authored per-resource scripts).
4. Support both PostgreSQL and SQL Server, using the same derived intent and dialect-specific emission.
5. Keep the mechanism **set-based** and efficient for multi-row cascades (identity updates can fan out).

## Non-Goals

- General cross-document equality enforcement.
- Enforcing *all* `equalityConstraints` in the database.
- Solving arbitrary cross-row constraints inside a collection beyond “update all rows in the collection for a document”
  when the schema expresses a root ↔ wildcard child equality.

## Proposal Overview

Add a new **derived maintenance feature**: **Document-Local Equality Propagation (DLEP)**.

DLEP is driven by `resourceSchema.equalityConstraints` that cross table scopes (root ↔ child, base ↔ extension, child ↔
child). For the supported shapes, it maintains equality in-transaction by propagating changes across table boundaries
keyed by the owning root `DocumentId`.

This is intentionally *not* “DB-level unification” (there is no single physical column across tables). Instead it is
**derived cross-table propagation** that is functionally equivalent to ODS/API’s “shared column + cascade” semantics for
unified key parts.

### Minimum parity surface (what we implement first)

1. **Cross-table propagation for equality constraints that cross root ↔ child scopes**, updating dependent child
   **storage/canonical** columns set-based by owning root `DocumentId`.
2. **Reference-site retargeting when required**: if the dependent endpoint is inside a composite reference FK
   (`..._DocumentId + identity parts`), propagate the entire reference site (not just the unified key part) so the FK
   remains valid and the reference is effectively “retargeted” as ODS/API would do.
3. **SQL Server baseline parity fix**: include non-root reference sites when deriving
   `IdentityPropagationFallback` triggers (remove the “only root-table bindings” restriction in
   `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`).

## Detailed Design

### 1) Derive cross-scope equality propagation rules

Introduce a derivation step that consumes `equalityConstraints` and produces a deterministic set of **propagation rules**
for constraints whose endpoints resolve to **different physical tables**.

Inputs:

- The effective ApiSchema `resourceSchema.equalityConstraints`.
- The derived relational model’s authoritative endpoint binding map:
  `DbColumnModel.SourceJsonPath → (DbTable, DbColumn)` (same mechanism used by row-local key unification).
- Column storage metadata from key unification:
  - path/binding columns may be `UnifiedAlias`,
  - writes must target the canonical/storage column (`DbColumnModel.Storage`).

For each cross-table equality constraint:

1. Resolve each endpoint JSONPath to exactly one **path column** (fail fast on ambiguity or missing mapping).
2. Map each path column to its **storage column**:
   - if path column is `Stored`, storage column is itself;
   - if path column is `UnifiedAlias`, storage column is its canonical column.
3. Capture **presence gating** for each endpoint:
   - reference identity-part endpoints are gated by the reference site’s `..._DocumentId` presence column;
   - optional scalar/descriptor endpoints are gated by their synthetic `..._Present` flag (if present).
4. Capture the **owning root DocumentId column** for each table in the constraint:
   - root/extension tables: `DocumentId`;
   - child tables: the parent/root `..._DocumentId` key column (the first component of the parent FK), including nested
     collections (still includes the root `DocumentId`).

Output model (conceptual):

- `EqualityPropagationGroup`
  - `SourceTable`, `SourceStorageColumn`, optional `SourcePresenceColumn`
  - `OwningRootDocumentIdColumn` (on `SourceTable`)
  - `Dependents[]`:
    - `DependentTable`, `DependentStorageColumn`, optional `DependentPresenceColumn`
    - `OwningRootDocumentIdColumn` (on `DependentTable`)
    - `UpdateKind`:
      - `ScalarStorageColumnUpdate`, or
      - `ReferenceSiteRetarget` (see below)

#### Deterministic source-of-truth selection

Because a cross-table equality cannot be physically unified, DLEP needs a deterministic direction to avoid trigger cycles.

Rule (initial parity scope):

- For root ↔ child constraints: **root wins** (root endpoint is the source-of-truth).
- For base ↔ extension constraints: **base wins**.
- For child ↔ child constraints: deterministic ordering by `(tableName, columnName, sourceJsonPath)` (lexicographic) to
  pick a stable canonical endpoint.

This matches the “parent as source” intuition from ODS/API and keeps the first implementation bounded. If later parity
work requires symmetrical propagation (responding to changes on either side), we can extend this with conflict detection
and bidirectional convergence; that is explicitly out-of-scope for the initial implementation.

### 2) Propagation execution model (DB-driven)

DLEP runs inside the database transaction as part of derived maintenance, similar to:

- identity propagation (PostgreSQL `ON UPDATE CASCADE`; SQL Server fallback triggers), and
- `dms.ReferentialIdentity` maintenance triggers.

Recommended execution strategy:

- Emit one deterministic **maintenance routine per resource** (or per source-table group) that accepts a set of root
  `DocumentId`s and performs all required dependent updates set-based.
- Invoke the routine from existing per-table triggers on the **source-of-truth tables** when the relevant source storage
  columns change (value-diff guard, not `UPDATE(column)` gating).

Why a routine instead of one-off triggers per edge:

- Keeps artifact count bounded and stable as schema evolves.
- Concentrates recursion-avoidance and “is distinct” guards in one place.
- Enables set-based batching for multi-row cascades (critical for identity updates).

Dialect notes:

- PostgreSQL: `CREATE OR REPLACE FUNCTION ...` invoked from a trigger.
- SQL Server: `CREATE OR ALTER PROC ...` invoked from a trigger (TVP or temp table for `DocumentId` set).

### 3) Update semantics for dependent endpoints

#### 3.1 Scalar / descriptor storage columns

For a dependent endpoint that is a scalar or descriptor FK value stored in a canonical column:

- Update dependent rows for the affected root `DocumentId`s.
- Apply presence gating when defined (do not “materialize” optional paths):
  - only update when `PresenceColumn IS NOT NULL`.
- Use a null-safe “is distinct” guard to avoid churn and recursion:
  - PostgreSQL: `IS DISTINCT FROM`
  - SQL Server: `(a <> b OR (a IS NULL AND b IS NOT NULL) OR (a IS NOT NULL AND b IS NULL))`

#### 3.2 Reference-site retargeting (composite FK endpoints)

If the dependent endpoint sits inside a **composite reference FK** (`..._DocumentId` + identity parts), ODS/API semantics
are effectively “retarget the reference when the unified key part changes”.

In the redesign, this must be explicit because updating only the identity part storage column can violate the composite FK
unless the `..._DocumentId` already points at the matching referenced row.

When a propagation rule targets an identity-part column that belongs to a reference group:

- Treat the *reference site* as the dependent target, not the single column.
- Update in one set-based statement:
  1. Compute the new identity values for the reference site:
     - unified parts come from the source-of-truth value,
     - non-unified parts retain their existing stored values (or are themselves updated by other rules in the same pass).
  2. Resolve the new target `..._DocumentId` by joining on the referenced resource’s identity:
     - concrete target: join to the referenced resource root table’s API-semantic identity unique key (binding columns),
     - abstract target: join to `{schema}.{AbstractResource}Identity`,
     - (alternative) join via `dms.ReferentialIdentity` if we need polymorphic behavior without per-target joins.
  3. `SET`:
     - the dependent `..._DocumentId` to the resolved DocumentId, and
     - all dependent identity-part **storage** columns to the computed identity values.
- Presence gating:
  - only retarget rows where the dependent reference site is currently present (`..._DocumentId IS NOT NULL`).
  - do not create references where none existed.
- Failure behavior:
  - If the join cannot resolve a target row for any updated reference site, the write must fail in-transaction (do not
    silently null out the reference).
  - This should mirror ODS/API’s behavior where cascades/updates can fail due to FK violations when the retargeted
    referenced row does not exist.

Complexity note:

- This is the highest-complexity part of DLEP because it is effectively **reference resolution inside the DB**, not “copy a
  scalar”. The maintenance routine must:
  - compute a new referenced-identity tuple (mixing unified and non-unified parts),
  - resolve that tuple → a target `DocumentId` deterministically (and uniquely) across dialects, and then
  - update `..._DocumentId` and all identity-part **storage** columns atomically and presence-gated.
- It is straightforward for simple concrete targets (single-part identities) but becomes significantly more complex for:
  - multi-part natural keys,
  - polymorphic/abstract targets (requiring `{AbstractResource}Identity` or `dms.ReferentialIdentity`-based resolution),
  - reference-bearing identity parts (identity elements that are themselves references),
  - and strict failure semantics when resolution yields no match (or multiple matches).

This is the key mechanism that makes DLEP equivalent to ODS/API’s “shared column participates in multiple FKs” behavior.

### 4) Trigger integration points

DLEP must run when source-of-truth storage values change due to:

- direct writes to the source table, and
- indirect writes caused by identity propagation (cascades/fallback triggers) into the source table.

The reliable hook is the existing per-table derived triggers that already run for indirect updates:

- Root tables: `DocumentStamping` triggers already have value-diff compare sets for identity projection.
- Child tables: `DocumentStamping` triggers fire on updates, but do not currently carry identity compare sets (they can be
  extended if needed for DLEP sources in non-root tables).

Implementation options:

1. **Extend `DocumentStamping` triggers** for source tables to invoke the DLEP routine when relevant columns change.
   - Pros: no new trigger inventory entries; runs where we already stamp and emit change events.
   - Cons: `DocumentStamping` becomes “kitchen sink” maintenance; needs careful deterministic guards.
2. **Add a new trigger kind** (recommended for clarity):
   - `TriggerKindParameters.DocumentLocalEqualityPropagation(...)`
   - emitted only on tables that are selected as a source-of-truth for at least one group.
   - trigger body does:
     - identify affected root `DocumentId`s from `inserted`/`deleted`,
     - call the routine, guarded by value-diff checks.

The second option keeps responsibilities explicit and testable in the derived trigger inventory.

### 5) Derived model / pack format changes

Add new derived intent to the DerivedRelationalModelSet:

- new `TriggerKindParameters` subtype (if we choose the “new trigger kind” option), and/or
- a new serialized “equality propagation groups” section if runtime needs it (DDL generator definitely does).

Versioning requirements:

- This is a semantic change to derived maintenance and must be gated by `RelationalMappingVersion` (consistent with the
  key unification design’s versioning rules).
- Producers must bump `RelationalMappingVersion` when DLEP is enabled in emitted artifacts; consumers must fail fast on
  mismatched versions.

### 6) SQL Server baseline parity fix (non-root reverse references)

Independently of DLEP, SQL Server’s identity propagation fallback triggers must include non-root referrers, per the design
intent in `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (“fan out to all impacted
referrer tables (root and non-root reference sites)”).

Current gap:

- `DeriveTriggerInventoryPass.BuildReverseReferenceIndex(...)` filters to root-table reference bindings only
  (`// Only consider root-table bindings ...`), so child/extension tables are not updated by propagation fallback.

Change:

- Remove or relax the filter so reverse reference indexing includes reference bindings in child and extension tables.
- Ensure propagation updates target **canonical/storage** columns (never computed aliases) and that affected documents are
  stamped correctly (child table updates should bump representation stamps as they already do).

This fix is a prerequisite for correctness on SQL Server even without DLEP.

## Operational Considerations

- **Fan-out**: DLEP can update many rows per affected document (all elements in one or more collections). The maintenance
  routine must be set-based and batch-friendly, and should include “is distinct” guards to avoid rewriting unchanged rows.
- **Deadlocks**: Similar to identity propagation, DLEP may increase deadlock risk under concurrent write load. The existing
  deadlock retry guidance in `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` applies.
- **Telemetry**: Add counters for “documents affected” and “rows updated” per DLEP invocation to detect pathological
  cascades.

## Rollout Plan (Phased)

1. **Phase 0 (baseline)**: Fix SQL Server non-root identity propagation fallback trigger derivation.
2. **Phase 1**: Implement DLEP for root ↔ child equality constraints targeting scalar/descriptor storage columns only.
3. **Phase 2**: Add reference-site retargeting for dependent endpoints inside composite reference FKs.
4. **Phase 3 (optional)**: Expand supported shapes (child ↔ child, base ↔ extension) and add conflict detection where
   needed.

## Testing Strategy

- Add a relational-model/DDL inventory test that ensures:
  - cross-table equality constraints produce DLEP artifacts,
  - DLEP targets canonical/storage columns (never `UnifiedAlias`), and
  - presence gating is applied when required.
- Add E2E coverage for the scenario in `key-unification-children-problem.md`:
  - produce a document `X` with the root ↔ child equality constraint shape,
  - perform an identity update that changes the root value via propagation,
  - assert child collection rows are updated and reconstituted JSON remains consistent.
- Add a SQL Server-focused test verifying non-root propagation fallback updates child/extension referrers.

## Open Questions

1. For the motivating SSA scenario, what is the “upstream change” we must respond to?
   - a referenced target identity update (`SchoolId` changes on `School` while `School_DocumentId` stays stable), or
   - a reference retargeting (`StudentSchoolAssociation` now points to a different `School`, implying `School_DocumentId`
     changes)?
   These imply different propagation requirements (identity parts only vs reference-site retargeting).
2. Are we willing to accept potentially large collection fan-out updates as part of identity propagation parity?
3. Should DLEP be modeled as an extension of “key unification”, or as a separate derived-maintenance feature that happens
   to be driven by `equalityConstraints`?
