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

# Relational CDC and Document Projection

## Authority and Document Ownership

This is the authoritative integration and deployment design for relational DMS change
data capture (CDC) and the `dms.DocumentCache` projection that supplies its upsert
payloads. Runtime DMS owns explicit projection targets, projection mechanics, and
per-database projection health. Deployment automation owns CDC target selection,
durable connector/source bindings, topics, provider migrations, connector lifecycle,
combined CDC readiness, bootstrap, and CDC operations. Verification follows the same
boundary.

Two focused decision records own the decisions and contracts they name:

- [Relational CDC projector and sources](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
  owns the projector/source choice, freshness rule, and cache/domain lifecycle boundary.
- [Kafka topic and message contract](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md)
  owns the public topic, key, value, tombstone, and compatibility contract.

Epics and stories are delivery plans, not additional design authorities. Supporting
backend documents may state facts local to their subject and link here, but must not
redefine the cross-cutting behavior in these three documents.

Older references to legacy `dms.Document` JSON columns, `EdfiDoc`, OpenSearch, a shared
`edfi.dms.document` topic, or `deleted=true` messages are historical and are not active
relational CDC contracts.

## Scope and Architecture

The CDC deployment publishes a compacted document-state stream by reading database
transaction logs with Debezium in Kafka Connect. Runtime DMS supplies the projected
upsert source but does not control Kafka Connect. CDC is not a request-path dual write:
API write correctness remains tied to the relational database even when Kafka or
projection is unavailable.

One connector captures two complementary relational sources. The exact source-operation
mapping and lifecycle rationale are defined in the
[projector/source ADR](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md).
In summary, projected cache state supplies document upserts and canonical document
lifecycle supplies deletes.

`dms.DocumentCache` is optional for ordinary DMS operation and required only for
capabilities that consume projected documents. The canonical relational tables remain
the authority for writes, authorization, identity resolution, Change Queries, and
correct GET/query behavior.

Change Queries remain a separate polling API compatibility surface, including
`/deletes`, `/keyChanges`, and live-resource version filters based on `ContentVersion`,
`ContentLastModifiedAt`, and `tracked_changes_*` tables. They are not a Kafka source.

Kafka Connect is the v1 deployment model. Debezium Server is deferred, embedded
Debezium is not a reference path, and DMS does not publish directly to Kafka.

## Configuration and Projection Target Selection

Runtime DMS has one explicit projection-target contract and no Kafka-specific target or
separate process-wide projection selector. The target list is process-local configuration:
a deployment designates projector hosts by giving those DMS processes the relevant target
entries. A process with an empty list performs no projection work. Multiple processes may
carry the same target entries when duplicate, idempotent projection is desired.

```text
DataManagement:DocumentCache:Targets = [{ TenantKey, DataStoreId }, ...]
DataManagement:DocumentCache:ReadAcceleration:Enabled = false | true
DataManagement:DocumentCache:Projector:IncrementalScanInterval = <positive duration>
DataManagement:DocumentCache:Projector:FullAuditInterval = <positive duration>
DataManagement:DocumentCache:Projector:PageSize = <positive integer>
DataManagement:DocumentCache:Projector:MaxConcurrentTargets = <positive integer>
DataManagement:DocumentCache:Readiness:MaximumAuditAge = <positive duration>
```

The target and read-acceleration defaults are an empty list and `false`. The implementation
provides conservative, tested defaults for the five execution/readiness settings. Those
numeric defaults are operational tuning, not part of the projection or stream contract:
they are configurable, published in supported appsettings and operator documentation,
reported at startup, and may be adjusted between releases from PostgreSQL and SQL Server
qualification evidence. Configuration rejects nonpositive durations, page sizes, or
concurrency and requires `MaximumAuditAge` to be greater than `FullAuditInterval` so a
normally scheduled audit does not become stale before its successor is due.

A target entry enables projection for that logical `(tenant key, DataStoreId)` whether the
consumer is CDC, diagnostics, indexing, or another deployment-selected capability.
`ReadAcceleration:Enabled` is only a use-path gate: when enabled, DMS may use fresh cache
rows for explicitly listed targets, but it does not select every loaded or subsequently
discovered data store. Projector timing and capacity settings tune work already selected by
`Targets`; they never discover another target or change API routing.

Entries must be unique after applying the same case-insensitive tenant-key normalization
as `IDataStoreProvider`. DMS runs one logical reconciliation execution context for each
entry. The configured membership set is bound at startup; adding or removing an entry
requires a configuration rollout. DMS does not infer membership from HTTP requests,
route qualifiers, JWT `DataStoreIds`, the most recent request, or the complete CMS
inventory.

Target membership and target resolution have different lifetimes. An entry that is not
yet present in CMS remains an explicit unresolved projection target and is retried after
CMS refresh or on the bounded supervisor interval. A tenant or data store created after
DMS startup can therefore begin projection only when it was already listed. An unlisted
late-created target remains relational-only until a configuration rollout. If an
already-resolved entry receives replacement connection metadata from CMS, DMS replaces
that target's execution context, resets its projection-health evidence, and reports the
new context's current source fingerprint. DMS does not classify the old and new
observations as the same or different physical database, compare either observation with
a connector binding, or change Kafka artifacts.

Deployment automation selects CDC targets separately and must configure every CDC target
on at least one designated DMS projector host as a `DocumentCache:Targets` entry. Kafka
infrastructure, `-EnableKafkaUI`, and `-EnableKafkaCdc` do not implicitly select DMS
projection targets. All projection and CDC health remains observational and never changes
`IDataStoreSelection`, normal request routing, or API read/mutation availability.

## Cached Document Contract

`dms.DocumentCache` contains one caller-agnostic, pre-profile, full API-shaped projection
per current document. Its row contains:

- `DocumentId`
- `DocumentUuid`
- `ProjectName`
- `ResourceName`
- `ResourceVersion`
- `ContentVersion`
- `StreamEtag`
- `LastModifiedAt`
- `DocumentJson`
- `ComputedAt`

`DocumentId` is the compact internal primary key and an `ON DELETE CASCADE` foreign key to
`dms.Document`. `DocumentUuid` is a non-indexed denormalized copy of the canonical public
identity used as the Debezium message key. Provider-specific cache insert/update triggers
join by the existing `DocumentId` primary key and reject the statement unless the cache UUID
equals `dms.Document.DocumentUuid` for that same row. The foreign key independently rejects
a missing/deleted parent. Because canonical `DocumentUuid` is immutable and unique and the
cache has one row per canonical `DocumentId`, the trigger establishes cache UUID uniqueness
without a cache UUID index or a new composite index on `dms.Document`.

`DocumentJson` is produced by the same relational read-plan and reconstitution rules as
GET/query response assembly. It includes stable top-level `id` and
`_lastModifiedDate`. When link injection is compiled into the read plan, it also includes
reference `link` subtrees. It does not contain authorization arrays, EdOrg hierarchy
JSON, API client identity, or readable-profile-specific projections.

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
the provider trigger independently rejects a mismatched denormalized UUID if a defective or
unsupported writer reaches the database.
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

The current database difference remains both the durable work inventory and the
projection-completeness source:

```text
dms.Document
LEFT JOIN dms.DocumentCache ON DocumentId
WHERE DocumentCache.DocumentId IS NULL
   OR DocumentCache.ContentVersion <> Document.ContentVersion
```

The projector classifies that difference rather than treating every unequal row as
ordinary repair work:

| Cache state | Meaning | Projector action |
| --- | --- | --- |
| Row missing | Repairable projection lag | Materialize and insert |
| `DocumentCache.ContentVersion < Document.ContentVersion` | Repairable projection lag | Materialize and replace |
| Versions equal | Fresh | No action |
| `DocumentCache.ContentVersion > Document.ContentVersion` | Invariant violation | Do not materialize, retry, or overwrite automatically |

A cache-ahead row cannot arise from supported same-source projection and canonical-write
concurrency: canonical `ContentVersion` values advance monotonically and guarded cache
writes never replace a higher cache version. It therefore indicates cache corruption, an
in-place/partial canonical database restore or reset, or unsupported reuse of projected
state against another canonical source. It remains part of the exact completeness
difference and makes projection readiness false, but it is not placed in the normal retry
set. A process-local cache-ahead observation is cleared only when a later incremental
source change or full audit proves that the row is no longer ahead; the required restart
audit re-establishes the observation after process loss.

Candidate discovery is separate from completeness verification. For each selected data
store, the projector has two cooperating lanes:

1. A frequent incremental lane keyset-pages current `dms.Document` rows after a
   process-local `(ContentVersion, DocumentId)` cursor, ordered by those columns. Each
   page left-joins `dms.DocumentCache` by `DocumentId`. Missing and cache-behind rows
   become materialization candidates; cache-ahead rows become observed invariant
   violations. The cursor advances to the last source row examined whether the cache row
   was behind, fresh, or ahead; failed repairable candidates remain in the in-memory retry
   set described below.
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
relationship. Bounded paging must carry an audit-local scan position forward so that
repairing an early page does not cause every later page to rescan the already-fresh
prefix. An audit may race with writes; guarded upserts make its repairs safe. A nonzero
finishing aggregate causes repair to continue for missing and cache-behind rows. A
cache-ahead count instead preserves unhealthy readiness and waits for the explicit
recovery described below. The full-audit interval is bounded so it also bounds discovery
latency for repairable work that the incremental lane cannot see.

For each candidate from either lane, the projector:

1. Captures `(DocumentId, ContentVersion)` and the source metadata needed by the
   materializer.
2. Reconstitutes the caller-agnostic cached document without holding a source-row lock.
3. Validates the embedded/relational metadata invariant.
4. Performs the shared guarded cache upsert.

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

The database guard is a strong commit-order fence, not only a conditional read from an
MVCC snapshot. It writes only when the source `dms.Document` still exists at the captured
`ContentVersion`, and it prevents that source row from changing until the cache write
commits. A stale materialization no-ops and is rediscovered at its current version; a
deleted document cannot be recreated; and a lower version cannot overwrite a higher
cache version. Reconciliation and optional direct fill after a relational read use the
same guard.

Materialization and invariant validation finish before the guard transaction begins. V1
then performs one candidate per short transaction:

1. Acquire a write-conflicting lock on the current `dms.Document` row identified by
   `DocumentId`, and evaluate `ContentVersion == captured ContentVersion` against the row
   obtained after any concurrent writer finishes.
2. If the row is missing or its version differs, make no cache write and report a stale
   skip.
3. While retaining the source-row lock, insert the cache row when absent or replace it
   only when the existing cache version is lower than the captured version. A duplicate
   same-version result is already fresh and does not need another captured update. An
   existing higher cache version is an invariant violation, not a stale-write repair.
4. Commit the cache write and release the source-row lock together.

This transaction is the linearization boundary. If the projector acquires the source-row
lock first, its cache write commits before a later canonical update can advance the
version. If the canonical writer acquires the lock first, the projector observes the new
version after waiting and skips its stale result. The `DocumentCache(DocumentId)` foreign
key remains the delete fence, but its referential lock is not the content-version fence.

The provider contract is:

- PostgreSQL uses `READ COMMITTED` and a keyed `SELECT ... FOR NO KEY UPDATE` (or a
  strictly stronger equivalent) with both `DocumentId` and captured `ContentVersion` in
  the predicate. PostgreSQL re-evaluates that locking predicate after a concurrent row
  update completes. `FOR NO KEY UPDATE` conflicts with the ordinary non-key update that
  advances `ContentVersion` without unnecessarily blocking `FOR KEY SHARE` checks. A
  plain conditional `INSERT ... SELECT` and the foreign-key check alone are insufficient.
- SQL Server runs the guard transaction at `READ COMMITTED`, whether or not
  `READ_COMMITTED_SNAPSHOT` is enabled, and uses a keyed locking read with `UPDLOCK` (or a
  strictly stronger equivalent). It evaluates the captured version on the locked current
  row and retains the update lock through the cache upsert and transaction completion. It
  must not use a row-versioned source read as the fence.

Deadlock, serialization, and bounded lock-timeout outcomes produce no cache write. The
projector handles them through its existing retry/backoff path. Optional direct fill uses
a short bounded lock wait and abandons the fill on contention without affecting the
relational response. Guard transactions do not span candidates or materialization;
future batching requires a separate measured design with stable lock ordering and proven
lock-escalation and tail-latency bounds.

There are no projection queues, enqueue APIs, persisted cursors, backfill epochs,
projector-state rows, failure rows, retry classifications, dead-letter transitions, or
requeue APIs in v1. The process-local incremental and audit cursors are disposable scan
positions only. Empty-cache population, ordinary truncation/rebuild, repairable recovery,
and completeness all derive from the current database difference. A maximum scanned or
projected version cannot prove completeness because a lower current version may still be
missing. Cache-ahead recovery is the exceptional operator procedure below, not another
projector workflow.

Failures use bounded in-memory exponential backoff with jitter keyed by opaque data-store
identity, `DocumentId`, and current `ContentVersion`. A deferred candidate does not
starve other work. Its entry is removed when the version changes, the document is
deleted, or the cache becomes fresh. Restart loses only the delay and rediscovers work
from database state. V1 has no retry budget or persisted attempt count; a persistent
failure remains visible until its underlying data, mapping, or service cause is fixed.

### Bounded In-Process Execution Policy

Projection is background work in the DMS application process. V1 uses one serialized
execution loop per resolved target and a process-wide `MaxConcurrentTargets` gate. At most
one incremental page, audit page, or candidate materialization is active for a target at a
time, and no more than the configured number of targets perform projection database/CPU
work concurrently. A page contains at most `PageSize` source rows and is fully drained
before the loop fetches another page; the process does not build an unbounded candidate
queue. Waiting targets receive permits fairly so one large rebuild cannot permanently
exclude another target.

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
   pass through the same loop rather than tight-looping. Retry backoff and the concurrency
   gate remain in force. A cache-ahead-only result remains unhealthy and waits for explicit
   recovery rather than scheduling futile repair.
5. Health and readiness reads are observational. They do not start or wait for an audit.
   Until the immediate startup/rebuild audit completes, or when the latest exact audit is
   older than `MaximumAuditAge`, the target is simply not ready.

Cancellation is observed between pages and candidates and while waiting for the global
gate. Shutdown does not begin new work and allows only the current short guarded cache
transaction to finish or roll back within its existing command/lock timeout. One target's
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
consumers may reject the lower replacement as stale.

Recovery depends on whether the projection can have entered downstream ordered state:

- If the projection is internal-only, such as read acceleration, and no downstream system
  could have observed the row, delete the affected cache row, or truncate the cache when
  the scope is not safely enumerable, and let ordinary reconciliation rebuild missing
  rows from canonical state.
- If an active or historical connector or another ordered downstream consumer may have
  observed the higher cache version, stop the affected publication path and create a new
  downstream state namespace. For Kafka CDC, this means a new immutable binding
  generation, topic, and consumer state namespace. Clear the incompatible cache state,
  complete ordinary reconciliation, and snapshot the rebuilt cache into that new
  namespace. Do not publish the lower version as an in-place correction to the old one.
- Treat any in-place canonical database restore/reset as the CDC case above even when the
  physical database name or connection metadata did not change.

The runbook requires operators to establish which case applies before deleting projected
state. No recovery rewrites canonical `ContentVersion`, an immutable binding record, or an
existing topic generation.

The hosted supervisor creates an isolated, non-HTTP service scope for each startup
target and explicitly selects its data store. It does not depend on
`ResolveDataStoreMiddleware` or reuse request-scoped `IDataStoreSelection`. One
unavailable data store does not stop peers. Multiple DMS replicas may perform duplicate
scans safely because candidate discovery is read-only and writes are idempotently fenced.
Deployments avoid redundant work by placing target entries only on designated projector
hosts. Correctness does not require a distributed lease; when more than one host is
configured for the same target, each independently applies the bounded execution policy.

## Cache-Backed Reads and Domain Lifecycle

When read acceleration is enabled, authorization and query candidate selection still
use relational sources. A cache row may supply response-body assembly only when it is
fresh. Missing or stale rows fall back to relational reconstitution; readable-profile
projection, link stripping, and served `_etag` composition then run identically for both
paths. The read path does not enqueue projection work, though it may perform the shared
guarded direct fill after fallback as an optional optimization.

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
intentional compacted-topic rebuild is a connector snapshot/topic recovery operation,
not a cache-delete operation. Schema reprovisioning must not reuse cache rows across
incompatible effective schemas.

## Projection Health and Deployment-Owned CDC Readiness

Projection health is evaluated for each explicit `(tenant key, DataStoreId)` execution
context. It reports at least:

- target resolution and required-table existence,
- the current provider and an opaque provider-resolved physical-source fingerprint
  computed with the same provider-specific algorithm used by deployment automation,
- whether the in-process loop is running,
- whether an incremental scan or full audit is in progress,
- the effective execution settings, last/next incremental and audit eligibility times, and
  whether work is waiting for the process-wide target-concurrency gate,
- the latest completed full audit's observation time, duration, and age,
- that audit's exact total unresolved, missing-row, cache-behind-row, and
  cache-ahead-invariant counts,
- its oldest unresolved source timestamp and age, derived from
  `dms.Document.ContentLastModifiedAt`,
- currently known unresolved incremental candidates, retry deferrals, and cache-ahead
  invariant observations, identified as process-local observations rather than exact
  database counts.

Optional counts by project/resource may be exposed when operationally safe. Process-local
incremental cursor, last scan, successful upsert, and last error are diagnostic only.
`LastScannedContentVersion`, `LastProjectedContentVersion`, and last-success timestamps
are never completeness evidence.

Health reads return the latest audit snapshot; they do not synchronously execute, enqueue,
or wait for a full anti-join. Configurable audit-age, unresolved-count, and
oldest-unresolved-age thresholds distinguish a fresh zero observation or brief
asynchronous lag from a stale audit or sustained degradation. A nonzero finishing audit
invalidates completeness until a later exact finishing aggregate returns zero.
Incremental discovery makes readiness false
while that known candidate, retry, or cache-ahead invariant remains unresolved, but
successful repair does not force another full scan; the last exact-zero audit retains its
original observation time and must still satisfy the audit-age threshold. A same-version
timestamp comparison is not another freshness or completeness test; embedded metadata
consistency is enforced when a row is materialized and written. DMS projection readiness
for one target requires a resolved execution context, a sufficiently recent exact-zero
finishing audit, and no currently known unresolved incremental candidate, retry deferral,
or cache-ahead invariant.

DMS exposes only this per-database projection result. It does not expose
`CanRegisterConnector`, compare the current source fingerprint with an expected source,
retain source-drift state, inspect provider capture artifacts, call Kafka Connect, or
calculate a deployment aggregate.

Deployment automation calculates end-to-end CDC readiness for each binding from:

- a durable binding record that matches the target's currently resolved physical source,
- a DMS projection-health result whose current source fingerprint matches that binding,
- provisioned `dms.Document` and `dms.DocumentCache` tables and provider CDC/key
  prerequisites,
- topic name, fixed partition count, ACL, transform, and connector configuration that
  match the binding record,
- a running connector with completed snapshot/catch-up through a database source
  position observed after DMS reported a sufficiently recent exact-zero audit,
- a second DMS projection-health observation that remains ready for the same source, and
- connector lag within its configured threshold.

There is no backfill epoch, completed backfill target, or maximum projected version in
this readiness calculation. The external status operation retains each target's result
when calculating a deployment aggregate. A combined-readiness failure does not gate
normal DMS traffic.

## Deployment-Owned CDC Target and Physical Source Binding

Each logical instance topic maps to exactly one physical database containing the
captured tables. Registering separately authorized topics for multiple CMS aliases of
the same physical document set is rejected because the rows contain no tenant or
data-store discriminator.

The logical CDC target identity is `(deployment key, tenant key, DataStoreId)`. Each
immutable connector/topic generation adds a positive integer `generation` to that
identity. Tenant identity remains deployment/administrative state and is not published
in the topic or message. Route-qualifier-only changes do not affect connector or topic
identity.

Deployment resolves a provider-specific physical database identity for every
deployment-selected CDC target. Comparison does not rely only on raw connection-string
text: semantically equivalent strings and server aliases must be normalized or confirmed
after connection.
Diagnostics identify conflicting opaque data-store IDs without credentials, tenant
display names, or unsanitized physical identifiers.

Deployment automation durably records the immutable binding before creating external CDC
artifacts. Runtime DMS records no expected binding and latches no source-drift condition.
Its health surface reports only the current database being projected. The deployment
status operation compares that observation with the durable binding; a missing target,
retryable resolution failure, or confirmed mismatch makes combined CDC readiness false
without changing DMS request routing.

The portable binding-record shape is:

```json
{
  "version": 1,
  "deploymentKey": "dms-local",
  "tenantKey": "default",
  "dataStoreId": "1",
  "instanceKey": "data-store-1",
  "generation": 1,
  "provider": "postgresql",
  "physicalSourceFingerprint": "sha256:...",
  "connectorName": "dms-local-data-store-1-g1",
  "topicName": "edfi.dms.instance.data-store-1-g1.documents.v1",
  "partitionCount": 1,
  "contractVersion": "1"
}
```

The record contains no connection string or credential. Credential, timeout, pooling,
application-name, host-alias, or equivalent connection changes produce the same
fingerprint when the provider and physical database are unchanged. `partitionCount` is a
positive topic-creation value and is immutable within the binding generation because the
public consumer contract uses per-key partition offsets to order equal-version corrective
republishes.

For local development and CI, the deployment automation stores one JSON record per
generation under a deployment-owned persistent state root, with the default layout:

```text
eng/docker-compose/.cdc-state/bindings/
  {filesystemSafeDeploymentKey}/{instanceKey}/{generation}.json
```

`instanceKey` is the deployment-controlled Kafka-safe opaque identifier from the topic
contract; it does not contain a tenant or other display name. Path components are
filesystem-safe encodings rather than unsanitized administrative values. `.cdc-state`
must be ignored by Git and is separate from
`.bootstrap/bootstrap-manifest.json`; the bootstrap manifest remains prepared-input
handoff, not mutable CDC control-plane state. JSON files use owner-only permissions where
the platform supports them. The local implementation is supported only when one
deployment controller owns a persistent filesystem. Production or
multi-controller automation stores the same logical record in its existing durable state
backend, such as remote infrastructure state or operator state, with atomic create or
compare-and-set semantics.

Binding creation and cleanup follow a fail-closed order:

1. Resolve the provider-specific physical database identity and intended artifact names.
2. Atomically create the immutable binding record before creating a topic, connector, or
   provider capture artifact. A record that already exists must match exactly; automation
   never rewrites its binding fields.
3. If any governed artifact exists without its binding record, or differs from the
   record, stop and require explicit adoption or cleanup. Do not infer or overwrite a
   binding from an existing topic name or connector configuration. Explicit adoption
   requires an operator-supplied complete record plus live verification of the physical
   source and every retained artifact.
4. On retirement, either retain the binding record with every retained governed
   artifact, or delete every governed topic, offset, ACL, PostgreSQL slot/publication,
   and SQL Server capture artifact before deleting the binding record. A normal process
   restart or stack stop deletes neither.

The binding record lives at least as long as any governed artifact. Local teardown may
remove it only in the same destructive volume-removal workflow that removes all of those
artifacts. A crash may leave an unused record, which safely supports an idempotent retry;
it must never leave surviving artifacts reusable after automatic record deletion.

V1 never reassigns an existing topic or connector generation to a different physical
database. Moving a `DataStoreId` to another physical document set creates a new binding
generation, connector, topic, and consumer state namespace. Removing a target requires
explicit retain-or-delete decisions for every generation. In-place source reset and
topic reuse are deferred. The same-source provisioning workflow remains idempotent for
an exact binding match, and deployment automation rejects separately authorized bindings
for multiple CMS aliases of the same physical document set.

## Connector Topology and Provider Setup

The reference architecture registers one logical connector per DMS instance with
`tasks.max = 1`, so both source tables share a connector task before routing to the
public topic.

### PostgreSQL

- Use the Debezium PostgreSQL connector with `pgoutput` and logical replication.
- Use a least-privilege replication/login principal rather than a superuser.
- Create one narrowly scoped publication and one replication slot per instance
  connector; include only `dms.DocumentCache` and `dms.Document`.
- Configure `DocumentUuid` as the Debezium message key for both tables.
- `DocumentCache.DocumentUuid` is a custom logical message key, not the cache primary key;
  it does not require a cache UUID index. Its uniqueness follows from the cache identity
  validation trigger and canonical UUID uniqueness.
- Set `dms.Document` to `REPLICA IDENTITY FULL` so its non-primary-key
  `DocumentUuid` is available in delete records.
- Verify exact quoted table identifiers, `message.key.columns`, replica-identity SQL,
  and connector properties against the pinned Connect/Debezium image.

Generated PostgreSQL DDL may expose a quoted identifier such as
`dms."DocumentCache"`; template tests resolve the emitted name rather than assuming it.

PostgreSQL replication slots are database-scoped, so one connector cannot span the
database-per-instance isolation model.

### SQL Server

- Use the Debezium SQL Server connector and enable CDC for the instance database.
- Enable capture only on `dms.DocumentCache` and `dms.Document`, including
  `DocumentUuid`.
- Use a least-privilege login with CDC read access.
- Configure `DocumentUuid` as the Debezium message key for both tables.
- `DocumentCache.DocumentUuid` remains non-indexed; provider CDC captures the column and
  the configured custom key does not change the table's `DocumentId` clustered key.
- Set `time.precision.mode=adaptive` explicitly. Under this mode SQL Server
  `datetime2(7)` values, including `DocumentCache.LastModifiedAt`, are captured as
  `INT64` values with the `io.debezium.time.NanoTimestamp` logical type.
- Require the Ed-Fi `DocumentState` SMT to convert `LastModifiedAt` from that
  logical type to the public UTC RFC 3339/ISO-8601 string. `connect` mode is forbidden
  because it loses precision above milliseconds, and a plain rename would leak the
  `INT64` representation into the public contract.

SQL Server can capture multiple databases in one connector, but the reference deployment
still registers one logical connector per instance for offset, routing, failure, and
runbook parity with PostgreSQL. Advanced hosts may consolidate only when they preserve
per-instance topics and ACLs, `DocumentUuid` tombstones, and acceptable failure/offset
isolation.

Provider CDC/key setup is opt-in and does not run during ordinary relational provisioning
when CDC is not selected.

## Connector Transform Pipeline

The connector uses one Ed-Fi-owned `DocumentState` SMT as the contract boundary between
raw Debezium records and the public topic. The transform implements the source mapping
from the
[projector/source ADR](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
and the serialized contract from the
[topic/message ADR](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md)
in the following logical order. After capture, steps 2–8 occur within one transform
invocation:

```text
transforms=documentState
transforms.documentState.type=org.edfi.kafka.connect.transforms.DocumentState
transforms.documentState.provider=<postgresql|sqlserver>
transforms.documentState.target.topic=<instance document topic>
```

Those are its only contract configuration values. The `dms.DocumentCache` and
`dms.Document` source identities, Debezium operation mapping, source columns, and v1
public fields are fixed transform behavior rather than a configurable mapping language.

1. Capture both tables with `DocumentUuid` in each Debezium key.
2. Inspect the original Debezium source table and operation before discarding the
   envelope. Accept cache create, update, and snapshot/read records as upserts; accept
   canonical document deletes as authoritative deletes; drop every other captured
   operation.
3. Extract and validate `DocumentUuid` from the Debezium key, convert it to lowercase
   `D`-format text, and use it for both upserts and authoritative tombstones.
4. For a retained cache upsert, unwrap the row and parse `DocumentJson` directly into a
   structured JSON object. No independent generic expand-JSON SMT participates in the
   relational connector.
5. Normalize `LastModifiedAt` to the public UTC RFC 3339/ISO-8601 representation. For SQL
   Server, convert `io.debezium.time.NanoTimestamp` nanoseconds since the Unix epoch
   without loss of 100-nanosecond precision, emitting at most seven fractional digits
   with a trailing `Z`. Reject an unexpected provider representation or precision loss.
6. Build the complete lower-camel public envelope, copy the opaque DMS-computed
   `StreamEtag` to `document._etag`, remove all internal and operational fields, add
   `contractVersion`, and verify that the public key and normalized timestamp exactly
   match `document.id` and `document._lastModifiedDate`.
7. For a retained canonical delete, replace the value with a record-level null tombstone.
   Suppress Debezium's additional automatic tombstone, for example with
   `tombstones.on.delete=false`, so one canonical delete produces exactly one public
   tombstone and cache deletion produces none.
8. Route either retained result to the configured instance document topic. Returning
   `null` from the transform drops an operation excluded by the source mapping.

The transform consumes schema-backed raw Debezium records and emits only one of three
results: a final public upsert, a final public tombstone, or no record. Expected excluded
operations are dropped; a malformed retained record, unexpected source shape, invalid
`DocumentJson`, inconsistent embedded metadata, or unsupported temporal logical type
fails transformation rather than publishing a partial or ambiguous record.

Kafka Connect does not calculate `schemaEpoch`, interpret
`DataManagement:ResourceLinks:Enabled`, or reproduce DMS ETag encoding. `StreamEtag` is
opaque connector input; the transform only copies it to `document._etag`. The connector
does not split this contract across stock predicates, unwrap/rename/routing SMTs, or an
independent generic JSON expander. Keeping source classification, key/value shaping,
tombstone synthesis, consistency checks, and routing in one transform avoids
ordering-sensitive intermediate records. Tests assert published record bytes and
semantics, not only generated connector JSON. Version-specific properties and the
transform class are verified against the pinned
`edfialliance/ed-fi-kafka-connect` image.

The current pinned image uses Debezium 2.7, whose SQL Server connector supports
`adaptive` and `connect` temporal modes but not the newer `isostring` mode. Consequently,
v1 assigns SQL Server timestamp conversion to the required Ed-Fi `DocumentState` SMT. A
future image may move that responsibility to `time.precision.mode=isostring` only through
an explicit design and contract-test change; the same transform continues to own the rest
of the document-state contract, and connector templates never rely on a Debezium default.
See Debezium's
[2.7 SQL Server temporal mapping](https://debezium.io/documentation/reference/2.7/connectors/sqlserver.html#sqlserver-temporal-values)
and the
[current SQL Server temporal mapping](https://debezium.io/documentation/reference/stable/connectors/sqlserver.html#sqlserver-data-types).

## Stream Contract Compatibility, Repair, and Version Cutover

The [topic/message ADR](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes)
defines both the v1 compatibility boundary and the equal-version consumer rule. V1 adds
no projection-generation column or public ordering field. `ContentVersion` remains the
sole cache freshness value and orders different canonical states; the Kafka partition
offset orders multiple projections of the same canonical state. The topic partition count
and pinned key-based partitioner are immutable so one key retains that per-partition
ordering for the topic's lifetime.

Contract tests pin public field names, JSON types, key/tombstone behavior, document
semantics, and metadata relationships. They prove that Kafka Connect copies the opaque
DMS-computed `StreamEtag` exactly, but do not freeze its byte value independently of the
current DMS composer. A refactor or bug fix may change `DocumentJson` or `StreamEtag` for
an unchanged `ContentVersion` when the result remains compatible with the documented v1
contract.

### Compatible projection correction in `documents.v1`

For a conforming output correction, operators:

1. Mark the CDC target not ready and stop every old cache writer, including projector
   loops and optional direct fill.
2. Deploy the corrected materializer/composer while old cache writers remain stopped.
3. Clear `dms.DocumentCache` with the provider-supported rebuild operation. The existing
   connector remains registered against the same binding and ignores cache maintenance
   deletes/truncation.
4. Start only corrected projector writers and run full reconciliation until an exact
   finishing audit reports zero missing, cache-behind, and cache-ahead rows. Rebuilt cache
   inserts publish at later offsets with unchanged `contentVersion` values.
5. Wait for connector catch-up through a post-audit source position, recheck projection
   readiness, and restore combined CDC readiness.

The correction does not advance canonical `ContentVersion`, reset offsets, create a new
topic, or reserve a new binding generation. Consumers replace an equal-`contentVersion`
record at a later partition offset. Ordinary reconciliation continues to treat an existing
equal-version row as fresh; the explicit clear-and-rebuild operation produces the
corrective inserts. Old cache writers remain stopped throughout the rebuild so two
materializer implementations cannot alternate output for one version.

### New-topic cutover

Changing the topic partition count or pinned key partitioner creates a new binding
generation, topic, and consumer state namespace because offsets cannot order across the
old and new partitions. It may retain `documents.v1` and `contractVersion: 1` when the
public key/value/delete contract is otherwise unchanged.

A change to key encoding, required field names or JSON types, delete semantics, or the
document contract itself requires a new topic contract such as `documents.v2`. Operators
reserve a new binding generation and topic, completely reproject the cache under the new
contract, register the new connector with a fresh snapshot, wait for combined readiness,
and bootstrap consumers in the new state namespace. Schema reprovisioning follows this
path when retained consumer state could otherwise observe reused keys and versions under
an incompatible schema.

The cutover does not advance canonical `ContentVersion`. Normal API correctness does not
depend on projection, but old-contract projector writers must be drained before the cache
is rebuilt. One cache row cannot supply two incompatible contracts concurrently; a
zero-gap overlap requirement needs separate versioned projection state and another design
decision.

## Enablement and Initial Readiness Sequence

The connector supports an initial snapshot of `dms.DocumentCache`. It may snapshot both
included tables, but the operation filter drops every `dms.Document` snapshot record.

For a new instance:

1. Select the deployment CDC target and ensure the same `(tenant key, DataStoreId)` is an
   explicit DMS `DocumentCache:Targets` entry.
2. Resolve the physical source and atomically create its deployment-owned immutable
   binding record.
3. Provision and validate the relational schema and cache table, apply provider CDC/key
   setup, and create the binding's instance topic and ACLs.
4. Register the connector from that exact binding before DMS reconciliation or
   application writes that must be observed.
5. Start or roll out DMS so guarded cache upserts flow through established capture.
6. Wait for DMS projection readiness, observe a database source position afterward, and
   wait for connector snapshot/catch-up through that position.
7. Recheck DMS projection readiness for the same source fingerprint and advertise
   combined CDC readiness only when connector lag is also acceptable.

For an existing instance, perform the same one-shot sequence before write/delete traffic
that the host expects CDC to observe. If capture cannot be registered first, quiesce
traffic until it is registered. Existing cache rows are handled by the connector's
initial snapshot; ordinary reconciliation repairs remaining missing or cache-behind rows.
Any cache-ahead row is an invariant failure and must follow the explicit recovery path
before CDC readiness can pass.

## Local Bootstrap and CI

Local bootstrap exposes an explicit opt-in such as `-EnableKafkaCdc`.

- `-EnableKafkaUI` starts only Kafka UI.
- CDC opt-in starts Kafka and Kafka Connect if needed, resolves the selected configured
  deployment target, verifies that it is an explicit DMS projection target, and generates
  a connector without hard-coded database, topic, slot/capture, or data-store values.
- The local default binding state root is `eng/docker-compose/.cdc-state`; an explicit
  `-CdcBindingStatePath` may select another persistent deployment-owned location. It is
  never placed in `.bootstrap/bootstrap-manifest.json`.
- Binding reservation and registration are idempotent for an exact binding match and
  fail closed for missing or mismatched state around existing artifacts.
- Bootstrap prints connector name, provider, database, opaque instance key, and topic;
  secrets are excluded.
- It calculates the combined readiness sequence above and reports whether binding,
  migration, DMS projection, connector catch-up, or lag failed.
- E2E setup registers capture against the same provisioned database used by the DMS test
  process before issuing writes it expects to consume.
- A normal local stop retains binding, connector, and Kafka state. Destructive local
  volume teardown removes governed artifacts first and their binding records last.

Production-like automation repeats this one-shot workflow for each deployment-selected
CDC target using its durable state backend.

## DDL and Query Support

No DMS relational `DocumentCacheProjectionState`, `DocumentCacheProjectionFailure`, or
other durable projection-workflow table is provisioned. This does not replace the
deployment-owned CDC binding record. Supporting DDL is limited to the projection and
measured access paths:

- `DocumentCache(DocumentId)` remains the primary/foreign key with cascade deletion.
- `DocumentCache.DocumentUuid` is a non-indexed denormalized connector-key column.
  Provider-specific insert/update triggers reject any value that differs from
  `Document.DocumentUuid` for the same `DocumentId`; no composite parent index or cache UUID
  unique index is provisioned.
- `DocumentCache.StreamEtag` stores the DMS-computed opaque ETag for the fixed CDC
  representation; it is not used by API reads.
- Provider-specific constraints ensure `DocumentJson` is a JSON object.
- `dms.Document(ContentVersion, DocumentId)` supports incremental discovery and bounded
  full-audit paging and is required when `dms.DocumentCache` is provisioned.
- Add no additional projector/diagnostic index beyond the data-model-defined access
  paths until realistic provider query-plan measurements demonstrate a need.

If realistic steady-state and audit benchmarks remain unacceptable with the incremental
lane and required index, the next design step is a small transactionally maintained
pending-work table or flag. That durable optimization is deferred from v1 and must not
replace full audits as completeness evidence without a separate correctness decision.

PostgreSQL and SQL Server may use different plans while exposing equivalent logical
reconciliation, health, and fencing behavior.

## Security, Telemetry, and Operations

Topic-per-instance ACLs are the Kafka authorization boundary. Shared topics requiring
consumer-side instance filtering are not supported. The stream contains sensitive data;
Kafka Connect internal topics, its REST API, and database credentials are not exposed to
third-party consumers. Local insecure defaults must be replaced in production.

Structured logs and metrics cover:

- incremental source rows examined, candidates, cursor advances, and scan durations,
- full-audit rows examined, finishing counts, observation age, and durations,
- effective projector settings, due/overdue audits, coalesced audit requests, active and
  concurrency-gated targets, and bounded page sizes,
- projection attempts, successes, and failures,
- retry deferrals and backoff duration,
- guarded stale-write skips,
- guard lock-wait duration, timeout, deadlock, and serialization-retry counts,
- unresolved, missing, cache-behind, and cache-ahead-invariant counts plus oldest
  unresolved age,
- cache hits, misses, stale misses, and relational fallback,
- unresolved explicit targets, retryable source-resolution failures, and the current
  opaque projection-source fingerprint.

Deployment-owned CDC status additionally covers binding presence and match, connector
running state, lag, last error, snapshot completion, existing artifacts without binding
state, source mismatch, and generation migration state.

Use provider, safe project/resource identity, failure category, target-resolution state,
and opaque data-store identity only where cardinality policy permits. Never log
document bodies, raw student data, connection strings, credentials, tenant display
names, or unsanitized physical identifiers.

The deployment status operation identifies binding generation, connector,
provider/source, topic, PostgreSQL slot or SQL Server capture instance, DMS projection
health, snapshot state, lag, and last error. A failure for one target does not conceal
peer status or stop unrelated DMS API instances.

Runbooks cover connector restart, cache rebuild, same-topic compatible projection repair,
cache-ahead invariant diagnosis and the internal-only/downstream-state recovery split,
guard-lock contention and timeout diagnosis, offset reset, resnapshot, topic recreation,
target migration/retirement, and provider artifact cleanup.
They document the shipped projector defaults, how to tune intervals, page size, target
concurrency, and maximum audit age, and how to identify API-resource contention or an
audit that cannot complete within its readiness window.
They cover binding-state backup, fail-closed missing-state recovery, explicit adoption,
cleanup ordering, and new-generation source migration; they never repair a mismatch by
rewriting an immutable binding record. They distinguish a conforming same-topic repair
from an incompatible-contract cutover. The former stops old cache writers, clears and
rebuilds the cache, and lets later equal-version offsets replace prior values. The latter
completely reprojects into a new versioned topic and bootstraps new consumer state. Neither
path advances canonical `ContentVersion`. Offset reset and resnapshot can replay current
document state. Topic/offset/ACL/slot/capture deletion is destructive and always explicit;
removal from configuration is not cleanup authority.

## Verification

Fast contract and transform tests cover every retained and dropped source operation,
serialized key/value bytes, duplicate-tombstone suppression, JSON expansion, exact
copying of the DMS-computed stream ETag, timestamp format, metadata consistency, and
topic routing. Materializer tests prove `StreamEtag` is produced by the shared DMS
served-ETag composer for the fixed stream representation and remains coherent with the
row's `ContentVersion` and effective schema. V1 fixtures pin contractual public fields,
types, selectors, and metadata relationships without independently freezing opaque ETag
bytes. Ordering tests prove higher versions replace, lower versions are ignored, and a
later partition offset replaces an equal version. Repair tests clear and rebuild cache
state into the same topic without advancing `ContentVersion`; incompatible-contract tests
use a new topic suffix and matching `contractVersion`.

Deployment-state tests cover atomic first creation, exact-match retry, immutable-field
mismatch including an attempted partition-count change, provider aliases that resolve to
the same fingerprint, existing artifacts with missing state, cleanup ordering, normal-stop
retention, destructive-teardown removal, and new-generation source migration.
Multi-controller state backends additionally prove compare-and-set behavior. No test
repairs a mismatch by rewriting a binding, changes a topic's partition count in place, or
reuses a topic generation for a different source.

SQL Server template tests require the explicit `time.precision.mode=adaptive` setting.
Transform tests use realistic `io.debezium.time.NanoTimestamp` values with zero, one,
three, six, and seven significant fractional digits and assert lossless UTC strings,
trailing `Z`, the seven-digit maximum, exact equality with
`document._lastModifiedDate`, and rejection of unexpected or raw numeric output.

PostgreSQL and SQL Server integration/E2E coverage proves:

- provider key and delete-record prerequisites,
- create/update/snapshot upserts conform to the topic/message ADR,
- canonical deletion emits a tombstone when no cache row exists,
- cache delete/truncate/rebuild emits no domain tombstone,
- a compatible correction stops old cache writers, rebuilds corrected equal-version
  cache rows into the same topic, and makes the later per-key offset authoritative without
  resetting connector offsets or allocating a new binding generation,
- a cache upsert committed before canonical deletion appears before its tombstone for
  that key in the routed public topic,
- when a canonical update wins the source-row lock, a projector that captured the prior
  version observes the new version after waiting and commits no cache row,
- when the projector wins the source-row lock, its cache write commits before the
  canonical update advances `ContentVersion`, and the resulting mismatch is repaired,
- a foreign-key check or conditional MVCC source read without the provider locking guard
  is not treated as sufficient stale-write fencing,
- projection selection, empty-cache population, update, restart, retry, rebuild,
  fencing, health, cache fallback, and mixed-target isolation,
- ordinary high-version updates are discovered through the incremental lane without a
  full relationship scan,
- a source update committed after the startup audit's finishing observation but before
  incremental scanning begins remains above the pre-audit boundary and is discovered by
  the incremental lane,
- a lower version committed after the incremental cursor advances is repaired by the
  next full audit,
- cache-row loss below the incremental cursor is repaired by the next full audit,
- advancing the cursor past a failed candidate retains that candidate for bounded
  in-memory retry,
- a cache-ahead row is reported as an invariant violation, receives no materialization or
  retry loop, keeps projection readiness false, and cannot be overwritten with the lower
  canonical version,
- internal-only cache-ahead recovery by cache-row deletion rebuilds from canonical state,
  while a possibly published higher version requires a new downstream state namespace;
  Kafka CDC uses a new binding generation/topic and fresh snapshot,
- projection or connector failure never blocks normal API deletion.

Projector scheduling tests prove one serialized loop per target, process-wide target
concurrency, bounded pages with no unbounded candidate queue, fair progress across a large
and small target, audit-request coalescing, startup/rebuild immediate audits, observational
health reads, interval-based incremental and full-audit eligibility, stale-audit readiness,
and graceful cancellation. Configuration tests validate all execution settings and pin the
implementation-tuned defaults as documented release behavior rather than stream-contract
constants.

Performance qualification compares projector-disabled and projector-enabled source-write
throughput and p95/p99 latency for uniformly distributed writes, a deliberately hot
single document, duplicate projector replicas, and optional direct fill. Rebuild tests
also measure PostgreSQL WAL/page-write amplification from source-row locking and SQL
Server lock waits or escalation. The lock is never held during materialization, and V1
does not batch multiple guarded candidates into one transaction.

The quarantined KafkaMessaging scenarios are replaced only after the relational scenarios
pass consistently. SQL Server ordering requires a real connector and routed Kafka topic;
fixture-only transform coverage is insufficient.

## Historical and Deferred Material

Legacy document-store connector configurations referenced JSON columns such as
`EdfiDoc`, security elements, authorization EdOrg arrays, and hierarchy JSON that do not
exist in the relational schema. OpenSearch/Elasticsearch read-store support has been
dropped.

A relational outbox remains a future option if DMS needs explicit domain events rather
than a compacted document-state stream. Debezium Server and provider-specific managed
Kafka deployment guides are also outside v1.
