---
jira: DMS-1449
jira_url: https://edfi.atlassian.net/browse/DMS-1449
epic: DMS-1402
---

# Story: Implement Natural-Key SQL Builders and Cardinality Contracts

## Outcome

Provide provider-specific, composable natural-key lookup statements and strict ordinal/cardinality
contracts behind unused internal seams before introducing the new resolver.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1445 — natural-key probe metadata](03-natural-key-probe-metadata.md).
- Depends on [DMS-1448 — descriptor validation, index, and FK foundations](06-descriptor-validation-index-and-fk-foundations.md).
- Blocks DMS-1450.

## Implementation Scope

- Add PostgreSQL typed-`unnest` group builders.
- Add SQL Server OPENJSON + `FORCE ORDER` group builders.
- Add the union-projection single-statement form, parameter-budget guard, and ordinal result reader.
- Keep all new builders behind unused internal seams; do not compose them into production resolution.
- Enforce at most one match per input ordinal. Treat multiple matches as invariant corruption rather
  than hiding them with `TOP 1`, `LIMIT 1`, or row selection.

## Acceptance Criteria

- SQL-shape tests pin batch-size-independent PostgreSQL text and, for every descriptor fold, the
  `lower(… COLLATE "pg_c_utf8")` expression on both the column and the parameter side (never an
  unqualified `lower()`).
- SQL Server tests pin the leftmost OPENJSON input, explicit DMS identity collation on every textual
  key operand and inside each descriptor fold, and one statement-level `FORCE ORDER`.
- Abstract lookups project concrete `ResourceKeyId`.
- Tests cover parameter-limit failure and zero-, one-, and multiple-match results.
- The lookup statement is composable without changing production runtime composition.
