---
jira: TBD
source_spike: DMS-1246
related:
  - DMS-1245
---

# Epic: `dms.DocumentCache` Projection

## Description

Implement `dms.DocumentCache` as the optional materialized JSON projection defined by
DMS-1246:

- [`0001-role-and-enablement.md`](../../design-docs/document-cache/0001-role-and-enablement.md)
- [`0002-projector-freshness-and-backfill.md`](../../design-docs/document-cache/0002-projector-freshness-and-backfill.md)
- [`0003-cache-and-domain-lifecycle-separation.md`](../../design-docs/document-cache/0003-cache-and-domain-lifecycle-separation.md)
- [`0004-failure-health-and-ddl-support.md`](../../design-docs/document-cache/0004-failure-health-and-ddl-support.md)

The epic delivers a DMS-owned asynchronous projector, optional cache-backed reads,
restartable backfill, stale-write fencing, retry/dead-letter handling, and observable
projection health.

`dms.DocumentCache` remains optional for ordinary DMS correctness. Authorization, writes,
identity resolution, Change Queries, and normal GET/query correctness continue to use
canonical relational sources. When Kafka CDC is enabled, cache create/update/snapshot
events supply document upserts, while authoritative deletes come independently from
`dms.Document`. Cache deletion never represents domain deletion.

## Stories

- `TBD` — `00-documentcache-configuration-and-mode-boundaries.md` — Add DocumentCache configuration boundaries
- `TBD` — `01-documentcache-ddl-and-projector-state.md` — Emit DocumentCache projector state and failure DDL
- `TBD` — `02-document-materializer-service.md` — Add reusable caller-agnostic document materialization
- `TBD` — `03-async-projector-worker.md` — Add asynchronous DocumentCache projector worker
- `TBD` — `04-initial-backfill-and-rebuild.md` — Add restartable initial backfill and rebuild support
- `TBD` — `05-cache-backed-read-path.md` — Add fresh-cache read path with relational fallback
- `TBD` — `07-projector-stale-write-fencing.md` — Enforce projector stale-write and post-delete fencing
- `TBD` — `08-projection-retry-dead-letter-and-repair.md` — Add projection retry, dead-letter, and repair handling
- `TBD` — `09-documentcache-health-readiness-and-telemetry.md` — Add DocumentCache health, readiness, and telemetry
- `TBD` — `11-documentcache-integration-tests-and-runbooks.md` — Add integration coverage and DocumentCache runbooks

Stories for CDC pre-delete materialization and provider materialize-then-delete verification
were removed. Delete key/filter/routing/ordering verification belongs to `17-cdc-kafka`
and uses `dms.Document` as its source.

## Cross-Story Dependency Notes

- Story 00 defines the simplified `Disabled | Async` projector configuration. Kafka CDC
  requires `Async`, but it does not create another projector mode or mutation gate.
- Story 01 precedes Stories 03, 04, 08, and 09 because they persist or inspect projector
  state and failure rows.
- Story 02 is the common materialization path for the projector and optional read-through
  fill. It depends on the relational read path and update-tracking semantics.
- Stories 03 and 04 can proceed in parallel after Stories 01 and 02. Story 04 owns the
  bounded backfill epoch; Story 03 owns ongoing asynchronous catch-up.
- Story 05 depends on Stories 00 and 02 and always falls back to relational
  reconstitution for missing/stale cache rows.
- Story 07 supplies a shared guarded cache write for projection, backfill, retry, and
  read-through work.
- Story 08 depends on Stories 01, 03, and 07.
- Story 09 consumes state, backfill, fencing, and failures. Its health result is
  observational and supplies only the upsert-projection portion of CDC readiness.
- Story 11 closes the epic with provider integration tests and runbooks.

## Dependency Matrix with `17-cdc-kafka`

| `17-cdc-kafka` story | Depends on `18-document-cache` | Dependency type | Notes |
| --- | --- | --- | --- |
| `17-00-documentcache-cdc-prerequisites.md` | 18-00, 18-01, 18-04, 18-07, 18-08, 18-09 | Hard for upsert readiness | Supplies projector configuration, state, bounded backfill, fencing, failures, and health. Authoritative delete capture is owned by E17. |
| `17-01-cdc-ddl-support.md` | 18-01 | Soft | Uses the projected table DDL; E17 owns two-table CDC/key/replica setup. |
| `17-02-connector-template-generation.md` | 18-01 | Soft until upsert smoke tests | Templates can begin with fixtures. |
| `17-03-bootstrap-enable-kafka-cdc.md` | 18-00, 18-04, 18-09, plus 17-00 | Hard | Bootstrap validates projection prerequisites and waits for source/connector readiness. |
| `17-04-message-contract-tests.md` | 18-02 | Soft | Supplies realistic cache payload fixtures; delete/filter/order tests do not depend on cache materialization. |
| `17-05-e2e-kafka-scenarios.md` | 18-00, 18-03, 18-04, 18-09, plus 17-00 through 17-04 | Hard for complete create/update coverage | Delete scenarios remain valid even before a cache row exists. |
| `17-06-ops-docs-runbooks.md` | 18-08, 18-09, 18-11 | Hard for final docs | Consumes projection failure/readiness/recovery guidance. |

## Scope Guardrails

- Do not make normal DMS API correctness depend on `dms.DocumentCache`.
- Do not use `dms.DocumentCache` for authorization or identity resolution.
- Do not create profile-specific, link-free, or consumer-specific cache rows.
- Do not add a CDC-specific projector mode, per-document delete lock, pre-delete
  reconstitution, or mutation gate.
- Do not move connector key/filter/routing logic into this epic.
- Do not publish Kafka messages directly from DMS.
- Do not expose `DocumentId` as a public Kafka identifier.

## Completion Criteria

- DMS can run with `dms.DocumentCache` disabled when neither read acceleration nor Kafka
  CDC is enabled.
- DMS can run one asynchronous projection mode for cache-backed reads, indexing, and CDC
  upserts while falling back to relational reconstitution for cache misses/staleness.
- A hosted supervisor runs isolated projector execution contexts for the startup
  projection inventory without request-scoped routing dependence.
- Bounded backfill, retries, failures, repair, lag, and readiness are observable per data
  store without changing API behavior.
- Projector, backfill, retry, and read-through writes cannot overwrite newer cache rows or
  recreate cache rows after canonical deletion.
- Cache deletion/rebuild never blocks API deletion and never represents domain deletion.
- PostgreSQL and SQL Server coverage proves the projector/read/fencing contracts.
