---
jira: DMS-936
jira_url: https://edfi.atlassian.net/browse/DMS-936
---

# Story: SQL Dialect Abstraction + Writer

## Description

Introduce a shared dialect abstraction used by both:

- DDL generation (`reference/design/backend-redesign/design-docs/ddl-generation.md`), and
- compiled SQL plan emission (future; `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`)

Implementation note: split “dialect” into two layers so E01 and E02 cannot drift:

- **Shared dialect rules** (reusable component, used by both E01 derivation and E02 emission):
  - identifier length limits + shortening (truncate + hash),
  - scalar type mapping defaults.
- **SQL emission dialect + writer** (E02/E15 concern, composes over the shared rules):
  - identifier quoting rules (always-quote),
  - idempotent DDL patterns (`IF NOT EXISTS`, catalog checks, `CREATE OR ALTER`, etc.),
  - stable SQL formatting/canonicalization rules.

## Acceptance Criteria

- Dialect implementations exist for:
  - PostgreSQL
  - SQL Server
- Writer always quotes identifiers:
  - PostgreSQL: `"Name"`
  - SQL Server: `[Name]`
- Type mappings match `reference/design/backend-redesign/design-docs/data-model.md` defaults.
- Dialect supports the required programmable-object patterns:
  - PostgreSQL: `CREATE OR REPLACE FUNCTION`, `DROP TRIGGER IF EXISTS ...`, `CREATE TRIGGER ...`
  - SQL Server: `CREATE OR ALTER VIEW/TRIGGER` where applicable.
- SQL text output is stable for the same model (canonicalization rules applied).

## Tasks

1. Define a shared dialect-rules abstraction (e.g., `ISqlDialectRules`) capturing:
   - identifier length limits + shortening (truncate + hash),
   - scalar type mapping defaults.
2. Define an emission-focused dialect abstraction (e.g., `ISqlDialect`) that composes over the shared rules and captures:
   - quoting,
   - DDL capability differences and existence-check templates.
3. Implement PostgreSQL and SQL Server variants for both layers:
   - `PgsqlDialectRules` / `MssqlDialectRules`,
   - `PgsqlDialect` / `MssqlDialect`.
4. Implement a shared SQL writer/formatter that enforces canonicalization (`\n`, indentation, keyword casing).
5. Add unit tests for quoting, shortening/type defaults, and deterministic output for a small sample model.
