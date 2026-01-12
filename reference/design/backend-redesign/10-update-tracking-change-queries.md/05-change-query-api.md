# Story: Change Query API Endpoints (Optional / Future-Facing)

## Description

Implement the HTTP/API layer for Change Queries consistent with Ed-Fi semantics (where applicable), backed by the journal-driven selection algorithm.

Note: The redesign documents treat Change Queries as a future requirement, but the update-tracking tables and journals are part of the core contract. This story is for the API surface and can be scheduled independently.

## Acceptance Criteria

- A Change Query endpoint exists (or is feature-flagged) that:
  - accepts a change version window and paging parameters,
  - returns items with `ChangeVersion` values derived per `update-tracking.md`,
  - returns current representations (not historical snapshots).
- Endpoint is backed by the journal-driven selection logic and uses deterministic paging.
- Basic integration test verifies the endpoint returns expected results for a small change window.

## Tasks

1. Define the endpoint shape and routing consistent with the DMS API surface.
2. Integrate selection + reconstitution + derived metadata into the endpoint response.
3. Add feature flag/config gating if Change Queries are not enabled by default.
4. Add integration tests for a minimal end-to-end scenario.

