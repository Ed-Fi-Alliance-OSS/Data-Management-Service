# Story: Compile and Canonicalize SQL Plans for Mapping Packs

## Description

Compile dialect-specific SQL plans into canonicalized SQL strings and deterministic binding metadata suitable for embedding in mapping packs, per:

- `reference/design/backend-redesign/flattening-reconstitution.md` (plan shapes + SQL canonicalization)
- `reference/design/backend-redesign/aot-compilation.md` (what gets precomputed)
- `reference/design/backend-redesign/mpack-format-v1.md` (determinism rules)

This story focuses on compiling the SQL and plan metadata that the pack payload carries (write/read/projection plans), not DDL emission.

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
- Unit tests validate:
  - canonicalization stability,
  - parameter naming determinism,
  - plan/model reference integrity (plans only reference embedded model elements).

## Tasks

1. Implement (or reuse) a plan compiler that produces per-resource:
   - write plans (insert/update/delete-by-parent),
   - read/reconstitution hydration plans,
   - identity projection plans (incl. abstract targets),
   - descriptor expansion plans as required.
2. Centralize SQL canonicalization in the shared dialect SQL writer so DDL and plan SQL cannot drift.
3. Add unit tests that:
   1. compile plans twice and assert identical SQL text,
   2. permute input ordering and assert identical output,
   3. validate plan references exist in the model.

