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

- Depends on 18-00 through 18-06 and informs CDC story 19-07.

## Deliverables

1. Add provider fixtures for explicit target selection, late resolution of already-listed
   targets, projection, fallback, failure, restart, monotonic upsert, delete fencing,
   health, and rebuild.
2. Exercise CDC projection-completeness transitions without requiring a Kafka connector.
3. Publish DocumentCache operation/troubleshooting guidance and cross-link CDC connector
   operations separately. Include cache-ahead diagnosis and require operators to
   establish whether CDC could have published the higher version before recovery. Require
   a full cache clear and latch reset in one provider transaction; never document a
   latch-only reset. State that v1 is delivered only through create-only provisioning of a
   new database and provides no upgrade path for a legacy cache schema. For SQL Server,
   document the projection-scoped RCSI prerequisite, inspection and offline enablement,
   row-version-store capacity/health monitoring, and that runtime DMS validates but never
   changes the database option. Do not describe RCSI as mandatory for an unlisted
   relational-only SQL Server data store.
4. Document the shipped implementation defaults and tuning guidance for scan/audit
   intervals, page size, concurrent targets, maximum audit age, and the direct-fill timeout,
   including how to diagnose audit overruns and API-resource contention.
5. Document ordinary cache clear/rebuild as eventual recovery, not as an exact CDC baseline
   replacement. State that a production same-topic equal-version correction is deferred
   until an owned cross-replica/external-writer fence exists. Direct byte-changing API/cache
   repair to 18-08 only for an explicitly offline data store, and do not claim that either
   path restores exact combined readiness after first-write admission.

## Acceptance Evidence

- PostgreSQL and SQL Server integration tests cover all completed E18 story outcomes,
  including `StreamEtag` consistency, metadata consistency, lower-version gaps,
  target-scoped failure backoff and database rediscovery, cache-ahead invariant handling,
  indexed incremental discovery, periodic full audits, and API independence.
- SQL Server integration tests prove RCSI-disabled and unreadable targets perform no
  projection, cache use, or latch mutation; enabling RCSI makes the target eligible for a
  fresh startup audit; and a synchronized RCSI-backed comparison cannot falsely latch a
  mixed source/cache observation. Mixed-target coverage proves canonical API and peer
  isolation.
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
- Provider behavior tests prove ordinary reconciliation does not rewrite an existing
  equal-version row, while an explicit clear/rebuild can produce rows with the same
  canonical `ContentVersion`. These mechanics do not constitute a supported production
  baseline-replacement workflow or another exact readiness guarantee.
- Runbook tests cover internal-only full-cache clear/latch-reset/rebuild and hand off
  possibly observed cache-ahead state to a new downstream state namespace, including E19's
  new-generation topic/snapshot recovery. Provider tests prove source equality and restart
  do not clear the latch, that a set latch disables cache reads and writes, and that the
  explicit recovery clears the full cache before resetting the latch in the same
  transaction; instructions never lower a cache version or reset only the latch.
- Runbook procedures are checked against implemented configuration, health output, and
  recovery behavior. They never instruct operators to enable DocumentCache on an
  already-provisioned database or alter legacy `Etag`/UUID-constraint inventory in place.
  They do not instruct runtime DMS to execute `ALTER DATABASE` and distinguish the scoped
  projection prerequisite from general SQL Server DMS guidance.
- Scheduling coverage proves bounded in-process work remains isolated across targets and
  that an overdue audit degrades readiness instead of creating overlapping catch-up work.

## Out of Scope

- Kafka connector setup, ACLs, offsets, and topic management.
- Consumer application guidance.
