---
jira: DMS-1000
jira_url: https://edfi.atlassian.net/browse/DMS-1000
---

# Story: Invalidate Identity Resolution Caches After Commit

## Description

Ensure any caches used for identity resolution (`ReferentialId → DocumentId`) remain correct after identity changes:

- Identity updates can fan out via cascades/triggers and change referential ids for more than the directly written document.
- Cache entries must be updated/evicted after commit for impacted keys, or the cache must be short-TTL/disabled for correctness.

## Acceptance Criteria

- After identity update commit, subsequent requests resolve identities using updated mappings (no stale cache hits).
- Cache invalidation covers:
  - primary referential ids,
  - superclass alias referential ids,
  - any affected dependents whose identities change due to cascades/triggers.
- Cache invalidation is performed after commit (no population from uncommitted state).

## Tasks

1. Identify all cache layers used for identity resolution and define an invalidation API.
2. Define how impacted referential ids are discovered for invalidation:
   - direct-write keys only (with short TTL as the correctness backstop), or
   - a DB-driven “changed identity” outbox/journal to enumerate impacted keys.
3. Implement after-commit invalidation hooks in the backend transaction boundary.
4. Add tests validating cache correctness across an identity update.
