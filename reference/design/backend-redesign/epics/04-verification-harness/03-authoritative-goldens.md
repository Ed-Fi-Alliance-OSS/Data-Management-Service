---
jira: DMS-960
jira_url: https://edfi.atlassian.net/browse/DMS-960
---

# Story: Authoritative Golden Directory Comparisons (No DB)

## Description

Add authoritative fixtures representing real schema sets (e.g., DS core, DS + TPDM) and compare `expected/` vs `actual/` via directory diff, per `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`.

## Acceptance Criteria

- Authoritative tests run without databases and compare directories:
  - `expected/` vs `actual/`
- Tests fail with a readable diff output (`git diff --no-index` recommended).
- “Bless/update goldens” mode exists and is disabled in CI (env var controlled).

> **Note:** Authoritative tests run in the standard CI pipeline alongside other unit tests.
> A separate CI category/filter was originally planned but dropped as unnecessary — the
> authoritative tests are fast enough to run on every PR.

## Tasks

1. Add one authoritative fixture (DS core) following the standard layout.
2. Implement directory-diff comparison logic and error output.
3. Implement `UPDATE_GOLDENS=1` (or similar) mode to copy `actual/` to `expected/`.
