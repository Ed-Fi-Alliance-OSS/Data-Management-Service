---
jira: DMS-1391
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# Story: Performance Harness and Traditional Baseline

## Outcome

Provide a reproducible cross-provider performance harness and capture a narrow traditional-paging
baseline from an identified commit that predates shared page-selection compiler and collection
execution changes, without sequencing those implementation stories behind harness delivery.

## Design References

- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](../../design-docs/partitioned-cursor-paging.md#risks-and-guardrails)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- No hard dependency on another story in this epic. DMS-1391 and DMS-1385 may proceed independently;
  the baseline records an identified pre-change commit regardless of delivery order.
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
- Baseline artifacts identify the pre-change commit and pinned environment used for comparison with
  the DMS-1385 compiler and DMS-1386 collection hydration-batch changes.
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
