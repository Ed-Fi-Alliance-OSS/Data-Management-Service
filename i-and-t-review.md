# Index + Trigger Inventory Review (Story 07 bugfix) — Key Unification Readiness

## Scope / what I reviewed

- Design docs:
  - `reference/design/backend-redesign/epics/01-relational-model/07-index-and-trigger-inventory.md`
  - `reference/design/backend-redesign/design-docs/key-unification.md`
- Implementation (DDL emission excluded by request):
  - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveIndexInventoryPass.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/ApplyDialectIdentifierShorteningPass.cs` (inventory-only rewriting)
  - Related storage-mapping logic for consistency comparisons:
    - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/ReferenceConstraintPass.cs`
- Unit tests:
  - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/DeriveIndexInventoryPassTests.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/DeriveTriggerInventoryPassTests.cs`

## Executive summary

The bugfix work around index/trigger inventories is broadly aligned with the new key-unification contract:

- **FK-support index derivation** is effectively “storage-only”: the pass now *fails fast* when foreign keys reference
  unified alias columns or synthetic presence columns.
- **SQL Server identity propagation fallback** payload derivation correctly resolves identity endpoints to **canonical
  stored columns**, and it **de-duplicates** unified members so the trigger updates storage once.
- **Root identity projection columns** used by stamping/maintenance triggers correctly incorporate identity-component
  reference part columns (not the reference `..._DocumentId`), which is required for `allowIdentityUpdates` scenarios.

The main remaining opportunities are (1) reducing duplicated “storage resolution” helpers across passes, and (2) adding
one or two targeted invariant checks/tests so key unification can evolve without regressing inventories.

---

## Index inventory (`DeriveIndexInventoryPass`)

### What looks correct / strong

- **PK/UK index intent derivation** matches the story design:
  - PK-implied index name matches the PK constraint name.
  - UK-implied indexes reuse the unique constraint names.
- **FK-support index policy** matches the story design:
  - One candidate per FK (`fk.Columns` in key order).
  - Suppression when the FK columns are a **leftmost prefix** of any already-derived index (PK, UK, prior IX).
- **Key unification safety**: `ValidateForeignKeyColumns` blocks two key-unification footguns:
  - FK columns that target `ColumnStorage.UnifiedAlias` (binding/alias) instead of the canonical stored column.
  - FK columns that target synthetic optional-path presence flags (storage-only `bool?`/`bit?` columns).
- **Good test coverage** for key-unification-related invariants and “converged” storage keysets:
  - `Given_Key_Unification_Storage_Columns_For_FK_Indexes` verifies two FK endpoints that converge to the same storage
    column list produce only one FK-support index.
  - Alias FK columns and synthetic presence FK columns fail fast (with fixture passes).
  - Stored columns that merely *look* like unification columns by suffix (`_Unified`, `_Present`) are allowed.

### Risks / correctness gaps (key unification integration)

1. **FK target side isn’t validated here**
   - This pass validates `foreignKey.Columns` against the *local* table model, but does not validate
     `foreignKey.TargetColumns` against the referenced table model.
   - Under the key-unification design, FKs must reference **storage columns on both sides**. If a future regression
     were to populate `TargetColumns` with binding/alias columns, this pass wouldn’t catch it.
   - Recommendation: add a set-level “FK invariant validator” (or extend this pass using set context) that asserts
     both `Columns` and `TargetColumns` resolve to `ColumnStorage.Stored` columns.

2. **Presence-gate validation is asymmetric**
   - `BuildSyntheticPresenceFlagSet` validates presence columns *only when they look like synthetic flags*.
   - If a unified alias mistakenly points at a scalar presence column that is **boolean stored but has a non-null
     `SourceJsonPath`**, it is neither recognized as synthetic nor rejected as invalid, so the metadata error can slip
     through this pass.
   - Recommendation: when a unified alias uses a scalar presence gate, validate the full synthetic-flag contract
     (`ScalarKind.Boolean`, nullable, stored, and `SourceJsonPath == null`).

### Duplication / simplification opportunities

- The “presence column set” logic is implemented in multiple places with slightly different rules:
  - `DeriveIndexInventoryPass.BuildSyntheticPresenceFlagSet` (boolean + nullable + stored + source-null).
  - `ReferenceConstraintPass.BuildPresenceColumnSet` (scalar-only heuristic).
  - `DeriveTriggerInventoryPass.BuildPresenceGateColumnSet` (collects all presence columns, including reference-site).
- Recommendation: extract a shared helper (e.g., `UnifiedAliasMetadata`) that:
  - validates alias → canonical existence,
  - classifies presence columns (reference-site vs synthetic),
  - exposes `ResolveToStorageColumn()` and `IsSyntheticPresenceFlag()` consistently.

---

## Trigger inventory (`DeriveTriggerInventoryPass`)

### What looks correct / strong

1. **DocumentStamping triggers**
   - Emits one stamping trigger per schema-derived table (root + child + extension) for non-descriptor resources.
   - Correctly sets `KeyColumns` to the table’s document id key-part:
     - root table: `DocumentId`
     - child/ext: `{RootBaseName}_DocumentId` (detected via `IsDocumentIdColumn`)
   - Root stamping triggers carry `IdentityProjectionColumns`; child/ext stamping triggers do not.

2. **ReferentialIdentityMaintenance + AbstractIdentityMaintenance triggers**
   - Root-only maintenance triggers and subclass abstract maintenance triggers match the story design intent.
   - Identity projection columns are resolved from `identityJsonPaths` and are **ordered by `identityJsonPaths`**
     (tests cover interleaving).

3. **Identity projection columns correctly include identity-component reference parts**
   - `BuildRootIdentityProjectionColumns` expands identity-component reference paths into the local stored identity-part
     columns (`DocumentReferenceBinding.IdentityBindings`), instead of using the stable `..._DocumentId`.
   - This is critical: if `allowIdentityUpdates` causes referenced identity parts to change, the referrer’s `..._DocumentId`
     can remain stable while identity parts change; triggers must still detect and stamp/recompute.

4. **MSSQL IdentityPropagationFallback triggers are storage-correct under key unification**
   - `CollectPropagationFallbackActions` maps:
     - referrer binding column (possibly `UnifiedAlias`) → **canonical stored column**
     - referenced binding column (possibly `UnifiedAlias`) → **canonical stored column**
   - `AddIdentityColumnPair` de-duplicates unified members so a unification class only produces one update mapping.
   - Deterministic ordering is enforced when emitting trigger payload (`BuildIdentityColumnPairSignature` ordering).
   - Tests explicitly verify:
     - unified bindings collapse to one canonical pair, and
     - emitted pair columns are `ColumnStorage.Stored` columns (never aliases).

### Risks / correctness gaps (key unification integration)

1. **Dead/unused data in this pass**
   - `resourcesByKey` stores `ResourceEntry(index, model)`, but the `index` is never used in this file.
   - Recommendation: store `ConcreteResourceModel` directly, or keep `ResourceEntry` but remove the unused index
     construction for this pass to reduce noise and allocations.

2. **Implicit coupling to earlier passes for presence-column validity**
   - `BuildPresenceGateColumnSet` does not validate that `UnifiedAlias.PresenceColumn` actually exists, is stored, etc.
   - In the default pipeline, that metadata is effectively validated elsewhere (e.g., index-pass synthetic presence
     validation), but this creates cross-pass coupling that is easy to break in tests or partial pipelines.
   - Recommendation: either:
     - validate presence columns in `DeriveTriggerInventoryPass` (cheap table-local lookup), or
     - centralize validation in a shared helper used by all consumers.

3. **Storage-resolution logic is duplicated**
   - This pass’s `ResolveStorageColumn` is conceptually the same as `ReferenceConstraintPass.ResolveStorageColumn`, with
     slightly different presence-column semantics.
   - Recommendation: consolidate into one shared storage resolver with explicit parameters for:
     - “treat scalar presence columns as invalid” (FK derivation),
     - “treat any presence column as invalid” (propagation identity columns).

---

## Test suite notes (inventory-focused)

### What’s strong

- Index derivation:
  - FK leftmost-prefix suppression.
  - Key-unification invariants: alias/presence misuse fails fast; custom canonical names are handled (no suffix heuristics).
  - Converged-endpoint behavior is deterministic and collision-free.
- Trigger derivation:
  - Root vs child stamping behavior and key columns.
  - Identity projection ordering and inclusion of identity-component reference parts.
  - Dialect behavior for propagation fallback (MSSQL only).
  - Key-unification collapse and “stored-only” propagation column pairs.

### Suggested additional regressions (small, high value)

- **FK target-side invariant**: a fixture where `foreignKey.TargetColumns` contains a `UnifiedAlias` should fail fast
  (either in the constraint pass or via a new invariant pass). This locks in the key-unification “FKs are storage-only”
  rule on both sides.
- **Synthetic presence flag validation**: a fixture where a unified alias points at a scalar presence column that is
  boolean/stored but has a non-null `SourceJsonPath` should fail fast (to prevent accidental “presence” columns that
  are actually API-bound fields).

---

## Bottom line

The current index/trigger inventory work is largely ready for the key-unification design:

- Index derivation enforces “FKs are storage-only” and behaves well when multiple FK endpoints converge on the same
  storage key set.
- Trigger derivation correctly uses binding columns for identity compare sets and storage columns for propagation
  updates, and it collapses unified identity parts deterministically.

The main improvements I’d make before/while integrating the updated key-unification design are:

1. Add a set-level invariant (or targeted tests) that asserts FK **target columns** are storage-only too.
2. Consolidate storage/presence resolution + validation into a shared helper to reduce drift between passes.
