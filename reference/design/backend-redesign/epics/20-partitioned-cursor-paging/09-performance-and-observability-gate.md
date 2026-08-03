---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S09: Performance and Observability Gate

## Outcome

Provide a reproducible cross-provider harness, pre-change baselines, bounded telemetry, execution
plans, and final evidence that cursor latency is depth-insensitive without regressing traditional
paging.

## Design References

- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](EPIC.md#risks-and-guardrails)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- Baseline phase: no E20 implementation dependency; it must complete before E20-S03 changes
  planner SQL.
- Final-gate phase: hard dependencies on completed E20-S03 through E20-S08 behavior.
- Existing E12 benchmark planning and E13 parity/E2E infrastructure are reusable inputs, not
  substitutes for the required E20 evidence.

## Implementation Scope

- Add or explicitly integrate and pin a repeatable PostgreSQL/SQL Server benchmark runner,
  configuration, fixture loader, run manifest, and stable JSON/CSV result format.
- Capture traditional paging baselines before planner changes.
- Provision the epic's smoke, million-row, authorized, filtered, sparse-id, and descriptor data
  sets and execute its page/partition matrix.
- Capture PostgreSQL and SQL Server plan and I/O evidence using the mechanisms specified by the
  epic.
- Add bounded telemetry for mode, sizes/counts, duration, provider, command category, and outcome.
- Produce a final report evaluating every threshold and explaining any variance or proposed index.

## Acceptance Evidence and Test Expectations

- A clean environment can reproduce the same scenario definitions and machine-readable outputs
  for both providers.
- Baseline artifacts are identified by commit/environment before E20-S03 planner changes.
- The final report includes p50/p95, command counts, reads/buffers, CPU/time, plans, returned
  rows/tokens, and pass/fail evaluation against every authoritative epic threshold.
- Cursor plans contain no offset/count work, use range access where expected, and add no command;
  partition plans perform one candidate pass and return ids only.
- Telemetry tests prove token text, decoded bounds, filter names/values, identities, and candidate
  ids are never recorded.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL and real SQL Server use pinned versions, equivalent fixtures, and separately retained
  plan evidence; results are compared but provider-specific behavior is not hidden.
- Include unfiltered, selectively filtered, representative authorized, and descriptor namespace
  scenarios from the epic's matrix.

## Explicit Exclusions / Not Assigned

- Functional implementation belongs to E20-S00 through E20-S08.
- Production capacity sizing, dashboards, paid APM, and generalized load-test expansion are not
  assigned.
- DDL or indexes are not delivered unless a separately reviewed provider plan demonstrates the
  need and scope is explicitly authorized.
