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
- [`0002-projector-freshness-and-reconciliation.md`](../../design-docs/document-cache/0002-projector-freshness-and-reconciliation.md)
- [`0003-cache-and-domain-lifecycle-separation.md`](../../design-docs/document-cache/0003-cache-and-domain-lifecycle-separation.md)
- [`0004-reconciliation-health-and-ddl-support.md`](../../design-docs/document-cache/0004-reconciliation-health-and-ddl-support.md)

The epic delivers a DMS-owned asynchronous reconciliation loop, optional cache-backed
reads, stale-write fencing, exact mismatch-derived health, and bounded in-memory retry.
The database difference between `dms.Document` and `dms.DocumentCache` is the only
durable work inventory.

`dms.DocumentCache` remains optional for ordinary DMS correctness. Authorization, writes,
identity resolution, Change Queries, and normal GET/query correctness continue to use
canonical relational sources. For each `KafkaCdc:Targets` entry, cache
create/update/snapshot events supply document upserts, while authoritative deletes come
independently from `dms.Document`. Cache deletion never represents domain deletion.

## Stories

- `TBD` — `00-documentcache-configuration-and-target-selection.md` — Add DocumentCache configuration and target selection
- `TBD` — `02-document-materializer-service.md` — Add reusable caller-agnostic document materialization
- `TBD` — `03-async-projector-reconciliation-loop.md` — Add the asynchronous DocumentCache reconciliation loop
- `TBD` — `05-cache-backed-read-path.md` — Add fresh-cache read path with relational fallback
- `TBD` — `07-projector-stale-write-fencing.md` — Enforce projector stale-write and post-delete fencing
- `TBD` — `09-documentcache-health-readiness-and-telemetry.md` — Add DocumentCache health, readiness, and telemetry
- `TBD` — `11-documentcache-integration-tests-and-runbooks.md` — Add integration coverage and DocumentCache runbooks

The projector-state/failure DDL, initial population/rebuild orchestration, and
retry/dead-letter/repair stories were removed. Core E02 already owns
`dms.DocumentCache` DDL; all remaining cases are handled by Story 03's ordinary
reconciliation query and in-memory backoff.
Stories for CDC pre-delete materialization and provider materialize-then-delete verification
remain excluded; delete capture belongs to `17-cdc-kafka` and uses `dms.Document`.

## Cross-Story Dependency Notes

- Story 00 derives the projection target set from standalone DocumentCache enablement,
  read acceleration, and `KafkaCdc:Targets`. Each Kafka target selects projection for
  itself without a separate projector mode or process-wide CDC flag.
- Story 02 is the common materialization path for reconciliation and optional direct
  read-through fill.
- Story 03 owns one loop per data store for initial population, ongoing catch-up, restart,
  rebuild, and retry.
- Story 05 always falls back to relational reconstitution for missing/stale rows and does
  not enqueue work.
- Story 07 supplies the shared guarded cache write for reconciliation and optional direct
  fill.
- Story 09 derives health and completeness from current mismatch count and oldest
  mismatch age. It does not infer completeness from a maximum projected version.
- Story 11 closes the epic with provider integration tests and runbooks.

## Dependency Matrix with `17-cdc-kafka`

| `17-cdc-kafka` story | Depends on `18-document-cache` | Dependency type | Notes |
| --- | --- | --- | --- |
| `17-00-documentcache-cdc-prerequisites.md` | 18-00, 18-03, 18-07, 18-09 | Hard for upsert readiness | Supplies configuration, reconciliation, fencing, and exact completeness health. Core E02 supplies source/cache DDL; authoritative delete capture is owned by E17. |
| `17-01-cdc-ddl-support.md` | — | No E18 dependency | Core E02 supplies `dms.DocumentCache`; E17 owns two-table CDC/key/replica setup. |
| `17-02-connector-template-generation.md` | — | No E18 dependency until upsert smoke tests | Templates can begin with fixtures. |
| `17-03-bootstrap-enable-kafka-cdc.md` | 18-00, 18-03, 18-09, plus 17-00 | Hard | Bootstrap establishes capture, waits for zero projection mismatches, and verifies connector/source-position catch-up. |
| `17-04-message-contract-tests.md` | 18-02 | Soft | Supplies realistic cache payload fixtures; delete/filter/order tests do not depend on cache materialization. |
| `17-05-e2e-kafka-scenarios.md` | 18-00, 18-03, 18-09, plus 17-00 through 17-04 | Hard for complete create/update coverage | Delete scenarios remain valid even before a cache row exists. |
| `17-06-ops-docs-runbooks.md` | 18-03, 18-09, 18-11 | Hard for final docs | Consumes mismatch health, bounded retry, and recovery guidance. |

## Scope Guardrails

- Do not make normal DMS API correctness depend on `dms.DocumentCache`.
- Do not use `dms.DocumentCache` for authorization or identity resolution.
- Do not create profile-specific, link-free, or consumer-specific cache rows.
- Do not add projection queues/enqueue APIs, backfill epochs, progress cursors,
  projector-state tables, failure tables, dead-letter handling, or manual repair state.
- Do not add a CDC-specific projector mode, per-document delete lock, pre-delete
  reconstitution, or mutation gate.
- Do not move connector key/filter/routing logic into this epic.
- Do not publish Kafka messages directly from DMS.
- Do not expose `DocumentId` as a public Kafka identifier.

## Completion Criteria

- DMS can run without projection when standalone DocumentCache and read acceleration are
  disabled and `KafkaCdc:Targets` is empty.
- A hosted supervisor runs isolated reconciliation loops for the startup projection
  target set without request-scoped routing dependence.
- One loop per data store handles empty-cache population, ongoing writes, restart,
  rebuild, and retries by repeatedly querying current missing/version-mismatched
  rows.
- Current mismatch count and oldest mismatch age are observable per data store; zero
  mismatches is the projection-completeness signal for CDC.
- A successful high `ContentVersion` cannot hide an outstanding lower-version mismatch.
- Guarded writes cannot overwrite newer cache rows or recreate cache rows after canonical
  deletion.
- Cache deletion/rebuild never blocks API deletion and never represents domain deletion.
- PostgreSQL and SQL Server coverage proves reconciliation, read, fencing, health, and
  restart behavior without persistent workflow state.
