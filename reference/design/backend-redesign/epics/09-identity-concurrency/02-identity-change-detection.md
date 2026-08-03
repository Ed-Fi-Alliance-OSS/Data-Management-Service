---
jira: DMS-998
jira_url: https://edfi.atlassian.net/browse/DMS-998
---

# Story: Detect Identity Projection Changes Reliably

> **Superseded by the `dms.Document` / `dms.ReferentialIdentity` removal:** the `dms.ReferentialIdentity`
> maintenance this story gates. Identity-change *detection* itself survives — the stamping triggers still
> bump `IdentityVersion` / `IdentityLastModifiedAt` only when a stored identity value actually changes.
> Only the derived identity index the detection also fed is gone. See
> [`docs/RELATIONAL-BACKEND.md` §4](../../../../../docs/RELATIONAL-BACKEND.md#4-debugging-the-writeread-paths-and-update-tracking-stored-stamps).

## Description

Detect whether a write changes the document’s identity projection values, so that:

- `dms.ReferentialIdentity` is updated only when necessary,
- and `IdentityVersion/IdentityLastModifiedAt` are stamped only on actual identity projection changes.

Identity projection includes scalar identity parts and identity components sourced from references, whose propagated
identity columns are maintained by native FK cascades.

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
