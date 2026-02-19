---
jira: DMS-1028
jira_url: https://edfi.atlassian.net/browse/DMS-1028
---

# Story: Thin Slice — Runtime Plan Compilation + Caching (Root-Only)

## Description

Deliver the first end-to-end usable “thin slice” of the plan compilation layer by compiling and caching a minimal mapping set for *root-only* resources (single-table resources with no child/extension tables).

This creates a usable runtime compilation fallback that unblocks runtime mapping-set selection and enables early integration/testing without waiting for full child-table and projection plan coverage.

Design references:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (plan compilation + caching)
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (how compiled plans are used at runtime)
- `reference/design/backend-redesign/epics/06-runtime-mapping-selection/02-mapping-set-selection.md` (consumer of runtime-compiled mapping sets)

## Acceptance Criteria

- For a fixed `(EffectiveSchemaHash, dialect, relational mapping version)`, compiled plan SQL strings are byte-for-byte stable:
  - `\n` line endings only,
  - stable indentation and keyword casing per dialect,
  - stable alias naming,
  - stable parameter naming derived from bindings.
- The thin-slice compiler can produce, for a root-only resource:
  - a root-table write plan (`InsertSql`, optional `UpdateSql`, ordered `ColumnBindings`),
  - a root-table hydration read plan (`SelectByKeysetSql`),
  - a root-table page keyset query plan (`PageDocumentIdSql`, `TotalCountSql`).
- Runtime compilation is cached and concurrency-safe:
  - mapping-set compilation for a selection key occurs at most once per process (concurrent requests share the same in-flight compile),
  - plan lookups do not recompile once cached.
- Unit tests validate:
  - canonicalization stability,
  - parameter naming determinism,
  - plan/model reference integrity,
  - cache compile-once behavior under concurrency.

## Tasks

1. Implement a root-only plan compiler:
   - build minimal write/read/query plans for a single-table resource model.
2. Implement a runtime plan provider/cache keyed by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`:
   - compile on first use,
   - store compiled plans and deterministic binding metadata,
   - concurrency-safe (single in-flight compile per key).
3. Add unit tests that:
   - compile the same root-only resource twice and assert identical SQL text,
   - permute model input ordering and assert identical output,
   - validate plan references exist in the embedded model,
   - assert compile-once behavior under concurrency.

