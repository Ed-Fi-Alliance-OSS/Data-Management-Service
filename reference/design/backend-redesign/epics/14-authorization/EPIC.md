---
jira: DMS-1029
jira_url: https://edfi.atlassian.net/browse/DMS-1029
---

# Epic: Authorization (Relational Primary Store)

## Description

This epic owns the full authorization system for the relational primary store.

All authorization stories (design, DDL/provisioning, runtime integration, and tests) should live under this epic.

## Outcomes

- v1 authorization design with clearly stated scope, non-goals, and open questions.
- Concrete database object inventory (tables/views/indexes/functions) plus provisioning/fingerprinting strategy.
- Defined runtime integration points (read path filtering + authorized paging, write-path maintenance, caching/claim evaluation behavior).
- Test/verification plan for the follow-on implementation work.

## Stories

- `DMS-1026` — `00-auth-placeholder.md` — Authorization design spike (v1)
- `DMS-1049` — `01-emit-edorg-hierarchy-table-and-triggers.md` — Emit the auth.EducationOrganizationIdToEducationOrganizationId table and related triggers
- `DMS-1050` — `02-emit-people-auth-views.md` — Emit people auth views
- `DMS-1052` — `03-emit-mssql-tvps-and-pgsql-throw-error.md` — Emit MSSQL TVPs and PGSQL throw_error function
- `DMS-1053` — `04-resolve-securable-element-column-path.md` — Create the ResolveSecurableElementColumnPath function
- `DMS-1054` — `05-emit-auth-indexes.md` — Emit indexes for the Relationship-based and Namespace-based strategies
- `DMS-1094` — `05b-emit-person-join-indexes.md` — Emit people indexes needed for joins
- `DMS-1055` — `06-relationship-auth-get-many.md` — Implement EdOrg-only Relationship-based Authorization for GET-many
- `DMS-1095` — `06b-people-relationship-auth-get-many.md` — Implement People-involved Relationship-based Authorization for GET-many
- `DMS-1056` — `07-relationship-auth-crud.md` — Implement Relationship-based Authorization Strategies for GET-by-id, POST, PUT, and DELETE
- `DMS-1057` — `08-namespace-auth-strategy.md` — Implement Namespace-based Authorization Strategy
- `DMS-1058` — `09-design-ownership-token-maintenance.md` — Design Ownership-token maintenance in CMS
- `DMS-1059` — `10-emit-ownership-column-and-index.md` — Emit the CreatedByOwnershipTokenId column and index
- `DMS-1060` — `11-ownership-auth-strategy.md` — Implement Ownership-based Authorization Strategy
- `DMS-1061` — `12-view-based-resolve-column-path.md` — Add support for View-based strategy in the ResolveSecurableElementColumnPath function
- `DMS-1062` — `13-view-based-auth-get-many.md` — Implement View-based Authorization Strategy for GET-many
- `DMS-1063` — `14-view-based-auth-crud.md` — Implement View-based Authorization Strategy for GET-by-id, POST, PUT, and DELETE
- `DMS-1064` — `15-enumerate-multi-hop-person-paths.md` — Enumerate all DS 5.2 resources with multi-hop Person authorization join paths
- `DMS-1065` — `16-further-performance-optimizations.md` — Further Performance Optimizations
- `DMS-1090` — `17-no-further-auth-required-strategy.md` — Implement NoFurtherAuthorizationRequired Strategy
- `DMS-1091` — `18-formalize-auth-startup-task.md` — Formalize Auth Startup as IDmsStartupTask
