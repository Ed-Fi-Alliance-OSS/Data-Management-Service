# Trigger + Index Findings for Key Unification (post DMS-945 / commit 24655fe972409888)

This note captures gaps between the **key unification** design (`reference/design/backend-redesign/design-docs/key-unification.md`)
and the now-landed **index/trigger inventory derivation** implementation added in commit `24655fe972409888` (DMS-945).

Scope: trigger/index **support needed for key unification**, given the current `DeriveIndexInventoryPass` /
`DeriveTriggerInventoryPass` contracts and behaviors.

## 1) SQL Server cascade vs fallback strategy mismatch

Key unification’s design assumes:

- SQL Server should **attempt** declarative `ON UPDATE CASCADE` for eligible reference FKs, and
- only emit trigger-based propagation fallback **when SQL Server rejects** the FK graph due to multiple-cascade-path/cycle restrictions.

However, the current relational-model derivation unconditionally disables update cascades for *all* reference FKs on MSSQL:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/ReferenceConstraintPass.cs` (see `onUpdate` selection around `SqlDialect.Mssql ? ReferentialAction.NoAction : ...`).

Implications / required design update:

- `key-unification.md` should be updated to reflect the current “**always `NO ACTION` on MSSQL + always trigger-based propagation fallback**” strategy, **or**
- the implementation should be changed back to “try cascade, fallback on rejection” if that remains the intended design.

## 2) IdentityPropagationFallback trigger “owning table” ambiguity (and likely inversion)

To replace `ON UPDATE CASCADE`, an identity-propagation fallback trigger must fire when the **referenced/target** row’s
identity columns change, and then update **referrer** rows.

But the current trigger intent derivation records propagation triggers as owned by the **referrer** table:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`
  - `DbTriggerInfo.Table` is set to the referrer root table.
  - `DbTriggerInfo.TargetTable` is set to the referenced resource root or abstract identity table.
- The unit tests explicitly lock this in:
  - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/DeriveTriggerInventoryPassTests.cs` asserts (for example) that `TR_Enrollment_Propagation_School` has `Table.Name == "Enrollment"` and `TargetTable.Name == "School"`.

This is a design gap for key unification (and for propagation generally), because DDL emission will eventually need to
know **which table the trigger is created on** vs **which table is being updated by the trigger**. Today the contract
does not model this clearly:

- `DbTriggerInfo` documents `TargetTable` only for abstract identity maintenance, not propagation fallback:
  - `src/dms/backend/EdFi.DataManagementService.Backend.External/DerivedRelationalModelSetContracts.cs`
- The current DDL emitter always emits `CREATE TRIGGER ... ON <trigger.Table>`:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs`

Required design clarification/change:

- Define (in docs + contracts) what `Table` and `TargetTable` mean for `DbTriggerKind.IdentityPropagationFallback`.
  - If the trigger truly fires on the referenced table, the contract likely needs an explicit `TriggerTable` and
    `AffectedTable` (or similar), not a single ambiguous `Table`.
  - If the current `DbTriggerInfo` shape is retained, the DDL emitter must be able to interpret propagation triggers as
    “create trigger on `TargetTable` that updates `Table`”, which conflicts with current semantics for other trigger kinds.

## 3) Propagation fallback must update canonical storage columns under unification

Key unification requires trigger-based propagation fallback to update **canonical stored columns** for unified identity
parts (never the alias/binding columns), because unified member columns become computed/generated read-only aliases:

- `reference/design/backend-redesign/design-docs/key-unification.md` (SQL Server fallback section; triggers must update canonicals)

The current propagation-trigger intent records “propagated columns” as:

- `binding.IdentityBindings.Select(ib => ib.Column)` in
  `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`

Under key unification, those `ib.Column` values are exactly the *per-site binding column names* that are intended to
become unified aliases (read-only). There is no current mechanism in the trigger inventory contract to map these to
canonical storage columns.

Required design update:

- When key unification is introduced (`ColumnStorage` + canonical/alias metadata), propagation-trigger derivation or
  DDL emission must map “binding columns” to **storage columns** (canonical) before generating trigger SQL.
- The design should also specify de-duplication behavior when multiple propagated binding columns map to the same canonical
  column (common under unification).

## 4) Identity projection columns: current derivation does not include propagated identity values

The redesign’s referential-id recomputation and identity stamping depend on **locally stored identity columns**,
including **propagated reference identity values** for identity-component references:

- `reference/design/backend-redesign/design-docs/overview.md` (referential ids recomputed from locally present identity columns, including propagated reference identity values)
- `reference/design/backend-redesign/design-docs/update-tracking.md` (identity projection includes reference identity values stored alongside identity-component references; canonical under key unification)

But the current trigger inventory derives root `IdentityProjectionColumns` by reusing the *root unique-constraint* mapping logic:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs` uses
  `BuildReferenceIdentityBindings(...)` and, when an `identityJsonPath` is sourced from a reference object path, it
  adds **only** the reference FK column (`..._DocumentId`) to the identity projection set.
- This mirrors `RootIdentityConstraintPass` behavior:
  - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/RootIdentityConstraintPass.cs`

That mapping is reasonable for the *relational guardrail* UNIQUE constraint (which intentionally uses stable
`..._DocumentId` columns), but it is insufficient for:

- DB-side referential-id recomputation (UUIDv5 over identity values), and
- correct identity-stamp bumping when cascades (or fallback propagation) update propagated reference identity values while
  `..._DocumentId` remains stable.

Required design update:

- Define a trigger-derivation identity-projection column set that is **not** the same as the root UNIQUE constraint’s
  column set.
- For each `DocumentReferenceBinding` where `IsIdentityComponent == true`
  (`src/dms/backend/EdFi.DataManagementService.Backend.External/RelationalModelTypes.cs`),
  include the stored reference identity columns (`IdentityBindings[*].Column`) in the identity projection column list.
- Under key unification, those “identity projection values” must be interpreted as presence-gated unified-alias
  expressions or directly compared via the alias column value-diff, not by “column updated” detection.

## 5) “Identity columns changed” must be value-diff based under unified aliases

Key unification introduces generated/computed alias columns whose values change when the canonical storage column
changes, even though alias columns are never written directly. Therefore, trigger implementations must not use
“column updated” detection for unified members, and must instead use **old vs new value-diff** semantics (null-safe)
based on the presence-gated canonical expression for unified aliases.

Gaps exposed by DMS-945’s trigger inventory contract:

- Story text and the current `DbTriggerInfo.IdentityProjectionColumns` documentation can be read as “use these columns
  in `UPDATE OF (...)` / `IF UPDATE(...)` gating”:
  - `reference/design/backend-redesign/epics/01-relational-model/07-index-and-trigger-inventory.md`
  - `src/dms/backend/EdFi.DataManagementService.Backend.External/DerivedRelationalModelSetContracts.cs`
- Key unification explicitly requires the opposite: value-diff gating for unified members.

Required design update:

- Update the “trigger inventory” story/design wording and the `DbTriggerInfo` contract documentation to explicitly state
  that `IdentityProjectionColumns` are a **value-diff comparison set**, not an “updated columns” set.
- Ensure the eventual DDL emitter has enough metadata to compare unified values correctly (either by comparing the
  computed alias column values directly, or by expanding `ColumnStorage.UnifiedAlias` to a presence-gated expression).

## 6) Propagation fallback trigger coverage is root-table only

`DeriveTriggerInventoryPass` only emits propagation fallback triggers for **root-table** reference bindings:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs` filters
  out bindings where `binding.Table` is not the resource root table.

Under key unification and the broader redesign, references can exist in:

- child/collection tables, and
- `_ext` extension tables.

When referenced identities update, those tables’ propagated identity columns also need to be kept consistent on SQL
Server (since cascades are disabled there today), or representation/query semantics will drift from PostgreSQL.

Required design update:

- Specify whether SQL Server propagation fallback is required for non-root reference bindings, and if so,
  extend inventory derivation to cover child/extension reference sites as well.

## 7) Index inventory: filtered/partial indexes for unified predicate performance are not representable

Key unification notes that if predicate rewriting from alias → canonical is not implemented, performance may require
additional indexes, preferably filtered/partial on the canonical column gated by presence (`P IS NOT NULL`).

The current index-inventory contract cannot represent filtered/partial predicates:

- `DbIndexInfo` is `{ Name, Table, KeyColumns, IsUnique, Kind }` only:
  - `src/dms/backend/EdFi.DataManagementService.Backend.External/DerivedRelationalModelSetContracts.cs`

Required design update (if this option remains on the table):

- Either defer this entirely (and require predicate rewriting instead), or
- extend the index inventory model to represent dialect-specific filtered/partial index predicates in a SQL-free way.

## 8) Minor documentation alignment: pass naming / ordering reference

`key-unification.md` references an `IndexAndTriggerInventoryPass`, but the implementation is split into:

- `DeriveIndexInventoryPass`
- `DeriveTriggerInventoryPass`

See:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Build/RelationalModelSetPasses.cs`

This should be updated in the key-unification doc to match the current pipeline shape.

