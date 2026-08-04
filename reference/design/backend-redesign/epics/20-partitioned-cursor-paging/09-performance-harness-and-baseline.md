---
jira: DMS-1391
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# Story: Performance Harness and Traditional Baseline

## Outcome

Provide a reproducible cross-provider performance harness and capture the narrow pre-change
traditional-paging baseline required before DMS-1385 modifies the shared page-selection compiler and
before its downstream story mutates shared traditional collection execution.

## Design References

- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](../../design-docs/partitioned-cursor-paging.md#risks-and-guardrails)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- No hard dependency on another story in this epic; this story must complete before DMS-1385.
  DMS-1383 and DMS-1384 may proceed in parallel, and this story should be started alongside them so
  the gate never idles DMS-1385.
- The baseline is regression insurance over the shared page-selection compiler that DMS-1385
  modifies. DMS-1385 keeps traditional page-selection output behaviorally and textually unchanged,
  so the baseline is the evidence that traditional SQL and latency did not move, not a record of an
  expected change. DMS-1386's selected-id result set in the collection hydration batch is the first
  change that does alter shared traditional runtime execution.
- Existing E12 benchmark planning and E13 parity/E2E infrastructure are reusable inputs, not
  substitutes for the evidence this epic requires.

## Implementation Scope

- Add or explicitly integrate and pin a repeatable PostgreSQL/SQL Server benchmark runner,
  configuration, fixture loader, run manifest, and stable JSON/CSV result format.
- Capture only the three traditional offset scenarios used by the epic's comparison gates:
  offset 0, a one-page shallow offset, and a recorded deep offset, for page sizes 25 and 500.
- Use the epic's single primary fixture, the same one DMS-1392 reuses, so baseline and final-gate
  numbers are directly comparable. Do not provision the authorized, filtered, or descriptor variants
  here.
- Record commit/environment identity, p50/p95, command counts, returned rows, reads or buffers,
  database CPU/time, and PostgreSQL and SQL Server plans.
- Retain machine-readable baseline artifacts for direct comparison by DMS-1392.

## Acceptance Evidence and Test Expectations

- A clean environment can reproduce the same three scenario definitions and machine-readable
  outputs for both providers.
- Baseline artifacts identify the commit and pinned environment and exist before DMS-1385
  page-selection compiler work begins and, transitively, before DMS-1386 changes the shared
  collection hydration batch.
- Each scenario records page size, offset, p50/p95, command count, returned rows, reads/buffers,
  CPU/time, and the provider plan in the epic's result format.
- Harness smoke tests detect invalid configuration, fixture, provider, and incomplete result data.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL and real SQL Server use pinned versions and equivalent traditional-paging fixtures.
- This pre-change baseline does not add new authorization scenarios; representative authorized,
  filtered, descriptor, cursor, and partition measurements belong to DMS-1392.

## Explicit Exclusions / Not Assigned

- Cursor and partition measurements, the authorized/filtered/descriptor variants, final threshold
  evaluation, and index recommendations belong to DMS-1392. Bounded runtime telemetry belongs to
  DMS-1393.
- Functional implementation belongs to DMS-1383 through DMS-1390.
- Production capacity sizing, dashboards, paid APM, and generalized load-test expansion are not
  assigned.
