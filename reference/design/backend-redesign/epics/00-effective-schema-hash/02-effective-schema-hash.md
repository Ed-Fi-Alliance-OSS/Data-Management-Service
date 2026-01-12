# Story: Compute `EffectiveSchemaHash`

## Description

Implement the deterministic `EffectiveSchemaHash` algorithm defined in `reference/design/backend-redesign/data-model.md`, including:

- OpenAPI payload exclusion
- per-project canonical JSON hashing
- effective schema manifest string hashing
- inclusion of a DMS-controlled `RelationalMappingVersion`

The output is a lowercase hex SHA-256 hash (64 chars).

## Acceptance Criteria

- `EffectiveSchemaHash` output is lowercase hex and 64 characters.
- Hash is stable across:
  - file ordering differences,
  - JSON property ordering differences,
  - whitespace/line ending differences.
- Hash changes when:
  - any non-OpenAPI schema content changes, or
  - `RelationalMappingVersion` changes.
- Hash does **not** change when only excluded OpenAPI payload sections change.
- Fixture-based tests lock expected hash outputs for at least one small fixture schema set.

## Tasks

1. Implement `EffectiveSchemaHashCalculator` following the “Algorithm (suggested)” section in `reference/design/backend-redesign/data-model.md`.
2. Centralize the `RelationalMappingVersion` constant and ensure it is included in the hashed manifest.
3. Add unit tests validating:
   1. stability across ordering/formatting,
   2. OpenAPI exclusion behavior,
   3. `RelationalMappingVersion` participation.
4. Add at least one checked-in small fixture with a known expected `EffectiveSchemaHash`.

