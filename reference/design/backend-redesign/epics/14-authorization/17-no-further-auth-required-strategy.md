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

`NoFurtherAuthorizationRequired` AND-composition with relationship strategies
is covered at the contract level, so the no-op semantics hold across both
read- and write-side planning regardless of how each surface is wired up later:

- GET-many AND-composition is covered through the classifier and query
  planning path: the classifier routes `NoFurtherAuthorizationRequired` into a
  separate no-op bucket so it never contributes a relationship subject, check,
  or error hint to the read-side query plan.
- CRUD AND-composition is covered at the planner / proposed-value contract
  level: `RelationshipAuthorizationPlanner.PlanProposedValues(...)` reuses the
  same classification, so `NoFurtherAuthorizationRequired` emits no proposed
  check spec, does not shift the configured-index or relationship-local
  ordering of sibling relationship strategies, and does not contribute
  security-configuration failures.

Full end-to-end relational CRUD authorization — wiring the proposed-value
check specs into the write path, enforcing them at execution time, and
exercising mixed strategies through E2E flows — remains owned by the
relationship CRUD authorization work tracked outside DMS-1090.
