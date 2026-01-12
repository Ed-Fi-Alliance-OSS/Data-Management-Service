# Story: Guardrails for Extreme Fanout and Closure Sizes

## Description

Add configurable bounds to prevent runaway closure recompute and hub contention scenarios:

- maximum closure size,
- maximum closure iterations,
- maximum lock rows,
- and fail-fast behavior with actionable diagnostics when limits are exceeded.

This is informed by risk analysis in `reference/design/backend-redesign/strengths-risks.md`.

## Acceptance Criteria

- When configured limits are exceeded, the write fails with:
  - a clear error message,
  - and metrics/logs that include closure size and limit values.
- Limits are configurable per deployment.
- Failures do not leave derived artifacts partially updated (transaction rollback).

## Tasks

1. Define configuration options for closure/lock bounds.
2. Implement enforcement points during closure expansion and lock acquisition.
3. Add tests covering:
   1. limit exceeded path,
   2. diagnostic content,
   3. rollback behavior.

