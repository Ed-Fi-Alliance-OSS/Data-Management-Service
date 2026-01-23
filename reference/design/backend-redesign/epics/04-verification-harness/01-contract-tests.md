---
jira: DMS-958
jira_url: https://edfi.atlassian.net/browse/DMS-958
---

# Story: Unit/Contract Tests (Determinism + Fail-Fast Rules)

## Description

Add fast, DB-free unit/contract tests that lock down the determinism and fail-fast rules for:

- `EffectiveSchemaHash` calculation
- `dms.ResourceKey` seed derivation and seed hash
- naming rules + overrides grammar/validation
- type mapping defaults and required metadata (e.g., missing `maxLength` is an error)

## Acceptance Criteria

- Tests run without docker/database dependencies.
- Tests cover both positive and negative paths, including:
  - unknown `nameOverrides` keys → fail fast,
  - collisions after overrides/truncation → fail fast,
  - missing string `maxLength` → fail fast,
  - missing decimal precision metadata → fail fast.
- Tests validate that OpenAPI sections are excluded from hashing/model derivation.

## Tasks

1. Add focused unit tests for hash/seed derivation determinism and invariants.
2. Add naming/override grammar tests (valid/invalid cases).
3. Add type-mapping contract tests for required metadata and dialect mapping outputs.
4. Ensure tests follow the repo’s NUnit conventions (Given_* fixtures; It_* tests).
