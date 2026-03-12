---
jira: DMS-1062
jira_url: https://edfi.atlassian.net/browse/DMS-1062
---

# Story: Implement View-based Authorization Strategy for GET-many

## Description

Implement the view-based authorization strategy for the GET-many scenario per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- When the authorization strategy name is not a known built-in strategy, the system falls back to the view-based strategy and extracts the basis resource from the strategy name using the `{BasisResource}With{SomeDescription}` convention.
- When searching for the basis resource, resources from the standard (edfi) are prioritized over resources from extensions (e.g., edfi.Student is selected instead of homograph.Student).
- The join path from the resource table to the basis resource is resolved using `ResolveSecurableElementColumnPath(subjectResourceFullName, basisResourceFullName)`, and the result is used to construct the SQL joins/subqueries against the custom auth view (`auth.{StrategyName}`).
- If there's no join path to the basis resource the strategy cannot be applied and an appropriate error is raised.
- The custom auth view outputs DocumentId (not natural keys/USIs), and the join uses DocumentId accordingly.
- GET-many results are filtered so that only resources matching the custom auth view are returned.
- View-based strategies are combined with AND semantics — they act as additional filter criteria applied alongside other AND strategies (Namespace-based, Ownership-based).
- View-based strategies execute before relationship-based (OR) strategies, and their order relative to Namespace-based follows the order configured in CMS.
- When authorization fails (no matching rows), the result set is simply empty — no error is thrown for GET-many; the filter naturally excludes unauthorized resources.
- When the custom auth view does not exist or returns invalid columns, DMS returns HTTP 500 with `type: urn:ed-fi:api:system` (same as ODS behavior). See `auth.md` §"View-based authorization strategy".
- Works for both PostgreSQL and SQL Server.
- Tests cover the scenarios described in `auth.md`
