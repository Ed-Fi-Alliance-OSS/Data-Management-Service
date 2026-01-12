# Story: Instrumentation for Locks, Closures, and Edge Maintenance

## Description

Add instrumentation for correctness-critical and performance-sensitive operations:

- identity lock acquisition times and counts,
- identity closure size/iterations/duration,
- reference edge diff sizes and write counts,
- deadlock/serialization retries and failures.

Align with the instrumentation suggestions in `reference/design/backend-redesign/strengths-risks.md`.

## Acceptance Criteria

- Metrics/logs exist for:
  - `IdentityLockSharedAcquisitionMs`, `IdentityLockUpdateAcquisitionMs`, `IdentityLockRowsLocked`
  - `IdentityClosureSize`, `IdentityClosureIterations`, `IdentityClosureMs`
  - edge maintenance row counts and diff sizes
  - deadlock/serialization retries + exhausted retries
- Instrumentation does not require schema changes beyond what the redesign already specifies.

## Tasks

1. Define metric names and dimensions (resource type, dialect, instance id where applicable).
2. Add timing and counter instrumentation to identity lock, closure recompute, and edge maintenance code paths.
3. Add minimal tests that assert metrics/log hooks fire for representative operations.

