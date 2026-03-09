# Problem: Cascading Key Changes Into Child Collections Under Key Unification

## Status

Draft.

## Purpose

Describe a document-shape scenario we need to support where an upstream identity/key update propagates into a document’s
**root scope**, but must also propagate into **child collection scopes** to preserve `equalityConstraints`.

This document also captures why the current backend-redesign design (and current implementation) does not support the
scenario.

## Background (current redesign)

The backend redesign relies on:

- **Stable relationship keys**: references are stored as `..._DocumentId` foreign keys (no rewrite of `..._DocumentId`
  when natural keys change).
- **Stored identity parts at each reference site**: referenced natural-key components are duplicated into the referrer’s
  row as stored “identity-part” columns to support query/reconstitution.
- **DB-driven identity propagation**:
  - PostgreSQL: `ON UPDATE CASCADE` on eligible composite reference FKs.
  - SQL Server: `ON UPDATE NO ACTION` + trigger-based propagation fallback.
- **Key unification**: uses `equalityConstraints` to unify duplicated stored scalar/descriptor columns into a single
  canonical stored column *within one physical table row* (aliases remain available for API-path semantics).

Key unification is explicitly scoped as **row-local** and intentionally does not enforce full document-level equality,
including root ↔ child collection equality (see `reference/design/backend-redesign/design-docs/key-unification.md`
“Scope of DB-Level Unification”).

## Scenario We Need To Support

Assume:

1. Resource `X` includes a reference to `StudentSchoolAssociation` (SSA) that is an **identity component** for `X`.
2. `X` has a **collection** (child table) whose element objects include a reference to `School`.
3. The effective `ApiSchema` includes an `equalityConstraints` entry that semantically requires:

   - `$.studentSchoolAssociationReference.schoolReference.schoolId`
     equals
   - `$.someCollection[*].schoolReference.schoolId`

So `SchoolId` is duplicated across two scopes in `X`:

- Root scope: via the SSA identity-component reference binding on `X`’s root row.
- Child scope: via a school reference binding on each collection element row.

### Change event

An update occurs that changes **SSA’s effective `SchoolId`** (e.g., identity updates are allowed and SSA’s school
identity-part columns change).

### Expected behavior (what we want)

After the SSA update commits:

- `X`’s **root** stored SSA identity-part `SchoolId` value is updated (this already happens via identity propagation).
- `X`’s **collection rows** are updated so their stored `SchoolId` values also reflect the new value, preserving the
  document-level equality constraint for all elements.
- Reconstituted JSON for `X` remains self-consistent (no “root says SchoolId=A, collection says SchoolId=B”).

## Why The Current Design Does Not Support This

### 1) Key unification is table/row-local (cross-table equality is ignored)

The redesign uses `equalityConstraints` only as a signal for “duplicated storage in the same row” and does not attempt
to enforce root ↔ child equality in the database.

In practice this means:

- Root and child-table `SchoolId` columns cannot be unified into a single canonical stored column.
- They remain **independent stored values** that can drift after cascaded updates.

This is not just a documentation gap: the current implementation of key unification explicitly ignores equality
constraints whose endpoints bind to different physical tables (`KeyUnificationIgnoredReason.CrossTable` in
`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/KeyUnificationPass.cs`).

### 2) Identity propagation follows reference edges, not equality edges

Identity propagation is defined over composite reference FKs (or their SQL Server trigger fallback). It updates the
referrer’s stored identity-part columns **only for tables that directly reference the updated target**.

In this scenario:

- `X` root references SSA → SSA identity changes propagate into `X` root as designed.
- `X` child collection rows reference **School**, not SSA → SSA identity changes have no propagation path into the child
  table’s `SchoolId` columns.

So the root value changes but the child values do not.

### 3) SQL Server propagation fallback currently only considers root-table reference sites

The design docs call out that SQL Server propagation fallback triggers should fan out to “root and non-root reference
sites” (`reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`).

However, the current derived-trigger inventory limits reverse-reference indexing to root-table bindings only (see
`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`, the
“Only consider root-table bindings” filter).

Even for scenarios where a child table *does* directly reference the updated target, SQL Server fallback propagation
would currently miss those non-root referrers.

## Why This Matters

If a document-level equality constraint can become false **after commit** due to upstream identity propagation:

- Reconstituted representations can violate schema-level invariants that Core expects to hold.
- Subsequent writes of `X` may fail Core validation unless the client happens to “repair” the document.
- The system can return inconsistent data to clients without any explicit write to `X`.

## Design Ideas To Support The Scenario (High Level)

The key observation is that the desired behavior is **document-local propagation across scopes**, not just reference-edge
propagation.

Below are candidate directions (not mutually exclusive).

### A) Implement SQL Server propagation fallback for non-root reference sites (baseline parity fix)

Bring the implementation in line with the design intent:

- Include child/extension table reference bindings when deriving `IdentityPropagationFallback` triggers.
- Ensure propagation updates canonical/storage columns for those non-root tables (and stamps documents appropriately).

This does not solve the “SSA update must reach a child table that does not reference SSA” case by itself, but it is a
prerequisite for correctness wherever child tables *do* have direct reference edges.

### B) Add “document-local equality propagation” for cross-table equality constraints

Introduce a new derived artifact type for `equalityConstraints` that cross a collection boundary (root ↔ child,
child ↔ child, base ↔ extension):

- When the canonical value at one endpoint changes (due to a direct write or upstream propagation), issue a set-based
  update into the other endpoint table(s) keyed by the owning `DocumentId`.
- For wildcard endpoints (e.g., `[*]`), update **all rows** in the collection for the document.

This can be implemented via:

- Dialect-specific triggers generated from the derived mapping set, or
- A small, deterministic set of “maintenance procedures” invoked by existing stamping/identity triggers.

#### “Maintenance procedures” (more detail)

Instead of generating one-off trigger bodies per cross-table equality edge, derive a small, stable set of routines that
perform document-local propagation in a set-based way.

High-level shape:

- **Derivation**: from `equalityConstraints`, identify cross-table constraints (root ↔ child, child ↔ child, base ↔
  extension) and group them deterministically (e.g., by the resolved canonical storage column on the “source” endpoint).
- **Canonical/source choice**: pick a deterministic “source-of-truth endpoint” for each group (commonly “root wins”, then
  stable tie-breaking by `(table, column, path)` ordering).
- **Routine emission**:
  - PostgreSQL: `CREATE OR REPLACE FUNCTION {schema}.TF_{Resource}_PropagateEquality(...)` that updates dependent tables
    for one `DocumentId` (or for a set of `DocumentId`s).
  - SQL Server: `CREATE OR ALTER PROC {schema}.PR_{Resource}_PropagateEquality(@DocIds ...)` (e.g., a TVP of `DocumentId`)
    that performs set-based updates to dependent tables.
- **Invocation**: call the routine from existing triggers on the source table when the source value changes.
  - Practical hook points are the already-required per-table stamping/identity-maintenance triggers, because they fire on
    *direct* writes and also on *cascaded/propagated* updates (which is exactly when we need this to run).
  - The trigger-side guard should be null-safe “value changed” detection on the *stored* source column(s).
- **Update logic**: for each dependent endpoint:
  - resolve endpoint columns to canonical/storage columns (never write `UnifiedAlias` columns),
  - update rows keyed by owning `DocumentId` (for collections: update all element rows for that document),
  - and only perform updates when the current dependent value is distinct (idempotent / avoids churn and recursion).

This approach keeps the number of emitted artifacts bounded (one routine per resource or per source-table group), while
still allowing many dependent endpoints to be updated efficiently and deterministically.

### C) Treat some cross-scope duplicates as “inherited” and stop storing them in the child table

If a child field is always equality-constrained to a root identity component, consider modeling it as:

- Stored only once (at the canonical scope), and
- Projected into the child during reconstitution via join/projection (or via generated columns if supported/portable).

This avoids needing to propagate updates into potentially many collection rows, but changes the relational contract for
read/reconstitution and may reduce the ability to validate child-level reference identity purely row-locally.

### D) Propagate full reference objects when equality implies “same reference everywhere”

If the equality constraint implies not just `SchoolId` equality but effectively “the same school reference”, then the
child rows may need to track changes to the canonical reference by updating:

- the child `..._DocumentId`, and
- the child identity-part columns.

This is a stronger form of propagation than the redesign’s current “identity parts only” propagation and would require
careful cycle/ordering design and operational safeguards (fan-out, deadlocks).

### E) Narrow the support surface (fallback)

If the above approaches are too costly, a constrained alternative is to define supported/unsupported constraint shapes,
for example:

- Support only cross-table equality where the child does not have its own independent reference edge (pure scalar
  duplication).
- Or require those equality constraints to be represented in schema such that the child table directly references the
  same target whose identity changes.

This is not preferred if the goal is full parity with document-level `equalityConstraints`, but it is an option.

## Implementation Notes (for Design Idea B)

1. **Reference-site vs scalar propagation**
   - If a dependent endpoint is a *reference identity-part column* that participates in a composite FK (e.g., a child
     table stores `School_DocumentId` plus `SchoolId` under a `FOREIGN KEY (School_DocumentId, SchoolId) ...`), then
     “propagate `SchoolId` only” may violate the composite FK unless the `..._DocumentId` already points to the same
     target row.
   - Supporting this scenario may therefore require one of:
     - limiting cross-scope equality propagation to scalar/descriptor columns that are not part of a composite reference
       FK,
     - propagating an entire reference site (update `..._DocumentId` and identity-part columns together, potentially
       requiring `ReferentialId → DocumentId` resolution inside the DB), or
     - changing modeling so the child does not store an independent reference site when it is equality-constrained to an
       inherited/root value.

2. **Which upstream change are we responding to?**
   - A target-resource identity update (e.g., `SchoolId` changes on `School` while `School_DocumentId` remains stable)
     aligns naturally with the redesign’s “identity parts propagate” model.
   - A reference retargeting (e.g., SSA now points to a different School) is a different class of change: it implies the
     `..._DocumentId` itself changes. Cross-scope equality propagation that expects to keep child rows aligned may need a
     stronger “reference propagation” story than identity-part updates alone.

3. **Avoiding trigger recursion and churn**
   - The propagation routine should use null-safe “is distinct” guards so it is idempotent; repeated invocation should
     converge without repeatedly rewriting the same values.
   - Updating child rows will fire the child tables’ stamping triggers (which is desirable because `X`’s representation
     changes), but the propagation routine should avoid re-updating the source column to prevent oscillation.

4. **Performance and fan-out**
   - This feature can update many rows per changed document (all elements in one or more collections). It should be
     implemented set-based and batched, and it likely needs telemetry/guardrails similar to identity propagation fan-out.

## Open Questions

1. What exact change drives “SSA SchoolId update” in the supported scenario?
   - A `School` identity update (`SchoolId` changes but `School_DocumentId` does not), or
   - SSA changing to reference a different `School` (`School_DocumentId` changes)?
   These have different implications for what must be propagated (identity parts only vs reference key changes).
2. Are we willing to accept fan-out updates to potentially large collections as part of identity propagation?
3. Should cross-scope equality propagation be part of “key unification” or modeled as a separate derived-maintenance
   feature?
