# Epic: Strict Identity Maintenance & Concurrency

## Description

Implement strict, transactional identity correctness and concurrency behavior described in:

- `reference/design/backend-redesign/transactions-and-concurrency.md`
- `reference/design/backend-redesign/data-model.md` (`dms.ReferentialIdentity`, abstract identity tables, propagated identity columns)

Key goals:
- Maintain `dms.ReferentialIdentity` transactionally (never stale after commit), including superclass/abstract alias rows.
- Ensure identity updates propagate via database cascades/triggers (no application-managed identity-closure traversal).
- Implement deadlock retry and operational guidance for identity updates with potentially large fan-out.

Authorization remains out of scope.

## Stories

- `00-locking-and-retry.md` — Implement lock ordering + deadlock retry policy
- `01-referentialidentity-maintenance.md` — Insert/update primary + alias referential identities
- `02-identity-change-detection.md` — Detect identity projection changes reliably
- `03-identity-propagation.md` — Identity propagation via cascades/triggers (no closure traversal)
- `04-cache-invalidation.md` — Evict/update identity-resolution caches after commit
