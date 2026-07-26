# Design Pivot Plan: Durable `DocumentCache` Projection Work

## Purpose

Amend the DMS-1245 and DMS-1246 design so projection completeness comes from durable,
transactionally recorded per-document work instead of periodic comparison of every
`dms.Document` row with `dms.DocumentCache`.

The pivot must remove full relationship scans from startup, restart, steady-state
reconciliation, projection health, and caught-up observations. An O(N) operation remains
acceptable only for an explicit baseline, cache rebuild, or operator-requested integrity
scrub.

This is a design and planning amendment. It does not implement the schema or runtime.

## Target Decision

The amended design should establish the following contract before implementation starts.

### Durable work inventory

Add an always-provisioned table equivalent to:

```text
dms.DocumentProjectionWork
--------------------------------
DocumentId                  PK, FK to dms.Document ON DELETE CASCADE
RequiredContentVersion      bigint
FirstEnqueuedAt             timestamp
LastEnqueuedAt              timestamp
```

The physical design should include:

- The compact `DocumentId` primary key.
- An index supporting fair oldest-work paging, expected to be
  `(FirstEnqueuedAt, DocumentId)`.
- No claim, lease, attempt-count, dead-letter, or per-worker ownership columns in v1.
- Provider-equivalent PostgreSQL and SQL Server types, constraints, trigger/function
  names and counts, deterministic DDL, introspection, and DB-apply coverage.

The queue coalesces repeated mutations of one document. Its maximum ordinary size is the
number of distinct documents changed while projection is behind, not the number of
mutations.

### Durable projection lifecycle state

Extend `dms.DocumentCacheState` with durable state equivalent to:

```text
ProjectionLifecycleState = Disabled | Resetting | Rebuilding | Tracking
CacheAheadRecoveryRequired
```

`ProjectionLifecycleState` is one constrained provider-equivalent value, not two
independent booleans:

- `Disabled`: transactional enqueueing, projector/direct-fill writes, and cache-backed
  reads are disabled.
- `Resetting`: transactional enqueueing remains enabled, but projector/direct-fill
  cache writes and acknowledgements and cache-backed reads are disabled while an
  administrative workflow clears projected state. This is a durable, restart-safe reset
  fence; the projection is neither operational nor caught up in this state.
- `Tracking`: transactional enqueueing is enabled and ordinary queue processing is
  allowed. Cache-backed reads remain subject to the ordinary row-freshness and safety-latch
  rules. This name alone does not imply that the projection is operational or caught up.
- `Rebuilding`: transactional enqueueing and projector/direct-fill writes remain enabled,
  but cache-backed reads are disabled until bounded seeding finishes and durable work
  drains. The projection is neither operational nor caught up in this state.

`CacheAheadRecoveryRequired` remains the separate, orthogonal durable safety latch from
the existing design. It is set only when a current cache version is ahead of the current
canonical source. That condition may already have been published and cannot be repaired
by changing internal work inventory. The set latch blocks every cache-backed read and
projection/direct-fill write without changing the lifecycle state; proven-internal-only
cache-ahead recovery clears it while entering `Rebuilding`.

A required work version different from the canonical source, or missing required work for
a behind cache after the baseline has covered that document, is an internal work-inventory
anomaly rather than a cache-ahead incident. It does not set the cache-ahead latch. During
ordinary `Tracking`, mismatched work remains pending and blocks caught-up status until an
explicit scrub repairs it. During an explicit `Rebuilding` workflow, the rebuild
coordinator conditionally repairs mismatched existing work to the current canonical
requirement while holding its existing administrative mutex; it does not start a second
scrub workflow or release and reacquire the mutex. An explicit scrub that discovers
missing work transactionally inserts the current canonical requirement before continuing.
The amendment must define these transitions:

- Provisioning initializes the lifecycle to `Disabled`.
- A new empty database may use the guarded direct transition from `Disabled` to `Tracking`
  before its first canonical write. The transition must prove that the canonical, cache,
  and work tables are empty; otherwise it fails closed and the existing-database activation
  workflow is required.
- Offline activation of an existing database transitions from `Disabled` to `Rebuilding`,
  then to `Tracking` only after bounded seeding finishes and durable work drains.
- Cache rebuild requires a clear cache-ahead latch before it transitions from `Tracking`
  or `Rebuilding` to `Resetting`. Proven-internal-only cache-ahead recovery requires the
  latch to be set. Each clears the applicable projected state behind that durable fence
  and then transitions to `Rebuilding`.
- Successful rebuild transitions from `Rebuilding` to `Tracking`.
- Offline deactivation transitions from `Tracking` or `Rebuilding` through `Resetting` to
  `Disabled` while clearing cache and work state.
- No other lifecycle transition is supported in v1.
- Tracking is enabled by provisioning/deployment control before the first canonical write
  to a projection-selected database, or later through the explicit offline
  read-acceleration activation workflow below.
- Runtime `DocumentCache:Targets` configuration never silently enables database tracking.
  A configured target whose lifecycle state is `Disabled` is projection-unavailable and
  performs no cache work; that condition alone does not make the normal relational API
  unhealthy.
- Removing a runtime target pauses processing but does not discard durable work or disable
  tracking.
- V1 CDC keeps its existing new-physical-database, pre-first-write restriction. Later CDC
  retrofit remains unsupported.

Every lifecycle-changing, cache/work-clearing, baseline-seeding, recovery, integrity-scrub,
or representation-restamp workflow must first acquire one provider-equivalent,
database-scoped administrative mutex through one shared provider adapter. The exact v1
identities are:

- PostgreSQL uses `pg_advisory_lock(bigint)` with
  `(811646948::bigint << 32) | currentDatabaseOid::bigint`, where `811646948`
  (`0x3060BFE4`) is the fixed projection-administration namespace and
  `currentDatabaseOid` is the current database row's unsigned
  `pg_catalog.pg_database.oid` value in
  the low 32 bits. It releases that exact key with `pg_advisory_unlock(bigint)`.
- SQL Server uses `sp_getapplock` with the exact resource
  `EdFi.DMS.DocumentProjection.Administration.v1`, `Exclusive` mode, session ownership,
  and the explicit database principal `public`. A negative return code is failure. It
  releases the same resource, owner, and principal with `sp_releaseapplock`.

These identities are database-derived and must not use tenant key, `DataStoreId`,
connection-string database name, connection alias, or mutable `DataStoreIdentity`.
Therefore aliases of one physical database contend on one mutex while different databases
on the same server or PostgreSQL cluster can be administered concurrently. The coordinator
holds the mutex on a dedicated open connection across the complete multi-transaction
workflow, including its final lifecycle transition when one exists. The connection must
not return to a general pool while holding the mutex, and normal completion must release
the mutex explicitly before the connection can be reused.
Ordinary canonical writers, projector workers, direct fill, reads, and health checks do
not acquire this mutex. Loss of the owning database session releases the mutex and aborts
the coordinator; it must not make a later final transition without reacquiring the mutex
and revalidating durable state. After an interruption, an operator reissues the same
administrative operation. A replacement coordinator validates the durable lifecycle state
and safely repeats or resumes that explicitly requested operation; it never infers the
operation from `Resetting` alone.

The new-empty-database `Disabled -> Tracking` transition is one short administrative
transaction while canonical write admission remains closed and in-flight writers have
drained. After acquiring the administrative mutex, it:

- takes a provider-equivalent lock on `dms.Document` that blocks canonical inserts,
  updates, and deletes for the duration of the transaction;
- locks the singleton `DocumentCacheState` row exclusively;
- verifies that the lifecycle is `Disabled` and `CacheAheadRecoveryRequired` is clear;
- verifies that `dms.Document`, `dms.DocumentCache`, and `dms.DocumentProjectionWork` are
  empty; and
- transitions the lifecycle to `Tracking` and commits before canonical write admission
  opens.

This is a one-time activation lock, not an ordinary canonical-write or projection lock. If
any table is nonempty, the transaction makes no state change. Read-acceleration activation
must use `Disabled -> Rebuilding -> Tracking`; v1 CDC retrofit remains unsupported. A
canonical insert deliberately raced against this transaction must have one of two outcomes:
it commits before the guarded transition and causes the transition to reject the nonempty
database, or it commits after the transition and transactionally enqueues projection work.

The administrative mutex does not fence ordinary cache transactions. Every cache-write
or cache-write/acknowledgement transaction instead reads the singleton
`DocumentCacheState` row under the existing provider-equivalent shared row lock, verifies
that the lifecycle is `Tracking` or `Rebuilding` and that
`CacheAheadRecoveryRequired` is clear, and holds that lock through commit. Short
transitions into and out of `Resetting` take the same row lock exclusively. Entering
`Resetting` therefore waits for cache transactions already in progress; later cache
transactions observe `Resetting` and perform no write or acknowledgement. The exclusive
state-row lock is not held while projected rows are physically cleared.

Later read-acceleration activation and deactivation use one deliberately simple, repeatable
offline administrative workflow. Runtime configuration never performs these transitions.
Before either transition, operators must stop all DMS replicas, projector processes,
direct-fill activity, seed and bulk loaders, administrative writers, and every other
canonical writer, then allow in-flight database transactions to finish.

To disable projection:

- transition to `Resetting` in one short transaction under the exclusive singleton-state
  lock;
- clear `dms.DocumentProjectionWork` and `dms.DocumentCache` using bounded transactions or
  a qualified provider-specific fast clear;
- in one final short transaction under the exclusive singleton-state lock, verify both
  tables are empty, transition to `Disabled`, and clear
  `CacheAheadRecoveryRequired` only after proving that the projection was internal-only
  and could not have been observed downstream.

After the final transaction, the database may return online without projection. Canonical
reads and writes remain available, the enqueue trigger records no new work, and no stale
projected state remains eligible for use. A crash during the clear leaves the database
durably `Resetting` and ineligible for cache use; the next mutex owner repeats or resumes
the clear before transitioning to `Disabled`.

To enable projection, the workflow first clears any residual work or cache rows using
bounded transactions while the database remains offline and `Disabled`. It then uses one
short provider transaction under the exclusive singleton-state lock that:

- verifies that `ProjectionLifecycleState` is `Disabled`;
- verifies that the work and cache tables are empty;
- transitions `ProjectionLifecycleState` to `Rebuilding`.

While canonical write admission remains closed, the administrative workflow scans
`dms.Document` through a captured upper `DocumentId` boundary and seeds projection work in
bounded, backpressured windows. It starts only the designated projector execution needed to
drain that work, waits until windowed seeding is complete and the work table is empty, and
then transitions `ProjectionLifecycleState` from `Rebuilding` to `Tracking` before
canonical write admission opens.

Activation is restart-safe: the `Rebuilding` lifecycle state remains durable, the
baseline may restart from the beginning, and work upserts are idempotent. Deactivation is
durably fenced: after an interruption, the database is either still enabled, safely
`Resetting`, or cleanly disabled. The operations may be repeated, giving the lifecycle:

```text
activation:                    Disabled -> Rebuilding -> Tracking
rebuild (latch clear):         Tracking|Rebuilding -> Resetting -> Rebuilding -> Tracking
internal recovery (latch set): Tracking|Rebuilding -> Resetting -> Rebuilding -> Tracking
deactivation:                  Tracking|Rebuilding -> Resetting -> Disabled
```

V1 introduces no epoch, durable baseline cursor, or general transition framework.
`Resetting` is the single durable phase needed to separate the short state-lock boundary
from a potentially large physical clear. These read-acceleration operations do not create
CDC eligibility, perform Kafka catch-up, establish a provider heartbeat barrier, or
provide a streaming baseline. A database with an active or historical downstream consumer
or CDC binding is not eligible for this simple toggle; its containment and recovery remain
governed by the CDC design.

Internal recovery follows the rebuild lifecycle path, but it keeps
`CacheAheadRecoveryRequired` set throughout `Resetting` and clears the latch only in the
verified transition to `Rebuilding`.

An ordinary rebuild atomically requires the latch to be clear before entering `Resetting`.
A set latch rejects that command without changing lifecycle, cache, work, or latch state
and routes the operator to cache-ahead recovery or publication containment.

### Transactional enqueue

Add one logical, provider-equivalent, set-based enqueue mechanism on `dms.Document`.

- PostgreSQL uses two `AFTER STATEMENT` triggers because transition tables cannot be
  combined with multiple trigger events:
  - an `INSERT` trigger using a `NEW TABLE` transition relation;
  - an `UPDATE` trigger using `OLD TABLE` and `NEW TABLE` transition relations and
    filtering rows whose `ContentVersion` actually changed.
- SQL Server uses one set-based `AFTER INSERT, UPDATE` trigger over `inserted` and
  `deleted`. Because supported resource `*_Stamp` triggers update `dms.Document`, a SQL
  Server projection target requires the server-level `nested triggers` option to have
  `value_in_use = 1`.
- PostgreSQL may share protected helper logic between its two trigger functions, but the
  generated DDL contains two trigger definitions.
- All provider implementations:
  - read `ProjectionLifecycleState` once per triggering statement;
  - in `Tracking`, `Resetting`, or `Rebuilding`, enqueue every inserted document and every
    real `ContentVersion` change;
  - in the same canonical transaction, insert or update
    `DocumentProjectionWork.RequiredContentVersion` to the greater existing or new
    version;
  - preserve `FirstEnqueuedAt` while work remains pending;
  - advance `LastEnqueuedAt` only when the required version advances;
  - cover direct resource writes, child/extension writes, propagated reference-identity
    changes, descriptor writes, bulk restamping, and any other supported path that
    converges on `dms.Document.ContentVersion`;
  - record no work while the lifecycle state is `Disabled`.
- `ON DELETE CASCADE` removes obsolete work when `dms.Document` is deleted.

The design must keep enqueueing inside the canonical database transaction. Application
best-effort enqueueing is not a completeness mechanism.

While the lifecycle is `Tracking`, `Resetting`, or `Rebuilding`, projection-work
recording is a mandatory part of every canonical document mutation. Failure to insert or
advance `DocumentProjectionWork` must abort the complete canonical transaction. Trigger
code must not suppress enqueue errors, and no supported application path may commit the
canonical change and retry enqueueing separately.

Transient failures such as deadlocks or serialization failures must retry the complete
canonical transaction from its beginning according to the provider retry policy; retrying
only the trigger or work-table upsert is invalid. Non-transient failures, including
missing schema, permission failures, constraint failures, and unavailable queue storage,
fail the canonical write and produce target-specific diagnostics.

This intentionally couples canonical-write availability to the enqueue schema while
projection tracking is enabled. It does not couple writes to projector-process
availability or queue drain: the projector may be stopped and the queue may grow without
preventing unrelated canonical writes. In `Disabled`, no projection work is recorded and
this coupling does not apply.

### Projection and acknowledgement

Replace incremental `ContentVersion` scanning and periodic full audits with fair,
bounded paging of `DocumentProjectionWork`.

For each selected work row, the projector:

1. Captures `DocumentId` and `RequiredContentVersion`.
2. Materializes the latest coherent canonical document using the existing materializer.
3. Performs the existing optimistic current-version check.
4. In one short transaction:
   - reads `DocumentCacheState` under the provider-equivalent shared state-row lock and
     verifies that the lifecycle is `Tracking` or `Rebuilding`;
   - verifies the cache-ahead recovery latch remains clear;
   - obtains one current-visibility classification of the current canonical
     `Document.ContentVersion`, optional `DocumentCache.ContentVersion`, and optional
     `DocumentProjectionWork.RequiredContentVersion`;
   - performs the monotonic cache insert/update only when the classification is valid and
     the materialized candidate still matches the current canonical and required work
     versions;
   - treats current durable `S = C = W` as already projected and conditionally
     acknowledges that work regardless of the worker-local candidate version;
   - never writes an older materialized candidate and otherwise leaves current work
     pending when the cache is absent or behind;
   - deletes the work row only when its `RequiredContentVersion` still equals the current
     canonical and cached version.

The cache write and work acknowledgement must commit or roll back together. A newer
canonical transaction either advances the existing work row before acknowledgement or
recreates it after acknowledgement.

#### Current source/cache/work classification

The worker-local materialized candidate is not evidence that the cache is ahead. For
example, a worker holding candidate version 10 may observe cache version 11 after another
worker has validly projected canonical version 11. Classification must instead compare the
three current durable values in one provider-consistent statement snapshot:

- `S`: current `Document.ContentVersion`;
- `C`: current `DocumentCache.ContentVersion`, if a cache row exists;
- `W`: current `DocumentProjectionWork.RequiredContentVersion`, if a work row exists.

Representative classifications are:

| `S` | `C` | `W` | Classification and action |
| ---: | ---: | ---: | --- |
| 11 | 10 | 11 | Healthy pending projection; write cache version 11 and conditionally acknowledge work. |
| 11 | 11 | 11 | Already projected; conditionally acknowledge redundant work. |
| 11 | 11 | absent | Current for this document; perform no work. |
| 11 | 12 | any | Cache is genuinely ahead of the canonical source; set the cache-ahead recovery latch. |
| 11 | absent or at most 11 | 10 | Work is behind the canonical source; an ordinary worker leaves it pending for explicit scrub repair. |
| 11 | absent or at most 11 | 12 | Work is ahead of the canonical source; an ordinary worker leaves it pending for explicit scrub repair. |
| 11 | 10 | absent | Cache is behind but required work is missing; require explicit scrub to enqueue version 11. |

Here, `any` includes an absent row, and an absent cache row is behind an existing
canonical source. Cache-ahead takes precedence over any overlapping work anomaly because
it is the only relationship that may represent unsafe projected or published state. Every
invalid relationship performs no cache write or acknowledgement. Only current `C > S`
invokes the durable cache-ahead recovery incident path. A missing canonical document is
not one of these incidents; the existing foreign-key delete cascade and post-delete fence
remove or reject obsolete work and cache activity.

Under supported writes, the canonical document version and coalesced work requirement are
committed atomically, so visible work must require the current canonical version. An
observed `W != S` therefore indicates a restore, corruption, unsupported direct mutation,
or broken enqueue mechanism rather than ordinary projector concurrency. Similarly, in
ordinary `Tracking`, a behind cache without work violates projection completeness.
Unseeded rows during an incomplete explicit `Rebuilding` baseline are governed by the
lifecycle fence and are not classified as missing work until they have entered the seeded
work/acknowledgement path or the baseline has completed.

During ordinary `Tracking`, an existing `W != S` row is durable blocked work: the worker
leaves it unacknowledged, reports bounded diagnostics, advances fairly past it, and
therefore keeps caught-up status false. The explicit scrub conditionally replaces the
mismatched requirement with the current `S`. During an explicit `Rebuilding` workflow,
the rebuild coordinator performs that same conditional repair for mismatched existing
work encountered by its bounded source pages, reports the anomaly, and continues under
the administrative mutex it already owns. It does not run the full scrub or enqueue
unseeded documents outside the current bounded page. For `C < S` with absent `W`, the
standalone scrub conditionally inserts `W = S`; ordinary projection then repairs the
cache. These repairs use one coherent source/cache/work classification and conditional
work DML so a concurrent canonical change wins and its newer transactional requirement
is not lost. They do not set the cache-ahead latch.

Before a scrub discovers absent work, queue-empty status cannot represent that anomaly.
This is the deliberate supported-mutation boundary, not a completeness claim about
unsupported direct mutation or restore. After either event is suspected, operators must
run the explicit scrub before relying on caught-up status.

Work anomalies alone do not prove that a cache value was published. A separately detected
source restore, CDC continuity failure, or other evidence of possibly published
inconsistent state remains governed by the existing CDC containment contract; it is not
inferred solely from `W != S`.

Three separate reads are insufficient because a concurrent canonical commit or
acknowledgement could make values from different database moments appear inconsistent.
The current source, cache, and work values must be classified by one current-visibility
statement. Conditional cache DML and work deletion must still repeat the required version
predicates so a commit after that statement cannot acknowledge newer work.

An older worker candidate is never written and is not itself an incident. When the current
durable classification is `S = C = W`, the worker conditionally acknowledges the redundant
work regardless of its candidate version because the cache already contains the required
canonical version. When `W = S` and the cache is absent or behind, the worker leaves
current work pending. When work is absent and `C = S`, there is nothing to acknowledge.
None of these stale-candidate cases sets the latch. On a suspected invalid classification,
the worker first performs no cache write or acknowledgement. Only suspected cache-ahead
state enters the short incident transaction: it takes the singleton state row exclusively,
re-runs the same one-statement classification, and sets
`CacheAheadRecoveryRequired` only if `C > S` is still present. Once set, the latch makes
cache reads fall back to relational reconstruction and blocks projection/direct-fill
writes until the applicable cache-ahead recovery workflow completes. Work-only anomalies
remain isolated to their durable work row and explicit scrub path.

`DocumentProjectionWork` is therefore an intentional, short-lived per-document
serialization point. A canonical writer and projector may briefly wait on each other when
updating or deleting the same work row. This is not a source-row commit-order fence: the
projector performs materialization and coherence checking outside the acknowledgement
transaction and never holds a work-row lock during that work.

Within the acknowledgement transaction, the projector performs cache safety checks and the
monotonic cache write before conditionally deleting the work row as its final DML
operation. It must not lock the work row before accessing `DocumentCache` or the parent
`Document` required by foreign-key and UUID validation. PostgreSQL and SQL Server
implementations must document their equivalent lock order and retry the complete
cache/acknowledgement transaction after a deadlock.

To make a restarted baseline or rebuild cheap, a selected work row may take an
equal-version fast path before materialization. When one current-visibility classification
observes that the canonical document, cache row, and work row all have the same
`ContentVersion`, the shared acknowledgement component may conditionally delete the work
row without reconstituting document JSON. The acknowledgement transaction must still
verify that the work row requires that version and the cache contains that version.
This durable-state fast path remains valid after materialization when the worker-local
candidate has become stale.
Concurrent canonical mutation remains safe because it either advances the work row before
the conditional acknowledgement or recreates it afterward.

Optional direct fill should use the same cache-write/conditional-acknowledgement component.
It may acknowledge matching work but must remain best effort and must not fail the
relational response.

V1 should retain duplicate-safe processing rather than add distributed claims. Designated
projector hosts avoid waste; multiple configured replicas remain correct through monotonic
cache writes and conditional acknowledgement. Paging must advance past a poison item and
wrap fairly so one persistent failure cannot starve later work.

### Projection operational health, catch-up, and CDC admission

The design must expose three distinct contracts:

1. **Projection operational health:** the target can safely perform ordinary projection
   work.
2. **Projection caught-up status:** the target was operational and had no committed
   durable work at one observation boundary.
3. **Initial CDC admission:** projection caught-up status has been composed with the
   provider source-position heartbeat barrier while canonical write admission is closed.

Neither projection operational health nor projection caught-up status is ordinary DMS
API health or readiness. Normal API routing remains based on the canonical relational
path. A projection that is unavailable or behind causes cache-backed reads to fall back
to relational reconstruction; queue presence or a projection-only failure must not by
itself remove an otherwise healthy DMS replica from normal API routing.

Projection operational health is the composition of process eligibility and a durable
database observation.

Process eligibility requires:

- The target is resolved to the expected physical source.
- The required projection schema is validated, including the singleton state row, work
  table, constraints, indexes, and enabled provider enqueue trigger or triggers.
- Provider prerequisites are satisfied. For SQL Server, this includes RCSI while it
  remains required and `nested triggers` having `value_in_use = 1`.
- The target execution context is running and is not stopped or faulted.

SQL Server target validation reads `sys.configurations.value_in_use` for
`name = 'nested triggers'` alongside the existing RCSI validation. A disabled or
unreadable setting makes only projection and cache use for that target ineligible and
not operational; it does not make the canonical relational API or unrelated targets
unhealthy. Runtime DMS reports an explicit diagnostic and never changes the server
setting.

The durable operational-health observation is:

```text
ProjectionLifecycleState = Tracking
AND CacheAheadRecoveryRequired is false
```

Queue presence does not participate in operational health. A healthy projector may have
pending work during normal sustained writes, retry, or recovery from an outage.

Projection caught-up status requires process eligibility and one database statement that
observes:

```text
ProjectionLifecycleState = Tracking
AND CacheAheadRecoveryRequired is false
AND NOT EXISTS (SELECT 1 FROM dms.DocumentProjectionWork)
```

The statement must read lifecycle state, the cache-ahead safety latch, and work existence
from one provider-consistent statement snapshot. It may return the operational-health and
caught-up fields together. Failure to execute the observation produces unknown projection
status; a previous successful observation is not reused.

This is an exact observation that no committed durable projection work existed at that
statement boundary. It is not an independent source/cache integrity scan and depends on
the validated enqueue mechanism and supported-mutation boundary. After write admission
opens, caught-up status remains observational and does not gate normal API traffic.

No process-local repair-required or in-flight-failure latch participates in caught-up
status.
Materialization, cache-write, cancellation, and transient database failures leave the work
row durable because cache write and acknowledgement are atomic. Consequently, failed work
keeps caught-up status false through queue presence. Worker failure, retry, and backoff
state remain projection-health diagnostics; historical failures do not keep a recovered,
empty, operational target from reporting caught up.

Remove these settings and concepts from ordinary projection:

- `IncrementalScanInterval`
- `FullAuditInterval`
- `MaximumAuditAge`
- process-local `ContentVersion` cursor
- latest exact-zero audit and its age
- audit finishing aggregates as operational-health or caught-up evidence

Retain or introduce only queue-oriented settings such as poll interval, page size,
process-wide target concurrency, failure backoff, baseline-seeding high-water mark, and
direct-fill timeout. The baseline-seeding high-water mark may be a derived implementation
limit rather than an independently configurable setting.

Hot-path projection health and caught-up status must use indexed state, existence, and
oldest-work checks. Do not replace a 100-million-row source audit with a routine exact
`COUNT(*)` over a potentially huge backlog. Exact counts may be an explicit diagnostic
operation; normal telemetry may use bounded/provider-estimated counts.

Initial CDC admission applies only while canonical write admission remains closed. After
DMS reports projection caught up, deployment automation captures the provider
source-position heartbeat barrier, waits for the connector to cross it, and performs a
second durable projection caught-up observation before opening write admission. Running
connector state or lag alone remains insufficient. After admission opens, queue growth
does not revoke admission; combined CDC health, lag, and caught-up status are
observational and do not claim a new exact canonical/cache/Kafka baseline.

### Baseline, rebuild, scrub, and cache-ahead recovery

The design must distinguish the following paths:

1. **New empty database:** use the guarded `Disabled -> Tracking` transaction while write
   admission is closed. It takes the one-time writer-blocking `dms.Document` lock and
   verifies that canonical, cache, and work tables are empty before enabling tracking. A
   nonempty database is rejected; no O(N) baseline is needed for a successful transition.
2. **Offline read-acceleration activation:** after stopping writers and draining in-flight
   transactions, atomically transition from `Disabled` to `Rebuilding`, scan
   existing `dms.Document` rows through a captured upper `DocumentId` boundary, and seed
   bounded, backpressured windows of work. Keep canonical write admission closed until
   seeding completes, durable work drains, and the lifecycle transitions to `Tracking`.
   This operation has no CDC or streaming catch-up semantics.
3. **Cache clear/rebuild:** while canonical writes remain online, acquire the
   administrative mutex and, in one short transaction under the exclusive state-row
   lock, verify lifecycle `Tracking` or `Rebuilding` and a clear cache-ahead latch before
   transitioning to `Resetting`. A set latch rejects the command before any lifecycle,
   cache, work, or latch mutation and directs the operator to the applicable cache-ahead
   recovery or publication-containment procedure. With the guard satisfied, clear only
   `DocumentCache` using bounded transactions or a qualified provider-specific fast
   clear; do not clear pending work. Transactional enqueueing continues while cache writes
   and acknowledgements remain fenced. After the cache is empty, transition to
   `Rebuilding`, then seed bounded, backpressured windows of work through an O(N) pass.
   Keep the projection non-operational and not caught up until seeding completes, durable
   work drains, and the lifecycle returns to `Tracking`. A crash in `Resetting` safely
   restarts or resumes the explicitly requested clear only while the latch remains clear;
   a crash in `Rebuilding` safely restarts the baseline from the beginning.
4. **Integrity scrub:** an explicit or very infrequent operator action may scan the full
   canonical/cache/work relationship only after preflight requires lifecycle `Tracking`
   and a clear cache-ahead latch. Any other lifecycle or a latch already set rejects before
   the scan or mutation. The intentionally O(N) scrub conditionally enqueues missing work,
   repairs mismatched work requirements to the current canonical version, and may set the
   cache-ahead recovery latch only for a current `C > S` relationship; it never clears the
   latch. It holds the administrative mutex so it cannot classify state concurrently with
   a reset or rebuild. Baseline high-water, backpressure, and durable-cursor requirements
   do not apply to scrub. Scrub recency is not projection operational-health or caught-up
   evidence.
5. **Proven-internal-only cache-ahead recovery:** close write admission, stop projection
   execution, enter `Resetting` with the latch still set, and clear cache and work. Enter
   `Rebuilding` and clear the latch only after both tables are verified empty, then reopen
   admission and run the bounded baseline. If downstream publication is possible or
   uncertain, preserve the latch and projected state for the deferred new-namespace
   recovery contract instead.

`Resetting` is the atomic rebuild boundary. The transition into it takes the exclusive
singleton-state lock and therefore waits for earlier cache-write/acknowledgement
transactions to finish. Once it commits, subsequent projector and direct-fill
transactions take the shared state lock, observe `Resetting`, and perform no cache write
or acknowledgement. Cache-backed reads also fall back to relational reconstruction.
Canonical transactions continue to enqueue work because `Resetting` remains an
enqueue-enabled state. This ensures no cache result can be acknowledged and then erased by
the clear, without holding the singleton-state lock for the duration of a large clear.

Initial-baseline and rebuild seeding must be windowed and backpressured rather than insert
the complete source population into `DocumentProjectionWork` ahead of projection:

1. After the lifecycle enters `Rebuilding`, capture the current maximum `DocumentId` as
   the scan boundary. An empty source uses the logical minimum.
2. Keyset-scan `dms.Document` through that boundary in bounded `DocumentId` pages.
   Documents inserted or changed after the boundary are covered by transactional
   enqueueing, and deletes cascade pending work.
3. Seed each bounded page in one transaction and advance the in-memory keyset cursor only
   after that transaction commits. If a work upsert fails its foreign key because a
   document was deleted after page selection, roll back the page and reread from the last
   committed keyset position. The deleted document is then absent while surviving
   documents are still seeded. Repeated deletion races use bounded retry and backoff; they
   neither create poison work nor set `CacheAheadRecoveryRequired`. Within that same
   bounded page, if existing work requires a version different from the current canonical
   version, report the anomaly and use the standalone scrub's conditional work-only repair
   to set the requirement to the current version. The conditional DML must preserve a
   concurrent newer canonical requirement. This is part of the explicit rebuild and does
   not run a second full scrub or enqueue documents outside the current page.
4. Seed bounded windows while total pending work remains below a bounded high-water mark.
   Fair paging allows healthy queued work to progress despite a limited number of
   persistent failures. Failed work remains pending.
5. When the high-water mark is reached, pause seeding until work drains. If persistent
   failures consume the available backlog capacity, seeding remains paused and the rebuild
   cannot complete until enough failures are remediated. Backpressure uses a bounded
   provider-equivalent `high-water mark + 1` observation, not an exact `COUNT(*)`.
6. Canonical writes may grow the queue beyond the seeder's limit; the limit bounds only
   baseline-generated amplification. Offline activation admits no such writes. Online
   rebuild, which keeps admission open, and internal recovery, after admission reopens,
   rely on transactional enqueueing for writes around the captured boundary.
7. V1 persists no durable baseline cursor. A crash leaves the lifecycle in `Rebuilding`;
   after acquiring the administrative mutex, a replacement coordinator captures a new
   boundary and restarts the scan from the beginning. Already-current rows use the
   equal-version conditional-acknowledgement fast path without rematerializing JSON.
8. Transition from `Rebuilding` to `Tracking` only after the complete bounded scan has
   finished and the durable work inventory is empty.

Restart-from-the-beginning is a production-qualification decision, not an unmeasured
assumption. DMS-1317 must interrupt and restart baseline/rebuild processing at
representative supported scale and evaluate predefined completion-time, database-load,
and repeated queue-DML/write-amplification limits. If the behavior exceeds a limit, create
a new ticket to design and implement a durable baseline cursor and make that ticket a
production-qualification prerequisite.

For a cache-ahead incident proven to be internal-only, recovery is an offline
administrative operation because it must clear potentially stale cache state and establish
fresh projection work. After acquiring the administrative mutex, operators must close
canonical write admission, drain in-flight canonical transactions, and stop projector and
direct-fill execution so no pre-recovery materialization can survive into the rebuilt
cache. One short transaction under the exclusive `DocumentCacheState` lock verifies that
tracking remains enabled (`ProjectionLifecycleState` is not `Disabled`) and
`CacheAheadRecoveryRequired` is set, then enters `Resetting` while leaving the latch set.
The coordinator clears all `dms.DocumentCache` and `dms.DocumentProjectionWork` rows using
bounded transactions or a qualified provider-specific fast clear.

One final short transaction under the exclusive singleton-state lock verifies that the
database remains `Resetting`, the latch remains set, and both tables are empty; it then
transitions to `Rebuilding` and clears `CacheAheadRecoveryRequired`. Canonical write
admission may reopen after that commit, the coordinator starts fresh projector execution,
and transactional enqueue remains active while a bounded baseline reseeds work from
current `dms.Document` rows. The projection remains non-operational and not caught up until
baseline seeding completes and the work inventory drains, then the lifecycle transitions
to `Tracking`.

A crash while clearing leaves both `Resetting` and the cache-ahead recovery latch durable,
so cache use and projection writes remain fenced and the next administrative-mutex owner
repeats or resumes the clear. A crash after entering `Rebuilding` restarts the baseline.
Supporting live canonical writes while clearing old work would require a work
generation/epoch; v1 deliberately chooses offline recovery instead.

Clearing pending work is required because a restored or corrupted source may have a
canonical `ContentVersion` lower than a previously required version. Baseline upsert must
not retain that stale higher requirement.

If an inconsistent cache version may have been published, or downstream observation is
uncertain, this recovery workflow is prohibited. The cache-ahead recovery latch, cache,
and work inventory remain intact under the existing deferred new-namespace recovery
contract.

Direct cache/work-table mutation, cache truncation, and work deletion outside supported
administrative operations remain unsupported. Database permissions and runbooks must make
that boundary explicit.

### CDC and public contract

`DocumentProjectionWork` is an internal projector table:

- It is excluded from PostgreSQL publications and SQL Server CDC capture instances.
- It is excluded from connector include lists and public message transformation.
- It never creates a Kafka document event.
- Public upserts still come from `dms.DocumentCache`.
- Public tombstones still come from `dms.Document` deletes.
- The public key, value, `contentVersion`, ETag, partitioning, compaction, and consumer
  contracts do not change.

### Access-path and performance decision

Remove `IX_Document_ContentVersion_DocumentId` if a repository-wide query audit confirms it
was introduced only for projector incremental discovery. Replace it with the work-table
access paths required for paging and oldest-work observation.

Qualification must measure the new cost deliberately:

- Canonical write latency and throughput with lifecycle state `Disabled`, `Resetting`,
  and `Tracking`.
- Trigger/upsert amplification during indirect reference propagation and bulk restamping.
- Shared singleton-state lock throughput, exclusive-transition drain time, and cache
  writer behavior while `Resetting`.
- PostgreSQL WAL, vacuum pressure, dead tuples, and index bloat.
- SQL Server transaction-log volume, ghost records, lock/deadlock behavior, and index
  maintenance.
- Queue-drain throughput, oldest-work latency, outage growth, and recovery.
- Interrupted baseline/rebuild restart-from-the-beginning duration, database load, and
  repeated queue-DML/write amplification at representative supported scale.
- Large-cache reset duration, transaction-log amplification, blocking, cleanup, and crash
  restart for bounded clearing and any provider-specific fast-clear implementation.
- Caught-up `NOT EXISTS` and oldest-work plans with very large `dms.Document` and work
  populations.

## Amendment Sequence

### 1. Amend the normative projector decision first

Rewrite
`reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md`
as the primary decision owner.

Required changes:

- Replace “current source/cache difference is durable work inventory” with transactional
  durable-work recording.
- Remove incremental cursor and scheduled/startup full-audit lanes.
- Specify enqueue, paging, materialization, cache-write/acknowledgement, retry, fairness,
  restart, multi-replica, delete, and crash semantics.
- Define one current-visibility source/cache/work classification, distinguish a stale
  worker candidate from a genuinely invalid durable relationship, make current
  `S = C = W` authoritative for redundant acknowledgement regardless of candidate version,
  retain cache-ahead-only latching, route work-only anomalies to blocked-work diagnostics
  and explicit scrub during ordinary tracking, and let an explicit rebuild conditionally
  repair mismatched existing work within its bounded source pages.
- Preserve the no source-row commit-order fence decision and show why enqueue-versus-ack
  interleavings are safe.
- Preserve monotonic cache upsert, post-delete fencing, cache-ahead recovery latching,
  cache-ahead publication containment, relational fallback, and cache/domain lifecycle
  separation.
- Define the shared/exclusive singleton-state lock protocol, database-scoped
  administrative mutex, durable `Resetting` fence, baseline, rebuild, explicit scrub,
  and offline internal-only cache-ahead recovery behavior.
- Require SQL Server projection-target validation to fail projection and cache
  eligibility when the server-level `nested triggers` option is disabled or unreadable,
  without changing the setting at runtime or gating the canonical relational API.
- Update rationale, consequences, and alternatives. Mark durable pending work as accepted
  because the anticipated full-audit qualification condition has been met.
- Remove claims that full audits prove routine completeness or that no durable projector
  workflow table exists.

Do not edit downstream documents until this owner has a coherent end-to-end contract.

### 2. Amend physical schema and canonical stamping owners

#### `reference/design/backend-redesign/design-docs/data-model.md`

- Add the provider-neutral and provider-specific `DocumentProjectionWork` schema.
- Extend `DocumentCacheState` with the constrained projection lifecycle state and the
  orthogonal `CacheAheadRecoveryRequired` latch, including legal values, transitions,
  constraints, and insert-if-absent behavior. Include `Resetting` as an enqueue-enabled but
  cache-write/acknowledgement-disabled state.
- Add the provider-specific `dms.Document` enqueue triggers/functions, including their
  names, counts, and semantics.
- Add work paging/oldest-work indexes.
- Reassess and likely remove `IX_Document_ContentVersion_DocumentId`.
- Specify provider-equivalent least-privilege execution so canonical writers can enqueue
  through the trigger but cannot directly mutate work rows. For PostgreSQL, define and
  qualify a hardened `SECURITY DEFINER` trigger-function owner and fixed safe
  `search_path`; for SQL Server, define and qualify the equivalent ownership-chain or
  `EXECUTE AS` contract. Projector writers can acknowledge, CDC readers cannot capture
  work, and unsupported principals cannot mutate queue state.
- Renumber affected physical-object sections or preserve stable explicit anchors, then
  repair every inbound link.

#### `reference/design/backend-redesign/design-docs/ddl-generation.md`

- Add the work table, lifecycle-state column, `CacheAheadRecoveryRequired` column,
  trigger/function,
  constraints, indexes, grants,
  deterministic ordering, manifests, introspection, and DB-apply expectations.
- Remove the statement that projector workflow tables are intentionally absent.
- Update singleton initialization and rerun preservation rules.
- Update object dependency order so `Document`, state, work, trigger, and foreign keys
  apply correctly on both providers.

#### `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`

- Add transactional enqueue to the canonical write/stamping sequence.
- Define fail-closed enqueue semantics: any enqueue failure aborts the complete canonical
  transaction in `Tracking`, `Resetting`, and `Rebuilding`; no error suppression or
  post-commit enqueue retry is permitted.
- Define canonical writer, projector acknowledgement, direct fill, and delete lock/order
  interactions.
- Define the one-statement source/cache/work classification, stale-candidate write
  suppression, durable-state-driven equal-version acknowledgement, conditional
  acknowledgement predicates, durable cache-ahead incident-latch transaction, blocked-work
  behavior, conditional scrub repair, and reuse of that conditional work-only repair by
  the explicit rebuild coordinator.
- Define shared singleton-state locking for cache-write/acknowledgement transactions,
  short exclusive `Resetting` transitions, and provider-equivalent session-owned
  administrative mutex behavior.
- State that the queue removes the late-commit gap without adding a source-row
  commit-order fence.
- Document the required lock order and provider retry policy for PostgreSQL and SQL
  Server. An enqueue-related deadlock, serialization failure, or lock timeout that the
  policy classifies as retryable replays the complete canonical transaction rather than
  only the trigger or work-table statement.
- Document that SQL Server indirect `*_Stamp` updates reach the `dms.Document` enqueue
  trigger only when the server-level `nested triggers` option is enabled, and cross-link
  the projection-target prerequisite validation.
- Revisit the SQL Server RCSI rationale. Retain it conservatively unless the amended
  source/cache classification and read path are proven correct without it.

#### `reference/design/backend-redesign/design-docs/update-tracking.md`

- State that every supported initial or changed `ContentVersion` transaction also updates
  projection work when tracking is enabled.
- State that failure to record required projection work rolls back the complete
  `ContentVersion` transaction in every enqueue-enabled lifecycle state.
- Cover set-based stamp triggers, indirect cascades, descriptors, no-op writes, and
  restamping.
- Preserve Change Queries semantics and clarify that projection work is not Change Query
  history.

### 3. Amend integration, health, catch-up, operations, and bootstrap documentation

#### `reference/design/cdc-streaming.md`

- Replace audit/cursor configuration with queue-processing configuration.
- Define durable lifecycle transitions and their relationship to process-local
  `DocumentCache:Targets`.
- Define the `Resetting` fence, database-scoped administrative serialization, online
  cache rebuild, and offline cache-ahead recovery contracts.
- Replace audit health fields with lifecycle state, queue presence, oldest-work age,
  processing/failure/backoff state, enqueue failures, and optional bounded backlog
  estimates. Keep enqueue failures distinct from projector-processing failures because
  the former fail canonical writes while the latter leave durable work for retry.
- Define projection operational health independently from projection caught-up status;
  queue presence affects only caught-up status and lag diagnostics.
- State explicitly that neither projection signal gates ordinary DMS API routing, which
  remains based on the canonical relational path.
- Replace recent exact-zero audit readiness with durable-work drain as the caught-up
  observation.
- Update the initial CDC admission and provider-barrier sequence.
- Update schema inventory, security, telemetry, performance qualification, cache rebuild,
  cache-ahead recovery and publication containment, work-anomaly scrub, restamp follow-up,
  local bootstrap, CI, and runbook requirements.
- State that work-table changes are not captured by Debezium.
- Update `CDC-INV-01`, `CDC-INV-03`, `CDC-INV-04`, `CDC-INV-10`,
  `CDC-INV-14`, and `CDC-INV-15` ownership/evidence descriptions.
- Run a final search eliminating operational-health and caught-up references to full audits,
  incremental cursors, and `MaximumAuditAge`.

#### Bootstrap design owners

Review and amend:

- `reference/design/backend-redesign/design-docs/bootstrap/bootstrap-design.md`
- `reference/design/backend-redesign/design-docs/bootstrap/command-boundaries.md`
- `reference/design/backend-redesign/design-docs/bootstrap/reference-initdev-workflow.md`

Document that explicit CDC bootstrap:

- proves new-database eligibility and rejects the database before binding reservation when
  any canonical, cache, or work row already exists;
- creates or exact-matches the immutable binding and then invokes the guarded
  new-empty-database transition before seed/API writes, with canonical write admission
  closed, and defines fail-closed retry classification for binding/lifecycle crash states;
- configures a matching DMS target;
- starts projection and waits for work drain;
- captures the provider heartbeat barrier afterward;
- never treats DMS startup itself as authority to enable tracking;
- leaves mutable projection/CDC state outside the bootstrap manifest as already designed.

Review E16 bootstrap epic/story files for cross-links. Change them only if their command
surface or ownership text must mention the new tracking-enable step; E19-S04 remains the
implementation owner for CDC opt-in orchestration.

#### Public message contract

Review
`reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md`
and make only consistency edits:

- Replace generic reconciliation wording where it implies audits.
- Describe convergence through durable work.
- Preserve every public message and consumer contract.
- Preserve cache-ahead and incompatible-contract recovery semantics.

#### Supporting backend documents

Run a terminology and cross-link review over:

- `reference/design/backend-redesign/design-docs/overview.md`
- `reference/design/backend-redesign/design-docs/summary.md`
- `reference/design/backend-redesign/design-docs/new-startup-flow.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/link-injection.md`
- `reference/design/backend-redesign/design-docs/expandjsonsmt-replacement.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`
- `reference/design/multitenancy-analysis.md`

The representation, link, profile, materializer, multitenancy, and public source contracts
should remain unchanged. Update only stale schema inventories, projector terminology,
projection-health/caught-up wording, or broken links.

### 4. Amend root planning and presentation artifacts

#### `DMS-1245-1246.md`

- Replace DMS-1314’s full-audit description with durable-work processing.
- Update DMS-1310, DMS-1311, DMS-1313, DMS-1316, DMS-1317, DMS-1318, DMS-1319,
  and DMS-1323 summaries for their new responsibilities.
- Update the suggested implementation order so schema/stamping and tracking activation
  precede queue processing and initial CDC admission.
- Add the central planning distinction: ordinary completeness is queue-based; O(N) scans
  are explicit baseline/rebuild/scrub operations.

#### `PRESENTATION.md`

- Replace diagrams and slides that teach incremental discovery plus full-audit
  completeness.
- Add the canonical-write-to-work-table flow, cache-write/conditional-ack transaction, and
  enqueue-versus-ack race diagrams.
- Replace audit-age readiness with distinct projection operational-health, caught-up, and
  initial CDC-admission signals, preserving the provider publication barrier.
- Update failure, restart, recovery, settings, telemetry, risk, and story-impact slides.
- Preserve the materializer, cache-ahead, Kafka source, topic, message, delete, and
  consumer-ordering slides unless their projector wording is stale.

### 5. Amend E18 epic and every story file

Keep Jira keys stable. Keep current file paths unless a rename provides material value;
stable paths avoid needless link churn.

| File | Planned amendment |
| --- | --- |
| `reference/design/backend-redesign/epics/18-document-cache/EPIC.md` | Make transactional work recording, queue processing, rebuild, projection operational health, and queue-based caught-up status explicit completion outcomes without gating normal API routing on queue drain. |
| `00-documentcache-schema-and-provider-ddl.md` (`DMS-1310`) | Add work table, constrained lifecycle state, orthogonal `CacheAheadRecoveryRequired` latch, two PostgreSQL enqueue triggers, one SQL Server enqueue trigger, supporting functions, indexes, provider-equivalent least-privilege trigger execution, grants, manifests, introspection, rerun rules, and removal/reassessment of the source scan index. Require fail-closed transactional enqueue in every enqueue-enabled state and add `Disabled`/`Resetting`/`Rebuilding`/`Tracking`, multi-row trigger, rollback, and direct-work-mutation denial DB-apply evidence. |
| `01-documentcache-configuration-and-target-selection.md` (`DMS-1311`) | Add durable lifecycle-state validation, guarded new-empty-database `Disabled -> Tracking` and repeatable offline activation/deactivation command/preflight contracts, `Resetting` mismatch handling, target pause/resume semantics, revised queue settings, and failure for config/database-state mismatch. The new-empty contract requires the one-time writer-blocking `dms.Document` lock, exclusive state-row lock, empty canonical/cache/work checks, and fail-closed outcome; DMS-1314 owns guarded execution. Validate SQL Server `nested triggers` alongside RCSI; a disabled or unreadable setting fails projection/cache eligibility with an explicit diagnostic without changing the setting or gating the canonical relational API. |
| `02-document-materializer-service.md` (`DMS-1312`) | Preserve materialization scope. Update fixtures and wording so a selected work item materializes the latest required canonical version. No architectural rewrite. |
| `03-monotonic-cache-upsert-and-delete-fencing.md` (`DMS-1313`) | Expand the shared writer to atomically acknowledge matching work. Add one-statement source/cache/work classification, stale-candidate write suppression, durable-state-driven equal-version acknowledgement regardless of candidate version, cache-ahead-only recovery latching, blocked mismatched-work behavior, enqueue-versus-ack, equal-version acknowledgement without rematerialization, newer-version preservation, direct-fill acknowledgement, delete, crash, work-row serialization, shared singleton-state locking, exclusive reset fencing, lock order, canonical-write wait, complete-canonical-transaction retry, and multi-writer evidence. |
| `04-async-projector-reconciliation-loop.md` (`DMS-1314`) | Replace incremental and full-audit adapters with fair bounded work paging. Own the shared exact-identity provider mutex adapter, guarded new-empty and offline activation/deactivation execution, their provider concurrency tests, and `Resetting` orchestration. Add poison-item traversal without an unbounded seeding-progress guarantee, wraparound, restart, duplicate replica, backlog recovery, clear-latch-guarded online cache rebuild with fail-closed set-latch rejection, cache-ahead recovery, bounded projected-state clearing, windowed and backpressured baseline/rebuild seeding, bounded conditional repair of mismatched existing work encountered by rebuild pages under the already-held mutex, retry of baseline pages invalidated by concurrent deletion, a serialized O(N) scrub admitted only from clear-latch `Tracking` that conditionally repairs work anomalies and never clears the latch, cancellation, and target backoff. |
| `05-cache-backed-read-path.md` (`DMS-1315`) | Preserve relational fallback and freshness checks. Add durable lifecycle-state and `CacheAheadRecoveryRequired` eligibility, mandatory fallback while `Resetting`, and direct-fill conditional acknowledgement. |
| `06-documentcache-health-readiness-and-telemetry.md` (`DMS-1316`) | Replace audit observations with lifecycle state, `CacheAheadRecoveryRequired`, queue-empty, oldest-work, worker, failure, enqueue-failure, and bounded backlog observations. Keep enqueue failures distinct from projector-processing failures: the former fail canonical writes, while the latter retain durable work for retry. Provide bounded structured failure diagnostics that identify affected `DocumentId` values without using them as unbounded metric labels. Define projection operational health independently from projection caught-up status: queue presence makes caught-up false but does not make an otherwise functional projector unhealthy. Keep both signals separate from ordinary DMS API health/readiness so projection backlog or failure cannot remove a replica from normal API routing. Expose `Resetting` or a set cache-ahead recovery latch as projection non-operational without treating either as canonical API failure. Prohibit routine exact backlog counts and synchronous scans from health requests. |
| `07-documentcache-integration-tests-and-runbooks.md` (`DMS-1317`) | Add provider matrices for transactional enqueue, forced enqueue failure and complete canonical rollback, complete-transaction deadlock retry, least-privilege trigger execution and direct-work-mutation denial, projector-stopped write availability, disabled-state writes, guarded new-empty activation and its racing-insert outcomes, cascade/restamp coverage, source/cache/work classification, stale-candidate write suppression, `S = C = W` acknowledgement regardless of candidate version, cache-ahead-only latching, blocked and scrub-repaired work-version mismatches, rebuild-page repair of mismatched existing work without a second scrub or mutex handoff, crash windows, restart without a source audit, poison fairness, poison work exhausting seeding capacity, concurrent delete between baseline page selection and work upsert, long outage, clear-latch-guarded rebuild and set-latch rejection, serialized administration, reset-fence crashes, bounded large-cache clearing, offline cache-ahead recovery, clear-latch `Tracking` scrub admission and fail-closed rejection, disabled targets, and no-scan operational-health/caught-up observations at scale. Qualify interrupted baseline/rebuild restart from the beginning at representative supported scale against predefined completion-time, database-load, and repeated queue-DML/write-amplification limits. If a limit is exceeded, create a new ticket to design and implement a durable baseline cursor and make that ticket a production-qualification prerequisite. Document how to identify and remediate persistent failures that pause seeding. |
| `08-representation-restamp-utility.md` (`DMS-1318`) | Make the utility lifecycle-aware under the projection administrative mutex. In clear-latch `Tracking`, require each restamped `ContentVersion` transaction to enqueue work automatically and replace audit follow-up with queue drain. In clear-latch `Disabled`, allow explicit canonical-only restamping with no projection or Kafka claim. Reject `Resetting`, `Rebuilding`, and a set recovery latch while retaining offline safety, manifests, resume, Change Query, ETag, and CDC effects. |

Update `reference/design/backend-redesign/epics/DEPENDENCIES.md`:

- Make E18-S00 the schema prerequisite for durable-state validation and queue work.
- Keep materializer and cache-writer sequencing explicit.
- Ensure E18-S03 owns the atomic cache-write/ack component consumed by E18-S04 and direct
  fill.
- Ensure E18-S08 depends on queue-capable DDL, lifecycle validation, administrative
  serialization, and health evidence.
- Recheck E19 dependencies after its caught-up/admission/bootstrap amendments.

Review `reference/design/backend-redesign/epics/JIRA-INDEX.md`. Update it only if a story
title or path changes.

### 6. Amend E19 epic and every story file

| File | Planned amendment |
| --- | --- |
| `reference/design/backend-redesign/epics/19-cdc-kafka/EPIC.md` | State that supported streaming consumes a transactionally complete, queue-driven projection and uses projection caught-up status for initial CDC admission without coupling normal API readiness to queue drain. Preserve the public CDC scope. |
| `00-documentcache-cdc-prerequisites.md` (`DMS-1319`) | Replace exact-zero audit inputs with durable projection operational-health and caught-up inputs. Keep binding, source identity, continuity, heartbeat barrier, initial CDC-admission, and aggregate status ownership. |
| `01-cdc-ddl-support.md` (`DMS-1320`) | Explicitly exclude `DocumentProjectionWork` from provider capture and CDC grants. Preserve `Document`, `DocumentCache`, and heartbeat setup. |
| `02-connector-template-generation.md` (`DMS-1321`) | Assert connector include lists exclude the work table. Otherwise preserve templates and provider topology. |
| `03-document-state-transform.md` (`DMS-1322`) | Review only. No new work-table record shape belongs in the transform; fixtures should fail if an unexpected work-table record reaches it. |
| `04-bootstrap-enable-kafka-cdc.md` (`DMS-1323`) | While canonical write admission is closed, prove new-database eligibility and reject nonempty databases rather than attempting CDC retrofit; then atomically create or exact-match the immutable binding and invoke the guarded new-empty-database transition before the first write. Validate the resulting durable state, start queue processing, wait for drain, then complete the heartbeat barrier. Add fail-closed partial/retry behavior, including exact-binding `Disabled` activation retry only with a clear latch, exact-binding empty `Tracking` continuation only with a clear latch, and rejection of any set cache-ahead latch, unbound `Tracking`, or mismatched state. |
| `05-message-contract-tests.md` (`DMS-1324`) | Preserve public record tests. Add evidence that work-table activity emits no public/progress record and that initial CDC admission follows projection catch-up plus the provider barrier. |
| `06-e2e-kafka-scenarios.md` (`DMS-1325`) | Drive API writes into durable work, verify projection and publication, and cover projector restart/backlog recovery without a startup full scan on both providers. |
| `07-ops-docs-runbooks.md` (`DMS-1326`) | Replace audit tuning/diagnosis with queue backlog, oldest work, poison failure, activation mismatch, rebuild, scrub, enqueue-failure diagnosis, and per-write availability/overhead guidance. Make clear that projector downtime permits queued writes but enqueue failure rejects canonical writes. Preserve connector/topic/security/source-history procedures. |

### 7. Synchronize Jira after repository design approval

After the design amendment is reviewed:

- Add a concise pivot note to completed spikes `DMS-1245` and `DMS-1246`, linking the
  amended design owner and explaining that their full-audit contingency was activated.
- Update the `DMS-1308` and `DMS-1309` epic descriptions from the amended epic files.
- Update affected open story descriptions `DMS-1310` through `DMS-1326` from their local
  story files.
- Preserve issue keys and existing relationships.
- Change Jira summaries only if the repository story titles are intentionally changed.
- Verify Jira descriptions link to normative design owners rather than duplicating detailed
  concurrency contracts.

Jira mutations should occur only after explicit approval of the final repository text.

## Required Evidence Matrix

### Schema and provider behavior

- PostgreSQL and SQL Server DDL snapshots contain equivalent work, constrained lifecycle,
  and `CacheAheadRecoveryRequired` objects.
- Provider constraints reject unknown lifecycle values, and administrative tests reject
  every unsupported lifecycle transition.
- PostgreSQL DB-apply evidence covers both statement-level trigger events, and SQL Server
  DB-apply evidence covers its combined multi-event trigger; all three handle multi-row
  statements set-wise.
- SQL Server target validation proves that `nested triggers` with `value_in_use = 1`
  permits indirect `*_Stamp` updates to enqueue work, while a disabled or unreadable
  setting makes projection and cache use ineligible without changing the server setting
  or making the canonical relational API unhealthy.
- Provisioning reruns preserve lifecycle, `CacheAheadRecoveryRequired`, and pending-work
  state.
- Lifecycle state `Disabled` produces no work; `Tracking`, `Resetting`, and `Rebuilding`
  all enqueue.
- In every enqueue-enabled state, a forced enqueue error rolls back the complete canonical
  transaction, including all canonical rows changed by that transaction; no partial
  canonical mutation commits.
- A failed set-based enqueue for any member of a multi-document statement rolls back the
  entire statement and transaction.
- With the projector stopped, canonical writes continue to commit and accumulate durable
  work; with lifecycle state `Disabled`, canonical writes commit without work-table
  mutation.
- Canonical writers can enqueue through the provider trigger execution context but cannot
  directly insert, update, or delete `DocumentProjectionWork`.
- `Resetting` prevents projector/direct-fill cache writes and acknowledgements, reports
  projection non-operational and not caught up, and makes cache-backed reads use
  relational fallback.
- `Rebuilding` permits queue processing but reports projection non-operational and not
  caught up and remains ineligible for cache-backed reads.
- Disabling or removing an enqueue trigger makes the projection non-operational even when
  the work table is empty, without making the canonical relational API unhealthy.
- In `Tracking`, `Resetting`, and `Rebuilding`, insert and every real `ContentVersion`
  change enqueue exactly one coalesced current requirement. In `Disabled`, they record no
  projection work.
- Multi-row stamp/restamp statements enqueue every affected document in an enqueue-enabled
  lifecycle state.
- The representation-restamp utility holds the administrative mutex, uses clear-latch
  `Tracking` for projection/publication mode and clear-latch `Disabled` for canonical-only
  mode, and rejects transitional or latched state before changing a stamp.
- Delete cascades cache and work without manufacturing a public cache tombstone.
- Work table is absent from provider capture/include lists.

### Concurrency and crash behavior

Prove on both providers:

- Projector acknowledges version N before version N+1 commits: N+1 creates work.
- Version N+1 advances work before N acknowledges: N acknowledgement fails.
- Version N+1 is uncommitted while N acknowledges: either serialized outcome preserves
  N+1 work.
- Crash before materialization, before cache transaction, and during the cache transaction
  leaves either work pending or cache and acknowledgement committed together.
- Every failed or cancelled item remains visible in `DocumentProjectionWork`; no
  process-local failure flag is required to preserve completeness.
- Current `S = C = W` is acknowledged safely even when the worker-local candidate is
  stale.
- A stale candidate is never written. When `W = S` and the cache is absent or behind, it
  leaves current work pending and does not set the cache-ahead recovery latch.
- A cache version ahead of the current canonical source does not cause acknowledgement
  and sets `CacheAheadRecoveryRequired`.
- Work behind or ahead of the current canonical source, and missing work for a behind
  cache in the ordinary acknowledgement path, perform no cache write or acknowledgement
  and do not set `CacheAheadRecoveryRequired`. Existing mismatched work remains pending,
  produces bounded diagnostics, and keeps caught-up status false.
- An explicit scrub conditionally changes mismatched work to the current canonical
  requirement and inserts missing work without losing a concurrent newer canonical
  requirement.
- During `Rebuilding`, the coordinator reuses that conditional work-only repair for
  mismatched existing work encountered in a bounded source page, reports the anomaly, and
  continues under its already-held administrative mutex. It does not run a full scrub,
  hand off the mutex, or enqueue unseeded documents outside the current page.
- Suspected cache-ahead state is reclassified from current source, cache, and work in one
  statement before the durable cache-ahead recovery latch is set.
- Two projectors and projector/direct-fill races remain idempotent.
- Delete versus materialization/cache-write preserves the foreign-key post-delete fence.
- A cache-write/acknowledgement transaction that holds the shared singleton-state lock
  completes before an exclusive transition into `Resetting`; one that starts afterward
  observes `Resetting` and performs no cache write or acknowledgement.
- Two administrative coordinators for the same database cannot overlap. Loss of the
  session-owned administrative mutex prevents the former owner from performing a final
  lifecycle transition, and a replacement owner recovers from durable state.
- Administrative commands that resolve one physical database through different connection
  aliases contend on the exact shared provider mutex. Two different databases on the same
  SQL Server or PostgreSQL cluster can hold their respective mutexes concurrently. SQL
  Server tests use different eligible caller principals to prove the explicit `public`
  application-lock scope remains common.
- Canonical writers take no deliberate projector lock on the source `Document` row, but
  may briefly wait on work-row acknowledgement. Tests prove that no work-row lock is held
  during materialization, failure backoff, or external I/O, and measure the resulting
  canonical-write wait time.
- Lock order and retryable failure handling are qualified for PostgreSQL and SQL Server.
  A forced enqueue deadlock or serialization failure proves that the complete canonical
  transaction is replayed and leaves one committed canonical result with its coalesced
  durable work; enqueue is never retried separately after commit.

### Scheduling and fairness

- Page memory is bounded.
- Poison rows remain pending; fair paging prevents a limited number from starving later
  work already admitted to the queue.
- Cursor wraparound revisits failed work.
- Restart resumes from durable work without scanning `dms.Document`.
- Multiple targets share the global concurrency gate fairly.
- A long outage coalesces versions per document and drains successfully.
- Baseline/rebuild seeding does not insert the complete source population ahead of
  processing; its contribution to pending work remains within the bounded high-water mark.
- If persistent failures consume the available seeding capacity, seeding pauses, the
  lifecycle remains `Rebuilding`, and caught-up remains false until enough failures are
  remediated.
- Bounded failure diagnostics identify the affected documents, and the operator runbook
  explains remediation.

### Baseline, scrub, and cache-ahead recovery

- New-empty tracking activation holds the administrative mutex, takes the one-time
  provider-equivalent writer-blocking lock on `dms.Document`, locks the singleton state row
  exclusively, and transitions from `Disabled` to `Tracking` only when the cache-ahead
  latch is clear and `dms.Document`, `dms.DocumentCache`, and
  `dms.DocumentProjectionWork` are all empty.
- A nonempty new-database activation attempt makes no lifecycle change and directs
  read-acceleration users to the offline `Disabled -> Rebuilding -> Tracking` workflow;
  v1 CDC bootstrap rejects it.
- A canonical insert deliberately raced with guarded activation either commits first and
  makes activation reject the nonempty database, or commits after activation and creates
  its transactional projection work. No outcome commits an untracked canonical document.
- Configuration/database-state mismatch fails closed.
- Offline deactivation enters `Resetting`, clears cache and work through the supported
  bounded or provider-qualified path, and reaches `Disabled` only after both are verified
  empty.
- A deactivation crash leaves `Resetting` durable and cache-ineligible; the next
  administrative-mutex owner safely repeats or resumes it.
- Offline activation may be repeated after deactivation, remains `Rebuilding` until its
  baseline drains, then transitions to `Tracking`; it has no CDC or streaming catch-up
  effect.
- Bounded baseline/rebuild is restart-safe and keeps projection operational-health and
  caught-up status false.
- Baseline/rebuild captures a `DocumentId` upper boundary, while transactional enqueueing
  and delete cascades cover inserts, updates, and deletes around that boundary.
- A concurrent delete between baseline page selection and work upsert rolls back and
  rereads that page from the last committed keyset position. The missing document is
  skipped, surviving documents are seeded, and the race creates neither poison work nor a
  cache-ahead recovery incident.
- A baseline/rebuild page that encounters `W != S` conditionally repairs that existing
  work to the current `S` without losing a concurrent newer canonical requirement, does
  not enqueue documents outside the bounded page, and does not require a second scrub
  coordinator or administrative-mutex handoff.
- A billion-document baseline does not enqueue a billion work rows ahead of projection.
- V1 persists no baseline cursor; after interruption, a replacement coordinator reacquires
  the administrative mutex, captures a new boundary, and restarts the scan from the
  beginning while the lifecycle remains `Rebuilding`.
- Restarted baseline/rebuild processing conditionally acknowledges already-current
  source/cache/work versions without rematerializing document JSON.
- Online cache rebuild atomically requires lifecycle `Tracking` or `Rebuilding` and a
  clear cache-ahead latch before entering `Resetting`; a set latch rejects without
  changing lifecycle, cache, work, or latch state and routes the operator to cache-ahead
  recovery or publication containment.
- With that guard satisfied, online rebuild clears cache while preserving pending work,
  continues transactional enqueueing, and enters `Rebuilding` only after cache is empty.
- A rebuild crash while `Resetting` cannot expose a partially cleared cache or allow cache
  acknowledgement; restart repeats or resumes the clear.
- Internal-only cache-ahead recovery closes canonical write admission, drains in-flight
  transactions, stops projector/direct-fill execution, enters `Resetting` with
  `CacheAheadRecoveryRequired` still set, and clears cache and work before entering
  `Rebuilding`, clearing the latch, and starting fresh projector execution.
- Internal-only recovery cannot leave a work row whose required version is ahead of the
  restored canonical source and therefore cannot be acknowledged.
- Internal-only recovery admits no concurrent canonical write while work is being cleared;
  writes admitted after the transition to `Rebuilding` are represented by transactional
  enqueue.
- No committed recovery state has both `CacheAheadRecoveryRequired` clear and
  `ProjectionLifecycleState = Tracking` before baseline seeding and work drain complete.
- Possibly published cache-ahead state remains fenced under the existing deferred
  new-namespace rule.
- Cache-ahead recovery rejects the internal-only reset when downstream publication is
  possible or uncertain, preserving the cache, work inventory, and cache-ahead recovery
  latch for diagnosis.
- Explicit scrub discovers missing, behind, and ahead cache states plus missing or
  mismatched work without becoming a periodic operational-health or caught-up dependency.
  Preflight admits only lifecycle `Tracking` with a clear cache-ahead latch; every other
  lifecycle and a pre-existing set latch reject before the intentionally O(N) scan or
  mutation. An admitted scrub repairs work-only anomalies, may set the durable latch only
  for current cache-ahead state, and never clears it.
- After a suspected restore or unsupported direct mutation, runbooks require an explicit
  scrub before operators rely on queue-empty caught-up status; routine no-scan status does
  not claim to discover previously absent work.

### Projection health, catch-up, API routing, and CDC admission

- Queue absence uses an indexed `NOT EXISTS` plan independent of total document count.
- Oldest-work lookup uses its intended index.
- Projection-health and caught-up polling performs no source/cache full scan or routine
  exact backlog count.
- A new committed mutation leaves an operational target healthy but makes projection
  caught-up status false until the work is acknowledged.
- Queue presence, projection backlog, or a projection-only failure does not remove an
  otherwise healthy DMS replica from normal API routing.
- Enqueue failures are reported separately from projector-processing failures: an enqueue
  failure rejects its canonical write, whereas a projector failure leaves durable work
  pending for retry.
- Ordinary API traffic remains available through relational fallback while projection is
  non-operational or not caught up.
- Initial CDC bootstrap creates or exact-matches the immutable binding before guarded
  tracking activation. An exact binding with lifecycle `Disabled` and a clear latch retries
  activation; an exact binding with lifecycle `Tracking`, a clear latch, and empty tables
  resumes setup; a set cache-ahead latch, unbound `Tracking`, any other lifecycle, a binding
  mismatch, or unexpected pre-capture rows fails closed and requires
  cleanup/reprovisioning as applicable.
- Initial CDC admission waits for projection catch-up, the provider heartbeat barrier, and
  a second projection caught-up observation before opening canonical write admission.
- Queue growth after canonical write admission opens does not revoke CDC admission or
  ordinary API readiness.
- Work-table changes never reach public or progress topics.

### Performance qualification

Run provider-specific benchmarks before accepting the design as implementation-ready:

- Baseline canonical write throughput with lifecycle state `Disabled`.
- Canonical write throughput and latency with lifecycle state `Tracking` and a caught-up
  projector.
- Canonical write throughput and enqueue correctness while lifecycle state is `Resetting`.
- Sustained writes with projector delayed and active.
- Same-document update throughput and canonical-write lock-wait latency while
  acknowledgement is active.
- Update/delete versus cache-write/acknowledgement deadlock frequency and complete-
  transaction retry behavior.
- Verification that projector cancellation, timeout, or failure cannot leave a work-row
  lock open beyond the short database transaction.
- High-fan-out identity propagation.
- Bulk restamp.
- Queue drain after controlled outages.
- Peak seed-generated backlog and log amplification during a large baseline/rebuild.
- Interrupted baseline/rebuild restart-from-the-beginning completion time, database load,
  and repeated queue-DML/write amplification at representative supported scale.
- Shared singleton-state lock throughput and exclusive `Resetting` transition drain time
  at expected projector concurrency.
- Large-cache and work reset duration, blocking, log amplification, cleanup, and
  crash/restart behavior using the supported bounded and provider-specific clear paths.
- Proof that holding the administrative mutex does not block ordinary canonical or
  projector transactions.
- PostgreSQL WAL/vacuum/bloat and SQL Server log/ghost/index behavior.
- Projection operational-health, caught-up, and oldest-work queries with a
  100-million-document source and representative empty, small, and large work inventories.

Record explicit pass/fail thresholds during refinement. The threshold must protect normal
write service levels and demonstrate that operational-health and caught-up observation
cost does not scale with total `dms.Document` cardinality. If restart-from-the-beginning
qualification fails, create a new ticket to design and implement a durable baseline cursor;
completion of that new ticket is required for production qualification.

## Documentation Validation

Before merging the amendment:

1. Search the repository for stale ordinary-path terms:

   ```bash
   rg -n -i \
     'full audit|incremental cursor|exact-zero audit|MaximumAuditAge|current database difference|no durable projection queue' \
     --glob '*.md'
   ```

   Every remaining match must describe an explicit baseline/rebuild/scrub, historical
   decision, or superseded presentation material.

2. Search for the removed projector index and settings and resolve every reference.
3. Validate all relative Markdown links and anchors, especially renumbered `data-model.md`
   sections.
4. Verify `CDC-INV-*` evidence ownership matches the amended E18/E19 story files.
5. Compare every local epic/story file with its planned Jira description before Jira sync.
6. Review diffs for accidental changes to the public Kafka contract, API cache freshness,
   authorization, Change Queries, or relational fallback.

## Completion Criteria

The pivot is complete when:

- One normative design consistently defines transactional work recording and
  conditional acknowledgement.
- In every enqueue-enabled state, canonical mutation and required work recording commit or
  roll back together under a qualified least-privilege and complete-transaction retry
  contract, while projector downtime alone does not block canonical writes.
- No startup, restart, steady-state, operational-health, or caught-up path requires
  scanning every current document.
- Optional projection has an explicit durable activation and disablement lifecycle.
- Initial baseline, clear-latch-guarded rebuild, clear-latch `Tracking` scrub, and
  cache-ahead recovery have distinct fail-closed semantics. A set latch rejects ordinary
  rebuild and scrub without mutation, while possibly published cache-ahead data retains
  its stricter deferred new-namespace containment.
- Cache/work clearing is protected by the durable `Resetting` fence without requiring one
  unbounded clear transaction or an exclusive singleton-state lock for the duration.
- One database-scoped administrative mutex serializes each complete lifecycle-changing,
  baseline, rebuild, recovery, scrub, and representation-restamp workflow across
  coordinators.
- PostgreSQL and SQL Server physical and concurrency contracts are equivalent.
- Projection operational health is distinct from projection caught-up status and neither
  signal gates ordinary DMS API routing.
- Initial CDC bootstrap reserves the immutable binding before guarded activation and has
  explicit fail-closed classifications for every binding/lifecycle crash boundary.
- Initial CDC admission composes projection caught-up status with the existing provider
  heartbeat barrier.
- Every E18 and E19 epic/story file either reflects its changed responsibility or records
  an intentional no-change review.
- Supporting planning, presentation, bootstrap, dependency, traceability, and Jira
  material is synchronized.
- The implementation stories require measured per-write and queue-drain performance
  evidence at the intended scale.
