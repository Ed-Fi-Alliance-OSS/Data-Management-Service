# Story: Invalidate Identity Resolution Caches After Commit

## Description

Ensure any caches used for identity resolution (`ReferentialId → DocumentId`) remain correct after identity changes:

- Normal writes and identity closure recompute may change referential ids.
- Cache entries must be updated/evicted after commit for all impacted keys.

## Acceptance Criteria

- After identity update commit, subsequent requests resolve identities using updated mappings (no stale cache hits).
- Cache invalidation covers:
  - primary referential ids,
  - superclass alias referential ids,
  - any affected dependents from closure recompute.
- Cache invalidation is performed after commit (no population from uncommitted state).

## Tasks

1. Identify all cache layers used for identity resolution and define an invalidation API.
2. Capture affected referential ids during writes/closure recompute for invalidation.
3. Implement after-commit invalidation hooks in the backend transaction boundary.
4. Add tests validating cache correctness across an identity update.

