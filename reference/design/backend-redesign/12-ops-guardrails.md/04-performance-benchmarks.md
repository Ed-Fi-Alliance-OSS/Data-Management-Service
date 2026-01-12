# Story: Benchmark Harness for Read/Write Hot Paths

## Description

Create a repeatable benchmark harness to measure:

- write throughput/latency for representative resources (including nested collections),
- read latency for GET-by-id and query paging,
- and lock/closure contention under concurrent workloads.

This supports early detection of performance regressions and validates the design’s assumptions.

## Acceptance Criteria

- A runnable benchmark workflow exists (script + configuration) that can target pgsql and mssql.
- Benchmarks capture:
  - throughput,
  - average/99p latency,
  - lock wait time metrics,
  - and DB round-trip counts.
- Results are reproducible enough to compare across changes (same fixture data + same environment).

## Tasks

1. Define benchmark scenarios and representative fixtures/data sizes.
2. Implement a benchmark runner (script-first) that provisions a DB, loads data, and runs timed operations.
3. Capture and report metrics in a stable format (json/csv).
4. Add documentation for running benchmarks locally and interpreting results.

