# Index + Trigger Inventory Review (Story 07 / DMS-945) — readiness for Key Unification

## Scope

- Design refs:
  - `reference/design/backend-redesign/design-docs/key-unification.md`
  - `reference/design/backend-redesign/epics/01-relational-model/07-index-and-trigger-inventory.md`
- Implementation focus:
  - Index inventory: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveIndexInventoryPass.cs`
  - Trigger inventory: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`
  - Identifier shortening for inventories: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/ApplyDialectIdentifierShorteningPass.cs`
  - Trigger DDL identity-diff compare logic: `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs`

## Executive summary

The bugfix work around story 07 is directionally strong and materially closer to the `key-unification.md` design than
the previous heuristic-based approach:

- The model now has first-class storage metadata (`DbColumnModel.Storage` with `ColumnStorage.Stored` /
  `ColumnStorage.UnifiedAlias`), and the index/trigger inventories actively use it.
- Index derivation fails fast if FK endpoints target unified aliases or synthetic presence columns (key-unification
  invariants).
- Trigger identity-projection derivation is now based on identity JSONPaths and expands identity-component references
  to identity-part columns (so identity changes can be detected even when `..._DocumentId` remains stable).
- SQL Server identity-propagation fallback payload is storage-aware (alias → canonical mapping) and de-duplicates
  converged unified pairs deterministically.
- Trigger DDL “identity changed” gating correctly uses null-safe value-diff semantics and expands unified aliases to the
  presence-gated canonical expression (no `UPDATE(column)` / `UPDATE OF column` gating).

The main correctness risk I see in the current implementation is in **DDL emission for indexes** (it emits `CREATE
INDEX` for PK/UK-implied inventory entries even though PK/UK constraints are also emitted), which will be invalid DDL if
`RelationalModelDdlEmitter` is used beyond tests. There are also a couple of duplication/consistency issues that are
worth cleaning up before key unification is fully wired into the default pass list.

## Integration note (pass ordering)

Key unification is not currently in the default pass list (`RelationalModelSetPasses.CreateDefault()`), but the story 07
work is already written to *consume* key-unification storage metadata:

- `DeriveIndexInventoryPass` assumes FK constraints target stored columns and will now fail fast if an FK targets a
  unified alias.
- `DeriveTriggerInventoryPass` assumes unified aliases exist and resolves propagation payloads to canonical storage
  columns when they do.
- `RelationalModelDdlEmitter` expands unified aliases to presence-gated canonical expressions for identity diff gating.

When key unification is wired into the default pipeline, it should follow the ordering in `key-unification.md` (run
after `ReferenceBindingPass` and before constraint derivation + index/trigger inventory passes) so downstream consumers
see the unified model.

## Design alignment checks (key-unification.md)

### Identity change detection (triggers)

Design requirement: identity-change gating must be value-diff based and must treat unified members as the *presence-gated
canonical expression*, not “updated column” detection.

- ✅ `DbTriggerInfo.IdentityProjectionColumns` is documented as a null-safe value-diff compare set (contract level):
  `src/dms/backend/EdFi.DataManagementService.Backend.External/DerivedRelationalModelSetContracts.cs`
- ✅ DDL expansion for unified aliases uses canonical + optional presence gate:
  `RelationalModelDdlEmitter.BuildIdentityValueExpression(...)` → `BuildUnifiedAliasExpression(...)`.

### Propagation fallback (SQL Server)

Design requirement: propagation fallback must update **canonical/storage columns only**, never unified aliases.

- ✅ `DeriveTriggerInventoryPass.CollectPropagationFallbackActions(...)` resolves both referrer and referenced columns
  through `ResolveStorageColumn(...)` and only emits stored columns into `DbIdentityPropagationColumnPair`.

### FK / index inventory interaction

Design requirement: composite FKs must target canonical/storage columns (not binding/alias columns), and FK-supporting
indexes must be derived from the final FK columns.

- ✅ `DeriveIndexInventoryPass.ValidateForeignKeyColumns(...)` fails fast if FK columns reference a `UnifiedAlias` or a
  synthetic presence column.

## Findings: index inventory

### What looks correct

- `DeriveIndexInventoryPass` derives:
  - a PK-implied index (name mirrors PK constraint name),
  - one UK-implied index per `TableConstraint.Unique`, and
  - one FK-support index per `TableConstraint.ForeignKey`, suppressed when covered by an existing leftmost prefix
    (`IsLeftmostPrefixCovered(...)`).
- FK-support index derivation is storage-aware by validation: FK endpoints must be stored columns (not unified aliases),
  which matches the key unification design’s constraint targeting rule.

### Issues / suggestions

1) **`RelationalModelDdlEmitter` likely emits invalid index DDL if used**

`RelationalModelDdlEmitter.AppendCreateTable(...)` emits PK + UNIQUE constraints *and then* `AppendIndexes(...)` emits
`CREATE INDEX` for all inventory entries, including `DbIndexKind.PrimaryKey` and `DbIndexKind.UniqueConstraint`.

If this emitter is intended to become “real” DDL (not just a test shim), it should almost certainly:

- emit `CREATE INDEX` only for `DbIndexKind.ForeignKeySupport` and `DbIndexKind.Explicit`, **or**
- stop emitting PK/UK as table constraints and treat them as indexes only (but that would ripple into FK legality and
  constraint naming expectations).

2) **Presence-flag identification could be tightened**

`DeriveIndexInventoryPass.BuildPresenceColumnSet(...)` classifies “synthetic presence columns” using `ColumnKind.Scalar`
only. That’s probably fine with the current `KeyUnificationPass` behavior, but for future-proofing it may be worth
requiring something stronger (e.g., scalar kind boolean/bit and `SourceJsonPath == null`) so a mis-modeled presence gate
can’t accidentally block valid FK columns (or let invalid ones slip by).

3) **Minor duplication**

`DeriveIndexInventoryPass` builds a `columnsByName` dictionary and then calls `BuildPresenceColumnSet(...)`, which builds
another dictionary. Not a correctness issue, but it’s easy to simplify by threading `columnsByName` through.

## Findings: trigger inventory

### What looks correct

1) **Root identity projection columns**

`DeriveTriggerInventoryPass.BuildRootIdentityProjectionColumns(...)`:

- iterates `identityJsonPaths` in-order (good determinism),
- maps scalar identity paths to root table columns by `SourceJsonPath`,
- for identity-component references, it expands to the locally stored identity-part columns
  (`DocumentReferenceBinding.IdentityBindings[*].Column`) so that cascaded/propagated identity-part updates are detected
  even when `..._DocumentId` is unchanged.

This matches the `key-unification.md` intent that identity changes can arrive via canonical-column updates without any
direct writes to per-site alias columns.

2) **SQL Server propagation fallback payload is storage-aware**

`CollectPropagationFallbackActions(...)` now resolves unified aliases to canonical stored columns on both sides and
dedupes identity pairs via a typed `(DbColumnName, DbColumnName)` key (`AddIdentityColumnPair(...)`). This is exactly the
kind of “collapse after storage mapping” behavior needed when multiple identity endpoints converge onto one canonical
column.

3) **Deterministic ordering**

- Propagation triggers are emitted one-per-referenced-table in deterministic order (`EmitPropagationFallbackTriggers`).
- Referrer actions are ordered deterministically, and identity pair ordering is stable.

### Issues / suggestions

1) **Duplicated “presence column set” helpers with inconsistent semantics**

There are two `BuildPresenceColumnSet(...)` implementations:

- `DeriveIndexInventoryPass.BuildPresenceColumnSet(...)` tries to capture **synthetic presence flags only** (it filters by
  presence-column kind).
- `DeriveTriggerInventoryPass.BuildPresenceColumnSet(...)` captures **all** presence columns referenced by unified aliases
  (including reference `..._DocumentId` presence gates).

Both are defensible, but the names/comments imply the same thing and the error messages in
`DeriveTriggerInventoryPass.ResolveStorageColumn(...)` call them “synthetic presence columns” even though reference
presence columns can be included.

Recommendation:

- Rename one/both helpers to make the intent explicit (e.g., `BuildSyntheticPresenceFlagSet` vs
  `BuildAliasPresenceGateSet`), or centralize in a single helper that can return both sets.

2) **Silent “continue” when a mapping has no derived binding**

In `CollectPropagationFallbackActions(...)`, if a `DocumentReferenceMapping` has no corresponding
`DocumentReferenceBinding` (`bindingByReferencePath.TryGetValue(...)`), the code silently skips it.

If this is truly expected (e.g., because some mappings don’t result in stored references), it should be documented. If
it’s not expected, consider failing fast: silently missing a propagation action is the sort of error that is painful to
diagnose later.

3) **Shared table-scope resolution logic is still duplicated**

`DeriveTriggerInventoryPass.ResolveReferenceBindingTable(...)` is essentially the same “deepest matching scope” selection
problem as `ReferenceBindingPass.ResolveOwningTableBuilder(...)` (both call
`ReferenceObjectPathScopeResolver.ResolveDeepestMatchingScope(...)` with different wrappers).

Not a correctness bug today, but it’s easy for the two call sites to drift as key unification expands scope-related
edge cases. Consolidating the wrapper logic would reduce risk.

## Findings: trigger DDL emission (identity-diff gating)

### What looks correct

`RelationalModelDdlEmitter` now:

- emits null-safe value-diff predicates for identity comparisons (`IS DISTINCT FROM` on Pgsql; explicit null-safe
  inequality on Mssql), and
- expands any `UnifiedAlias` identity-projection column to the canonical + presence-gated expression
  (matching `key-unification.md`’s normative guidance).

### Issues / suggestions

The DDL emitter’s trigger statements are still clearly “skeleton” output (e.g., missing event lists / timing), and it
also treats triggers with empty `IdentityProjectionColumns` as “noop”. That may be intentional for now, but if/when this
becomes the real DDL path, it will need to:

- emit valid trigger syntax (timing + events), and
- separate “identity-changed gating” from “does work at all” (stamping triggers on child tables still need to bump
  Content stamps even when they have no identity projection columns).

## Priority recommendations (before wiring key unification into defaults)

1) Fix/clarify index DDL emission behavior for PK/UK-implied indexes (`RelationalModelDdlEmitter.AppendIndexes(...)`).
2) Normalize the “presence column set” helpers (naming + semantics + error messages) to avoid confusion once presence
   flags are added broadly by key unification.
3) Decide whether “missing binding for mapping” should be a hard error in propagation fallback derivation.
4) Consider consolidating table-scope resolution wrapper logic used by reference binding vs propagation derivation.
