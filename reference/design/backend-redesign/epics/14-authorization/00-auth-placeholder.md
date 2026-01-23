---
jira: DMS-1026
jira_url: https://edfi.atlassian.net/browse/DMS-1026
---

# Story: Authorization Placeholder (Design + Implementation Deferred)

## Description

Track the deferred authorization work for the relational backend redesign. The current authorization design is intentionally incomplete and will be handled later.

This story exists to:
- explicitly mark authorization as out-of-scope for current epics,
- capture the high-level follow-ups required to make the authorization design implementation-ready, and
- define the integration points that will require revisiting once `reference/design/backend-redesign/design-docs/auth.md` is finalized.

## Acceptance Criteria

- Placeholder exists and clearly states that authorization is deferred and out of scope for current DDL/mapping/runtime work.
- Placeholder references `reference/design/backend-redesign/design-docs/auth.md` as the draft starting point.
- No work in current epics depends on authorization tables, views, triggers, or query filters.

## Tasks

1. Finalize the authorization design document (`reference/design/backend-redesign/design-docs/auth.md`) and explicitly mark v1 scope vs out-of-scope.
2. Define the concrete database object inventory required for authorization (tables/views/indexes) and how it is generated/provisioned.
3. Define how authorization integrates with:
   1. query filtering and paging,
   2. write-path maintenance (if any),
   3. schema fingerprinting/mapping pack selection (if any).
4. Extend the DDL generator and verification harness to cover authorization objects when in scope.
5. Add runtime integration tests validating authorization semantics once implemented.

