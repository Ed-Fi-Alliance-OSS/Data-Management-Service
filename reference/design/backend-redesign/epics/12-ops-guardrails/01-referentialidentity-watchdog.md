# Story: Sampling-Based Integrity Watchdog (ReferentialIdentity + Journals)

## Description

Add an optional production feature that continuously samples documents and verifies:

- `dms.ReferentialIdentity` rows match the recomputed expected `ReferentialId`s for the sampled documents (primary + alias rows), and
- `dms.DocumentChangeEvent` can support Change Query selection for the current representation stamp (optional verification).

On mismatch:
- emit an alert/metric, and optionally
- self-heal by rebuilding the sampled document’s `dms.ReferentialIdentity` rows.

## Acceptance Criteria

- Watchdog runs on a configurable schedule and sample rate.
- On mismatch, emits:
  - a high-signal log entry,
  - metrics counters,
  - and (when enabled) performs a targeted repair safely.
- Watchdog behavior is configurable (audit-only vs self-heal vs mark unhealthy).

## Tasks

1. Implement a background service that selects sample documents and runs integrity verification.
2. Implement mismatch reporting and optional self-heal behavior.
3. Add metrics and config gating.
4. Add a targeted integration test that forces a mismatch and asserts the watchdog reports (and optionally repairs) it.
