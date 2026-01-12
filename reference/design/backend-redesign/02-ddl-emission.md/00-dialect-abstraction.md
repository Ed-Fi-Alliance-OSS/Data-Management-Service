# Story: SQL Dialect Abstraction + Writer

## Description

Introduce a shared dialect abstraction used by both:

- DDL generation (`reference/design/backend-redesign/ddl-generation.md`), and
- compiled SQL plan emission (future; `reference/design/backend-redesign/flattening-reconstitution.md`)

Responsibilities:
- identifier quoting rules (always-quote)
- identifier length limits + shortening
- scalar type mapping defaults
- idempotent DDL patterns (`IF NOT EXISTS`, catalog checks, `CREATE OR ALTER`, etc.)
- stable SQL formatting/canonicalization rules

## Acceptance Criteria

- Dialect implementations exist for:
  - PostgreSQL
  - SQL Server
- Writer always quotes identifiers:
  - PostgreSQL: `"Name"`
  - SQL Server: `[Name]`
- Type mappings match `reference/design/backend-redesign/data-model.md` defaults.
- Dialect supports the required programmable-object patterns:
  - PostgreSQL: `CREATE OR REPLACE FUNCTION`, `DROP TRIGGER IF EXISTS ...`, `CREATE TRIGGER ...`
  - SQL Server: `CREATE OR ALTER VIEW/TRIGGER` where applicable.
- SQL text output is stable for the same model (canonicalization rules applied).

## Tasks

1. Define `ISqlDialect` (or equivalent) capturing quoting, type mapping, and DDL capability differences.
2. Implement `PgsqlDialect` and `MssqlDialect` with:
   - quoting,
   - identifier shortening rules,
   - type mapping helpers,
   - existence-check templates.
3. Implement a shared SQL writer/formatter that enforces canonicalization (`\n`, indentation, keyword casing).
4. Add unit tests for quoting, type mapping, and deterministic output for a small sample model.

