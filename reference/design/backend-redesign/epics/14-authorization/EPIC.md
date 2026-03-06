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
