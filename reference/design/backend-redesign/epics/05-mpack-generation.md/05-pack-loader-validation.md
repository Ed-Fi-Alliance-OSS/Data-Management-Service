# Story: Pack Loader/Validator + Mapping Set Selection

## Description

Implement the consumer-side logic that:

- selects a mapping pack by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion, PackFormatVersion)`,
- validates and decodes the pack per `reference/design/backend-redesign/mpack-format-v1.md`,
- builds the runtime mapping set from the payload,
- and validates DB compatibility via the `dms.ResourceKey` seed gate (fast path via `dms.EffectiveSchema`, slow path diff).

This is shared infrastructure used by:
- DMS runtime (optional AOT mode), and
- verification harness integration tests.

## Acceptance Criteria

- Consumer validation implements the algorithm in `mpack-format-v1.md`:
  - header fields match expected,
  - bounded zstd decompression,
  - `payload_sha256` verified,
  - payload invariants verified.
- Pack selection and caching:
  - file-store lookup is deterministic,
  - packs are loaded lazily by effective hash (not all at startup),
  - loaded mapping sets are cached by selection key.
- DB compatibility validation:
  - compares `ResourceKeySeedHash/Count` fast path when present,
  - falls back to ordered `dms.ResourceKey` diff on mismatch for diagnostics,
  - fails fast on mismatch.

## Tasks

1. Implement a `MappingPackStore` (file-based) that discovers candidate packs by directory scan and indexes them by key.
2. Implement pack validation + decode + `MappingSet.FromPayload(...)`.
3. Implement DB validation against `dms.EffectiveSchema` and `dms.ResourceKey`.
4. Add unit tests for invalid pack cases:
   1. wrong dialect/version/hash,
   2. wrong SHA-256,
   3. overlarge payload length,
   4. payload invariant violations.
5. Add integration tests that:
   1. provision DB,
   2. load pack and validate seed gate,
   3. tamper DB seed data and assert failure with diff diagnostics.

