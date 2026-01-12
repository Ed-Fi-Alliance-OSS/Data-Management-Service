# Epic: Strict Identity Maintenance & Concurrency

## Description

Implement strict, transactional identity correctness and concurrency behavior described in:

- `reference/design/backend-redesign/transactions-and-concurrency.md`
- `reference/design/backend-redesign/data-model.md` (`dms.ReferentialIdentity`, `dms.IdentityLock`, `dms.ReferenceEdge`)

Key goals:
- Maintain `dms.ReferentialIdentity` transactionally (never stale after commit), including superclass/abstract alias rows.
- Enforce locking invariants using `dms.IdentityLock` and deterministic lock ordering with deadlock retry.
- Perform identity-closure recompute to a fixpoint for identity updates using `dms.ReferenceEdge(IsIdentityComponent=true)`.

Authorization remains out of scope.

## Stories

- `00-locking-and-retry.md` — Implement lock ordering + deadlock retry policy
- `01-referentialidentity-maintenance.md` — Insert/update primary + alias referential identities
- `02-identity-change-detection.md` — Detect identity projection changes reliably
- `03-identity-closure-recompute.md` — Expand+lock closure and recompute identities set-based
- `04-cache-invalidation.md` — Evict/update identity-resolution caches after commit

