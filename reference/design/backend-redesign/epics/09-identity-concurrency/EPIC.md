---
jira: DMS-995
jira_url: https://edfi.atlassian.net/browse/DMS-995
---


# Epic: Strict Identity Maintenance & Concurrency

> **Superseded by the `dms.Document` / `dms.ReferentialIdentity` removal:** this epic's subject — keeping
> `dms.ReferentialIdentity` transactionally correct, including its superclass alias rows and the caches
> over it — no longer exists. Identity resolution is an index seek on stored natural-key columns.
> Retained as a historical work record. See [`docs/RELATIONAL-BACKEND.md` §4](../../../../../docs/RELATIONAL-BACKEND.md#4-debugging-the-writeread-paths-and-update-tracking-stored-stamps).

## Description

Implement strict, transactional identity correctness and concurrency behavior described in:

- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- `reference/design/backend-redesign/design-docs/data-model.md` (`dms.ReferentialIdentity`, abstract identity tables, propagated identity columns)

Key goals:
- Maintain `dms.ReferentialIdentity` transactionally (never stale after commit), including superclass/abstract alias rows.
- Ensure identity updates propagate via database cascades/triggers (no application-managed identity-closure traversal).
- Implement deadlock retry and operational guidance for identity updates with potentially large fan-out.

Authorization remains out of scope.

## Stories

- `DMS-996` — `00-locking-and-retry.md` — Implement lock ordering + deadlock retry policy
- `DMS-997` — `01-referentialidentity-maintenance.md` — Insert/update primary + alias referential identities
- `DMS-998` — `02-identity-change-detection.md` — Detect identity projection changes reliably
- `DMS-999` — `03-identity-propagation.md` — Identity propagation via native cascades (no closure traversal)
- `DMS-1000` — `04-cache-invalidation.md` — Evict/update identity-resolution caches after commit
