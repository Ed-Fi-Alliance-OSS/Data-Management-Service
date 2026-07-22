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
schema and provider DDL, configuration and target selection, materialization, monotonic
writes, reconciliation, optional read acceleration, health/telemetry, provider tests, and
runbooks, plus an out-of-band representation-restamp utility for offline strong-validator
repairs. The CDC epic consumes this projection for upserts and
independently owns connector lifecycle deletes.
The small `dms.DocumentCache` table plus `dms.DataStoreIdentity` and
`dms.DocumentCacheState` singletons are always provisioned; optionality applies to
projection execution and cache-backed reads. V1 delivers this schema only through
create-only provisioning of new physical databases; upgrading an already-provisioned
database is outside this epic.

## Stories

- `TBD` — `00-documentcache-schema-and-provider-ddl.md` — Finalize schema and provider DDL
- `TBD` — `01-documentcache-configuration-and-target-selection.md` — Add configuration and target selection
- `TBD` — `02-document-materializer-service.md` — Add reusable caller-agnostic document materialization
- `TBD` — `03-monotonic-cache-upsert-and-delete-fencing.md` — Implement monotonic cache upsert and post-delete fencing
- `TBD` — `04-async-projector-reconciliation-loop.md` — Add the asynchronous reconciliation loop
- `TBD` — `05-cache-backed-read-path.md` — Add fresh-cache reads with relational fallback
- `TBD` — `06-documentcache-health-readiness-and-telemetry.md` — Add projection health and telemetry
- `TBD` — `07-documentcache-integration-tests-and-runbooks.md` — Add provider integration coverage and runbooks
- `TBD` — `08-representation-restamp-utility.md` — Add the out-of-band representation-restamp utility

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- The explicit target list selects and isolates projection, unresolved listed targets can
  become available after startup, and read acceleration selects no additional stores.
- SQL Server projection targets validate `READ_COMMITTED_SNAPSHOT ON` before source/cache
  comparison and fail only projection/cache use when it is absent or unreadable. Unlisted
  relational-only stores remain supported, DMS never changes the option at runtime, and no
  invalid observation can set the durable cache-ahead latch.
- Both providers pass coherent materialization with a final optimistic current-version
  check, reconciliation, atomic monotonic-upsert, delete-fence, restart, target-scoped
  failure backoff and database rediscovery, rebuild, health, and read-fallback integration
  coverage.
- In-process work uses documented implementation-tuned defaults, serialized per-target
  loops, bounded pages, fair process-wide target concurrency, coalesced audits, and
  observational health/readiness checks.
- Every projected row carries a `StreamEtag` produced by the shared DMS served-ETag
  composer for the fixed CDC representation; API reads continue to compose their own
  request-specific validators.
- Core DDL always emits `dms.DocumentCache` with `StreamEtag`, its supporting
  `dms.Document(ContentVersion, DocumentId)` index, and the `dms.DataStoreIdentity` and
  `dms.DocumentCacheState` singletons; no obsolete `DocumentCache.Etag` remains.
- New-database provisioning is the only supported v1 delivery path. No completion evidence
  claims an in-place schema upgrade or DocumentCache/CDC enablement for an
  already-provisioned database.
- `DocumentCache` retains one compact `DocumentId` primary/foreign-key index. Its
  non-indexed `DocumentUuid` is copied from the canonical row and provider validation
  triggers reject mismatches without adding a cache UUID index or a composite index to
  `dms.Document`.
- Exact source/cache differences establish repairable projection work without a durable
  workflow. Missing and behind rows repair automatically; observing an ahead row durably
  latches the database, disables cache reads and writes, and requires explicit CDC-aware
  full-cache recovery even if the source later reaches the same version.
- Projection absence or failure never compromises canonical API behavior or deletion.
- Byte-changing implementation corrections that would otherwise reuse a strong ETag use
  the dedicated out-of-band utility only while the selected data store is explicitly
  offline and all DMS replicas and external writers have been stopped outside the utility.
  The utility does not implement or certify that fence. It advances canonical content
  stamps and mirrors for the explicit affected scope, is safely resumable across bounded
  batches, and leaves ordinary reconciliation and CDC to publish corrected higher-version
  state eventually without claiming another exact CDC baseline.
- Runbooks describe implemented operation and link to the authoritative design.

Anything excluded or deferred by the authoritative design is outside this epic unless a
new decision record changes that design.
