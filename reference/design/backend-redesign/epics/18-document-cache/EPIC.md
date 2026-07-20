---
jira: TBD
source_spike: DMS-1246
related:
  - DMS-1245
---

# Epic: `dms.DocumentCache` Projector and CDC Source Guarantees

## Description

Implement `dms.DocumentCache` as the optional materialized JSON projection defined by DMS-1246:

- [`0001-role-and-enablement.md`](../../design-docs/document-cache/0001-role-and-enablement.md)
- [`0002-projector-freshness-and-backfill.md`](../../design-docs/document-cache/0002-projector-freshness-and-backfill.md)
- [`0003-cdc-delete-and-downstream-guarantees.md`](../../design-docs/document-cache/0003-cdc-delete-and-downstream-guarantees.md)
- [`0004-failure-health-and-ddl-support.md`](../../design-docs/document-cache/0004-failure-health-and-ddl-support.md)

The epic delivers a DMS-owned projector, optional cache-backed reads, restartable backfill, stale-write
fencing, retry/dead-letter handling, health/readiness signals, and the CDC-mode delete source-row guarantees
needed by the relational CDC/Kafka epic.

`dms.DocumentCache` remains optional for ordinary DMS correctness. Authorization, write correctness, identity
resolution, Change Queries, and normal GET/query behavior continue to use relational source tables and
`dms.Document`. When Kafka CDC is enabled, `dms.DocumentCache` becomes conditionally required because Debezium
captures it as the document-state source.

## Stories

- `TBD` — `00-documentcache-configuration-and-mode-boundaries.md` — Add DocumentCache configuration and mode boundaries
- `TBD` — `01-documentcache-ddl-and-projector-state.md` — Emit DocumentCache projector state and failure DDL
- `TBD` — `02-document-materializer-service.md` — Add reusable caller-agnostic document materialization
- `TBD` — `03-async-projector-worker.md` — Add asynchronous DocumentCache projector worker
- `TBD` — `04-initial-backfill-and-rebuild.md` — Add restartable initial backfill and rebuild support
- `TBD` — `05-cache-backed-read-path.md` — Add fresh-cache read path with relational fallback
- `TBD` — `06-cdc-pre-delete-materialization.md` — Add CDC-mode pre-delete source-row materialization
- `TBD` — `07-projector-stale-write-fencing.md` — Enforce projector stale-write and post-delete fencing
- `TBD` — `08-projection-retry-dead-letter-and-repair.md` — Add projection retry, dead-letter, and repair handling
- `TBD` — `09-documentcache-health-readiness-and-telemetry.md` — Add DocumentCache health, readiness, and telemetry
- `TBD` — `10-provider-cdc-delete-verification.md` — Verify provider CDC delete source-row behavior
- `TBD` — `11-documentcache-integration-tests-and-runbooks.md` — Add integration coverage and DocumentCache runbooks

## Cross-Story Dependency Notes

- Story 00 is the configuration gate. Later stories consume its `Disabled | Async | CdcRequired` projector mode
  and separate read-cache/CDC enablement settings. In v1 these process-wide settings apply to every loaded data
  store with a usable connection string.
- Story 01 should be implemented before Stories 03, 04, 08, and 09 because those stories persist or inspect
  projector state and failure rows.
- Story 02 is the common materialization path for the projector, read-through fallback, and CDC pre-delete
  materialization. It depends on the relational read path and update-tracking metadata semantics.
- Stories 03 and 04 can proceed in parallel after Stories 01 and 02. Story 04 owns bounded initial
  backfill/rebuild epoch readiness; Story 03 owns the non-HTTP multi-instance supervisor over the fixed startup
  inventory and ongoing asynchronous catch-up above each database's captured backfill target.
- Story 05 depends on Stories 00 and 02. It can ship before the projector is fully CDC-ready because cache-backed
  reads must fall back to relational reconstitution on misses or stale rows.
- Stories 06 and 07 are tightly coupled. Story 06 wires the delete path; Story 07 makes the write guards strong
  enough that projector retries/backfill cannot overwrite newer rows or recreate rows after delete.
- Story 08 depends on Stories 01, 03, and 07 so retries and dead letters use durable state and preserve fencing.
- Story 09 depends on Stories 01, 04, 06, 07, and 08 because readiness must report backfill, delete support,
  stale-write fencing, lag, and unresolved current projection failures.
- Story 10 depends on Stories 06 and 07 and should be completed before CDC/Kafka deletes are considered
  supported for PostgreSQL or SQL Server.
- Story 11 can start with draft docs and fixture tests, but final runbooks and integration coverage depend on
  Stories 00-10.

## Dependency Matrix with `17-cdc-kafka`

| `17-cdc-kafka` story | Depends on `18-document-cache` | Dependency type | Notes |
| --- | --- | --- | --- |
| `17-00-documentcache-cdc-prerequisites.md` | 18-00, 18-01, 18-04, 18-06, 18-07, 18-08, 18-09, 18-10 | Hard | This supplies registration prerequisites and completed source readiness from DocumentCache configuration, state, backfill, pre-delete materialization, fencing, failures, health, and provider verification. |
| `17-01-cdc-ddl-support.md` | 18-01, 18-10 | Hard for final verification | CDC key/replica setup can start from the existing `DocumentCache` shape, but final provider proof depends on the projected table, state DDL, and delete-source verification. |
| `17-02-connector-template-generation.md` | 18-01, 18-10 | Soft until smoke tests | Connector templates can be built with fixture records, but final delete/tombstone smoke coverage needs provider-verified `DocumentCache` deletes. |
| `17-03-bootstrap-enable-kafka-cdc.md` | 18-00, 18-04, 18-09, 18-10, plus 17-00 | Hard | Bootstrap validates registration prerequisites before connector creation and waits for completed source readiness afterward. |
| `17-04-message-contract-tests.md` | 18-02, 18-06, 18-07, 18-10 | Mixed | Fixture-only tests can start earlier. Source-level delete tests require materialization, fencing, and provider verification. |
| `17-05-e2e-kafka-scenarios.md` | 18-00, 18-03, 18-04, 18-06, 18-07, 18-09, 18-10, plus 17-00 through 17-04 | Hard | API-driven create/update/delete Kafka scenarios need the projector, readiness, immediate-delete path, and provider proof. |
| `17-06-ops-docs-runbooks.md` | 18-08, 18-09, 18-11 | Hard for final docs | CDC runbooks must document DocumentCache retry/dead-letter, health/readiness, recovery, and delete blocking behavior. |

## `18-document-cache` Outputs Consumed by `17-cdc-kafka`

| `18-document-cache` story | Unblocks / informs `17-cdc-kafka` |
| --- | --- |
| 18-00 | CDC/read-cache configuration boundaries for 17-00 and 17-03. |
| 18-01 | Provisioned source table, projector companion state, and DDL inventory for 17-00 and 17-01. |
| 18-02 | Canonical `DocumentJson`, `ContentVersion`, and `LastModifiedAt` materialization for 17-04 fixtures and 17-05 E2E assertions. |
| 18-03 | Fixed-inventory multi-instance projector lifecycle, ongoing projection, and lag semantics for 17-00, 17-05, and 17-06. |
| 18-04 | Bounded initial backfill epoch completion signal for 17-00 and 17-03. |
| 18-05 | Optional cache read behavior; no hard CDC dependency, but documents the non-CDC fallback boundary used by 17-06. |
| 18-06 | CDC-mode delete source-row guarantee for 17-00, 17-04, and 17-05. |
| 18-07 | Stale-write and post-delete fencing for 17-00, 17-04, and 17-05. |
| 18-08 | Projection failure/dead-letter state for 17-00, 17-03 diagnostics, and 17-06 runbooks. |
| 18-09 | Per-data-store readiness and telemetry surface consumed by 17-00, 17-03, 17-05, and 17-06. |
| 18-10 | PostgreSQL/SQL Server proof that source-row deletes produce observable CDC deletes for 17-01, 17-04, and 17-05. |
| 18-11 | DocumentCache operator guidance consumed by 17-06. |

## Scope Guardrails

- Do not make normal DMS API correctness depend on `dms.DocumentCache`.
- Do not use `dms.DocumentCache` for authorization or identity resolution.
- Do not create profile-specific, link-free, or consumer-specific cache rows.
- Do not move Kafka connector template generation or connector registration into this epic.
- Do not publish Kafka messages directly from the DMS request path.
- Do not use Change Queries as the Kafka source.
- Do not expose `DocumentId` as a public Kafka identifier.

## Completion Criteria

- DMS can run with `dms.DocumentCache` disabled when neither read acceleration nor Kafka CDC is enabled.
- DMS can run with asynchronous projection for cache-backed reads/indexing while falling back to relational
  reconstitution for cache misses, stale rows, or projector failures.
- A hosted supervisor explicitly runs projector execution contexts for every target in the fixed startup
  tenant/data-store inventory without depending on request-scoped routing; shared work identity includes the
  data-store boundary. Inventory changes require an explicit configuration change and restart.
- DMS can enter CDC-required projection mode once registration prerequisites are satisfied, while completed
  DocumentCache source readiness remains false until the bounded initial backfill epoch is complete, projector
  lag above the captured backfill target is visible and within threshold, no unresolved current projection
  failures remain, stale-write fencing is active, and provider-specific delete-source behavior is verified.
- CDC-mode deletes cannot remove `dms.Document` unless the delete transaction has verified or materialized the
  `dms.DocumentCache` source row needed for a Debezium row delete.
- Projector, backfill, retry, read-through, and pre-delete materialization writes cannot overwrite newer cache
  rows or recreate cache rows after delete.
- PostgreSQL and SQL Server coverage proves the DocumentCache projector/read/delete contracts and identifies any
  provider-specific CDC readiness limitation.
