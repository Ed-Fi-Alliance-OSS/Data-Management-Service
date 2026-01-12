# Story: Runtime Compatibility Gate Tests (Mapping Set ↔ DB Validation)

## Description

Implement and test the runtime compatibility gate described in `reference/design/backend-redesign/ddl-generator-testing.md`:

- DB records `EffectiveSchemaHash` and `ResourceKeySeedHash/Count`.
- Runtime (or tooling) validates that:
  - a matching mapping set is selected (runtime-compiled or pack-based), and
  - DB `dms.ResourceKey` matches the mapping set (fast path via `EffectiveSchema`, slow path diff for diagnostics).

This story focuses on the validation algorithm and its tests (even if mapping packs are not yet implemented, runtime compilation can be used as the mapping set source).

## Acceptance Criteria

- Validation logic reads DB fingerprint (`dms.EffectiveSchema`) and fails fast when:
  - `EffectiveSchemaHash` mismatches expected,
  - `ResourceKeySeedHash/Count` mismatches expected.
- On mismatch, a slow-path diff can be produced for `dms.ResourceKey` to aid diagnostics.
- Integration test exists:
  1. provision DB with fixture DDL,
  2. validate mapping set vs DB (passes),
  3. tamper `dms.ResourceKey` and validate (fails with useful report).

## Tasks

1. Implement a reusable validator component that compares a mapping set’s seed summary to the DB’s recorded values.
2. Implement optional slow-path diff reading `dms.ResourceKey` ordered by `ResourceKeyId`.
3. Add integration tests that run after DB-apply provisioning and exercise pass/fail paths.
4. Document how this validator will be reused by the DMS runtime on first use of a connection string.

