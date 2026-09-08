---
jira: DMS-1018
jira_url: https://edfi.atlassian.net/browse/DMS-1018
---

# Story: Guardrails for Identity-Update Fan-out and Retry Behavior

## Description

Add configurable operational guardrails for identity updates that can cause large cascade fan-out:

- enable/disable identity updates (`AllowIdentityUpdates`),
- optional per-resource allow/deny list for identity updates,
- bounded deadlock retry attempts with backoff,
- optional transaction timeout / lock timeout guidance,
- and fail-fast behavior with actionable diagnostics when guardrails are exceeded.

This is informed by risk analysis in `reference/design/backend-redesign/design-docs/strengths-risks.md`.

## Acceptance Criteria

- When configured limits are exceeded, the write fails with:
  - a clear error message,
  - and metrics/logs that include retry counts and relevant limit values.
- Limits are configurable per deployment.
- Failures do not leave derived artifacts partially updated (transaction rollback).

## Tasks

1. Define configuration options for identity-update gating and retry limits.
2. Implement enforcement points in the write transaction boundary (before executing identity updates).
3. Add tests covering:
   1. limit exceeded path,
   2. diagnostic content,
   3. rollback behavior.
