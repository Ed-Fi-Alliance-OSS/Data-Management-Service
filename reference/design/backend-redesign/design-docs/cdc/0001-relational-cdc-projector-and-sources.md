---
status: proposed
date: 2026-07-20
jira:
  - DMS-1245
  - DMS-1246
related:
  - DMS-1232
  - DMS-1089
---

# Decision Record: Relational CDC Projector and Sources

## Decision

DMS owns one asynchronous `dms.DocumentCache` projector. The cache is rebuildable
projected state, not canonical persistence. Capabilities that need projected documents
select the projector; ordinary DMS correctness does not depend on it.

One Debezium connector uses two complementary document sources plus one internal
source-position heartbeat source:

| Source event | Public document-state result |
| --- | --- |
| `dms.DocumentCache` create, update, or snapshot/read | Document upsert |
| `dms.Document` delete | Kafka tombstone |
| `dms.DocumentCache` delete or truncate | Ignore |
| Any other `dms.Document` operation or snapshot/read | Ignore |
| Any `dms.CdcHeartbeat` operation or Debezium heartbeat | Ignore; advance only the internal source offset |

`dms.DocumentCache.DocumentJson` supplies the caller-agnostic API-shaped upsert payload,
and `dms.DocumentCache.StreamEtag` supplies the DMS-computed ETag for that fixed stream
representation. `dms.Document` supplies the authoritative lifecycle delete and stable
`DocumentUuid`. Cache deletion has no domain meaning.

`dms.DocumentCache` retains `DocumentId` as its compact primary/foreign key and stores
`DocumentUuid` as a non-indexed denormalized connector-key column. Provider-specific cache
insert/update triggers compare that value with `dms.Document.DocumentUuid` through the
existing `DocumentId` primary key and reject a mismatch. Canonical `DocumentUuid` is
immutable, the parent already enforces its uniqueness, and the cache permits one row per
`DocumentId`; therefore both captured sources have the same logical UUID key without a
cache UUID index or a new composite index on the canonical table.

The projector uses the current source/cache difference as both durable work inventory
and completeness evidence. Row-level freshness is exactly `ContentVersion` equality with
the current `dms.Document.ContentVersion`. `LastModifiedAt` remains payload/diagnostic
metadata; `ComputedAt` remains operational metadata. Neither is another freshness test.
A version-equal row is nevertheless ineligible for use while the database singleton
`dms.DocumentCacheState.CacheAheadRecoveryRequired` latch is set.
Frequent candidate discovery uses a disposable process-local content-version cursor;
periodic full source/cache anti-join audits remain the only completeness proof.
The in-process projector uses one serialized loop per target, bounded pages, and a
process-wide target-concurrency gate. Incremental and audit intervals, page size,
concurrency, and maximum ready audit age are configurable with implementation-tuned
defaults; this record owns their scheduling and coalescing semantics.
For a SQL Server projection target, `READ_COMMITTED_SNAPSHOT ON` is a required provider
prerequisite. Every source/cache classification uses one `READ COMMITTED` statement after
validating that option on the same open target connection. A failed or unreadable validation
stops projection and cache use for that target before classification, so it cannot create a
durable cache-ahead incident from locking `READ COMMITTED`. This does not make RCSI a
requirement for unlisted relational-only SQL Server data stores, and runtime DMS never
changes the database option.

Missing cache rows and rows whose version is behind the canonical source are ordinary
repair work. A cache row whose version is ahead of the canonical source is instead an
invariant violation: the projector atomically sets the durable per-database recovery
latch, makes all cache rows ineligible for reads and further projection writes, makes
readiness false, and neither repairs nor overwrites the row automatically. Supported
same-source writes and monotonic projection cannot produce that state, so it indicates
cache corruption, an in-place source restore/reset, or unsupported reuse of projected
state against another canonical source. A later equal source/cache version never clears
the latch. Internal-only recovery clears the full cache and latch in one transaction and
rebuilds it. If a connector or another ordered downstream consumer may have observed the
higher version, v1 stops the old publication path, retains the latch, and remains not ready.
Safe recovery would require a new downstream state namespace; for Kafka CDC that future
workflow requires a new binding generation, topic, consumer state namespace, and snapshot.
That baseline-replacing workflow is deferred from v1, and the lower canonical version is
never published as an in-place correction to the old namespace.

Cache writes deliberately do not use a source-row commit-order fence. After materialization,
an optimistic current-visibility statement rejects the result if the current source row is
missing or its `ContentVersion` no longer matches the captured version. It must not reuse a
repeatable/snapshot view fixed before hydration. This prevents a source change committed
during multi-result-set reconstitution from producing mixed-version cache content. The
statement requests no update/write lock and carries no lock acquired by that check into the
cache transaction, although ordinary provider read locking may still block briefly. A short
single-document transaction then inserts a missing cache row or updates only a lower cache
`ContentVersion`; it never takes a PostgreSQL `FOR NO KEY UPDATE`, SQL Server `UPDLOCK`, or
another write-conflicting `dms.Document` lock as a commit-order fence. The cache transaction
may still acquire ordinary provider-specific parent-row or key locks through foreign-key
enforcement and the UUID-validation trigger; those integrity locks remain required and are
not the rejected content-version fence. An older coherent materialization may therefore
still commit after a newer canonical version. It cannot replace a higher cache row and
remains ordinary cache-behind work for reconciliation. The cache foreign key remains the
post-delete fence. The same optimistic check and monotonic upsert support ordinary
reconciliation and optional direct fill after relational read fallback.

Initial population, restart, rebuild, and readiness require a full audit. At startup or
restart, the projector captures the initial incremental boundary before that audit and
resumes incremental scanning from exactly that pre-audit key, never from a later maximum
that could skip a post-audit commit. Steady-state catch-up uses the incremental cursor and
the required `dms.Document(ContentVersion, DocumentId)` index. The cursor is never durable
work inventory or readiness evidence because sequence allocation is not transaction
commit order and cache work can appear below it. V1 adds no durable projection queue,
progress/high-watermark, backfill epoch, retry record, or database-backed repair workflow.
The one-bit cache-ahead latch is durable invariant safety state, not work inventory or
completeness evidence. An exact zero finishing audit count is projection completeness at
its observation only when that latch is clear; connector/source-position catch-up is a
separate deployment-owned CDC readiness concern. Because a lower-version transaction can
commit below the cursor after a live audit, initial combined readiness uses a fresh startup
audit while the newly provisioned database is still offline and has never admitted a
canonical mutation. V1 does not implement a cross-replica admission gate or transaction
drain and makes no exact-baseline claim after first-write admission.

API deletion remains independent of projection. It deletes the canonical relational
document and lets the connector derive the tombstone from that delete. It does not wait
for or materialize cache state. A tombstone without a preceding projected upsert is valid
state-stream behavior.

Deployment-owned initial readiness uses the captured heartbeat only to establish a provider
source-position barrier after a fresh startup exact-zero projection audit on that new
offline database. Later
operational status remains eventual-consistency health and does not claim another exact
baseline. PostgreSQL compares the database WAL position with Debezium's committed
completely-processed LSN; SQL Server
compares a post-audit heartbeat change's commit/change LSN position with the committed
connector offset. Running/task state or lag alone is not barrier evidence. The
integration design defines the provider capture and comparison algorithms.

This record owns the cached projection contract, reconciliation algorithm, and cache/domain
lifecycle detailed below. Physical relational objects are owned by
[data-model.md](../data-model.md). Configuration, health integration, provider deployment,
readiness, and operations are specified in
[Relational CDC and Document Projection](../../../cdc-streaming.md). The public record and
consumer contract is specified in
[0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md).

## Cached Document Contract

`dms.DocumentCache` contains one caller-agnostic, pre-profile, full API-shaped projection
per current document. Its physical columns, keys, constraints, indexes, and provider
validation triggers are defined only in
[`data-model.md`](../data-model.md#6-dmsdocumentcache-always-provisioned-optional-projection).
This record defines how the projector populates and uses that row.

`DocumentJson` is produced by the same relational read-plan and reconstitution rules as
GET/query response assembly. It includes stable top-level `id` and
`_lastModifiedDate`. When link injection is compiled into the read plan, it also includes
reference `link` subtrees. It does not contain authorization arrays, EdOrg hierarchy
JSON, API client identity, or readable-profile-specific projections.
`_lastModifiedDate` uses the existing DMS whole-second UTC
`yyyy-MM-ddTHH:mm:ssZ` representation. The `LastModifiedAt` cache column retains the
provider value, but its fractional seconds are deliberately not exposed in `DocumentJson`
or the public CDC envelope and do not participate in freshness or ordering.

The cache does not store an `_etag` inside `DocumentJson` and does not store an ETag
reusable for arbitrary API responses. It does store `StreamEtag`, the ETag for the fixed
CDC representation of that document kind. The cache-projection materializer computes it
by calling the same DMS served-ETag composer used by the API with the row's
`ContentVersion`, the selected mapping set's `EffectiveSchemaHash`, JSON format, no
readable profile, the published document's link mode, and identity content coding.
Ordinary resource documents are link-bearing in the cache and therefore use `l` even
when `DataManagement:ResourceLinks:Enabled` is false; descriptors use the backend's
descriptor representation context and therefore use `n`.

API serving ignores `StreamEtag` and composes `_etag` from `ContentVersion` and the
request's active `variantKey`. Readable-profile projection and
`DataManagement:ResourceLinks:Enabled` stripping happen after cache retrieval and do not
create additional cache rows.

A dedicated cache-projection materializer returns the row metadata and `DocumentJson` as
one coherent result. Before a cache write it validates:

```text
DocumentCache.DocumentUuid == Document.DocumentUuid for DocumentId
DocumentJson.id == DocumentUuid
DocumentJson._lastModifiedDate == formatted LastModifiedAt
StreamEtag == DMS served-etag composition for the fixed stream representation
```

An application invariant failure is a materialization failure and produces no cache write;
the data-model-owned provider trigger remains an independent physical backstop for a
mismatched denormalized UUID.
`LastModifiedAt` is sourced from `dms.Document.ContentLastModifiedAt`; this payload
invariant does not make the timestamp a freshness condition.

## Freshness and Reconciliation

`ContentVersion` is the sole cache freshness and reconciliation key:

```text
DocumentCache.ContentVersion == Document.ContentVersion
```

`LastModifiedAt` is payload and diagnostic metadata. `ComputedAt` is operational
metadata. Neither participates in cache freshness, completeness, API semantics,
Change Queries, or `_etag` state. `StreamEtag` is derived output validated during
materialization; comparing or recomputing it is not another reconciliation predicate.
Row-level freshness remains version equality, but no cache row is eligible for reads or
projection readiness while the database's durable cache-ahead recovery latch is set.

The current database difference remains both the durable work inventory and the
projection-completeness source:

```text
dms.Document
LEFT JOIN dms.DocumentCache ON DocumentId
WHERE DocumentCache.DocumentId IS NULL
   OR DocumentCache.ContentVersion <> Document.ContentVersion
```

Each source/cache comparison is one database statement; an implementation must not read
the canonical and cache versions in separate commands and classify the combined result.
On SQL Server, incremental pages, full-audit pages, the exact finishing aggregate, and
cache lookup execute explicitly at `READ COMMITTED` after validating RCSI on the same open
target connection. RCSI therefore supplies one statement-level source/cache snapshot. The
queries do not use `READCOMMITTEDLOCK` or another hint that restores locking reads. If the
prerequisite is false or cannot be validated, the operation stops before classification and
cannot set `CacheAheadRecoveryRequired` from its observation.
SQL Server documents the statement-snapshot and locking distinction in
[`SET TRANSACTION ISOLATION LEVEL`](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql).

The projector classifies that difference rather than treating every unequal row as
ordinary repair work:

| Cache state | Meaning | Projector action |
| --- | --- | --- |
| Row missing | Repairable projection lag | Materialize and insert |
| `DocumentCache.ContentVersion < Document.ContentVersion` | Repairable projection lag | Materialize and replace |
| Versions equal | Fresh only when the database cache-ahead latch is clear | No action |
| `DocumentCache.ContentVersion > Document.ContentVersion` | Invariant violation | Do not materialize, repair, or overwrite automatically |

A cache-ahead row cannot arise from supported same-source projection and canonical-write
concurrency: canonical `ContentVersion` values advance monotonically and monotonic cache
writes never replace a higher cache version. It therefore indicates cache corruption, an
in-place/partial canonical database restore or reset, or unsupported reuse of projected
state against another canonical source. It remains part of the exact completeness
difference and is not treated as ordinary repair work. When either discovery lane observes
one, it atomically sets the singleton `dms.DocumentCacheState.CacheAheadRecoveryRequired`
bit. After classifying the row, the setter does not reclassify or cancel the incident if the
source advances before the state update; the observation is reported only after the latch
commit. That durable per-database latch makes every cache row ineligible for cache-backed
reads and makes projection readiness false. It is never cleared by a later source version,
an equal source/cache version, a zero audit, canonical deletion, or process restart. Failure
to read or persist the latch is fail-closed for cache use and projection readiness.

Once the latch is set, reconciliation and direct fill perform no cache writes for that
database. A later canonical state reaching exactly the previously ahead `ContentVersion`
therefore cannot make the possibly corrupt cached row fresh. Only the explicit proven-
internal-only recovery operation below clears the cache and latch together; possibly
published state remains latched in v1.

Candidate discovery is separate from completeness verification. For each selected data
store, the projector has two cooperating lanes:

1. A frequent incremental lane keyset-pages current `dms.Document` rows after a
   process-local `(ContentVersion, DocumentId)` cursor, ordered by those columns. Each
   page left-joins `dms.DocumentCache` by `DocumentId`. Missing and cache-behind rows
   become materialization candidates; cache-ahead rows become observed invariant
   violations. The cursor advances to the last source row examined whether the cache row
   was behind, fresh, ahead, or failed during repair. A failed page marks the target as
   requiring repair and requests a coalesced immediate full audit; no failed document or
   version identity is retained after the page is drained.
2. A periodic full-audit lane evaluates the complete source/cache anti-join above. It
   repairs every missing or cache-behind row it finds, including rows below the
   incremental cursor, and reports cache-ahead rows without trying to overwrite them. It
   finishes with one exact aggregate observation of total unresolved, missing-row,
   cache-behind-row, and cache-ahead-invariant counts plus the oldest unresolved source
   timestamp. Startup, restart, cache rebuild, and any attempt to establish readiness
   require a completed full audit.

The logical incremental page is:

```text
dms.Document
LEFT JOIN dms.DocumentCache ON DocumentId
WHERE (Document.ContentVersion, Document.DocumentId) > incremental cursor
ORDER BY Document.ContentVersion, Document.DocumentId
LIMIT bounded source-row batch
```

The row-value cursor predicate is logical notation; each provider may render its
equivalent scalar predicate and bounded-fetch syntax. The page returns source/cache
version pairs even when they match so the cursor can advance across source rows another
replica has already projected.

A full repair audit may use one provider-optimized anti-join or bounded keyset pages of
source/cache version pairs, but one audit pass must cover the complete current
relationship. Bounded paging must carry an audit-local scan position forward even when a
candidate repair fails, so one failed document neither prevents later source rows from
being examined nor causes every later page to rescan an already-examined prefix. An audit
may race with writes; monotonic upserts make its repairs safe. A nonzero finishing aggregate
causes another backed-off repair pass for missing and cache-behind rows. A cache-ahead count
sets the durable recovery latch, preserves unhealthy readiness, and waits for the explicit
recovery described below. The full-audit interval is bounded so it also bounds discovery
latency for repairable work that the incremental lane cannot see.

For each candidate from either lane, the projector:

1. Captures `(DocumentId, ContentVersion)` and the source metadata needed by the
   materializer.
2. Reconstitutes the caller-agnostic cached document without requesting an update/write
   source-row lock or carrying a lock acquired by materialization into the later cache
   transaction.
3. After all materialization reads finish, re-reads the current source
   `(DocumentId, ContentVersion)` in a new current-visibility statement. The statement
   requests no update/write lock, and any read lock acquired by that statement is not
   carried into the cache transaction. A missing row or version mismatch is a stale skip
   and produces no cache write.
4. Validates the embedded/relational metadata invariant and composes the cache result.
5. Performs the shared monotonic cache upsert only if the durable cache-ahead latch remains
   clear in that cache transaction.

The final source-version read is an optimistic materialization-coherence check, not a
source/cache commit-order fence. Reconstitution hydrates multiple relational result sets;
the check prevents a source change committed during those reads from producing a mixed
document labeled with the captured version. It must observe current committed state rather
than reuse a repeatable/snapshot view fixed before hydration, and it does not request or
retain an update/write lock. Ordinary provider read locking may still block briefly when
row-versioned reads are unavailable. A source change may commit after the check and before
the cache upsert, which is the intentional monotonic-lag race defined below. Reconciliation
and optional direct fill use the same check.

The incremental cursor is an optimization, never durable work inventory or completeness
evidence. `ContentVersion` values come from a monotonic sequence, but sequence allocation
is not transaction commit order: a transaction can commit a lower allocated version
after the cursor has advanced past it. Cache-row loss or truncation can likewise create
work below the cursor. Full audits are therefore required even when incremental scans
are continuously successful.

Before the required startup or restart audit begins, the projector observes the maximum
current `(ContentVersion, DocumentId)` source key as that execution context's initial
incremental boundary; an empty source uses the logical minimum key. After the audit
finishes, the incremental cursor starts at exactly that pre-audit boundary. It must not
be initialized from a later maximum: a source change committed after the audit's
finishing observation but before incremental scanning begins must remain above the
boundary and be discovered by the incremental lane. A transaction that allocated a key
at or below the boundary but commits after the audit remains the acknowledged late-commit
case repaired by the next full audit. Restart may repeat work but cannot advance the
cursor over post-boundary work.

V1 deliberately does not establish a source/cache commit-order fence. Optional projection
must not acquire a PostgreSQL `FOR NO KEY UPDATE`, SQL Server `UPDLOCK`, or another
write-conflicting lock on `dms.Document` that can make a canonical writer wait for a cache
upsert. Cache-row transitions and consumer-applied non-null upserts are monotonic, and the
stream is eventually convergent rather than linearizable to the canonical source at each
cache commit. Raw Kafka delivery remains at-least-once and may contain duplicates or
lower-version replays; the consumer ordering rule handles ordering among non-null upserts.
A replay may also temporarily place an older upsert after a tombstone because the null
tombstone carries no `contentVersion`; the subsequent replayed tombstone restores deleted
state. V1 promises convergence after connector catch-up, not monotonic applied state across
that delete boundary.

Consequently, this ordinary race is allowed:

1. The projector captures and coherently materializes source version 10, and its final
   optimistic source-version check still observes version 10.
2. A canonical writer commits source version 11.
3. The projector commits cache version 10 because the cache row is absent or lower.
4. Incremental discovery or a full audit subsequently projects version 11.

The version-10 row is ordinary monotonic projection lag. A fresh-cache read rejects it
because source and cache versions differ. A downstream consumer that has not yet observed
version 11 may temporarily retain version 10; that is an explicit consequence of the v1
eventual-consistency contract. Once version 11 has been published, neither the database
upsert nor the consumer ordering rule permits version 10 to replace it. A downstream use
case that requires every upsert to have been canonical-current at its database commit
needs a stronger publication design rather than an implicit projector lock.

Materialization and invariant validation finish before the cache transaction begins. V1
then performs one candidate per short transaction. The transaction reads the singleton
cache-ahead latch under a provider-equivalent shared row lock, compatible across ordinary
cache writers but conflicting with setting or clearing the latch:

1. If the latch is set or its singleton row is missing/malformed, perform no cache write.
2. Serialize concurrent cache writers on the `DocumentCache(DocumentId)` row/key and
   evaluate the version predicate atomically in the cache DML against the current cache
   row after any conflicting cache writer. An application pre-read must not decide whether
   a later unconditional insert or update is safe.
3. Insert the cache row when absent or update it only when its existing `ContentVersion`
   is lower than the captured version.
4. Treat a same-version row as already fresh and a higher cache version as superseding the
   candidate. Neither receives a write. A higher-than-candidate result does not by itself
   establish that the cache is ahead of the current canonical source; that classification
   comes only from the source/cache comparison used by reconciliation and health.
5. Persist the complete cache row atomically. The UUID validation trigger rejects an
   inconsistent denormalized identity, and the `DocumentCache(DocumentId)` foreign key is
   the post-delete fence.
6. Commit without locking `dms.Document` against concurrent version updates. If the
   canonical version advanced, the resulting cache-behind row remains durable repair work.

PostgreSQL and SQL Server implement equivalent conditional insert/update behavior without
`FOR NO KEY UPDATE`, `UPDLOCK`, or a stronger lock on the source `dms.Document` row.
PostgreSQL may use an atomic `INSERT ... ON CONFLICT ... DO UPDATE ... WHERE` against the
cache key. SQL Server uses an equivalent transactionally safe conditional update/insert
that serializes an absent or existing cache key; a duplicate-key race is retried by
re-evaluating the current cache version. A provider implementation must not use a
read-then-unconditional-write pattern. A cache upsert may still encounter ordinary database
failures and uses the projector's target-scoped backoff and database rediscovery path, but
v1 adds no source-lock timeout, lock-wait telemetry, or deadlock policy specific to
projection.
The cache transaction is not lock-free with respect to `dms.Document`: foreign-key
enforcement and the UUID-validation trigger may acquire their ordinary provider-specific
parent-row or key locks. Those integrity locks are not an explicit write-conflicting
content-version fence and must remain intact.
Optional direct fill uses the same optimistic source-version check and monotonic upsert. It
never waits for a source-row content-version fence, but ordinary cache-row, foreign-key,
trigger, or database contention can still occur. A short direct-fill-specific database
deadline bounds that optional request-path work end to end. The deadline is not renewed per
statement or retry; each database operation uses only the remaining budget, and direct fill
does not change the projector's target-scoped failure or backoff state. Timeout, contention,
failure, or a concurrent canonical change abandons the fill without failing the relational
response.

Except for the singleton cache-ahead safety latch, there are no projection queues, enqueue
APIs, persisted cursors, backfill epochs, per-document projector/failure rows, retry
classifications, dead-letter transitions, or requeue APIs in v1. The process-local
incremental and audit cursors are disposable scan positions only. Empty-cache population,
ordinary truncation/rebuild, repairable recovery, and completeness all derive from the
current database difference. A maximum scanned or projected version cannot prove
completeness because a lower current version may still be missing. Cache-ahead recovery is
the exceptional operator procedure below, not another projector workflow.

Repair failures use capped in-memory exponential backoff with jitter scoped to the target
execution context, never to a document or version. A failed incremental page marks the
target as requiring repair, discards its candidates after draining the page, and delays the
coalesced immediate full audit. During a full audit, candidate failures remain only in the
current page; the audit advances through later pages, then a nonzero exact finishing
aggregate schedules another repair pass subject to the same target backoff. The current
database difference therefore rediscovers every failed candidate without a process-local
document retry map. Only a later exact-zero finishing audit clears the target's
repair-required observation and resets its failure backoff. Restart loses this process-local
state but immediately requires a new startup audit, so readiness cannot rely on the lost
observation. V1 has no retry budget or persisted attempt count; a persistent failure remains
visible in database state until its underlying data, mapping, or service cause is fixed.

### Bounded In-Process Execution Policy

Projection is background work in the DMS application process. V1 uses one serialized
execution loop per resolved target and a process-wide `MaxConcurrentTargets` gate. At most
one incremental page, audit page, or candidate materialization is active for a target at a
time, and no more than the configured number of targets perform projection database/CPU
work concurrently. A page contains at most `PageSize` source rows and is fully drained
before the loop fetches another page. Candidate memory is therefore bounded by active pages,
and only constant-size scheduling, failure, and backoff state is retained for each explicit
target; no candidate or retry queue grows with document count. Waiting targets receive
permits fairly so one large rebuild cannot permanently exclude another target.

The loop applies this schedule:

1. Resolution of a configured target starts an immediate full audit. Restart and an
   explicit cache-rebuild signal do the same.
2. While no audit is due, incremental discovery runs no more frequently than
   `IncrementalScanInterval`. The same interval bounds re-resolution attempts for an
   unavailable configured target, subject to failure backoff.
3. A steady-state full audit becomes due `FullAuditInterval` after the previous full audit
   finishes. Only one audit may be queued or running for a target. Startup, rebuild, and
   periodic requests coalesce into that one audit; they never create overlapping scans.
4. A finishing aggregate with repairable differences schedules another bounded repair
   pass through the same loop rather than tight-looping. Target-scoped failure backoff and
   the concurrency gate remain in force. A latched target remains unhealthy, performs no
   cache writes, and waits for either proven-internal-only recovery or, for possibly
   published state, the deferred new-namespace workflow rather than scheduling futile repair.
5. Health and readiness reads are observational. They do not start or wait for an audit.
   Until the immediate startup/rebuild audit completes, or when the latest exact audit is
   older than `MaximumAuditAge`, the target is simply not ready.

Cancellation is observed between pages and candidates and while waiting for the global
gate. Shutdown does not begin new work and allows only the current short monotonic cache
transaction to finish or roll back within its existing command timeout. One target's
failure or cancellation does not stop peer loops.

These settings make the operational bound explicit without pretending an audit is
instantaneous: repairable work invisible to the incremental cursor begins discovery no
later than one configured `FullAuditInterval` after the prior audit completed, then takes
the duration of its bounded audit/repair pass. If sustained load makes audits slower than
their interval or older than `MaximumAuditAge`, readiness becomes false and telemetry
shows the overdue/in-progress audit; the supervisor does not add parallel audits to catch
up.

### Cache-Ahead Invariant Recovery

The projector never automatically lowers a cache row from a higher `ContentVersion` to
the current canonical version. Doing so would make the relational cache appear fresh
while an active or previously active CDC topic could retain the higher version; conforming
consumers may reject the lower replacement as stale. The durable cache-ahead latch also
prevents a later equal canonical version from silently reclassifying the existing row as
fresh.

Recovery depends on whether the projection can have entered downstream ordered state:

- If the projection is internal-only, such as read acceleration, and no downstream system
  could have observed the row, stop cache writers, clear all `dms.DocumentCache` rows and
  the latch in one provider transaction, then let ordinary reconciliation rebuild from
  canonical state. The global rebuild avoids retaining document identifiers or trusting
  that the observed row was the only corrupt row.
- If an active or historical connector or another ordered downstream consumer may have
  observed the higher cache version, v1 stops the affected publication path, keeps the
  cache-ahead latch set, and leaves projection and CDC not ready. Safe recovery would
  require a new downstream state namespace. For Kafka CDC, that future workflow requires a
  new immutable binding generation, public and progress topics, a SQL Server schema-history
  topic when applicable, a consumer state namespace, and a fresh snapshot after rebuilding
  the cache. V1 does not implement that baseline-replacing workflow and never publishes the
  lower version as an in-place correction to the old namespace.
- Treat any in-place canonical database restore/reset that produces or may expose possibly
  published cache-ahead state as the deferred case above even when the physical database
  name or connection metadata did not change. Guarded source replacement by itself is not
  authority to clear this latch.

The runbook requires operators to establish which case applies before deleting projected
state. It permits the clear operation only with positive evidence that the projection was
internal-only. If downstream observation is possible or uncertain, operators contain
publication and retain the cache and latch for diagnosis. Clearing the latch without
clearing the entire cache in the same transaction is not a supported operation. No recovery
rewrites canonical `ContentVersion`, an immutable binding record, or an existing topic
generation.

E18 owns one target-scoped administrative recovery operation for the proven internal-only
case. It takes the exclusive singleton-state lock, verifies the latch is set, clears the
entire cache, clears the latch, and commits as one provider transaction; after commit it
requests an immediate full audit. It exposes no latch-only reset. Deployment/runbook
automation is responsible for proving the internal-only precondition or, when downstream
observation is possible or uncertain, stopping the old publication path and leaving the
latch set until the deferred new-namespace workflow is implemented.

The hosted supervisor creates an isolated, non-HTTP service scope for each startup
target and explicitly selects its data store. It does not depend on
`ResolveDataStoreMiddleware` or reuse request-scoped `IDataStoreSelection`. One
unavailable data store does not stop peers. Multiple DMS replicas may perform duplicate
scans safely because candidate discovery is read-only and writes are monotonic and
idempotent.
Deployments avoid redundant work by placing target entries only on designated projector
hosts. Correctness does not require a distributed lease; when more than one host is
configured for the same target, each independently applies the bounded execution policy.

## Cache-Backed Reads and Domain Lifecycle

When read acceleration is enabled, authorization and query candidate selection still
use relational sources. A cache row may supply response-body assembly only when it is
fresh and `dms.DocumentCacheState.CacheAheadRecoveryRequired` is false. Cache lookup reads
that singleton state with the row/freshness query rather than relying on process memory.
Missing, stale, or recovery-latched rows fall back to relational reconstitution;
readable-profile projection, link stripping, and served `_etag` composition then run
identically for both paths. The read path does not enqueue projection work, though it may
perform the shared monotonic direct fill after fallback as an optional optimization when
the latch is clear.

Deleting a cache row is projection maintenance and never means domain deletion. A
supported API delete continues to:

1. Resolve and authorize the canonical target.
2. Delete the concrete resource row, or descriptor row, while `dms.Document` still
   exists so Change Queries can record its tombstone.
3. Delete `dms.Document`, cascading relational cleanup including the cache row.

The API transaction does not verify cache existence or freshness, synchronously
materialize a pre-delete document, acquire a CDC-specific lock, wait for projector
readiness, or fail because projection is unavailable. Create followed by delete before
projection may therefore publish only a tombstone; state-stream consumers must tolerate
that case.

Cache truncation, eviction, cleanup, and rebuild publish no domain tombstones.
Reconciliation recreates upserts only for canonical documents that still exist. An
intentional compacted-topic rebuild would require the deferred new-topic cutover; it is not
a cache-delete operation or a v1 same-topic snapshot. Schema reprovisioning must not reuse
cache rows across incompatible effective schemas.

## Rationale

Canonical relational resource tables are normalized and do not contain the full public
JSON document. `dms.DocumentCache` already provides the stable, caller-agnostic
projection needed for CDC and optional read acceleration, but its lifecycle is unsuitable
for domain deletes because operators must be free to evict or rebuild it.

`dms.Document` already carries stable identity and authoritative lifecycle. Capturing its
deletes alongside cache upserts keeps API mutation correctness independent of projection,
makes cache maintenance safe, and avoids application/Kafka dual-write transactions.

The database difference is sufficient durable repair-work inventory. Every representation
change allocates a monotonic `ContentVersion`, making it an efficient incremental discovery
key, while full audits recover lower versions that commit late or cache rows lost below
the cursor. Timestamp comparison adds provider precision risks without adding correctness.
A monotonic idempotent upsert makes duplicate projectors and restart rediscovery safe;
refusing to lower an ahead cache row preserves the stream's non-null upsert ordering
contract. Because version equality cannot prove that a formerly ahead row now contains the
same canonical state, the singleton latch durably preserves that invariant observation
until proven-internal-only recovery or, for possibly published state, the deferred
new-namespace workflow.

This choice is conscious: cache-row transitions and consumer-applied non-null upserts are
monotonic, and the stream is eventually convergent, but raw at-least-once Kafka delivery
may contain duplicates or lower-version replays. Because a tombstone has no
`contentVersion`, replay may temporarily place an older upsert after a tombstone; the
subsequent replayed tombstone restores deleted state. V1 guarantees convergence after
connector catch-up, not monotonic applied state across that delete boundary. V1 also does
not guarantee that every cache upsert was canonical-current at its database commit. A
consumer that has not yet seen the newer version may temporarily retain the older
projection. Avoiding the stronger guarantee keeps optional projection and direct fill from
taking source-row locks that can conflict with canonical writers. A future downstream
requirement for linearizable publication requires a separate design and performance
decision.

## Consequences

- V1 projection and CDC support applies only to new physical databases provisioned with the
  completed E18 create-only schema before DMS mutations are admitted. It provides no
  in-place upgrade for an older `dms.DocumentCache` and no later CDC retrofit for an
  already-provisioned database.
- `dms.DocumentCache` is always provisioned. Every deployment-selected Kafka CDC target
  must also be an explicit DMS `DocumentCache:Targets` entry; non-target data stores leave
  the table empty and run no projector.
- A SQL Server `DocumentCache:Targets` entry is projection-eligible only while RCSI is
  validated. Failure disables projection and cache use for that target without gating its
  canonical relational API or unrelated targets; `ALLOW_SNAPSHOT_ISOLATION` is not required
  by the v1 projector.
- Authorization, identity, writes, Change Queries, and correct GET/query results continue
  to use relational sources.
- Reads may use only fresh cache rows while the durable cache-ahead latch is clear and
  always retain relational fallback.
- Projection lag and failure are observable but never gate normal API traffic.
- Initial combined CDC readiness is supported only while a newly provisioned database is
  offline and has not been published to a DMS replica or other writer. First-write admission
  waits through a fresh startup audit and the later provider publication barrier. After
  admission opens, projection and CDC status remain eventual; exact baseline replacement is
  deferred until a separately owned deployment capability can fence replicas and external
  writers and drain admitted work. DMS projection health does not activate such a gate.
- Projector and direct-fill cache writes take no explicit write-conflicting source-row lock
  as a content-version fence. They do not deliberately serialize ordinary canonical
  version updates behind cache commits; ordinary cache-row, trigger, and foreign-key
  concurrency can still block or abort one participant under provider transaction
  semantics, especially during deletion.
- Ordinary updates use indexed incremental discovery; full relationship scans are
  reserved for startup, rebuild, periodic audit, and readiness verification.
- Repair failures retain only capped target-scoped backoff and repair-required state. The
  active page bounds candidate memory, no failed document/version retry map survives page
  turnover, and full audits rediscover failures from the current database difference.
- Full audits repair missing and cache-behind rows. Cache-ahead rows durably latch the
  database, disable cache reads and writes, and keep readiness false across version
  equality and restart. V1 clears the latch only through the full-cache transaction after
  proving the projection was internal-only; possibly published state remains latched until
  the deferred CDC-aware new-namespace recovery exists.
- Cache clear/rebuild emits no domain tombstones. Equal-version rows are duplicate
  projections and do not replace consumer state; every byte-changing correction uses the
  explicitly offline representation-restamp utility and eventually publishes higher
  canonical versions when prior Kafka records do not require purging. This does not certify
  another exact CDC baseline. An incompatible-contract cutover after first-write admission
  is deferred until deployment owns the required writer fence and drain. Sensitive-data
  disclosure correction fences the connector, revokes consumer access, and requires
  verified destructive retirement of the affected binding generation; CDC remains
  unavailable afterward in v1. Loss of PostgreSQL WAL/slot or SQL Server CDC source-history
  continuity is never repaired by resnapshotting the existing topic and is an unrecoverable
  terminal condition for that binding in v1.
- Both document source tables use `DocumentUuid` as the connector key and share one
  connector task so a committed upsert preceding canonical deletion retains per-key
  order. The cache key column is non-indexed; its equality and logical uniqueness are
  consequences of the cache-validation trigger, compact `DocumentId` primary/foreign
  key, and canonical UUID uniqueness.
- DMS, not Kafka Connect or a downstream consumer, owns stream ETag derivation; the
  connector copies the projected opaque value into the public message shape.
- Consumers tolerate duplicate/replayed upserts and tombstones without a prior upsert.
  `contentVersion` rejects lower non-null state and treats equal non-null state as a
  duplicate; only a higher version replaces it. Across a tombstone, at-least-once replay
  may temporarily restore an older upsert until the replayed tombstone arrives.

## Alternatives Considered

| Alternative | Disposition |
| --- | --- |
| Capture only `dms.DocumentCache` | Rejected: projection maintenance would become domain deletion and cache failure would enter the API delete path. |
| Capture only `dms.Document` | Rejected: it has identity and stamps but no reconstituted JSON payload. |
| Capture normalized resource tables directly | Rejected: it exposes physical storage and requires consumers to reproduce joins, extensions, descriptors, and reconstitution. |
| Use Change Queries tables | Rejected: they are a polling compatibility surface, not a complete document-state payload source. |
| Add a relational outbox | Deferred until DMS needs explicit domain-event semantics rather than current document state. |
| Make cache population/use mandatory or describe it only as a read cache | Rejected: the table is always provisioned, while its optional multi-consumer projection role remains configuration-selected. |
| Configure a projector mode or separate Kafka boolean | Rejected: consuming capabilities already determine the exact target set and avoid invalid flag combinations. |
| Persist queues, epochs, progress, retry, or per-document failure rows | Rejected for v1: the current source/cache difference preserves repairable work. The singleton cache-ahead safety latch is the only durable incident state; add pending-work state only if indexed incremental-discovery and full-audit benchmarks require it. |
| Retain process-local retry entries keyed by document/version | Rejected: entry count would grow with persistent failures. Target-scoped backoff limits failure state, and full audits rediscover repairable work from the database. |
| Use the full mismatch anti-join for every steady-state poll | Rejected: it makes ordinary high-version update discovery scale with the complete document set. |
| Build JSON in database triggers | Rejected: it duplicates application reconstitution and increases provider-specific logic. |
| Add `(DocumentId, DocumentUuid)` as a canonical unique key and composite cache foreign key | Rejected: it adds a redundant wide index to the canonical `dms.Document` table for an optional projection. |
| Make cache `DocumentUuid` a primary or unique key | Rejected: it adds a random 16-byte cache index and, on SQL Server, would make a poor clustered insertion key. Trigger-enforced denormalization preserves the compact `DocumentId` key. |
| Trust only the projector to copy the right UUID | Rejected: a defective or unsupported cache writer could publish an upsert and tombstone under different keys. The cache-only validation trigger aborts the write before CDC can observe it. |
| Require synchronous read-through population | Rejected for correctness; direct fill remains an optional monotonic optimization. |
| Add a source-row commit-order fence | Rejected for v1: it would make optional projection take write-conflicting locks on canonical `dms.Document` rows. Monotonic cache upserts, fresh-read validation, reconciliation, consumer version ordering, and the cache foreign key provide the selected eventual-consistency and delete guarantees. |
| Require every cache upsert to be canonical-current at commit | Rejected for v1: a lower captured version may commit after a newer canonical version as ordinary monotonic projection lag. A consumer that has not yet observed the newer version may temporarily retain the lower state until reconciliation publishes the newer projection. |
| Use the incremental cursor, a high-watermark, `ComputedAt`, or `LastModifiedAt` as completeness evidence | Rejected: none proves that every current document is projected at its current representation version. |
| Store retry state on the cache row | Rejected: missing rows have nowhere to store it and operational fields would enter the captured row contract. |
| Automatically overwrite a cache-ahead row with the lower canonical version | Rejected: the state cannot result from supported same-source concurrency and the lower record may be ignored by consumers that retained the higher published version. V1 supports only proven internal-only cache repair; the required downstream state reset for possibly published state is deferred. |
| Clear a cache-ahead observation when source/cache versions later become equal | Rejected: equality does not prove payload identity and could make corrupt possibly published state appear fresh. V1 permits the full-cache recovery transaction only for a proven internal-only projection. |
| Fail normal reads when projection is unhealthy | Rejected: relational fallback preserves API correctness. |
| Treat connector status alone as CDC readiness | Rejected: a running connector cannot supply current documents that remain unprojected. |
| Establish an initial complete baseline under live writes with correlated canonical, audit, and publication boundaries | Deferred for v1: exact initial readiness is limited to a new offline database before first-write admission. |
| Derive tombstones from cache deletes | Rejected: cache and domain lifecycles are intentionally independent. |
| Publish from the API delete path | Rejected: it introduces a distributed write/retry boundary. |
