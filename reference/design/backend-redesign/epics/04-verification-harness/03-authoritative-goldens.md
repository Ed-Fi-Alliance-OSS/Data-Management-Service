# Story: Authoritative Golden Directory Comparisons (No DB)

## Description

Add authoritative fixtures representing real schema sets (e.g., DS core, DS + TPDM) and compare `expected/` vs `actual/` via directory diff, per `reference/design/backend-redesign/ddl-generator-testing.md`.

## Acceptance Criteria

- Authoritative tests run without databases and compare directories:
  - `expected/` vs `actual/`
- Tests fail with a readable diff output (`git diff --no-index` recommended).
- “Bless/update goldens” mode exists and is disabled in CI (env var controlled).
- Authoritative tests are in a separate test category (e.g., `Authoritative`) so default CI can exclude them.

## Tasks

1. Add one authoritative fixture (DS core) following the standard layout.
2. Implement directory-diff comparison logic and error output.
3. Implement `UPDATE_GOLDENS=1` (or similar) mode to copy `actual/` to `expected/`.
4. Wire tests into CI filters so default runs skip `Authoritative` by default.

