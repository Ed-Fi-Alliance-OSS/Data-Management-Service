# Story: SQL Canonicalization + Deterministic Ordering for DDL

## Description

Ensure emitted DDL is byte-for-byte stable by enforcing:

- canonical SQL formatting (`\n`, indentation, no trailing whitespace)
- deterministic statement ordering by phase and within-phase rules
- stable identifier and constraint/index naming

This story is the “golden stability” guardrail for fixtures and reproducible builds.

## Acceptance Criteria

- For a fixed fixture input, emitted `{dialect}.sql` output is byte-for-byte stable across runs.
- Script uses `\n` line endings only and contains no trailing whitespace.
- Statement ordering follows the phase order and within-phase ordering rules in `reference/design/backend-redesign/ddl-generation.md`.
- Snapshot tests cover at least one multi-project fixture and detect ordering/format drift.

## Tasks

1. Implement DDL phase ordering and stable within-phase ordering as a first-class emission pipeline.
2. Implement SQL formatting rules in the shared writer (avoid “pretty print” drift).
3. Add snapshot tests asserting exact text output for representative fixtures (pgsql + mssql).
4. Add a regression test that intentionally permutes input ordering and asserts identical output.

