---
jira: DMS-977
jira_url: https://edfi.atlassian.net/browse/DMS-977
---

# Story: Select Mapping Set by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`

## Description

Implement mapping set selection based on the database’s `EffectiveSchemaHash`:

- Determine the runtime dialect (PGSQL vs MSSQL).
- Select a matching mapping set using:
  - `.mpack` loading when enabled (see `reference/design/backend-redesign/design-docs/aot-compilation.md` and `reference/design/backend-redesign/design-docs/mpack-format-v1.md`), or
  - runtime compilation fallback when allowed.
- Cache mapping sets by selection key to avoid repeat compilation/decoding.

The mapping set returned by this story is the unified `MappingSet` shape described in `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`.

Runtime compilation fallback (when enabled) uses the shared plan compiler + cache owned by `reference/design/backend-redesign/epics/15-plan-compilation/EPIC.md`.

## Acceptance Criteria

- Selection key includes:
  - `EffectiveSchemaHash`
  - dialect
  - `RelationalMappingVersion`
  - and validates `PackFormatVersion` when using packs.
- When mapping packs are enabled and required:
  - missing or invalid pack causes requests for that DB to fail fast.
- When runtime compilation fallback is allowed:
  - missing pack triggers runtime compilation for that schema hash.
- Mapping set selection is cached and concurrency-safe (multiple requests for same hash do not compile/decode repeatedly).

## Tasks

1. Define a `MappingSetProvider` abstraction returning a mapping set for a selection key.
2. Implement a pack-backed provider integration (delegating to the pack loader/validator).
3. Implement runtime compilation fallback using the shared derivation + plan compilation pipeline.
4. Add unit tests for:
   1. required-pack behavior,
   2. fallback behavior,
   3. cache concurrency behavior.
