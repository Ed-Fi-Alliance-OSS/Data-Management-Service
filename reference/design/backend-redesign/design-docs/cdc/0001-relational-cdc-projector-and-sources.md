---
status: proposed
date: 2026-07-24
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
select the projector; ordinary DMS correctness and normal API routing do not depend on it.

Projection completeness comes from an always-provisioned, transactionally maintained
`dms.DocumentProjectionWork` table. Every supported canonical insert and every real
`dms.Document.ContentVersion` change records the required version in that table in the
same transaction. The projector fairly pages durable work, materializes the latest
coherent source, writes the cache monotonically, and conditionally acknowledges the work
in the same short database transaction as the cache write. Startup, restart, steady-state
processing, operational-health polling, and caught-up polling do not scan the complete
`dms.Document`/`dms.DocumentCache` relationship.

One Debezium connector uses two complementary public document sources plus one internal
source-position heartbeat source:

| Source event | Public document-state result |
| --- | --- |
| `dms.DocumentCache` create, update, or snapshot/read | Document upsert |
| `dms.Document` delete | Kafka tombstone |
| `dms.DocumentCache` delete or truncate | Ignore |
| Any other `dms.Document` operation or snapshot/read | Ignore |
| Any `dms.CdcHeartbeat` operation or Debezium heartbeat | Ignore; advance only the internal source offset |
| Any `dms.DocumentProjectionWork` operation | Not captured; no public or progress record |

`DocumentProjectionWork` exclusion is a connector/provider-capture invariant, not an SMT
drop rule. If a misconfigured connector nevertheless delivers a work-table record, the
public-contract transform fails closed as an unexpected source rather than silently
advancing the source offset. The
[topic/message contract](0002-kafka-topic-and-message-contract.md#connector-transformation)
owns that transform behavior.

`dms.DocumentCache.DocumentJson` supplies the caller-agnostic API-shaped upsert payload,
and `StreamEtag` supplies the DMS-computed ETag for that fixed representation.
`dms.Document` supplies the authoritative lifecycle delete and stable `DocumentUuid`.
Cache deletion has no domain meaning.

This record owns projection behavior and source selection. Physical objects are owned by
[data-model.md](../data-model.md). Configuration, deployment, health, CDC admission, and
operations are specified in
[Relational CDC and Document Projection](../../../cdc-streaming.md). The public Kafka
contract is specified in
[0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md).

## Cached Document Contract

`dms.DocumentCache` contains one caller-agnostic, pre-profile, full API-shaped projection
per current document. Its physical columns, keys, constraints, indexes, and validation
triggers are defined only in
[`data-model.md`](../data-model.md#6-dmsdocumentcache-always-provisioned-optional-projection).

The table retains `DocumentId` as its compact primary/foreign key and stores
`DocumentUuid` as a non-indexed denormalized connector-key column. Provider-specific
cache insert/update triggers compare that value with canonical `DocumentUuid` through the
existing `DocumentId` primary key and reject a mismatch. Canonical `DocumentUuid` is
immutable and unique, so both captured sources use the same logical UUID key without a
cache UUID index or another canonical composite index.

`DocumentJson` is produced by the same compiled relational read and reconstitution rules
as GET/query response assembly. It includes stable top-level `id` and
`_lastModifiedDate`; when link injection is compiled into the read plan, it includes
reference `link` subtrees. It excludes authorization arrays, EdOrg hierarchy JSON, API
client identity, and readable-profile-specific projection.

The cache does not store an `_etag` inside `DocumentJson`. It stores `StreamEtag`, the
ETag for the fixed CDC representation. The materializer calls the shared DMS served-ETag
composer with the row's `ContentVersion`, selected effective-schema hash, JSON format, no
readable profile, the fixed document-kind link mode, and identity content coding. API
serving ignores `StreamEtag` and composes the request-specific `_etag`.

A dedicated cache materializer returns row metadata and `DocumentJson` as one coherent
result and validates:

```text
DocumentCache.DocumentUuid == Document.DocumentUuid for DocumentId
DocumentJson.id == DocumentUuid
DocumentJson._lastModifiedDate == formatted ContentLastModifiedAt
StreamEtag == DMS served-ETag composition for the fixed stream representation
```

An invariant failure produces no cache write. `LastModifiedAt` remains payload and
diagnostic metadata; `ComputedAt` remains operational metadata. Neither is a freshness or
ordering predicate.

## Durable Work and Lifecycle

`dms.DocumentProjectionWork` contains one coalesced requirement per document:

```text
DocumentId                  primary key, foreign key to dms.Document on delete cascade
RequiredContentVersion      bigint
FirstEnqueuedAt             provider UTC timestamp
LastEnqueuedAt              provider UTC timestamp
```

The `(FirstEnqueuedAt, DocumentId)` index supports oldest-first paging and oldest-work
observation. V1 has no claim, lease, worker ownership, attempt count, dead-letter, epoch,
or durable baseline-cursor column. Repeated mutations update one row, so ordinary backlog
is bounded by the number of distinct changed documents rather than the number of changes.

The singleton `dms.DocumentCacheState` carries two independent safety concepts:

```text
ProjectionLifecycleState = Disabled | Resetting | Rebuilding | Tracking
CacheAheadRecoveryRequired = false | true
```

`ProjectionLifecycleState` is one constrained provider-equivalent value:

- `Disabled`: enqueueing, projector/direct-fill writes, acknowledgements, and cache-backed
  reads are disabled.
- `Resetting`: enqueueing remains enabled, but cache writes, acknowledgements, and
  cache-backed reads are fenced while an administrative workflow clears projected state.
- `Rebuilding`: enqueueing and projector/direct-fill writes are enabled, but cache-backed
  reads, operational-health success, and caught-up success remain disabled until bounded
  seeding completes and durable work drains.
- `Tracking`: enqueueing and ordinary work processing are enabled. Cache reads still
  require row freshness and a clear cache-ahead latch. The name alone does not imply that
  projection is caught up.

`CacheAheadRecoveryRequired` is an orthogonal durable safety latch set only when a current
cache version is ahead of the current canonical version. The latch blocks every
cache-backed read and cache/direct-fill write without changing lifecycle state. Missing
or mismatched work is an internal work-inventory anomaly, not a cache-ahead incident.

Provisioning inserts the singleton in `Disabled` with the latch clear and never resets
existing mutable state on rerun. Runtime `DocumentCache:Targets` configuration does not
change database lifecycle state. A configured `Disabled` target is projection-unavailable
but does not make the canonical relational API unhealthy. Removing a runtime target
pauses processing without discarding work or disabling tracking.

The only v1 lifecycle transitions are:

```text
new empty database:             Disabled -> Tracking
activation:                     Disabled -> Rebuilding -> Tracking
rebuild (latch clear):          Tracking|Rebuilding -> Resetting -> Rebuilding -> Tracking
internal recovery (latch set):  Tracking|Rebuilding -> Resetting -> Rebuilding -> Tracking
deactivation:                   Tracking|Rebuilding -> Resetting -> Disabled
```

Internal recovery is the proven-internal-only cache-ahead workflow. Its lifecycle path
matches rebuild, but the cache-ahead latch remains set through `Resetting` and is cleared
only in the verified transition to `Rebuilding`. No other transition is supported.

### Administrative Serialization and State-Row Fencing

Every lifecycle-changing, cache/work-clearing, baseline-seeding, recovery, integrity scrub,
or representation-restamp workflow first acquires one provider-equivalent, database-scoped,
session-owned administrative mutex through one shared provider adapter.

PostgreSQL uses `pg_advisory_lock(bigint)` with the exact key:

```text
(811646948::bigint << 32) | currentDatabaseOid::bigint
```

`811646948` (`0x3060BFE4`) is the fixed v1 projection-administration namespace.
`currentDatabaseOid` is the current database's unsigned `pg_catalog.pg_database.oid` in
the low 32 bits. Normal completion calls `pg_advisory_unlock(bigint)` with that exact key.

SQL Server uses `sp_getapplock` with the exact resource
`EdFi.DMS.DocumentProjection.Administration.v1`, `Exclusive` mode,
`@LockOwner = 'Session'`, and `@DbPrincipal = 'public'`. A negative return code aborts the
operation before it changes durable state. Normal completion calls `sp_releaseapplock`
with the same resource, owner, and principal.

Neither provider derives the identity from tenant key, `DataStoreId`, connection-string
database name, connection alias, or mutable `DataStoreIdentity`. Aliases of one physical
database therefore contend, while different databases on the same server or PostgreSQL
cluster may be administered concurrently. Every coordinator holds the mutex on a dedicated
open connection across the entire multi-transaction workflow, including the final state
transition when one exists.

Every coordinator-issued database mutation in that workflow executes on the same physical
database session that owns the mutex. This includes lifecycle or latch transitions,
cache/work clearing, baseline or scrub work-table changes, and representation-restamp
batches. The session may span multiple short transactions. Ordinary projector workers are
excluded and continue to use their own connections.

The connection does not return to a pool while holding the mutex. Normal completion
explicitly releases it. Connection resiliency does not transparently reconnect and continue
under presumed mutex ownership. Session loss releases the mutex, rolls back any active
transaction on that session, and aborts the coordinator; the former owner performs no later
database mutation through a replacement connection. An operator reissues the same
operation, and the replacement owner reacquires the mutex and revalidates durable state
before repeating or resuming it. A replacement never infers the intended operation from
`Resetting` alone.

Ordinary writers, projector workers, direct fill, reads, and health checks do not acquire
the administrative mutex. Every cache-write/acknowledgement transaction instead reads the
singleton state row under a provider-equivalent shared row lock, verifies lifecycle
`Tracking` or `Rebuilding` and a clear latch, and holds that lock through commit. Short
transitions into or out of `Resetting` take the same row lock exclusively. Therefore an
entry into `Resetting` waits for earlier cache transactions, and later cache transactions
observe the fence and do no cache write or acknowledgement. The exclusive row lock is not
held while large tables are physically cleared.

### Guarded New-Empty Activation

Before the first canonical write to a newly provisioned projection target, deployment
control may perform one guarded `Disabled -> Tracking` transaction while write admission
is closed and in-flight writers have drained. Under the administrative mutex, it:

1. takes a provider-equivalent lock on `dms.Document` that blocks inserts, updates, and
   deletes for the transaction;
2. exclusively locks the singleton state row;
3. verifies `Disabled` and a clear cache-ahead latch;
4. verifies `dms.Document`, `dms.DocumentCache`, and
   `dms.DocumentProjectionWork` are empty; and
5. for SQL Server, revalidates RCSI and server-level `nested triggers`; and
6. changes the lifecycle to `Tracking` and commits before write admission opens.

If any table is nonempty or a provider prerequisite is not satisfied, no state changes.
A deliberately racing canonical insert either commits first and makes activation reject
the nonempty database, or commits after activation and transactionally enqueues work. CDC
v1 rejects the nonempty case rather than retrofitting capture. Read acceleration on an
existing database uses the explicit offline activation workflow below.

## Transactional Enqueue

One set-based provider-equivalent mechanism on `dms.Document` records every initial
version and every real `ContentVersion` change:

- PostgreSQL uses an `AFTER INSERT ... REFERENCING NEW TABLE` statement trigger and a
  separate `AFTER UPDATE ... REFERENCING OLD TABLE ... NEW TABLE` statement trigger.
  PostgreSQL requires two trigger definitions because transition tables cannot combine
  these events. The update trigger filters unchanged versions.
- SQL Server uses one set-based `AFTER INSERT, UPDATE` trigger over `inserted` and
  `deleted`. Because resource `*_Stamp` triggers update `dms.Document`, a SQL Server
  projection target requires server-level `nested triggers` with `value_in_use = 1`.
  Target-context initialization and activation from `Disabled` validate this prerequisite;
  generated `*_Stamp` triggers do not query `sys.configurations`.

Each implementation reads exactly the `StateId = 1` lifecycle row once per triggering
statement. A missing singleton or an unreadable/invalid lifecycle is an enqueue failure,
never an implicit `Disabled` result. In `Tracking`, `Resetting`, or `Rebuilding`, the
trigger inserts or advances
`RequiredContentVersion` to the greater existing or new version, preserves
`FirstEnqueuedAt` while work remains pending, and advances `LastEnqueuedAt` only when the
required version advances. In `Disabled`, it records nothing.

The mechanism covers direct resource writes, child and extension changes, propagated
reference-identity changes, descriptors, bulk restamping, and every other supported path
that converges on `dms.Document.ContentVersion`. `ON DELETE CASCADE` removes obsolete
work on canonical deletion.

Work recording is mandatory in every enqueue-enabled lifecycle state. Failure to insert
or advance work aborts the complete canonical transaction; trigger code does not suppress
errors, and application code never commits canonical data and retries enqueueing
separately. A retryable deadlock, serialization failure, or lock timeout replays the
complete canonical transaction under the provider retry policy. Non-transient schema,
permission, constraint, or queue-storage failures reject the write with target-specific
diagnostics.

This intentionally couples write availability to the enqueue schema while tracking is
enabled. It does not couple writes to projector availability or queue drain: when the
projector is stopped, unrelated canonical writes continue and durable work accumulates.
Least-privilege trigger execution permits canonical writers to enqueue only through the
trigger while denying them direct work-table DML.

## Freshness and Reconciliation

Row-level freshness is exactly:

```text
DocumentCache.ContentVersion == Document.ContentVersion
```

A version-equal row remains ineligible when lifecycle is not `Tracking` or the cache-ahead
latch is set. Durable work, not a source/cache scan, is the completeness inventory.

Workers fairly keyset-page `DocumentProjectionWork` in
`(FirstEnqueuedAt, DocumentId)` order. A process-local page cursor may advance past a
failed item and wrap to the beginning, but it is neither durable state nor completeness
evidence. A small number of poison rows must not starve later admitted work. Pages and
per-target failure state remain bounded, and all failed work remains visible in the
database.

For each selected work row, a worker:

1. captures `DocumentId` and `RequiredContentVersion`;
2. may take the equal-version acknowledgement fast path described below;
3. otherwise materializes the latest coherent canonical document without holding a work
   row lock or deliberate write-conflicting source-row lock;
4. performs the optimistic current-version coherence check after hydration; and
5. enters one short cache-write/acknowledgement transaction.

That transaction:

- takes the shared singleton-state lock and verifies `Tracking` or `Rebuilding` plus a
  clear latch;
- reads current canonical version `S`, optional cache version `C`, and optional work
  requirement `W` in one provider-consistent statement snapshot;
- writes the cache monotonically only when the materialized candidate still equals the
  current canonical and work versions;
- treats current durable `S = C = W` as already projected and conditionally acknowledges
  the work regardless of the worker-local candidate version; and
- deletes work only when its required version still equals the current canonical and
  cached version.

The one-statement classification selects the action but is not a substitute for
concurrency predicates on later DML. Conditional cache DML repeats the candidate/source/
work version predicates, and conditional acknowledgement repeats the work/source/cache
version predicates. A canonical commit after classification must either prevent the stale
cache action when visible to that DML or preserve/recreate its newer work through the
enqueue/acknowledgement serialization described below.

The cache write and acknowledgement commit or roll back together. The cache safety checks
and monotonic DML precede the conditional work delete, which is the final DML operation.
The transaction must not lock the work row before accessing `DocumentCache` or the parent
`Document` needed by foreign-key and UUID validation. Provider implementations document
the equivalent lock order and retry the complete cache/acknowledgement transaction after
a deadlock.

### Current Source/Cache/Work Classification

The worker-local candidate is not durable classification evidence. One current-visibility
statement compares:

- `S`: current `dms.Document.ContentVersion`;
- `C`: current `dms.DocumentCache.ContentVersion`, if present;
- `W`: current `dms.DocumentProjectionWork.RequiredContentVersion`, if present.

| `S` | `C` | `W` | Classification and action |
| ---: | ---: | ---: | --- |
| 11 | 10 | 11 | Healthy pending projection; candidate 11 may be written, then work acknowledged. |
| 11 | 11 | 11 | Already projected; conditionally acknowledge even if the local candidate is older. |
| 11 | 11 | absent | Current for this document; no action. |
| 11 | 12 | any | Genuine cache-ahead state; set the durable latch after reclassification. |
| 11 | absent or at most 11 | 10 | Work is behind; leave it pending for explicit scrub repair. |
| 11 | absent or at most 11 | 12 | Work is ahead; leave it pending for explicit scrub repair. |
| 11 | 10 | absent | Cache is behind with missing work; explicit scrub inserts current work. |

`any` includes absent. Cache-ahead takes precedence because only `C > S` may represent
unsafe projected or published state. Every invalid relationship performs no cache write
or acknowledgement. A missing canonical row is not an incident; delete cascades and the
cache foreign key fence obsolete activity.

Supported canonical mutation commits `S` and `W` atomically, so `W != S` indicates
restore, corruption, unsupported direct mutation, or a broken enqueue mechanism. In
ordinary `Tracking`, mismatched work remains pending, emits bounded diagnostics, and keeps
caught-up false. An explicit scrub conditionally changes it to current `S`, or inserts
missing `W = S`, without losing a concurrent newer requirement. During an explicit
`Rebuilding` workflow, the rebuild coordinator applies the same conditional work-only
repair only to mismatched work encountered in its current bounded source page while
holding its existing administrative mutex. It does not run another scrub or seed outside
that page.

Canonical rows not yet visited by an incomplete `Rebuilding` baseline may legitimately
have neither cache nor work. The lifecycle fence already makes projection non-operational,
not caught up, and cache-ineligible, so those unseeded rows are not classified as
missing-work anomalies. They enter the ordinary source/cache/work classification only
after their baseline page has been seeded and processed, or after the baseline has
finished.

An older candidate is never written and is not itself a cache-ahead incident. When current
durable state is `S = C = W`, acknowledgement is safe regardless of the candidate. When
`W = S` and cache is missing or behind but the candidate is stale, work stays pending.
When work is absent and `C = S`, there is nothing to acknowledge.

Suspected cache-ahead state first performs no write or acknowledgement. A short incident
transaction exclusively locks the singleton state row, re-runs the one-statement
classification, and sets `CacheAheadRecoveryRequired` only if current `C > S` remains.
Three unrelated reads are insufficient because concurrent commits could combine values
from different database moments.

### Enqueue/Acknowledge Interleavings

`DocumentProjectionWork` is a short-lived per-document serialization point, not a source
row commit-order fence:

- If version N is acknowledged before N+1 commits, the N+1 canonical transaction inserts
  or recreates work.
- If N+1 advances work before N acknowledges, the N conditional delete fails.
- If N+1 is uncommitted while N acknowledges, provider serialization yields one of those
  two safe committed outcomes.

Materialization, backoff, cancellation, and external I/O hold no work-row lock. A writer
may briefly wait on an acknowledgement transaction for the same document, and an
acknowledgement may briefly wait on a writer, but unrelated documents continue. Delete
remains fenced by foreign keys. Multiple projectors and projector/direct-fill races remain
correct through monotonic writes and conditional acknowledgement.

Optional direct fill uses the same cache-write/acknowledgement component. It may
acknowledge matching work, remains best effort, and never fails the relational response.

### Bounded In-Process Execution Policy

Each configured target has an isolated execution context. A process-wide fair concurrency
gate bounds simultaneous target work. One target failure or cancellation does not stop
peers. Designated projector hosts avoid duplicate work, but correctness does not require a
distributed worker lease; multiple replicas may safely process the same rows.

The ordinary settings surface includes queue poll interval, page size, process-wide target
concurrency, failure backoff, baseline-seeding high-water mark, and direct-fill timeout.
The high-water mark may be derived rather than independently configurable. There is no
incremental-source scan interval, scheduled relationship-scan interval, scan-age
readiness setting, or exact routine backlog count.

Cancellation is observed between pages/items and while waiting for the global gate.
Shutdown starts no new work and allows only a current short database transaction to commit
or roll back within its command timeout. Restart resumes directly from durable work without
scanning `dms.Document`.

## Projection Operational Health, Caught-Up Status, and CDC Admission

Three contracts remain distinct:

1. **Projection operational health:** the target can safely process ordinary work.
2. **Projection caught-up status:** one statement observed an operational `Tracking`
   target with no committed durable work.
3. **Initial CDC admission:** deployment control composed caught-up status with the
   provider heartbeat barrier while canonical write admission remained closed.

Neither projection signal is ordinary DMS API health/readiness. Projection unavailable,
behind, or failed means cache-backed reads fall back to relational reconstruction. Queue
presence or a projection-only failure does not remove an otherwise healthy DMS replica
from normal API routing.

Process eligibility requires target/source resolution, a running execution context,
validated state/work schema and enabled enqueue trigger inventory, and successful
provider-prerequisite validation for the current execution context. SQL Server
additionally requires RCSI and
`sys.configurations.value_in_use = 1` for `nested triggers`. A false or unreadable result
makes projection/cache use ineligible without changing the server setting or the health of
the canonical relational API. V1 validates these settings when initializing the target
execution context and before activation from `Disabled`, but does not continuously
revalidate an active target. The correction-and-restart workflow after an
initialization-time failure is supported only when the observed lifecycle is `Disabled`.
For any other lifecycle, restart alone must not restore projection health or CDC
eligibility. A
`Tracking` target may have missed indirect projection work while `nested triggers` was
disabled and requires a writer fence plus integrity scrub or rebuild before eligibility
can be restored. V1 has no such fence for an admitted database, so the target remains
projection- and CDC-ineligible. An activation-preflight failure changes no lifecycle state
and may be retried after correction. Activation validation is command-local;
target-context initialization owns process-local validation. Changing either prerequisite
after successful validation while the target is active, including its effects and
recovery, is outside the supported v1 contract.

The durable operational-health observation is:

```text
ProjectionLifecycleState = Tracking
AND CacheAheadRecoveryRequired is false
```

Queue presence does not make an otherwise functional projector unhealthy.

Caught-up status requires process eligibility and one provider-consistent statement that
observes:

```text
ProjectionLifecycleState = Tracking
AND CacheAheadRecoveryRequired is false
AND NOT EXISTS (SELECT 1 FROM dms.DocumentProjectionWork)
```

Failure to execute the observation returns unknown; an earlier success is not reused.
This is exact only at its statement boundary and depends on the validated enqueue
mechanism and supported-mutation boundary. It is not an independent source/cache integrity
scan. Normal telemetry uses indexed work existence and oldest-work queries plus bounded or
provider-estimated backlog observations; exact counts are explicit diagnostics only.

For initial CDC admission, deployment control observes caught up, captures and crosses the
provider source-position heartbeat barrier, then performs a second caught-up observation
before opening canonical write admission. Queue growth after admission does not revoke API
or CDC admission; subsequent status is observational.

## Baseline, Rebuild, Deactivation, and Scrub

Every operation in this section holds the database administrative mutex.

### Offline Read-Acceleration Activation

Runtime configuration never activates database tracking. To activate an existing
`Disabled` database, operators stop all DMS replicas, projectors, direct fill, seed/bulk
loaders, administrative writers, and every other canonical writer, then drain in-flight
transactions. The coordinator revalidates provider prerequisites, including SQL Server
RCSI and server-level `nested triggers`, then clears residual cache/work state through
supported bounded/qualified paths, exclusively locks the state row in one short
transaction, verifies `Disabled`, a clear cache-ahead latch, and both tables empty, and
enters `Rebuilding`. Failed prerequisite validation leaves lifecycle and tables unchanged
and the operation may be retried after correction.

With write admission still closed, it starts only the designated projector execution
needed to drain seeded work, captures a maximum `DocumentId` boundary, and keyset-scans
canonical documents through that boundary in bounded pages. This baseline routine is also
used by online rebuild, which keeps write admission open, and by internal recovery after
write admission reopens. Offline activation itself admits no concurrent canonical writes;
the delete-race and concurrent-enqueue rules below apply when those other workflows run
the shared routine with writes admitted.

Each committed page idempotently seeds current required versions. A page invalidated by a
concurrent delete rolls back and rereads from its last committed key; the deleted row
disappears and survivors are seeded. Existing mismatched work in that page is
conditionally repaired.

Seeding is backpressured by a bounded `high-water mark + 1` observation. It pauses when
pending work reaches the high-water mark and resumes as work drains. Poison rows remain
pending and can pause completion rather than allowing unbounded seed amplification.
The limit bounds only baseline-generated amplification. Where a workflow admits canonical
writes during seeding, those writes may enqueue concurrently and grow total work beyond
the limit; transactional enqueue covers inserts/updates around the boundary, and deletes
cascade.

V1 persists no baseline cursor. After interruption, a replacement owner captures a new
boundary and restarts from the beginning while lifecycle remains `Rebuilding`.
Equal-version cache/source/work rows can be acknowledged without rematerializing JSON.
Qualification must measure this restart behavior; failure of the agreed scale thresholds
requires a new durable-baseline-cursor ticket before production qualification.

Only after scanning finishes and work is empty does the coordinator transition to
`Tracking` and open write admission. This activation has no CDC eligibility, Kafka
catch-up, or streaming-baseline effect.

### Online Cache Rebuild

An internal cache rebuild may keep canonical writes online only while the cache-ahead
latch is clear:

1. in one short transaction, exclusively lock the state row, verify lifecycle is
   `Tracking` or `Rebuilding` and the latch is clear, then enter `Resetting`;
2. clear only `DocumentCache` through bounded transactions or a qualified provider fast
   clear, preserving pending work while transactional enqueue continues;
3. enter `Rebuilding` after verifying cache empty;
4. run the bounded/backpressured baseline; and
5. enter `Tracking` only after seeding completes and work drains.

A set latch rejects the online-rebuild command before any lifecycle, cache, work, or latch
mutation. The command reports that the operator must use the cache-ahead recovery or
publication-containment contract below; it never selects recovery automatically because
downstream-observation eligibility must first be established.

A crash in `Resetting` leaves cache use and acknowledgement durably fenced; the next owner
repeats or resumes the explicitly requested clear only while the latch remains clear. A
crash in `Rebuilding` restarts the baseline.

### Offline Deactivation

Deactivation uses the same explicit offline writer fence as activation. It enters
`Resetting`, clears both work and cache through supported bounded/qualified paths, and in a
final exclusive state-row transaction verifies both empty before entering `Disabled`.
The latch may be cleared only with proof that projected state was internal-only and could
not have been observed downstream. A crash leaves `Resetting` durable, and the next owner
repeats or resumes the requested deactivation.

The simple offline activation/deactivation toggle is available only when projection is
proven internal-only. A database with an active or historical downstream consumer or CDC
binding is ineligible even if its connector is currently stopped or its runtime target
entry has been removed. Those databases remain governed by the CDC containment and
recovery contract; removing a runtime target only pauses processing and never authorizes
work/cache clearing or a transition to `Disabled`.

### Explicit Integrity Scrub

After acquiring the administrative mutex, scrub preflight requires lifecycle `Tracking`
and a clear cache-ahead latch. Any other lifecycle or a latch already set rejects the
command before the relationship scan or any cache, work, lifecycle, or latch mutation.

An admitted explicit or very infrequent operator-requested scrub performs the intentionally
O(N) canonical/cache/work relationship scan. It conditionally inserts missing work, repairs
mismatched requirements to current canonical versions, and sets the durable latch only for
current cache-ahead state. A scrub may set that latch but never clears it. Baseline-seeding
high-water, backpressure, and durable-cursor requirements do not apply to scrub; an
implementation may still page the scan for operational reasons. Scrub recency is not
operational-health or caught-up evidence. After a restore or unsupported direct mutation is
suspected, operators run a scrub before relying on queue-empty status.

Direct `DocumentProjectionWork` insert/update/delete, cache truncation or clearing, and
lifecycle-state mutation outside the supported runtime writer and serialized
administrative operations are unsupported. Database grants and operator guidance must
make this boundary explicit.

## Cache-Ahead Invariant Recovery

A current cache version greater than canonical cannot result from supported same-source
monotonic projection. It indicates corruption, in-place restore/reset, or unsupported
reuse. The projector never lowers it automatically. A later equal version, empty work
table, document deletion, or restart never clears the latch.

For a projection proven internal-only, recovery is an offline administrative workflow:

1. acquire the administrative mutex on its dedicated connection;
2. close canonical write admission and drain transactions;
3. stop projector and direct fill;
4. under the exclusive state-row lock, verify lifecycle is not `Disabled` and the latch is
   set, then enter `Resetting` while leaving the latch set;
5. clear all cache and work rows using supported bounded/qualified paths; and
6. in one final exclusive state-row transaction verify `Resetting`, latch set, and both
   tables empty, then enter `Rebuilding` and clear the latch.

Write admission may reopen after that final commit. Fresh projector execution performs
the bounded baseline while transactional enqueue covers new writes. Projection remains
non-operational and not caught up until seeding completes, work drains, and lifecycle
returns to `Tracking`.

Clearing work is required because a restored source may have a version lower than an old
requirement. A crash while clearing preserves both `Resetting` and the latch; a crash after
entering `Rebuilding` restarts baseline seeding.

If the higher cache value may have been published, or downstream observation is uncertain,
this recovery is prohibited. Publication is stopped, and cache, work, and latch remain for
diagnosis. Safe recovery requires a new downstream namespace; for Kafka that means a new
binding generation, topic, consumer state namespace, and snapshot. That workflow remains
deferred in v1. A lower canonical version is never published as an in-place correction to
the old namespace.

## Cache-Backed Reads and Domain Lifecycle

Authorization and query candidate selection always use relational sources. A cache row may
supply response-body assembly only when:

- lifecycle is `Tracking`;
- `CacheAheadRecoveryRequired` is clear; and
- cache and canonical `ContentVersion` are equal in the lookup observation.

Missing, stale, `Disabled`, `Resetting`, `Rebuilding`, latched, or projection-ineligible
states fall back to relational reconstitution. Readable-profile projection, link
stripping, and served `_etag` composition run identically for cached and relational paths.
Optional direct fill uses the shared cache-write/acknowledgement component.

API deletion remains independent of projection:

1. resolve and authorize the canonical target;
2. delete the concrete resource/descriptor while `dms.Document` exists so Change Queries
   can record its tombstone; and
3. delete `dms.Document`, cascading cache, work, and other relational cleanup.

The API does not wait for projection. Create followed by delete before projection may
publish only a tombstone, which is valid state-stream behavior. Cache/work clearing,
eviction, or rebuild publishes no domain tombstone.

## Rationale

The anticipated contingency in the earlier scan-based design has been met: qualification
must not make startup, restart, health, or caught-up cost proportional to total document
cardinality. Transactional coalesced work closes the late-commit discovery gap and makes
restart and failure recovery durable without retaining unbounded process state.

The design deliberately keeps work recording smaller than an event log and keeps worker
coordination duplicate-safe rather than lease-heavy. It also preserves the earlier
decision not to take a deliberate write-conflicting source-row lock as a cache commit-order
fence. Short contention moves to one document's work row, where conditional acknowledgement
makes every enqueue/acknowledge interleaving safe.

O(N) source scans remain appropriate when explicitly requested to establish or repair
inventory: initial activation of an existing database, rebuild, or integrity scrub. They
are not routine completeness observations.

## Consequences

- While lifecycle is enqueue-enabled, failure of the work schema/trigger rejects the
  affected canonical write. Projector downtime alone does not reject writes; work queues.
- PostgreSQL and SQL Server have provider-equivalent work, lifecycle, trigger, locking,
  least-privilege, and DB-apply contracts. SQL Server projection additionally requires
  RCSI and enabled nested triggers.
- Restart resumes from durable work with no canonical source scan.
- Poison work stays visible, blocks caught-up status, and cannot starve all later work.
- Cache and acknowledgement are atomic; process-local failure latches are not
  completeness state.
- Normal API health and routing remain relational and independent from projection
  operational health and caught-up status.
- V1 CDC remains restricted to a new physical database activated before first canonical
  write. Offline read-acceleration activation does not retrofit CDC.
- `DocumentProjectionWork` is never captured or published. Public key, value, ETag,
  partitioning, compaction, delete, and consumer contracts are unchanged.
- Performance qualification must measure write overhead, same-document contention,
  enqueue amplification, queue drain, baseline backpressure/restart, reset behavior,
  PostgreSQL WAL/vacuum/bloat, SQL Server log/ghost/deadlock behavior, and indexed status
  plans at representative scale.

## Alternatives Considered

| Alternative | Disposition |
| --- | --- |
| Use source/cache difference plus incremental scans and periodic complete relationship scans | Replaced: routine completeness and restart cost scale with total source cardinality, and lower-version late commits require repeated O(N) proof. |
| Persist one event per mutation | Rejected: v1 needs current projection work, not an immutable event log; one coalesced row per document bounds ordinary backlog. |
| Add leases, claims, attempts, or dead letters | Rejected for v1: monotonic duplicate-safe processing plus fair paging is sufficient and avoids another distributed ownership protocol. |
| Enqueue in application code after commit | Rejected: it reintroduces a gap in which canonical state commits without durable required work. |
| Build JSON in the enqueue trigger | Rejected: it duplicates application reconstitution and expands canonical write cost/provider logic. |
| Make cache population synchronous with canonical writes | Rejected: canonical writes record small durable work but do not perform document hydration or Kafka publication. |
| Use routine exact backlog counts | Rejected: operational status uses indexed existence/oldest-work and bounded estimates; exact counts are explicit diagnostics. |
| Add a durable baseline cursor now | Deferred pending measured restart-from-beginning qualification; it becomes required if scale thresholds fail. |
| Automatically repair work mismatches in ordinary workers | Rejected: `W != S` is an inventory anomaly. Ordinary tracking preserves evidence; explicit scrub/rebuild repair is serialized and conditional. |
| Automatically lower cache-ahead rows | Rejected: a higher state may have been published and cannot be corrected safely in the old ordered namespace. |
| Derive tombstones from cache/work deletion | Rejected: projected-state maintenance is not domain deletion. |
