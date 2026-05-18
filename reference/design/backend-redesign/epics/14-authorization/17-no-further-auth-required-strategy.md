---
jira: DMS-1090
jira_url: https://edfi.atlassian.net/browse/DMS-1090
---

# Story: Implement NoFurtherAuthorizationRequired Strategy

## Description

Implement the `NoFurtherAuthorizationRequired` authorization strategy per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- When a resource+action is configured with the `NoFurtherAuthorizationRequired` strategy, DMS grants access after authentication succeeds without performing any further authorization checks (no EdOrg, namespace, ownership, or view-based filtering).
- GET-many: No authorization filter is applied — all resources of the requested type are returned (subject to pagination and any non-auth filters).
- GET-by-id, POST, PUT, DELETE: No authorization check is executed — the operation proceeds as long as the caller is authenticated and has the appropriate claim for the action.
- When combined with other strategies via AND semantics, `NoFurtherAuthorizationRequired` is a no-op (it does not restrict and does not contribute error hints).

## Composition with relationship strategies

`NoFurtherAuthorizationRequired` GET-many AND-composition is covered at the
classifier level. CRUD AND-composition is intentionally deferred to DMS-1056,
where relationship CRUD authorization is implemented and mixed-strategy
behavior can be tested against the real authorization path.
