---
jira: TBD
source_spike: DMS-1246
related:
  - DMS-1245
---

# Epic: `dms.DocumentCache` Projection

## Design References

- [Authoritative projection and CDC design](../../../cdc-streaming.md)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Implement the reusable optional document projection defined by the design references:
configuration and target selection, materialization, reconciliation, monotonic writes,
optional read acceleration, health/telemetry, provider tests, and runbooks. The CDC epic
consumes this projection for upserts and independently owns connector lifecycle deletes.
The small `dms.DocumentCache` table plus `dms.DataStoreIdentity` and
`dms.DocumentCacheState` singletons are always provisioned; optionality applies to
projection execution and cache-backed reads.

## Stories

- `TBD` — `00-documentcache-configuration-and-target-selection.md` — Add configuration and target selection
- `TBD` — `01-document-materializer-service.md` — Add reusable caller-agnostic document materialization
- `TBD` — `02-async-projector-reconciliation-loop.md` — Add the asynchronous reconciliation loop
- `TBD` — `03-cache-backed-read-path.md` — Add fresh-cache reads with relational fallback
- `TBD` — `04-monotonic-cache-upsert-and-delete-fencing.md` — Implement monotonic cache upsert and post-delete fencing
- `TBD` — `05-documentcache-health-readiness-and-telemetry.md` — Add projection health and telemetry
- `TBD` — `06-documentcache-integration-tests-and-runbooks.md` — Add provider integration coverage and runbooks

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- The explicit target list selects and isolates projection, unresolved listed targets can
  become available after startup, and read acceleration selects no additional stores.
- Both providers pass coherent materialization with a final optimistic current-version
  check, reconciliation, atomic monotonic-upsert, delete-fence, restart, retry, rebuild,
  health, and read-fallback integration coverage.
- In-process work uses documented implementation-tuned defaults, serialized per-target
  loops, bounded pages, fair process-wide target concurrency, coalesced audits, and
  observational health/readiness checks.
- Every projected row carries a `StreamEtag` produced by the shared DMS served-ETag
  composer for the fixed CDC representation; API reads continue to compose their own
  request-specific validators.
- Core DDL always emits `dms.DocumentCache` with `StreamEtag`, its supporting
  `dms.Document(ContentVersion, DocumentId)` index, and the `dms.DataStoreIdentity` and
  `dms.DocumentCacheState` singletons; no obsolete `DocumentCache.Etag` remains.
- `DocumentCache` retains one compact `DocumentId` primary/foreign-key index. Its
  non-indexed `DocumentUuid` is copied from the canonical row and provider validation
  triggers reject mismatches without adding a cache UUID index or a composite index to
  `dms.Document`.
- Exact source/cache differences establish repairable projection work without a durable
  workflow. Missing and behind rows repair automatically; observing an ahead row durably
  latches the database, disables cache reads and writes, and requires explicit CDC-aware
  full-cache recovery even if the source later reaches the same version.
- Projection absence or failure never compromises canonical API behavior or deletion.
- Runbooks describe implemented operation and link to the authoritative design.

Anything excluded or deferred by the authoritative design is outside this epic unless a
new decision record changes that design.
