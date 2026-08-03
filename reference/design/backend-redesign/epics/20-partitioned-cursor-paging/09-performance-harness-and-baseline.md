---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S09: Performance Harness and Traditional Baseline

## Outcome

Provide a reproducible cross-provider performance harness and capture the narrow pre-change
traditional-paging baseline required before E20-S03 modifies the shared page-selection compiler and
before its downstream stories mutate shared traditional collection execution.

## Design References

- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](EPIC.md#risks-and-guardrails)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- No hard dependency on another E20 story; this story must complete before E20-S03. E20-S00a,
  E20-S00b, E20-S01, and E20-S02 may proceed in parallel.
- The baseline is regression insurance over the shared page-selection compiler that E20-S03
  modifies. E20-S03 keeps traditional page-selection output behaviorally and textually unchanged, so
  the baseline is the evidence that traditional SQL and latency did not move, not a record of an
  expected change. E20-S04's selected-id result set in the collection hydration batch is the first
  change that does alter shared traditional runtime execution.
- Existing E12 benchmark planning and E13 parity/E2E infrastructure are reusable inputs, not
  substitutes for the required E20 evidence.

## Implementation Scope

- Add or explicitly integrate and pin a repeatable PostgreSQL/SQL Server benchmark runner,
  configuration, fixture loader, run manifest, and stable JSON/CSV result format.
- Capture only the three traditional offset scenarios used by the epic's comparison gates:
  offset 0, a one-page shallow offset, and a recorded deep offset, for page sizes 25 and 500.
- Use a reproducible fixture large enough to exercise the recorded deep offset without requiring
  the final gate's million-row, authorized, filtered, sparse-id, or descriptor data sets.
- Record commit/environment identity, p50/p95, command counts, returned rows, reads or buffers,
  database CPU/time, and PostgreSQL and SQL Server plans.
- Retain machine-readable baseline artifacts for direct comparison by E20-S10.

## Acceptance Evidence and Test Expectations

- A clean environment can reproduce the same three scenario definitions and machine-readable
  outputs for both providers.
- Baseline artifacts identify the commit and pinned environment and exist before E20-S03
  page-selection compiler work begins and, transitively, before E20-S04 changes the shared
  collection hydration batch.
- Each scenario records page size, offset, p50/p95, command count, returned rows, reads/buffers,
  CPU/time, and the provider plan in the epic's result format.
- Harness smoke tests detect invalid configuration, fixture, provider, and incomplete result data.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL and real SQL Server use pinned versions and equivalent traditional-paging fixtures.
- This pre-change baseline does not add new authorization scenarios; representative authorized,
  filtered, descriptor, cursor, and partition measurements belong to E20-S10.

## Explicit Exclusions / Not Assigned

- Cursor and partition measurements, the full large-fixture matrix, final threshold evaluation,
  and index recommendations belong to E20-S10. Bounded runtime telemetry belongs to E20-S12.
- Functional implementation belongs to E20-S00a through E20-S08b.
- Production capacity sizing, dashboards, paid APM, and generalized load-test expansion are not
  assigned.
