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

## Scope (What This Story Is Talking About)

- Owns the first end-to-end runtime compilation fallback that returns a usable `MappingSet` for a selection key.
- “Root-only” means the resource’s `RelationalResourceModel.TablesInDependencyOrder` contains exactly one table (the root), with no child/collection tables and no `_ext` tables for that resource.
- Descriptor resources stored in `dms.Descriptor` are out of scope for this story (handled by the descriptor read/write stories).

## Acceptance Criteria

- For a fixed `(EffectiveSchemaHash, dialect, relational mapping version)`, compiled plan SQL strings are byte-for-byte stable:
  - `\n` line endings only,
  - stable indentation and keyword casing per dialect,
  - stable alias naming,
  - stable parameter naming derived from bindings.
- The thin-slice compiler can produce, for a root-only resource:
  - a root-table write plan (`InsertSql`, optional `UpdateSql`, ordered `ColumnBindings`),
  - a root-table hydration read plan (`SelectByKeysetSql`),
  - and the minimal request-scoped page keyset query compilation output needed to drive query reconstitution (`PageDocumentIdSql`, optional `TotalCountSql`).
- Runtime compilation is cached and concurrency-safe:
  - mapping-set compilation for a selection key occurs at most once per process (concurrent requests share the same in-flight compile),
  - plan lookups do not recompile once cached.
- For non-root-only resources, behavior is deterministic:
  - either plans are omitted and lookups fail fast with an actionable error message, or
  - the compiler rejects the mapping set at compile time with an actionable error message.
- Unit tests validate:
  - canonicalization stability,
  - parameter naming determinism,
  - plan/model reference integrity,
  - cache compile-once behavior under concurrency.

## Tasks

1. Implement a root-only plan compiler that builds minimal write/read plans for a single-table `RelationalResourceModel`.
2. Implement a thin-slice `MappingSet` compiler that:
   - iterates derived resources deterministically,
   - compiles plans for root-only relational-table resources,
   - applies all determinism and canonicalization rules from `01-plan-sql-foundations.md` and `02-plan-contracts-and-deterministic-bindings.md`.
3. Implement a runtime mapping-set cache keyed by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`:
   - compile on first use,
   - store compiled plans and deterministic binding metadata,
   - concurrency-safe (single in-flight compile per key).
4. Add unit tests that:
   - compile the same root-only resource twice and assert identical SQL text,
   - permute model input ordering and assert identical output,
   - validate plan references exist in the embedded model,
   - assert compile-once behavior under concurrency,
   - assert deterministic failure behavior for a non-root-only resource.
