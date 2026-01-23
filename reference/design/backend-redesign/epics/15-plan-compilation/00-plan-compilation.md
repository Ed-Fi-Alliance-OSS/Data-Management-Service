---
jira: DMS-1028
jira_url: https://edfi.atlassian.net/browse/DMS-1028
---

# Story: Runtime Plan Compilation + Caching (Shared with Mapping Packs)

## Description

Compile dialect-specific SQL plans into canonicalized SQL strings and deterministic binding metadata suitable for:

- runtime execution (cached by mapping-set selection key), and
- optional embedding in mapping packs (AOT mode),

per:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (plan shapes + SQL canonicalization)
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (`MappingSet` shape; plans populate the mapping set)
- `reference/design/backend-redesign/design-docs/aot-compilation.md` (what gets precomputed)
- `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (determinism rules)

This story focuses on compiling the SQL and plan metadata that executors consume (write/read/projection plans), not DDL emission.

## Acceptance Criteria

- For a fixed `(EffectiveSchemaHash, dialect, relational mapping version)`, compiled plan SQL strings are byte-for-byte stable:
  - `\n` line endings only,
  - stable indentation and keyword casing per dialect,
  - stable alias naming,
  - stable parameter naming derived from bindings (no GUIDs/hashes from unordered maps).
- Plan metadata includes deterministic binding/ordering information required by executors:
  - column order,
  - keyset shapes,
  - batching/parameterization limits where applicable.
- Runtime compilation is cached and concurrency-safe:
  - mapping-set compilation for a selection key occurs at most once per process (concurrent requests share the same in-flight compile),
  - per-resource plan lookups do not recompile SQL once cached.
- Unit tests validate:
  - canonicalization stability,
  - parameter naming determinism,
  - plan/model reference integrity (plans only reference embedded model elements).
  - cache concurrency behavior.

## Tasks

1. Implement (or reuse) a plan compiler that produces per-resource:
   - write plans (insert/update/delete-by-parent),
   - read/reconstitution hydration plans,
   - identity projection plans (incl. abstract targets),
   - descriptor expansion plans as required.
2. Centralize SQL canonicalization in the shared dialect SQL writer so DDL and plan SQL cannot drift.
3. Implement a runtime cache/provider keyed by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)` that:
   - compiles plans on first use (or on-demand per resource under a mapping set),
   - stores compiled plans and deterministic binding metadata,
   - is concurrency-safe.
4. Add unit tests that:
   1. compile plans twice and assert identical SQL text,
   2. permute input ordering and assert identical output,
   3. validate plan references exist in the model.
   4. assert compile-once behavior under concurrency.
