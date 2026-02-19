---
jira: TBD
jira_url: TBD
---

# Story: Plan Contracts + Deterministic Bindings (Parameter Naming, Ordering, Metadata)

## Description

Introduce the runtime “plan contract” types that executors consume, with explicit deterministic ordering/binding metadata so runtime execution never depends on parsing SQL text.

This story focuses on the *contracts and determinism rules*, not yet on compiling full per-resource plans.

These contracts are shared between:
- runtime compilation fallback (cached in-process), and
- mapping pack builders/decoders (AOT mode).

Design references:

- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (`MappingSet` shape + plan usage)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (plan shapes + binding invariants)
- `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (determinism rules for AOT mode)

## Scope (What This Story Is Talking About)

- Owns the stable, “plain data” contract shapes used by:
  - write executors (flatten + write),
  - read executors (keyset hydration),
  - projection executors (descriptor/reference identity projection).
- Owns deterministic ordering and naming rules needed to make compiled SQL and bindings reproducible.
- Does **not** own the full SQL-generation logic for specific plans (owned by later E15 stories).

## Acceptance Criteria

### Contract coverage (executor-facing)

- Plan contract types exist for:
  - write plans:
    - `ResourceWritePlan` with per-table `TableWritePlan`,
    - `TableWritePlan.InsertSql` / `UpdateSql` (root only) / `DeleteByParentSql` (non-root, replace semantics),
    - `ColumnBindings: IReadOnlyList<WriteColumnBinding>` in authoritative parameter/value order,
    - `WriteValueSource` coverage for: `DocumentId`, `ParentKeyPart(i)`, `Ordinal`, `Scalar(...)`, `DocumentReference(...)`, `DescriptorReference(...)`, and `Precomputed`,
    - key-unification inventory (`KeyUnificationWritePlan[]`) sufficient to populate all `Precomputed` bindings deterministically.
  - read/hydration plans:
    - `ResourceReadPlan` with per-table `TableReadPlan`,
    - `TableReadPlan.SelectByKeysetSql` compiled against a keyset table that exposes a single `BIGINT DocumentId` column (temp table / table variable; materialized by the executor).
  - query plans (request-scoped compilation results):
    - `PageDocumentIdSql` and optional `TotalCountSql` plus the parameter set required to execute them.

### Determinism rules (no drift, no SQL parsing)

- All ordering-sensitive collections are explicit and stable:
  - `ColumnBindings` ordering is the authoritative write-time parameter/value ordering.
  - Read-plan select-list ordering is stable and derived from the table model (so readers can consume by ordinal without name-based mapping).
- Parameter naming is deterministic and derived from bindings/model elements (no GUIDs, no unordered-map iteration), with a deterministic de-duplication scheme.
- Bulk insert batching metadata is deterministic and dialect-aware (e.g., SQL Server parameter limits) and is carried in the plan contract so executors do not “guess” batch sizes.

### AOT compatibility

- Contract types are “plain data” (records/structs): no delegates, compiled expressions, DI/service references, or live DB objects, so they can be serialized into (and decoded from) mapping packs.

### Testing

- Unit tests validate determinism under input-order permutations:
  - compile the same contract twice and assert identical outputs,
  - permute input ordering and assert identical outputs,
  - validate parameter-name de-duplication is stable.

## Tasks

1. Implement the plan contract types in a shared assembly reachable by both runtime and pack builders/decoders.
2. Define and implement deterministic naming utilities:
   - parameter naming conventions (binding-derived base names + stable de-duplication),
   - deterministic alias naming helper(s) used by plan compilers.
3. Define a write-plan batching contract (e.g., per-table `MaxRowsPerBatch`) derived from dialect rules/limits and stored on the plan so executors can batch safely.
4. Add unit tests that validate determinism under input-order permutations and duplicate-name scenarios.
