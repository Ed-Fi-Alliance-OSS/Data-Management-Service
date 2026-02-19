---
jira: TBD
jira_url: TBD
---

# Story: Plan SQL Foundations (Shared Canonical Writer + Dialect Helpers)

## Description

Establish a single canonicalized SQL emission foundation for *plan* SQL (not DDL) that is shared across all plan compilers, and is aligned with the DDL writer/canonicalization rules.

This story is the first thin-slice prerequisite for plan compilation:

- keep plan SQL byte-for-byte stable for a fixed selection key,
- prevent drift between DDL SQL and plan SQL formatting/quoting/casing rules, and
- provide a shared building block for later plan compilers (write/read/projection).

Design references:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (SQL canonicalization rules + plan shapes)
- `reference/design/backend-redesign/epics/02-ddl-emission/00-dialect-abstraction.md` (shared dialect + writer)

## Acceptance Criteria

- A shared plan-SQL emission helper exists (writer/formatter + quoting helpers) that enforces:
  - `\n` line endings only,
  - stable indentation,
  - no trailing whitespace.
- Plan compilers do not hand-roll formatting/quoting rules that could drift from the shared writer.
- `PageDocumentIdSqlCompiler` output is canonicalized and stable for both dialects.
- Unit tests validate plan SQL output stability and parameter-name validation behavior.

## Tasks

1. Define (or reuse) a shared SQL writer/formatter for plan SQL, reusing the dialect abstraction.
2. Refactor `PageDocumentIdSqlCompiler` to use the shared writer and dialect quoting helpers.
3. Add unit tests validating:
   - deterministic output (pgsql + mssql),
   - canonicalization rules (`\n`, indentation, no trailing whitespace),
   - parameter name validation failures.

