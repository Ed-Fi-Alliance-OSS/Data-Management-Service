---
jira: DMS-1042
jira_url: https://edfi.atlassian.net/browse/DMS-1042
---

# Story: Key Unification (Canonical Columns + Generated Aliases; Presence-Gated When Optional)

## Description

Implement ApiSchema key unification (`resourceSchema.equalityConstraints`) in the derived relational model per:

- `reference/design/backend-redesign/design-docs/key-unification.md`
- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (bind vs storage semantics)
- `reference/design/backend-redesign/design-docs/ddl-generation.md` (FK/UK/CK implications)

ApiSchema equality constraints indicate that the same logical identity value can appear at multiple JSON paths in one
resource document (often across multiple reference sites). The relational model must ensure these equality-constrained
values have a **single physical source of truth** per table row to prevent drift and to avoid unsafe “mid-cascade”
failure modes (e.g., PostgreSQL `ON UPDATE CASCADE` interacting with `CHECK (colA = colB)` across two independently
cascaded writable columns).

This story introduces (and/or aligns existing implementation with) the key-unification contract:

- For each same-table unification class, create one **canonical stored column** (writable; storage-only; participates
  in composite FKs and propagation).
- Preserve the existing per-site/per-path binding column shape, but convert unified binding columns into **generated
  persisted aliases** of the canonical column (`ColumnStorage.UnifiedAlias`).
- Preserve presence semantics:
  - Reference-site identity aliases are gated by `{RefBaseName}_DocumentId` presence.
  - Optional non-reference scalar/descriptor aliases are gated by synthetic nullable boolean presence flags
    (`{PathColumnName}_Present`), and must not “leak” canonical values into absent paths.
- Expose explicit model metadata so downstream consumers can deterministically answer:
  - “bind by API JsonPath” (binding/path column), vs
  - “write/FK/cascade against physical storage” (canonical/storage column).

Scope note: key unification is **row-local** and applies only when both constraint endpoints resolve to supported
endpoint kinds (`Scalar`, `DescriptorFk`) on the **same physical table**. Cross-table equality constraints remain
Core-only enforcement and must be surfaced via deterministic diagnostics (not silently dropped).

## Integration (ordered passes)

Key unification is a set-level pass that must run late enough that all candidate endpoint columns exist, and early
enough that all downstream consumers see a unified model:

- Set-level (`DMS-1033` ordered passes): run `KeyUnificationPass` after `ReferenceBindingPass` and before
  `AbstractIdentityTableAndUnionViewDerivationPass`, `ReferenceConstraintPass`, index/trigger inventory, dialect
  hashing/shortening, and canonical ordering.

Downstream consumers must follow the bind-vs-storage interpretation rules in `key-unification.md`:

- Constraint derivation:
  - composite reference FKs use canonical storage columns for unified identity parts,
  - all-or-none constraints remain on per-site binding columns (aliases) + `..._DocumentId`.
- Descriptor FK derivation:
  - anchor FK constraints on the storage column (canonical when unified), and
  - de-duplicate when multiple binding columns map to the same storage column.
- Dialect hashing/shortening and final canonical ordering must update and respect key-unification metadata.

## Acceptance Criteria

- For a fixture with applied same-table `equalityConstraints`:
  - each unification class produces exactly one canonical stored column (`SourceJsonPath = null`),
  - each member endpoint retains its binding column name + `SourceJsonPath`, but is marked
    `Storage = UnifiedAlias(Canonical, Presence?)`,
  - `DbTableModel.KeyUnificationClasses` is populated deterministically (class order and member order stable).
- Presence semantics are preserved:
  - reference-site unified aliases are presence-gated by the reference `..._DocumentId` column,
  - optional non-reference unified aliases are presence-gated by synthetic nullable boolean `..._Present` columns,
  - required non-reference unified aliases are ungated.
- Synthetic presence flags:
  - exist only for optional non-reference unified members,
  - are nullable stored scalar booleans with `SourceJsonPath = null`,
  - have a per-column `NULL-or-TRUE` hardening CHECK constraint (no `FALSE`/`0` allowed),
  - never participate in foreign keys.
- Storage targeting invariants hold:
  - no `TableConstraint.ForeignKey` references `UnifiedAlias` columns (local or target),
  - descriptor FK constraints are emitted once per `(table, storage_column)` pair (de-duplicated when unified),
  - FK-supporting referenced-key UNIQUE constraints use canonical storage columns after mapping and de-duplication.
- Equality-constraint endpoint resolution and diagnostics:
  - endpoints resolve strictly by exact `DbColumnModel.SourceJsonPath` match (no naming heuristics),
  - unresolved/ambiguous endpoints and unsupported endpoint kinds fail fast with actionable diagnostics,
  - every equality constraint is classified deterministically as `applied`, `redundant`, or `ignored` (`cross_table`)
    and is surfaced in relational-model manifest output.
- Determinism and dialect rewriting:
  - column ordering respects alias dependencies (canonical + presence columns precede dependent aliases),
  - dialect identifier shortening updates all key-unification references (canonical/presence pointers, member lists,
    and any constraints that reference key-unification support columns).

## Tasks

1. Model surface updates (derived model + contracts):
   - Ensure `DbColumnModel` carries `ColumnStorage` (`Stored` vs `UnifiedAlias(canonical, presence?)`).
   - Ensure `DbTableModel` carries ordered `KeyUnificationClasses`.
   - Add a `TableConstraint.NullOrTrue` (or equivalent) to represent synthetic presence-flag hardening constraints and
     ensure it participates in dialect hashing/shortening and manifest emission.
2. Implement/align `KeyUnificationPass` with `key-unification.md`:
   - Parse `resourceSchema.equalityConstraints` and merge extension constraints into the owning base resource model.
   - Resolve endpoints strictly via exact `DbColumnModel.SourceJsonPath` binding; fail fast on ambiguity.
   - Classify every equality constraint as `applied`/`redundant`/`ignored(cross_table)` and record deterministic
     diagnostics for manifest output.
   - Build per-table connected components for applied same-table edges.
   - Validate supported kinds (`Scalar`, `DescriptorFk`) and member type compatibility (scalar type exact match;
     descriptor target resource exact match).
   - For each unification class:
     - derive canonical kind/type/nullability deterministically,
     - allocate canonical column name per deterministic naming + collision rules,
     - add the canonical stored column (`SourceJsonPath = null`),
     - convert member binding columns to `UnifiedAlias` with presence gating:
       - reference-identity members gate by `{RefBaseName}_DocumentId` (via `DocumentReferenceBinding` membership),
       - optional non-reference members gate by synthetic `..._Present` flags with deterministic naming and collision
         handling,
       - required non-reference members are ungated aliases,
     - append one `NullOrTrue` CHECK constraint per synthetic presence flag column.
   - Populate `DbTableModel.KeyUnificationClasses` with deterministic ordering.
3. Update downstream passes to use storage mapping where required:
   - Ensure composite reference FK derivation maps both local and target identity-part columns to storage columns and
     de-duplicates repeated canonical columns after mapping while preserving identity-path order.
   - Update descriptor FK derivation to:
     - map descriptor binding columns to storage columns,
     - emit exactly one FK per `(table, storage_column)` pair, and
     - emit deterministic manifest diagnostics when de-duplication occurs.
   - Add/extend invariant validators to fail fast when:
     - any FK references a `UnifiedAlias` column,
     - any FK references a synthetic presence flag column,
     - any alias points to a missing/non-stored canonical column.
4. Deterministic ordering + identifier rewriting:
   - Update canonical ordering to enforce alias dependencies within `DbTableModel.Columns` (canonical and non-null
     presence columns precede dependent aliases).
   - Update dialect identifier hashing/shortening to rewrite:
     - `UnifiedAlias(CanonicalColumn, PresenceColumn)` pointers,
     - `KeyUnificationClass` member lists,
     - `NullOrTrue` constraints and any other key-unification-derived identifiers.
5. Tests + goldens:
   - Add unit tests covering:
     - reference-site unification (presence gate is `..._DocumentId`),
     - optional scalar unification (synthetic `..._Present` + `NullOrTrue` constraint),
     - unified descriptor endpoints (descriptor FK de-duplication + diagnostics),
     - cross-table equality constraints (ignored + reported),
     - strict endpoint resolution (unresolved/ambiguous/unsupported-kind fail-fast),
     - dialect shortening updates unification references,
     - column ordering dependency invariant.
   - Add/update golden/snapshot fixtures to validate manifest output includes key-unification metadata and
     diagnostics deterministically.

