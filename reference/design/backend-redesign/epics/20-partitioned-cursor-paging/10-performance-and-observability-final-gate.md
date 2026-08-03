---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S10: Performance and Observability Final Gate

## Outcome

Use the E20-S09 harness and baseline to produce bounded telemetry, cross-provider execution plans,
and final evidence that cursor latency is depth-insensitive without regressing traditional paging.

## Design References

- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](EPIC.md#risks-and-guardrails)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- Hard dependencies: completed E20-S02 through E20-S08 behavior plus E20-S09 harness and baseline
  artifacts.
- Existing E12 benchmark planning and E13 parity/E2E infrastructure remain reusable inputs.

## Implementation Scope

- Provision the epic's smoke, million-row, authorized, filtered, sparse-id, and descriptor data
  sets and execute the complete cursor/partition/final-comparison matrix on the pinned providers.
- Re-run the three traditional offset scenarios with the E20-S09 definitions for comparable
  post-change evidence.
- Capture PostgreSQL and SQL Server plan and I/O evidence using the mechanisms specified by the
  epic.
- Add bounded telemetry for mode, sizes/counts, duration, provider, command category, and outcome.
- Produce a final report evaluating every threshold and explaining any variance or proposed index.

## Acceptance Evidence and Test Expectations

- The final report includes p50/p95, command counts, reads/buffers, CPU/time, plans, returned
  rows/tokens, and pass/fail evaluation against every authoritative epic threshold.
- Cursor plans contain no offset/count work, use range access where expected, and add no command;
  partition plans perform one candidate pass and return ids only.
- Traditional offset results are directly compared with the identified E20-S09 baseline artifacts.
- Telemetry tests prove token text, decoded bounds, filter names/values, identities, and candidate
  ids are never recorded.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL and real SQL Server use pinned versions, equivalent fixtures, and separately retained
  plan evidence; results are compared but provider-specific behavior is not hidden.
- Include unfiltered, selectively filtered, representative authorized, and descriptor namespace
  scenarios from the epic's final matrix.

## Explicit Exclusions / Not Assigned

- Functional implementation belongs to E20-S00 through E20-S08, and harness/baseline ownership
  belongs to E20-S09.
- Production capacity sizing, dashboards, paid APM, and generalized load-test expansion are not
  assigned.
- DDL or indexes are not delivered unless a separately reviewed provider plan demonstrates the
  need and scope is explicitly authorized.
