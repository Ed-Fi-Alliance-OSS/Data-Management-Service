---
jira: TBD
jira_url: TBD
---

# Story: Plan Contracts + Deterministic Bindings (Parameter Naming, Ordering, Metadata)

## Description

Introduce the runtime “plan contract” types that executors consume, with explicit deterministic ordering/binding metadata so runtime execution never depends on parsing SQL text.

This story focuses on the *contracts and determinism rules*, not yet on compiling full per-resource plans.

Design references:

- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (`MappingSet` shape + plan usage)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (plan shapes + binding invariants)
- `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (determinism rules for AOT mode)

## Acceptance Criteria

- Plan contract types exist for:
  - write plans (`InsertSql`/`UpdateSql`/`DeleteByParentSql` + ordered `ColumnBindings`),
  - read/hydration plans (`SelectByKeysetSql` + table model reference),
  - root-table page keyset query plans (`PageDocumentIdSql`, `TotalCountSql`).
- Contracts explicitly carry deterministic ordering/binding metadata required by executors:
  - stable column binding order,
  - stable select-list order (read plans),
  - stable keyset/table shapes where applicable.
- Parameter naming is deterministic and derived from bindings (no GUIDs/hashes from unordered maps), with a deterministic de-duplication scheme.
- Unit tests validate determinism under input-order permutations.

## Tasks

1. Implement plan contract types in a shared assembly reachable by both runtime and pack builders.
2. Define and implement deterministic parameter naming conventions:
   - binding-derived base names,
   - stable conflict resolution for duplicates.
3. Add unit tests that:
   - compile contracts twice and assert identical outputs,
   - permute input ordering and assert identical outputs.

