---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Design References

- [Authoritative projection and CDC design](../../../cdc-streaming.md)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Validate the completed projection feature across providers and publish operator guidance
that links to, rather than restates, the authoritative design.

## Dependencies

- Depends on the remaining E18 stories and informs CDC story 19-07.

## Deliverables

1. Add provider fixtures for explicit target selection, late resolution of already-listed
   targets, projection, fallback, failure, restart, monotonic upsert, delete fencing,
   health, and rebuild.
2. Exercise CDC projection-completeness transitions without requiring a Kafka connector.
3. Publish DocumentCache operation/troubleshooting guidance and cross-link CDC connector
   operations separately. Include cache-ahead diagnosis and require operators to
   establish whether CDC could have published the higher version before recovery. Require
   a full cache clear and latch reset in one provider transaction; never document a
   latch-only reset.
4. Document the shipped implementation defaults and tuning guidance for scan/audit
   intervals, page size, concurrent targets, maximum audit age, and the direct-fill timeout,
   including how to diagnose audit overruns and API-resource contention.
5. Document the cache side of a compatible projection correction: stop old cache writers,
   including optional direct fill, use the provider-supported clear operation, start only
   corrected writers, and reconcile to an exact zero audit. Hand connector catch-up and
   equal-version consumer verification to 19-07.

## Acceptance Evidence

- PostgreSQL and SQL Server integration tests cover all completed E18 story outcomes,
  including `StreamEtag` consistency, metadata consistency, lower-version gaps, fair
  retry, cache-ahead invariant handling, indexed incremental discovery, periodic full
  audits, and API independence.
- Provider integration tests prove trigger-enforced UUID denormalization for insert and
  update, cascade deletion through the compact `DocumentId` FK, absence of a cache UUID
  index, and equivalent connector-key values from cache upserts and canonical deletes.
- Concurrency tests prove an update committed during materialization is rejected by the
  final optimistic check, a coherent delayed lower projection after that check is ordinary
  cache-behind work, no lower candidate replaces a higher cache row, and projection takes
  no explicit update/write canonical source-row lock as a content-version fence or carries
  a lock from the optimistic source check into the cache transaction. Tests preserve
  ordinary integrity locks acquired by foreign-key enforcement and the UUID-validation
  trigger.
- Rebuild tests use ordinary reconciliation and never introduce a separate backfill
  workflow.
- Compatible-correction tests prove ordinary reconciliation does not rewrite an existing
  equal-version row, while an explicit clear/rebuild produces corrected rows with the same
  canonical `ContentVersion` after old cache writers are stopped.
- Runbook tests cover internal-only full-cache clear/latch-reset/rebuild and hand off
  possibly observed cache-ahead state to a new downstream state namespace, including E19's
  new-generation topic/snapshot recovery. Provider tests prove source equality and restart
  do not clear the latch, that a set latch disables cache reads and writes, and that the
  explicit recovery clears the full cache before resetting the latch in the same
  transaction; instructions never lower a cache version or reset only the latch.
- Runbook procedures are checked against implemented configuration, health output, and
  recovery behavior.
- Scheduling coverage proves bounded in-process work remains isolated across targets and
  that an overdue audit degrades readiness instead of creating overlapping catch-up work.

## Out of Scope

- Kafka connector setup, ACLs, offsets, and topic management.
- Consumer application guidance.
