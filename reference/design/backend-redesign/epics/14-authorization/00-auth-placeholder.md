---
jira: DMS-1026
jira_url: https://edfi.atlassian.net/browse/DMS-1026
---

# Story: Authorization Design Spike (Relational Primary Store)

## Description

Produce an implementation-ready authorization design for the relational primary store.

The current authorization document is intentionally incomplete and is retained as a sketch:

- `reference/design/backend-redesign/design-docs/auth.md`

This spike turns that sketch into a complete v1 design and identifies the follow-on implementation work (DDL + runtime).

## Acceptance Criteria

- `reference/design/backend-redesign/design-docs/auth.md` is updated to an implementation-ready design with:
  - explicit v1 scope and non-goals,
  - concrete strategy semantics mapped to existing DMS concepts,
  - a selected baseline storage/query approach,
  - defined read/write path integration points and failure modes,
  - a concrete DB object inventory required for the baseline approach.
- A provisioning and fingerprinting plan exists for authorization objects (how they are generated/provisioned and how they participate in drift detection).
- A test/verification plan exists for future implementation (unit, integration, and parity where applicable).

## Tasks

1. Finalize the authorization design document (`reference/design/backend-redesign/design-docs/auth.md`) and explicitly mark v1 scope vs out-of-scope.
2. Decide the baseline authorization model:
  - view-based authorization (`auth.*` views) vs materialized membership tables, and
  - EdOrg hierarchy expansion strategy (recursive vs closure/tuple table).
3. Define the concrete database object inventory required for v1 authorization (tables/views/indexes/functions), including required keys and canonical naming rules.
4. Define how authorization integrates with:
  - query filtering and authorized paging,
  - descriptor endpoints (if different),
  - write-path maintenance (e.g., `dms.DocumentSubject`, EdOrg hierarchy maintenance),
  - schema fingerprinting and mapping-set selection (if authorization objects participate).
5. Define runtime behavior and failure modes:
  - claim evaluation and caching,
  - handling large claim sets,
  - behavior when required authorization objects are missing or stale.
6. Define a verification/test plan for the implementation epic:
  - deterministic DDL/object inventory verification,
  - runtime authorization integration tests across dialects,
  - parity tests against existing DMS/ODS semantics where applicable.
