---
jira: DMS-998
jira_url: https://edfi.atlassian.net/browse/DMS-998
---

# Story: Detect Identity Projection Changes Reliably

## Description

Detect whether a write changes the document’s identity projection values, so that:

- `dms.ReferentialIdentity` is updated only when necessary,
- and `IdentityVersion/IdentityLastModifiedAt` are stamped only on actual identity projection changes.

Identity projection includes scalar identity parts and identity components sourced from references (via propagated identity columns maintained by FK cascades/triggers).

## Acceptance Criteria

- No-op updates that do not change identity projection values do not update `dms.ReferentialIdentity` or bump identity stamps (best effort).
- Identity changes are detected when:
  - scalar identity values change, or
  - identity-component reference targets change.
- Tests cover both false positives (avoid) and false negatives (disallowed).

## Tasks

1. Emit per-dialect trigger logic that detects identity projection changes by comparing old/new identity columns.
2. Gate `dms.ReferentialIdentity` maintenance and identity-stamp updates on that detection.
3. Add tests for identity change detection scenarios (scalar + reference-sourced identity components).
