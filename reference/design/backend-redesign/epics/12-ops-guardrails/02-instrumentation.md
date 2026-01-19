# Story: Instrumentation for Cascades, Stamps/Journals, and Retries

## Description

Add instrumentation for correctness-critical and performance-sensitive operations:

- deadlock/serialization retries and failures,
- write transaction latency (including identity updates),
- stamp/journal write rates (`dms.Document` updates, `dms.DocumentChangeEvent` inserts),
- and (where measurable) cascade fan-out signals during identity updates.
- deadlock/serialization retries and failures.

Align with the instrumentation suggestions in `reference/design/backend-redesign/strengths-risks.md`.

## Acceptance Criteria

- Metrics/logs exist for:
  - deadlock/serialization retries + exhausted retries
  - write transaction duration (tagged by resource type and operation)
  - journal/stamp write counters (at minimum: `dms.DocumentChangeEvent` rows emitted)
- Instrumentation does not require schema changes beyond what the redesign already specifies.

## Tasks

1. Define metric names and dimensions (resource type, dialect, instance id where applicable).
2. Add timing and counter instrumentation to write transaction boundaries and journaling/stamping touch points.
3. Add minimal tests that assert metrics/log hooks fire for representative operations.
