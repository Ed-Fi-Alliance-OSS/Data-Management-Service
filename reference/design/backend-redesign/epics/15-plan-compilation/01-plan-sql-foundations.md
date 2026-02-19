---
jira: DMS-1043
jira_url: https://edfi.atlassian.net/browse/DMS-1043
---

# Story: Plan SQL Foundations (Shared Canonical Writer + Dialect Helpers)

## Description

Establish a single canonicalized SQL emission foundation for *plan* SQL (not DDL) that is shared across:

- plan compilation (`TableWritePlan` / `TableReadPlan` SQL), and
- request-scoped query SQL (e.g., “page keyset” queries).

This story is the first thin-slice prerequisite for plan compilation and reproducible mapping packs:

- keep plan SQL byte-for-byte stable for a fixed selection key,
- prevent drift between DDL SQL and plan SQL formatting/quoting/casing rules, and
- provide a shared building block for later plan compilers (write/read/projection).

Design references:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (SQL canonicalization rules + plan shapes)
- `reference/design/backend-redesign/design-docs/ddl-generation.md` (canonicalization + ordering rules shared with packs)
- `reference/design/backend-redesign/epics/02-ddl-emission/00-dialect-abstraction.md` (shared dialect rules + writer)

## Scope (What This Story Is Talking About)

- Owns the *writer/formatter* + dialect helpers used to emit SQL strings into compiled plans.
- Does **not** define per-plan alias naming or parameter naming conventions (owned by `02-plan-contracts-and-deterministic-bindings.md`), but must provide the primitives those conventions use.
- Does **not** implement full per-resource plan compilation (owned by later E15 stories).

## Acceptance Criteria

### Canonical SQL output

- A shared plan-SQL emission helper exists (writer/formatter + quoting helpers) that enforces:
  - `\n` line endings only,
  - stable indentation,
  - no trailing whitespace,
  - stable keyword casing per dialect.
- Identifiers are always quoted using the shared dialect abstraction:
  - PostgreSQL: `"Name"`
  - SQL Server: `[Name]`

### Adoption

- Plan compilers and query compilers do not hand-roll formatting/quoting rules that could drift from the shared writer.
- `PageDocumentIdSqlCompiler` output is canonicalized and stable for both dialects.

### Testing

- Unit tests validate deterministic output stability (pgsql + mssql) and canonicalization rules:
  - `\n` line endings,
  - indentation and keyword casing,
  - no trailing whitespace.
- If the query compiler validates parameter identifiers, unit tests cover deterministic failure behavior for invalid parameter names.
- Snapshot/golden tests are practical and cover at least:
  - one representative plan SQL emission (pgsql + mssql), and
  - one representative query SQL emission (`PageDocumentIdSqlCompiler`),
  comparing exact normalized SQL (or stable hashes of normalized SQL) per `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`.

## Tasks

1. Extend (or reuse) the shared SQL writer from `epics/02-ddl-emission/00-dialect-abstraction.md` so it can emit plan SQL:
   - common `SELECT`/`INSERT`/`UPDATE`/`DELETE` statement building blocks,
   - identifier quoting + parameter prefixing via dialect,
   - stable formatting/canonicalization rules.
2. Add a small SQL canonicalization helper intended for unit tests (and pack/fixture comparisons) so tests compare normalized SQL (or normalized hashes) rather than ad-hoc “pretty printed” variants.
3. Refactor `PageDocumentIdSqlCompiler` to use the shared writer and dialect quoting helpers.
4. Add unit tests validating deterministic, canonicalized output for both dialects.
5. Add (or extend) a small fixture/snapshot that locks down canonical plan/query SQL output and supports expected-vs-actual golden comparisons.
