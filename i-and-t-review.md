# Index + Trigger Inventory Review (DMS-945 / Story 07)

Scope of this review:

- Design refs: `reference/design/backend-redesign/design-docs/key-unification.md` and `reference/design/backend-redesign/epics/01-relational-model/07-index-and-trigger-inventory.md`
- Implementation focus: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel` (index/trigger derivation + identifier shortening) and the last 8 commits that bugfixed DMS-945.

Primary goal: assess whether the recent index/trigger work is correct *and* positioned to incorporate the new **key unification** design without major rework.

## High-level summary

The recent fixes significantly improve correctness for the **current** (pre-key-unification) relational model:

- Trigger identity projection now accounts for identity-component references by using **identity-part columns**, not just `..._DocumentId`.
- SQL Server propagation fallback is now modeled as **one trigger per referenced table** with deterministic fan-out payload, aligned with `key-unification.md`.
- Identifier shortening correctly rewrites propagation fallback payload identifiers.
- Index inventory now has guardrails for FK-support indexes in the presence of `_Unified` storage columns.

The biggest readiness gap for key unification is that the **codebase still has no first-class storage mapping** (e.g., `DbColumnModel.Storage` / `ColumnStorage.UnifiedAlias`). Several “key-unification ready” checks are currently implemented as **naming heuristics** and will become brittle (or incorrect) once canonical column naming is not a simple `"{Alias}_Unified"` pattern.

## Design alignment check (key-unification.md vs implementation)

### ✅ Matches design intent

- **Propagation fallback fan-out (SQL Server)**:
  - `DeriveTriggerInventoryPass` derives one `DbTriggerKind.IdentityPropagationFallback` trigger per referenced table and carries referrer actions. (`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`)
  - Payload ordering is deterministic (trigger table order + ordered referrer actions + ordered column pairs).

- **Root trigger identity projections include reference identity parts**:
  - `BuildRootIdentityProjectionColumns(...)` now expands identity-component references into local identity-part columns. This aligns with `transactions-and-concurrency.md` / `key-unification.md` guidance that identity changes can arrive via cascaded/propagated identity-part updates while `..._DocumentId` stays stable.

- **Identifier shortening covers propagation payload**:
  - `ApplyDialectIdentifierShorteningPass` rewrites tables/columns inside `DbIdentityPropagationFallbackInfo`. (`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/ApplyDialectIdentifierShorteningPass.cs`)

### ⚠️ Misaligned or fragile vs key unification

#### 1) “Storage column” invariants are currently enforced by suffix heuristics

`DeriveIndexInventoryPass.ValidateForeignKeyColumns(...)` tries to fail-fast when FK columns use:

- synthetic presence flags (suffix `_Present`), or
- binding/alias columns when a sibling `"{Column}_Unified"` exists.

File: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveIndexInventoryPass.cs`

Concerns:

- The heuristic **only detects alias misuse when the canonical column name is exactly `"{Alias}_Unified"`**.
- Key unification’s canonical column may be derived from an equivalence class (e.g., the first member, or a normalized base name), and **will not necessarily be a direct sibling of every alias**.
- False negatives: a FK could still (incorrectly) target an alias column whose canonical storage column has a different name → the current validation won’t catch it.
- False positives: any “real” column that happens to end with `_Present` / `_Unified` would trigger these checks.

Recommendation:

- Treat this as a temporary guardrail only.
- Once key unification is implemented, replace suffix checks with a real **storage mapping** check:
  - FK derivation should already target storage columns.
  - Index derivation should validate that FK columns are `Stored` (or mapped-to-stored), not `UnifiedAlias`.

#### 2) Propagation fallback payload is labeled “storage”, but today it’s derived from binding columns

The contract types are explicitly named:

- `DbIdentityPropagationColumnPair(ReferrerStorageColumn, ReferencedStorageColumn)`
- `DbIdentityPropagationReferrerAction(..., IdentityColumnPairs)`

But `DeriveTriggerInventoryPass.CollectPropagationFallbackActions(...)` currently derives these column names via:

- `binding.IdentityBindings` (referrer side)
- `BuildColumnNameLookupBySourceJsonPath(referencedTableModel, ...)` (referenced side)

File: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`

Why this matters for key unification:

- Under key unification, many of these binding columns will become **read-only aliases** (`UnifiedAlias`) and **cannot be updated**.
- The design is explicit: propagation triggers must update **canonical/storage columns only**, never aliases.

Action needed when key unification lands:

- Add storage metadata (or a lookup) so this pass can map:
  - referrer binding column → canonical storage column
  - referenced binding column → canonical storage column
- Perform de-duplication *after* storage mapping (so converged canonical columns collapse deterministically).

#### 3) Trigger naming token mismatch for propagation fallback

Design docs reference `PropagateIdentity` as the stable purpose token:

- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/epics/01-relational-model/07-index-and-trigger-inventory.md`

Implementation uses:

- `PropagationFallbackPrefix = "Propagation"` → trigger names like `TR_School_Propagation`

File: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs`

This is minor, but it’s a long-term compatibility issue because trigger names are a durable contract for migrations/rollouts.

Recommendation:

- Align to `PropagateIdentity` now (or update the docs to match implementation), before key unification expands trigger complexity.

#### 4) Identity projection ordering for reference identities is implicit

`BuildRootIdentityProjectionColumns(...)` adds *all* identity-part columns for a binding when it sees the first identity path under that reference.

This means the resulting order for reference identity parts is determined by:

- `DocumentReferenceBinding.IdentityBindings` order, which is derived from `documentPathsMapping.referenceJsonPaths[*]` order.

Why it matters:

- For value-diff gating alone, ordering is irrelevant.
- For referential-id recomputation triggers (future DDL), the ordering of identity inputs typically matters (UUIDv5 is order-sensitive).

Recommendation:

- Either:
  - Add explicit validation that `identityJsonPaths` order for identity-component reference paths matches `referenceJsonPaths[*]` order; or
  - Build identity projection columns by iterating `identityJsonPaths` and mapping each path to its exact identity-part column (so ordering is unambiguous).

## Code quality notes (duplication, dead code, simplification)

### DeriveIndexInventoryPass

- The per-table derivation approach (`tableIndexes` local list + `IsLeftmostPrefixCovered`) is clean and easy to reason about.
- `ValidateForeignKeyColumns(...)` is useful as a *fail-fast* invariant check, but it will need to be reworked once storage metadata exists (see above). Until then, keep it small and avoid expanding suffix heuristics further.

Potential simplifications:

- Replace manual prefix loop with `SequenceEqual` over spans/slices if desired, but current code is readable and likely fine.

### DeriveTriggerInventoryPass

- The propagation fan-out derivation is conceptually correct and test-covered (root + child referrers, abstract targets).
- There is duplicated “deepest matching scope prefix” logic:
  - `ReferenceBindingPass.ResolveOwningTableBuilder(...)`
  - `DeriveTriggerInventoryPass.ResolveReferenceBindingTable(...)`

Risk:

- Divergence over time (especially once `_ext` and key-unification-related scope behaviors evolve).

Recommendation:

- Centralize prefix-match selection in a shared helper (same semantics used by both binding + trigger derivation).

Minor cleanups:

- `AddIdentityColumnPair(...)` uses a string signature with `\u001F` delimiter; a `HashSet<(DbColumnName, DbColumnName)>` would be clearer and avoid delimiter edge cases.

## Test coverage gaps (specifically for key unification readiness)

The added tests are good for the current implementation, but they don’t yet prove key-unification readiness because the model has no storage metadata.

Suggested next tests once storage mapping exists:

1. **Propagation payload uses canonical storage columns**:
   - Fixture where referrer has `AliasColumn` + `CanonicalColumn` and `AliasColumn` is `UnifiedAlias`.
   - Assert `DbIdentityPropagationColumnPair.ReferrerStorageColumn == CanonicalColumn`.

2. **Index derivation rejects alias columns regardless of canonical naming**:
   - Fixture where canonical column name is *not* `"{Alias}_Unified"`.
   - Ensure validation still catches “FK targets alias”.

3. **Presence-gated alias impacts identity compare semantics** (future trigger DDL work):
   - Fixture where an identity projection includes a presence-gated unified alias.
   - Ensure the DDL generation compares the **presence-gated canonical expression** (not `UPDATE(alias)` gating).

## Concrete checklist to “make DMS-945 ready for key unification”

1. Add first-class storage metadata (`DbColumnModel.Storage` / `UnifiedAlias` + presence gate + canonical column).
2. Update FK derivation to target storage columns and de-duplicate after storage mapping (per `key-unification.md`).
3. Update `DeriveIndexInventoryPass` validation to check storage metadata (not suffixes).
4. Update `DeriveTriggerInventoryPass` propagation payload to map bindings → storage columns (and de-dupe after mapping).
5. Align propagation trigger naming token (`Propagation` vs `PropagateIdentity`) to avoid long-term drift.
6. When trigger DDL is implemented: ensure identity projection change detection is **null-safe value-diff** and uses the **presence-gated canonical expression** for unified aliases (no `UPDATE(column)` gates).

