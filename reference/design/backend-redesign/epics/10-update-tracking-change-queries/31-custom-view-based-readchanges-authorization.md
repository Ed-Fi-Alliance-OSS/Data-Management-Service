---
jira: DMS-1193
jira_url: https://edfi.atlassian.net/browse/DMS-1193
---

# Custom View-Based `ReadChanges` Authorization

## Description
Implement the custom view-based authorization strategy for the `/deletes` and `/keyChanges` endpoints.

Refer to `reference/design/backend-redesign/design-docs/auth.md` § "Custom view-based authorization strategy" and `reference/design/backend-redesign/design-docs/change-queries.md` § "Custom view-based strategies" for the design.

## Acceptance Criteria

- Every tracked-change table stores the tracked document's `DocumentId`, and person `DocumentId` tracked columns are populated by natural-key seek so a cascading key change records the old person.
- `/deletes` and `/keyChanges` recognize `{Basis}With{Description}` strategies, resolve the basis, and AND-compose the view check with the other configured strategies, reading old values only.
- Resolved custom views are validated per request with the existing validator; planning failures reuse the existing security-configuration ProblemDetails, with the ODS "Non-identifying properties" text carried over for a first hop that is neither identifying nor securable.
- Unit coverage on the planner, emitter, repository, and DDL, plus PostgreSQL and SQL Server integration scenarios; `auth.md`, `change-queries.md`, and the release-note follow-through are updated.