---
jira: DMS-1392
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# Story: Performance Final Gate

## Outcome

Use the DMS-1391 harness and baseline to produce cross-provider execution plans and final evidence
that cursor latency is depth-insensitive without regressing traditional paging.

## Design References

- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](../../design-docs/partitioned-cursor-paging.md#risks-and-guardrails)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- Hard dependencies: completed DMS-1385 through DMS-1390 behavior plus the DMS-1391 harness and
  baseline artifacts.
- Existing E12 benchmark planning and E13 parity/E2E infrastructure remain reusable inputs.

## Implementation Scope

- Provision the epic's narrow fixture set: the smoke set, the single 500,000-row primary fixture
  with sparse ids, its authorized variant read by a second principal, one filtered variant at
  approximately 10% selectivity, and the 25,000-descriptor set. Reuse the primary fixture rather
  than loading separate large data sets, and reuse the DMS-1391 fixture definition so the
  traditional comparison is like-for-like.
- Execute the epic's cursor/partition/final-comparison matrix on the pinned providers: page sizes 25
  and 500 at first/middle/last cursor ranges, partition counts 1/10/200 on the unfiltered primary
  fixture, and `partitionCount=10` on the filtered and authorized variants.
- Re-run the three traditional offset scenarios with the DMS-1391 definitions for comparable
  post-change evidence.
- Capture PostgreSQL and SQL Server plan and I/O evidence using the mechanisms specified by the
  epic.
- Produce a final report evaluating every threshold and explaining any variance or proposed index.

## Acceptance Evidence and Test Expectations

- The final report includes p50/p95, command counts, reads/buffers, CPU/time, plans, returned
  rows/tokens, and pass/fail evaluation against every authoritative epic threshold.
- Cursor plans contain no offset/count work, use range access where expected, and add no command;
  partition plans perform one candidate pass and return ids only.
- Traditional offset results are directly compared with the identified DMS-1391 baseline artifacts.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL and real SQL Server use pinned versions, equivalent fixtures, and separately retained
  plan evidence; results are compared but provider-specific behavior is not hidden.
- Include unfiltered, selectively filtered, representative authorized, and descriptor namespace
  scenarios from the epic's final matrix. Iteration counts are not reduced along with the fixtures:
  each scenario keeps at least five warmups and 30 measured warm-cache iterations, because a
  reported p95 is meaningless without them.

## Explicit Exclusions / Not Assigned

- Functional implementation belongs to DMS-1383 through DMS-1390, and harness/baseline ownership
  belongs to DMS-1391.
- Fixtures beyond the epic's narrow set, including million-row or multi-million-row data sets and a
  second filter selectivity, are out of scope unless a reviewed result shows the narrow set cannot
  discriminate a gate.
- Bounded production telemetry and its privacy tests belong to DMS-1393.
- Production capacity sizing, dashboards, paid APM, and generalized load-test expansion are not
  assigned.
- DDL or indexes are not delivered unless a separately reviewed provider plan demonstrates the
  need and scope is explicitly authorized.
