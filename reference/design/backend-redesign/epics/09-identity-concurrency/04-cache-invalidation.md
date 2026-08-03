---
jira: DMS-1000
jira_url: https://edfi.atlassian.net/browse/DMS-1000
---

# Story: Invalidate Identity Resolution Caches After Commit

> **Superseded by the `dms.Document` / `dms.ReferentialIdentity` removal:** the identity-resolution caches
> this story invalidates are caches over `ReferentialId → DocumentId`. There is no such stored mapping and
> no such cache: each request resolves identities by index seek within its own transaction. Retained as a
> historical work record. See [`docs/RELATIONAL-BACKEND.md` §4](../../../../../docs/RELATIONAL-BACKEND.md#4-debugging-the-writeread-paths-and-update-tracking-stored-stamps).

## Description

Ensure any caches used for identity resolution (`ReferentialId → DocumentId`) remain correct after identity changes:

- Identity updates can fan out through native FK cascades and row-local maintenance triggers, changing referential ids
  for more than the directly written document.
- Cache entries must be updated/evicted after commit for impacted keys, or the cache must be short-TTL/disabled for correctness.

## Acceptance Criteria

- After identity update commit, subsequent requests resolve identities using updated mappings (no stale cache hits).
- Cache invalidation covers:
  - primary referential ids,
  - superclass alias referential ids,
  - any affected dependents whose identities change due to cascades and row-local maintenance triggers.
- Cache invalidation is performed after commit (no population from uncommitted state).

## Tasks

1. Identify all cache layers used for identity resolution and define an invalidation API.
2. Define how impacted referential ids are discovered for invalidation:
   - direct-write keys only (with short TTL as the correctness backstop), or
   - a DB-driven “changed identity” outbox/journal to enumerate impacted keys.
3. Implement after-commit invalidation hooks in the backend transaction boundary.
4. Add tests validating cache correctness across an identity update.
