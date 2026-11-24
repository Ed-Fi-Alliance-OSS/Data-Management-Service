---
jira: DMS-1019
jira_url: https://edfi.atlassian.net/browse/DMS-1019
---

# Story: Repeatable Read/Write Benchmarks with Baseline Comparison

## Description

Establish the first step toward automated performance regression testing for the Ed-Fi API.
Given two supported DMS commits and a database provider, an engineer runs one unattended command
that compares an explicit, approved reference build with a candidate build. The comparison uses
the existing paging coverage and one representative serial write workload, retains reproducible
evidence, and rejects invalid comparisons.

The story extends the performance harness delivered by DMS-1391 and DMS-1392 (deterministic
fixtures, the real DMS pipeline in-process, provider instrumentation, environment manifests, and
versioned artifacts) and preserves the existing paging qualification workflow. Each provider is
qualified separately on one documented execution platform. The run contract, environment rules,
and qualification procedure are documented in the
[performance harness README](../../../../../src/dms/tests/EdFi.DataManagementService.Performance.Harness/README.md#benchmark-comparison).

## Workload

One request in flight, equivalent on PostgreSQL and SQL Server:

- **Reads:** the six existing traditional paging cells (offset zero, shallow, and deep, at page
  sizes 25 and 500) over the deterministic Student fixture.
- **Writes:** one serial Student update that changes a nested collection, with deterministic
  payloads, target selection, and state reset, and the persisted change verified outside the
  timed interval.

## Acceptance Criteria

- One documented command, parameterized by provider, completes the full workflow on the selected
  platform for both PostgreSQL and SQL Server, qualified separately: build both commits,
  provision isolated databases with each build's own provisioning, load fixtures, warm up,
  measure alternating-order pairs with a fresh process per side, compare, and retain artifacts,
  with no manual steps after the prerequisites.
- Results capture attempted, completed, and failed operation counts, mean and p50/p95 latency,
  sample counts, and raw samples. Warnings use calibrated p50 thresholds; p95 is always reported
  and warns only where calibration supports it; p99 is optional and descriptive. Database
  command counts are retained with provider-specific definitions and are distinguished from
  network round trips. Throughput, if reported, is labelled serial benchmark throughput, not
  capacity.
- Initial p50 thresholds and one rule for combining repeated pair results are configurable,
  documented, and justified by calibration evidence for each provider and scenario. The useful
  slowdown to detect and its demonstration scenarios are declared before qualification.
- At least five same-build pairs per provider show no spurious warnings, and the declared
  test-only slowdown is detected repeatably, above the threshold by a documented margin, through
  the same measurement and comparison path, with near-boundary probes reported. Qualification
  evidence states the demonstrated sensitivity and its limitations, and unsuccessful evidence is
  retained.
- The comparison distinguishes acceptable results, suspected regressions, and invalid or
  inconclusive comparisons. Timing regressions are advisory; setup failures, incorrect
  workloads, failed or missing work, and incompatible environments return a nonzero exit code.
- Automated checks cover workload correctness, artifact validation, comparison calculations,
  incompatible environments, failed operations, and exit-code behavior, without enforcing
  wall-clock thresholds in ordinary CI.
- Documentation covers the platform, environment qualification checks, supported commit range,
  baseline selection, environment changes and recalibration, artifact interpretation, and how to
  reproduce a reported regression.

## Out of Scope

- GET-by-id, insert, no-op update, concurrent and contended relationship/closure workloads, and
  new lock-wait instrumentation.
- Additional execution platforms and broader historical commit or schema compatibility.
- A dedicated benchmark host, scheduled or CI execution through a self-hosted runner, and
  enforced PR or release gates.
- HTTP-level throughput, sustained load, larger datasets, capacity sizing, and dashboards.
