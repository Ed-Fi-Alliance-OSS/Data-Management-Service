---
status: proposed
date: 2026-07-21
jira:
  - DMS-1245
  - DMS-1246
related:
  - DMS-1232
  - DMS-1089
  - DMS-1279
---

# Relational CDC and Document Projection

## Authority and Document Ownership

This is the authoritative integration and deployment design for relational DMS change
data capture (CDC) and the `dms.DocumentCache` projection that supplies its upsert
payloads. Runtime DMS owns explicit projection targets, projection mechanics, and
per-database projection health. Deployment automation owns CDC target selection,
durable connector/source bindings, topics, provider CDC setup, connector lifecycle,
initial combined CDC readiness for a new offline database, later observational CDC
status, bootstrap, and CDC operations. Verification follows the same boundary.

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

### Documentation Audit and Disposition

For `dms.DocumentCache` projection and relational CDC/Kafka subjects, this document and
the two decision records above are the normative design set. If another document conflicts
with that set, this set prevails even when the other document remains current for its own
subject. The classifications below apply only to each artifact's DocumentCache or CDC/Kafka
content, not to unrelated material in the same artifact.

| Existing material | Classification | Disposition |
| --- | --- | --- |
| This `cdc-streaming.md` document | Current | Rewritten as the authoritative cross-cutting design for relational DocumentCache projection and CDC/Kafka. |
| [`0001-relational-cdc-projector-and-sources.md`](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md) and [`0002-kafka-topic-and-message-contract.md`](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md) | Current | Normative only for the focused decisions they own; this document defines their integration and deployment context. |
| [`data-model.md`](backend-redesign/design-docs/data-model.md), [`transactions-and-concurrency.md`](backend-redesign/design-docs/transactions-and-concurrency.md), [`link-injection.md`](backend-redesign/design-docs/link-injection.md), and [`update-tracking.md`](backend-redesign/design-docs/update-tracking.md) | Current | Reconciled supporting descriptions of local relational, cache, representation, lifecycle, and ETag facts. They defer to the normative design set for projection and streaming behavior. |
| [`ddl-generation.md`](backend-redesign/design-docs/ddl-generation.md) and [`flattening-reconstitution.md`](backend-redesign/design-docs/flattening-reconstitution.md) | Current | Supporting DDL and API materialization context. JSON response streaming in reconstitution is not CDC/Kafka streaming. |
| [`expandjsonsmt-replacement.md`](backend-redesign/design-docs/expandjsonsmt-replacement.md) | Current | Owns the implemented generic expand-JSON transform only. Legacy DMS record-shape discussion is context; the relational source, transform, topic, and message contracts are owned here and in the focused ADRs. |
| [`multitenancy-analysis.md`](multitenancy-analysis.md) | Stale-but-useful | Its database-engine constraints and topic-per-instance isolation guidance were incorporated here. Its OpenSearch material is historical and is not part of the relational CDC design. |
| Deleted `remove-legacy-backend.md` | Historical | Records the completed removal of the document-store backend and its Kafka test path. It remains useful only as Git history and defines no active contract. |
| Legacy document-store connector configurations and KafkaMessaging setup/test instructions | Obsolete | Targeted removed JSON columns and the shared legacy topic. They must not be restored or used to configure relational CDC; the proposed relational E2E replacement is defined by this design and the implementation stories. |
| [`eng/docker-compose/README.md`](../../eng/docker-compose/README.md), [`local-development-setup.http`](../../src/dms/tests/RestClient/local-development-setup.http), and the [Instance Management E2E README](../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md) | Current | Describe present implementation state: Kafka infrastructure may be started, but relational connector registration has not landed. Their future opt-in must implement this design. |

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

The DDL always provisions `dms.DocumentCache` and its singleton invariant state, but
populating or reading the cache is optional.
Capabilities that consume projected documents explicitly select projection targets; an
ordinary DMS deployment leaves the small table empty and performs no projection work.
The canonical relational tables remain the authority for writes, authorization, identity
resolution, Change Queries, and correct GET/query behavior.

The v1 projection and CDC schema contract is supported only for a new physical database
created by the completed E18 create-only provisioning path. V1 does not upgrade or retrofit
an already-provisioned database. In particular, it does not replace legacy
`DocumentCache.Etag`, remove the obsolete cache UUID constraint, or add the new singleton,
trigger, and access-path inventory in place. Selecting CDC is part of initial database
provisioning before DMS mutations are admitted. An interrupted initial workflow may retry
only while deployment state still proves that the same newly provisioned database has not
left that workflow. Enabling projection or CDC later on an existing database, or moving data
from such an ineligible database into a replacement solely to obtain first-time enablement,
requires separately designed migration support. A database successfully enabled through
this new-database path may later exact-match its binding, validate artifacts, restart, and
use the guarded source-replacement recovery defined below; those operations are not another
initial enablement, never modify the core E18 schema, and expose only eventual status after
first-write admission.

Change Queries remain a separate polling API compatibility surface, including
`/deletes`, `/keyChanges`, and live-resource version filters based on `ContentVersion`,
`ContentLastModifiedAt`, and `tracked_changes_*` tables. They are not a Kafka source.

Kafka Connect is the v1 deployment model. Debezium Server is deferred, embedded
Debezium is not a reference path, and DMS does not publish directly to Kafka.

### V1 Readiness Scope

V1 does not implement a production mutation-admission or transaction-drain gate. Exact
combined readiness is supported only during initial provisioning of a new physical database
while that database is offline: it has not been published to any DMS replica, bulk or seed
loader, administrative mutation path, or other writer to the canonical resource tables.
The setup controller owns this ordering and must retain positive evidence that it created
the database and has not opened write admission. This is a provisioning lifecycle
precondition, not a cross-replica runtime gate.

The initial offline window may remain closed for connector registration, a complete
projection audit and backed-off repair passes, connector snapshot/catch-up, and the
post-audit provider barrier. An operator-configured automation timeout is diagnostic and
fail-closed; elapsed time never substitutes for completing the sequence below. Local and
E2E bootstrap satisfy the same contract by creating the database and completing CDC setup
before test/application writes begin.

After write admission opens, DMS projection health and combined CDC status are
observational, eventually consistent signals. A later ready observation does not certify a
new exact canonical/cache/Kafka baseline and does not automatically block or release normal
DMS traffic. V1 supports exact-match connector validation and restart for a database
enabled through the initial offline path, but recovery after writes begin retains these
eventual-consistency semantics.

V1 does not support an exact baseline-replacing projection repair or contract cutover for
an admitted database. Such a workflow requires a separately owned deployment capability
that can fence every DMS replica and external writer, drain admitted requests and database
transactions, and keep the fence closed through a fresh audit and publication barrier. The
deferred correction and cutover contracts below describe the safety requirements for that
future capability; they are not implemented production procedures and cannot be used to
claim exact readiness in v1.

### Pinned Connector Runtime

The v1 Ed-Fi Kafka Connect image must be rebuilt from the immutable Debezium 3.6 base
`quay.io/debezium/connect:3.6.0.Final@sha256:6f3fe6407bae8f2a7714b9fc174d545d52d81044b4f4add1565854f020943d47`.
The tag documents the qualified version and the digest prevents a registry update from
silently changing it. The resulting `edfialliance/ed-fi-kafka-connect` image adds the
Ed-Fi transforms and is itself selected by immutable digest in deployment; an unqualified
name or a floating `3.6`/`latest` tag is not a conforming v1 pin. Image qualification runs
the connector and transform suites on the included Kafka Connect 4.3.0 runtime.

This exact Debezium 3.6 connector combination is known to work with SQL Server 2025 and
the current `nvarchar(max)` `DocumentJson` schema, so SQL Server 2025 is an Ed-Fi
qualification target. If DMS-1279 later adopts SQL Server's native `json` type, that type's
connector mapping requires separate qualification before CDC uses it. Debezium's upstream
3.6 tested-version matrix lists SQL Server through 2022, so this is an Ed-Fi-tested
compatibility statement, not a claim that SQL Server 2025 appears in Debezium's upstream
support matrix. See the
[Debezium 3.6 release series](https://debezium.io/releases/3.6/).

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
DataManagement:DocumentCache:ReadAcceleration:DirectFillTimeout = <positive duration>
```

The target and read-acceleration defaults are an empty list and `false`. The implementation
provides conservative, tested defaults for the six execution, readiness, and direct-fill
settings. Those numeric defaults are operational tuning, not part of the projection or
stream contract: they are configurable, published in supported appsettings and operator
documentation, reported at startup, and may be adjusted between releases from PostgreSQL
and SQL Server qualification evidence. Configuration rejects nonpositive durations, page
sizes, or concurrency and requires `MaximumAuditAge` to be greater than
`FullAuditInterval` so a normally scheduled audit does not become stale before its
successor is due. `DirectFillTimeout` is a small request-path budget below the ordinary
database command timeout; exceeding it abandons only the optional fill.

A target entry enables projection for that logical `(tenant key, DataStoreId)` whether the
consumer is CDC, diagnostics, indexing, or another deployment-selected capability.
`ReadAcceleration:Enabled` is only a use-path gate: when enabled, DMS may use fresh cache
rows for explicitly listed targets, but it does not select every loaded or subsequently
discovered data store. Projector timing and capacity settings tune work already selected by
`Targets`; `DirectFillTimeout` bounds only optional request-path fill after relational
fallback. These settings never discover another target or change API routing.

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
`IDataStoreSelection` or normal request routing by itself. Only the initial setup controller
delays publishing a new offline database to writers while establishing first readiness;
DMS health polling does not activate or release a runtime gate.

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
therefore cannot make the possibly corrupt cached row fresh. Only the explicit recovery
operation below clears the cache and latch together.

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
   cache writes, and waits for explicit recovery rather than scheduling futile repair.
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
  observed the higher cache version, stop the affected publication path and create a new
  downstream state namespace. For Kafka CDC, this means a new immutable binding
  generation, public and progress topics, a SQL Server schema-history topic when
  applicable, and consumer state namespace. With old cache writers and the old connector
  stopped, clear all cache rows and the latch in one provider transaction, complete
  ordinary reconciliation, and snapshot the rebuilt cache into that new namespace. Do not
  publish the lower version as an in-place correction to the old one.
- Treat any in-place canonical database restore/reset as the CDC case above even when the
  physical database name or connection metadata did not change.

The runbook requires operators to establish which case applies before deleting projected
state. Clearing the latch without clearing the entire cache in the same transaction is not
a supported operation. No recovery rewrites canonical `ContentVersion`, an immutable
binding record, or an existing topic generation.

E18 owns one target-scoped administrative recovery operation. It takes the exclusive
singleton-state lock, verifies the latch is set, clears the entire cache, clears the latch,
and commits as one provider transaction; after commit it requests an immediate full audit.
It exposes no latch-only reset. Deployment/runbook automation remains responsible for
stopping an old publication path and allocating a new downstream namespace when required.

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
intentional compacted-topic rebuild is a connector snapshot/topic recovery operation,
not a cache-delete operation. Schema reprovisioning must not reuse cache rows across
incompatible effective schemas.

## Projection Health and Deployment-Owned CDC Readiness

Projection health is evaluated for each explicit `(tenant key, DataStoreId)` execution
context. It reports at least:

- target resolution and required-table existence,
- the current provider and the opaque physical-source fingerprint derived from the
  database-owned `dms.DataStoreIdentity.SourceIdentity`,
- whether the in-process loop is running,
- whether an incremental scan or full audit is in progress,
- the effective execution settings, last/next incremental and audit eligibility times, and
  whether work is waiting for the process-wide target-concurrency gate,
- the latest completed full audit's observation time, duration, and age,
- that audit's exact total unresolved, missing-row, cache-behind-row, and
  cache-ahead-invariant counts,
- the durable `CacheAheadRecoveryRequired` latch,
- its oldest unresolved source timestamp and age, derived from
  `dms.Document.ContentLastModifiedAt`,
- currently active unresolved incremental candidates plus the target-scoped
  repair-required observation, failure backoff, and next eligibility time, identified as
  process-local state rather than exact database counts.

Optional counts by project/resource may be exposed when operationally safe. Process-local
incremental cursor, last scan, successful upsert, and last error are diagnostic only.
`LastScannedContentVersion`, `LastProjectedContentVersion`, and last-success timestamps
are never completeness evidence.

Health reads return the durable latch plus the latest audit snapshot; they do not
synchronously execute, enqueue, or wait for a full anti-join. Configurable audit-age,
unresolved-count, and oldest-unresolved-age thresholds distinguish a fresh zero observation
or brief asynchronous lag from a stale audit or sustained degradation. A nonzero finishing
audit invalidates completeness until a later exact finishing aggregate returns zero. An
active incremental candidate makes readiness false until it succeeds or fails. A failed
incremental repair sets the target-scoped repair-required observation and keeps readiness
false until a later exact-zero finishing audit clears it; successful incremental repair
when that observation is clear does not force another full scan. The last exact-zero audit
retains its original observation time and must still satisfy the audit-age threshold.
A same-version timestamp comparison is not another freshness or completeness test;
embedded metadata consistency is enforced when a row is materialized and written. DMS
projection readiness for one target requires a resolved execution context, a sufficiently
recent exact-zero finishing audit, a clear durable cache-ahead latch, no active unresolved
incremental candidate, and no target-scoped repair-required observation.

An exact-zero audit is exact at its finishing observation, not continuous completeness
proof while canonical writes are admitted. In particular, a transaction may allocate a
lower `ContentVersion`, remain in flight while an audit finishes, and commit below the
incremental cursor afterward; the next full audit repairs it. DMS projection readiness
therefore remains an eventually consistent operational signal. Initial combined readiness
uses a fresh startup/restart audit while the new database is still offline and no canonical
mutation has ever been admitted, eliminating that late-commit case from the initial
baseline. V1 makes no equivalent exact-baseline claim after write admission opens.

DMS exposes only this per-database projection result. It does not expose
`CanRegisterConnector`, compare the current source fingerprint with an expected source,
retain source-drift state, inspect provider capture artifacts, call Kafka Connect, or
calculate a deployment aggregate.

Deployment automation evaluates end-to-end CDC component health for each binding from:

- a durable binding record that matches the target's currently resolved physical source,
- a DMS projection-health result whose current source fingerprint matches that binding,
- provisioned `dms.Document`, `dms.DocumentCache`, `dms.DocumentCacheState`, and opt-in
  `dms.CdcHeartbeat` tables and provider CDC/key prerequisites,
- public topic name, compact-only cleanup policy, explicit seven-day minimum tombstone
  retention, fixed partition count, ACL, transform, and partitioner algorithm that match
  the binding record and fixed v1 contract; the current operational `maxRecordBytes`,
  producer request/buffer memory, broker-size compatibility, and connector configuration;
  plus the derived compacted CDC progress topic and its connector-only ACL; for SQL Server,
  the derived internal schema-history topic, exact connector configuration, infinite
  retention without compaction, and connector-only ACLs; every applicable topic's actual
  replica count and explicit per-topic `min.insync.replicas` value must satisfy the active
  deployment durability profile,
- a running connector whose sole task is `RUNNING`, with completed snapshot/catch-up
  through the provider source-position barrier defined below, captured after DMS reported
  a sufficiently recent exact-zero audit,
- a second DMS projection-health observation that remains ready for the same source, and
- connector lag within its configured threshold.

These component conditions remain eventually consistent after write admission opens; they
do not assert that every currently committed document is projected at every health read.
There is no backfill epoch, completed backfill target, or maximum projected version in this
calculation. The external status operation retains each target's result when calculating a
deployment aggregate. A component-health failure does not automatically gate normal DMS
traffic. Only the initial new-database workflow delays first-write admission until its
sequence succeeds. It may explicitly abandon CDC and open first-write admission with the
target not ready, after which that database is no longer eligible for v1 first-time CDC
enablement.

### Provider Source-Position Barrier

Connector/task `RUNNING` state, elapsed time, Kafka topic offsets, and a lag metric do not
prove that every cache change committed by a projection audit has passed through the
connector. Nor can a post-audit publication barrier discover or repair a canonical row
that the audit missed. For initial enablement, the new database's offline precondition plus
the fresh exact-zero audit establishes the complete canonical/cache baseline; deployment
automation then uses one
source-position adapter per provider to compare a post-audit database position with the
connector's committed Debezium source offset and prove publication through that later
boundary.

Both adapters use the opt-in singleton `dms.CdcHeartbeat` table. It contains only
`HeartbeatId = 1`, a nonnegative `HeartbeatSequence` value, and `HeartbeatAt`; it
contains no document or tenant data. Connector setup includes this table in the
PostgreSQL publication or SQL Server CDC capture and configures a positive
`heartbeat.interval.ms`. Its fixed provider `heartbeat.action.query` atomically
increments `HeartbeatSequence` and updates `HeartbeatAt`. The default interval is 5,000
ms. Deployments may lower it or raise it within their readiness timeout, but template and
live-configuration validation reject zero, a negative value, a missing/conflicting action
query, or, for SQL Server, `poll.interval.ms > heartbeat.interval.ms`.

`dms.CdcHeartbeat` is an internal progress source. Its snapshot, create, update, and delete
records and Debezium heartbeat records are routed by the `DocumentState` transform to the
binding-scoped CDC progress topic before public-record validation; they are never routed to
the instance document topic. The progress topic is a transport acknowledgement boundary,
not a status store: deployment automation does not consume it and continues to read the
connector's committed source offsets through the REST API.

The transform must not return `null` for a heartbeat relied upon for readiness. In the
pinned Kafka Connect 4.3 runtime, a transformed-away record invokes `recordDropped()`
before `prepareToSendRecord()` and therefore never enters `SubmittedRecords`; invoking the
connector callback alone does not make that source offset committable. A routed heartbeat
enters the normal producer acknowledgement path, so its source offset becomes committable
only after that heartbeat and every earlier record for the same source partition have been
acknowledged. See Kafka Connect's
[source-task send loop](https://github.com/apache/kafka/blob/4.3.0/connect/runtime/src/main/java/org/apache/kafka/connect/runtime/AbstractWorkerSourceTask.java#L413-L425),
[`WorkerSourceTask` callbacks](https://github.com/apache/kafka/blob/4.3.0/connect/runtime/src/main/java/org/apache/kafka/connect/runtime/WorkerSourceTask.java#L130-L142),
and [`SubmittedRecords`](https://github.com/apache/kafka/blob/4.3.0/connect/runtime/src/main/java/org/apache/kafka/connect/runtime/SubmittedRecords.java#L34-L75).
The heartbeat table is not projection work, completeness evidence, a public event source,
or part of the immutable binding record.

For initial combined readiness only, deployment automation performs this sequence:

1. Verify that the setup controller created the selected new physical database and has not
   published it to any DMS replica or other writer. A previous zero audit is ineligible.
2. After capture artifacts and the connector are established, restart or roll out every
   DMS projector execution context selected for the target. This resets prior health
   evidence and forces the required immediate startup/restart audit while the database is
   still offline.
3. Wait for that fresh audit to finish at exact zero and retain its source fingerprint.
   Keep first-write admission closed; an older, stale, or merely recent audit cannot
   satisfy this step.
4. Capture a provider barrier from that same bound physical database after receiving the
   fresh health result, using the provider procedure below.
5. Read the connector's committed source offsets through
   `GET /connectors/{connectorName}/offsets`. Select exactly one source partition matching
   the connector and bound database: PostgreSQL requires exactly
   `{ "server": <configured topic.prefix> }`, while SQL Server requires exactly
   `{ "server": <configured topic.prefix>, "database": <configured database name> }`.
   A missing endpoint, missing or multiple matching partitions, snapshot offset, null
   field, malformed field, or source-partition mismatch is not ready.
6. Parse and compare the provider position. Poll until the committed connector position
   is greater than or equal to the captured barrier, while continuing to require the sole
   task to be `RUNNING`. Connector status, topic end offsets, elapsed time, and lag never
   substitute for this comparison.
7. Read projection health again and require it to remain ready for the same source
   fingerprint. Then apply the independent connector-lag threshold, transition the target
   to combined ready, and only afterward publish the database to canonical writers.

The database may remain offline as long as this sequence needs. A failure or timeout cannot
reuse the prior audit, publish the database as CDC-ready, or degrade into a status/lag-only
success. Automation may keep retrying before first-write admission or explicitly abandon
CDC and publish the database with the target not ready. Later observational status and
connector-only recovery use the component-health calculation above and retain the design's
bounded eventual-consistency semantics; they do not claim another exact baseline.

The barrier is transient status evidence and is not persisted in or used to mutate the
immutable binding.

**PostgreSQL adapter**

After the projection-health response selected by the applicable readiness sequence,
execute `SELECT pg_current_wal_lsn()` through the bound database connection. Normalize
PostgreSQL's `X/Y` value to its unsigned 64-bit WAL byte position. From the Connect source
offset, require the Debezium 3.6 `lsn_proc` field, which
is the last completely processed LSN, interpret its signed JSON integer as the same
unsigned 64-bit bit pattern, and require `lsn_proc >= barrierLsn`. Do not compare the less
strict `lsn`, `lsn_commit`, a replication-slot flush position, or formatted strings. The
heartbeat table is part of the publication, so the next action-query update from an idle
database drives logical decoding beyond the captured WAL position.

**SQL Server adapter**

After the projection-health response selected by the applicable readiness sequence, read
`HeartbeatSequence` through the bound database connection. Wait until the heartbeat CDC
capture instance exposes an update after-image
whose `HeartbeatSequence` is greater than that value. Use that row's `__$start_lsn` as the
barrier commit LSN and `__$seqval` as the barrier change LSN; this also proves that the SQL
Server capture job has processed a transaction committed after the audit observation.
Normalize each 10-byte LSN to Debezium's fixed-width `xxxxxxxx:xxxxxxxx:xxxx` form. From
the Connect source offset, require `commit_lsn`, `change_lsn`, and `event_serial_no`;
compare commit LSN, then change LSN, then event serial number as unsigned values. The
heartbeat update after-image has event serial number `2`, so the connector is caught up
only at or after `(barrierCommitLsn, barrierChangeLsn, 2)`. Null/snapshot positions do not
pass. Comparison uses the decoded unsigned bytes, not locale or string collation.

The pinned Connect/Debezium image must support the connector-offset REST endpoint and
these exact provider offset fields. Image qualification and provider integration tests
pin their shapes. See the Kafka Connect
[connector-offset REST API](https://kafka.apache.org/43/kafka-connect/user-guide/#connect_rest),
Debezium's
[PostgreSQL heartbeat guidance](https://debezium.io/documentation/reference/3.6/connectors/postgresql.html#postgresql-property-heartbeat-action-query),
and its SQL Server
[offset processing](https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html#sqlserver-overview)
and
[heartbeat guidance](https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html#sqlserver-property-heartbeat-action-query).

## Deployment-Owned CDC Target and Physical Source Binding

Each logical public instance topic maps to exactly one physical database containing the
captured tables. Registering separately authorized topics for multiple CMS aliases of
the same physical document set is rejected because the rows contain no tenant or
data-store discriminator.

The logical CDC target identity is `(deployment key, tenant key, DataStoreId)`. Each
immutable connector/topic generation adds a positive integer `generation` to that
identity. Tenant identity remains deployment/administrative state and is not published
in the topic or message. Route-qualifier-only changes do not affect connector or topic
identity.

Every provisioned database contains one immutable UUID in the singleton
`dms.DataStoreIdentity` row. DMS reads that UUID through the active target connection and
computes the physical-source fingerprint as follows:

```text
providerToken = "postgresql" | "sqlserver"
sourceIdentity = lowercase UUID D format, for example
                 "f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
payload = UTF-8("ed-fi-dms-source-v1" + NUL + providerToken + NUL + sourceIdentity)
physicalSourceFingerprint = "sha256:" + lowercaseHex(SHA-256(payload))
```

`NUL` is one zero byte (`0x00`). No connection-string, credential, host, port, database
name, DNS result, server name, or provider catalog identifier participates. Consequently,
all aliases and HA endpoints that reach the same database row produce the same value,
while independently provisioned databases receive different random source identities.
DMS is the authoritative current-source observer. Deployment automation stores and
compares the reported opaque fingerprint; tooling that reads the row directly must use
the exact algorithm above and the same conformance vectors.

Conformance vectors for source identity
`f81d4fae-7dec-11d0-a765-00a0c91e6bf6` are:

| Provider token | Required fingerprint |
| --- | --- |
| `postgresql` | `sha256:193c47b34d9751c73d06dbf5ccf2655a1cce46154a4808f152d3db0e91b676bc` |
| `sqlserver` | `sha256:1780ea8893149195e89a46c70698dfdf64e8e6f9b31c7b7e9a9872baff498d75` |

The row is inserted only when absent and ordinary provisioning never changes it. Provider
replication and failover retain it. Creation of an independent writable data store from a
template, clone, or copied backup assigns a new UUID before the data store becomes
available. A rollback or restore that replaces an existing source rotates
`SourceIdentity` through the explicit CDC recovery workflow and, when CDC state exists,
uses a new binding generation, public and progress topics, a SQL Server schema-history topic
when applicable, and consumer state namespace. Rotation is never part of ordinary DDL
rerun or DMS startup.
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
  "partitionerAlgorithm": "kafka-murmur2-v1",
  "contractVersion": "1"
}
```

The record contains no connection string, credential, or source UUID. Credential, timeout,
pooling, application-name, host-alias, or equivalent connection changes produce the same
fingerprint when they reach the same `dms.DataStoreIdentity` row. `partitionCount` is a
positive topic-creation value and is immutable within the binding generation because the
public consumer contract uses per-key partition offsets to order equal-version records.
`partitionerAlgorithm` is the immutable named behavior token
`kafka-murmur2-v1`; it is not a Java class or library version. For non-null serialized key
bytes `K` and partition count `N`, that token means
`(KafkaMurmur2(K) & 0x7fffffff) % N`, byte-for-byte matching Kafka's Java-client Murmur2
key partitioning. V1 rejects a missing or different token. Template generation maps the
token to a compatible implementation in the pinned connector image, and validation uses
fixed serialized-key/partition fixtures so an image or implementation change cannot
silently change the mapping. A different algorithm requires a new token and binding
generation; an implementation change that preserves every token-defined result does not.

`maxRecordBytes` is intentionally absent from the binding record. It is a positive signed
32-bit per-target operational ceiling for the pinned Kafka serialization and one-record
produce-request framing, not a claim about the largest valid document across configurable
schemas and extensions. It is not copied from the HTTP request-body limit because cache
materialization can inject links and the transform adds the public envelope. Deployment
automation may increase it in place through the coordinated procedure below without
changing the binding generation or topic.

`topicName` is the public instance document topic. Each binding also governs one internal
CDC progress topic whose name is derived exactly as `topicName + ".cdc-progress"`. The
derived name is not another binding field or operator input; template generation emits it,
and bootstrap and live-configuration validation reject any different value. The progress
topic has one partition and `cleanup.policy=compact`. It contains no public document state,
and its retained contents are not part of the public bootstrap contract.

A SQL Server binding additionally governs one Debezium internal database schema-history
topic whose name is derived exactly as `topicName + ".schema-history"`. PostgreSQL does not
use this artifact. The derived SQL Server name is not another binding field or operator
input, and a new binding generation always derives a new history topic. The topic has
exactly one partition, `cleanup.policy=delete`, `retention.ms=-1`, and
`retention.bytes=-1`; compaction or finite time/size retention could remove an earlier DDL
record that the connector must replay to reconstruct the schema at a retained source
offset. It uses the same local or production replication-factor and
`min.insync.replicas` durability profile as the other binding-governed topics. The history
topic is connector-internal state, not a public stream or consumer-bootstrap source.

The public topic has an explicit per-topic `delete.retention.ms` override of at least
`604800000` milliseconds (seven days). This minimum is a fixed v1 contract value, not a
binding field; a deployment may use a higher value without changing the binding generation.
Provisioning and live validation reject a missing per-topic override, even when the current
broker default is high enough, or an explicit value below the minimum. The progress topic
is excluded because it is not a public state-bootstrap source.

### Public consumer bootstrap

The public compacted topic is an authoritative bootstrap source only for a conforming
consumer. The bootstrap operation starts at the earliest available offset for every topic
partition, captures an end-offset barrier for each partition, and durably applies every
record through those barriers within 24 hours of beginning its first partition scan. Once
that durable barrier is complete, it continues incrementally from the next offsets. The
deadline applies to the complete wall-clock operation, including rebalances, stalls,
retries, and consumer-state persistence.

Kafka bounds a valid offset-zero scan by `delete.retention.ms`: a slower scan can observe
an obsolete upsert and miss the tombstone removed while the scan is in progress. A consumer
that cannot prove completion within 24 hours must not advertise its reconstructed state as
valid. It discards that state and restarts from the earliest offsets; it must increase
parallelism or throughput before production use if repeated scans cannot meet the deadline.
The fixed 24-hour deadline leaves at least six days between the consumer SLA and the v1
tombstone-retention minimum.

Each independently operated consumer owns its conformance evidence. Before production use,
it capacity-tests the largest retained topic log it claims to support, including
dirty/uncompacted records, partition skew, maximum-sized records, durable consumer-state
writes, and expected concurrent mutation traffic, and demonstrates completion within 24
hours. Live-key count alone is not sufficient. Deployment automation validates and reports
the public topic's configured retention but cannot measure or certify an arbitrary
third-party consumer. A consumer that needs a longer bootstrap is outside the v1 topic-only
reconstruction guarantee unless it uses a separately defined authoritative bootstrap
source.

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

1. Obtain DMS's current fingerprint derived from `dms.DataStoreIdentity` and resolve the
   intended artifact names.
2. Atomically create the immutable binding record before creating any derived topic, the
   connector, or a provider capture artifact. A record that already exists must match
   exactly; automation never rewrites its binding fields.
3. If any governed artifact exists without its binding record, or differs from the
   record, stop and require explicit adoption or cleanup. Do not infer or overwrite a
   binding from existing topic names or connector configuration. Explicit adoption
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
generation, connector, public topic, progress topic, SQL Server schema-history topic when
applicable, and consumer state namespace. Removing a target requires explicit
retain-or-delete decisions for every generation.
In-place source reset and topic reuse are deferred. The same-source provisioning workflow
remains idempotent for an exact binding match, and deployment automation rejects separately
authorized bindings for multiple CMS aliases of the same physical document set.

## Connector Topology and Provider Setup

V1 requires one logical connector per DMS instance with `tasks.max = 1`. A connector is
bound to exactly one instance database and binding generation. Document records route to
that binding's public topic, while heartbeat progress routes to its derived internal
progress topic; both use the same connector task, source partition, offset state, and
failure boundary. A connector must not span multiple instance databases, even when the
provider supports doing so.

Every v1 connector pins these source-producer overrides:

```text
producer.override.enable.idempotence=true
producer.override.acks=all
producer.override.retries=2147483647
producer.override.max.in.flight.requests.per.connection=5
producer.override.max.request.size=<maxRecordBytes>
producer.override.buffer.memory=<producerBufferBytes>
producer.override.compression.type=none
```

`acks=all` is paired with an explicit durability profile for both the public and CDC
progress topics and, for SQL Server, its schema-history topic. Production deployments
require a replication factor of at least three and an explicit per-topic
`min.insync.replicas` of at least two. Local development and CI may use a single-broker
profile with replication factor one and `min.insync.replicas=1`. Provisioning validates
the actual replica count and topic-level override before connector registration, and live
validation keeps combined readiness false when any applicable topic drifts below the
active profile. These are operational deployment settings rather than binding fields, and
changing them does not require a new binding generation.

The ordering and compression values are fixed v1 values; `max.request.size` and
`buffer.memory` come from the mutable per-target record-size policy. The generated producer
partitioner must implement the binding's `kafka-murmur2-v1` token; operators do not supply
a separate partitioner class or configuration. Together with one task and one routed
partition per key, the ordering values prevent a retried upsert from being permanently
reordered after its later tombstone. Compression is pinned to `none` so the record-size
contract does not depend on compression ratio. Template generation rejects duplicate or
conflicting producer properties. Registration fails before connector startup when the
Kafka Connect worker's client-configuration override policy does not permit these values,
and live connector validation rejects drift from them. V1 does not rely on producer
defaults supplied by the Kafka client or pinned Connect image.

The authoritative topic/message contract defines `maxRecordBytes` as an enforced
operational ceiling for a fully materialized public record and its one-record Kafka
framing. After the real transform and converters serialize each retained record, the
pinned producer's `max.request.size` check is the authoritative pre-publication guard. An
over-budget record emits no partial public record, fails the connector task under
`errors.tolerance=none`, and keeps combined readiness false. The topic sets
`max.message.bytes` to the same operational value. Before registration, deployment
automation verifies that broker request, record-batch, and replica-fetch limits accept the
same budget. A self-managed deployment configures `socket.request.max.bytes`, the
effective `message.max.bytes`/topic override, `replica.fetch.max.bytes`, and
`replica.fetch.response.max.bytes` accordingly; a managed deployment must provide an
equivalent verifiable capability. Independently operated consumers set
`max.partition.fetch.bytes` and `fetch.max.bytes` to at least the operational value and
allow enough memory to deserialize one record. Deployment also provisions Kafka Connect
worker heap beyond `buffer.memory`, which Kafka documents as approximate producer buffer
capacity rather than a hard total-memory bound. `producerBufferBytes` defaults to the
greater of `33554432` and `maxRecordBytes`; an operator may configure a larger value for
throughput, but never a smaller one. The ordinary HTTP request-body limit is not a
substitute for this check because link injection and envelope shaping occur afterward.

Every v1 connector also emits the top-level connector setting
`errors.tolerance=none`. This is a fixed v1 value rather than a binding or operator
input, even though `none` is the Kafka Connect default. Template generation rejects a
duplicate or conflicting value, registration reads the live configuration back, and
combined readiness fails if the setting is absent or differs. The relational connector
does not use error tolerance or a dead-letter queue to skip a retained source record.

Every connector also emits and live-validates the provider source-position heartbeat
settings defined above. `heartbeat.action.query` is generated from the emitted
`dms.CdcHeartbeat` identifiers and is not free-form operator input. Heartbeat timing is an
operational readiness setting rather than an immutable stream-contract or binding field.

Every connector explicitly sets `statistics.metrics.enabled=true`. Debezium 3.6 then
exposes minimum, maximum, average, P50, P95, and P99 statistics for
`MilliSecondsBehindSource`. These quantiles are operational telemetry; current lag still
participates in combined readiness and neither current nor historical lag substitutes for
the provider source-position barrier.

### PostgreSQL

- Use the Debezium PostgreSQL connector with `pgoutput` and logical replication.
- Use a least-privilege replication/login principal rather than a superuser. In addition
  to replication reads, grant only the access needed to read and update the internal
  heartbeat singleton; do not grant document-table writes.
- Create one narrowly scoped publication and one replication slot per instance
  connector; include only `dms.DocumentCache`, `dms.Document`, and the internal
  `dms.CdcHeartbeat` progress table.
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
- Configure its required Kafka-backed internal database schema history explicitly:

  ```text
  schema.history.internal.kafka.bootstrap.servers=<deployment Kafka bootstrap servers>
  schema.history.internal.kafka.topic=<binding public topic>.schema-history
  schema.history.internal.producer.enable.idempotence=true
  schema.history.internal.producer.acks=all
  schema.history.internal.producer.retries=2147483647
  schema.history.internal.producer.max.in.flight.requests.per.connection=1
  include.schema.changes=false
  ```

  The bootstrap servers identify the same Kafka cluster used by the Connect worker. The
  template supplies the connector principal's externalized security settings to both the
  `schema.history.internal.producer.*` and `schema.history.internal.consumer.*` clients;
  neither credentials nor security property values become binding fields or diagnostics.
  `include.schema.changes=false` suppresses the optional consumer-facing schema-change
  topic but does not disable the required internal history store. Template and live
  validation reject a missing, duplicate, or conflicting value.
- Enable capture only on `dms.DocumentCache` and `dms.Document`, including
  `DocumentUuid`, plus the internal `dms.CdcHeartbeat` progress table.
- Use a least-privilege login with CDC read access plus only the access needed to read and
  update the internal heartbeat singleton; do not grant document-table writes.
- Configure `DocumentUuid` as the Debezium message key for both tables.
- `DocumentCache.DocumentUuid` remains non-indexed; provider CDC captures the column and
  the configured custom key does not change the table's `DocumentId` clustered key.
- Set `time.precision.mode=isostring` explicitly. Debezium 3.6 then captures SQL Server
  `datetime2(7)` values, including `DocumentCache.LastModifiedAt`, as ISO-8601 `STRING`
  values with the `io.debezium.time.IsoTimestamp` logical type instead of signed
  nanoseconds.
- Require the Ed-Fi `DocumentState` SMT to parse and validate the `IsoTimestamp`, truncate
  fractional seconds rather than round, and emit the existing DMS whole-second UTC
  string that exactly matches `document._lastModifiedDate`.
- Set `unavailable.value.placeholder=__debezium_unavailable_value` explicitly. Debezium
  3.6 uses this marker when an unchanged SQL Server `varchar(max)`, `nvarchar(max)`, or
  `varbinary(max)` value is unavailable in an update event. A retained cache upsert whose
  required `DocumentJson` column value equals the marker fails transformation; it is never
  treated as JSON `null` or published as document state.

Although the Debezium SQL Server connector can capture multiple databases, v1 does not
support that topology. Each instance database has its own connector, binding generation,
offset state, failure boundary, public target topic, derived progress topic, and derived
schema-history topic. Multi-database consolidation requires a future source-aware routing
contract and is not an operator-configurable v1 exception.

[Debezium's SQL Server connector](https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html#sqlserver-schema-history-topic)
distinguishes the required internal history store from optional public schema-change
events. Normal connector stop or restart retains the history topic and Connect source
offsets. Missing, unreadable, empty-when-offsets-exist, or misconfigured history makes the
connector not ready; automation never recreates it silently around retained offsets. An
explicit destructive recovery stops the connector and resets or removes its offsets and
history together before a controlled initial snapshot. Binding retirement removes the
history topic and its ACLs with the connector and offsets before deleting binding state.

Provider CDC/key setup is opt-in and does not run during ordinary relational provisioning
when CDC is not selected.

## Connector Transform Pipeline

The connector uses one Ed-Fi-owned `DocumentState` SMT as the contract boundary between
raw Debezium records and the public document and internal progress topics. The transform
implements the source mapping
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
transforms.documentState.progress.topic=<derived CDC progress topic>
```

Those are its only contract configuration values. `progress.topic` is generated as
`target.topic + ".cdc-progress"`; it is not operator-configurable, and template and live
configuration validation reject another value. The `dms.DocumentCache`,
`dms.Document`, and `dms.CdcHeartbeat` source identities, Debezium operation mapping,
source columns, and v1 public fields are fixed transform behavior rather than a
configurable mapping language.

1. Capture both document tables with `DocumentUuid` in each Debezium key and capture the
   internal heartbeat singleton for source-position progress.
2. Inspect the original Debezium source table and operation before discarding the
   envelope. Retain `dms.CdcHeartbeat` table operations and Debezium heartbeat records as
   internal progress records; accept cache create, update, and snapshot/read records as
   public upserts; accept canonical document deletes as authoritative public deletes; and
   intentionally drop every other captured operation.
3. Extract and validate `DocumentUuid` from the Debezium key, convert it to lowercase
   `D`-format text, and use it for both upserts and authoritative tombstones.
4. For a retained cache upsert, unwrap the row and parse `DocumentJson` directly into a
   structured JSON object. No independent generic expand-JSON SMT participates in the
   relational connector.
5. Normalize `LastModifiedAt` to the existing DMS whole-second UTC
   `yyyy-MM-ddTHH:mm:ssZ` representation. For SQL Server, interpret
   `io.debezium.time.IsoTimestamp` as an ISO-8601 UTC string, deliberately truncate
   fractional seconds without rounding into the next second, and reject an unexpected
   provider representation, non-UTC value, or fractional/raw public value.
6. Build the complete lower-camel public envelope, copy the opaque DMS-computed
   `StreamEtag` to `document._etag`, remove all internal and operational fields, add
   `contractVersion`, and verify that the public key and normalized timestamp exactly
   match `document.id` and `document._lastModifiedDate`.
7. For a retained canonical delete, replace the value with a record-level null tombstone.
   Suppress Debezium's additional automatic tombstone, for example with
   `tombstones.on.delete=false`, so one canonical delete produces exactly one public
   tombstone and cache deletion produces none.
8. Route a retained document result to the configured instance document topic. Route a
   retained heartbeat record, without applying the public document contract, to the
   configured progress topic. Returning `null` from the transform drops only an operation
   excluded by the source mapping and never a progress record used by readiness.

The transform consumes schema-backed raw Debezium records and emits only one of three
classes of result: a final public upsert or tombstone, an internal progress record, or no
record. Expected excluded operations are dropped; a malformed retained record, unexpected
source shape, invalid `DocumentJson`, inconsistent embedded metadata, or unsupported
temporal logical type fails transformation rather than publishing a partial or ambiguous
record. Because the connector pins `errors.tolerance=none`, that failure stops the connector
task instead of skipping the record. A failed task makes combined readiness false; offset
or lag observations cannot reclassify it as caught up. Recovery requires correcting the
cause and restarting or replacing the connector so the retained record is processed under
the contract. Returning `null` for an explicitly excluded operation remains normal
transform behavior and does not use the error-tolerance path; readiness never depends on
such a record advancing the committed source offset.

Kafka Connect does not calculate `schemaEpoch`, interpret
`DataManagement:ResourceLinks:Enabled`, or reproduce DMS ETag encoding. `StreamEtag` is
opaque connector input; the transform only copies it to `document._etag`. The connector
does not split this contract across stock predicates, unwrap/rename/routing SMTs, or an
independent generic JSON expander. Keeping source classification, key/value shaping,
tombstone synthesis, consistency checks, and routing in one transform avoids
ordering-sensitive intermediate records. Tests assert published record bytes and
semantics, not only generated connector JSON. Version-specific properties and the
transform class are verified against the pinned Ed-Fi image built from the exact Debezium
3.6 base above.

Debezium 3.6's `isostring` mode removes the 2.7-era signed-`NanoTimestamp` parsing
workaround and preserves all seven SQL Server fractional digits in an unambiguous UTC
string. It does not replace the transform's responsibility to emit the existing DMS
whole-second representation and verify embedded timestamp equality. The same transform
continues to own the rest of the document-state contract, and connector templates never
rely on a Debezium default. See Debezium's
[3.6 SQL Server temporal mapping](https://debezium.io/documentation/reference/3.6/connectors/sqlserver.html#sqlserver-temporal-values).

## Stream Contract Compatibility, Repair, and Version Cutover

The [topic/message ADR](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes)
defines both the v1 compatibility boundary and the equal-version consumer rule. V1 adds
no projection-generation column or public ordering field. `ContentVersion` remains the
sole cache freshness value and orders different canonical states; the Kafka partition
offset orders multiple projections of the same canonical state. The topic partition count
and binding's `partitionerAlgorithm` token are immutable so one key retains that
per-partition ordering for the topic's lifetime.

V1 implements initial publication from a new offline database and the in-place record-size
policy change below. The compatible projection correction and new-topic cutover procedures
in this section are deferred design constraints. Integrating the representation-restamp
utility into an exact CDC baseline replacement is likewise deferred. None is an implemented
v1 production workflow because each requires the cross-replica and external-writer fence
excluded by [V1 readiness scope](#v1-readiness-scope).

Contract tests pin public field names, JSON types, key/tombstone behavior, document
semantics, and metadata relationships. They prove that Kafka Connect copies the opaque
DMS-computed `StreamEtag` exactly, but do not freeze its byte value independently of the
current DMS composer. A refactor or bug fix may change `DocumentJson` or `StreamEtag` while
remaining compatible with the documented v1 contract, but the strong-validator invariant
still applies: whenever the corrected public representation bytes differ, its
`StreamEtag` must also differ. Kafka's later partition offset orders equal-version records;
it does not make one strong ETag valid for two byte-different representations.

### Deferred compatible projection correction in `documents.v1`

A future equal-version path is allowed only when comparison of the affected old and
corrected representations proves that every byte change also changes `StreamEtag`.
Examples include a composer correction that changes the opaque tag itself or a correction
that changes no public representation bytes. That future deployment workflow must:

1. Enter the deployment-owned maintenance window, block and drain canonical mutations,
   mark the CDC target not ready, and stop every old cache writer, including projector
   loops and optional direct fill.
2. Deploy the corrected materializer/composer while old cache writers remain stopped.
3. Clear `dms.DocumentCache` with the provider-supported rebuild operation. The existing
   connector remains registered against the same binding and ignores cache maintenance
   deletes/truncation.
4. Start only corrected projector writers and run full reconciliation until an exact
   finishing audit reports zero missing, cache-behind, and cache-ahead rows. Rebuilt cache
   inserts publish at later offsets with unchanged `contentVersion` values.
5. Complete the provider source-position barrier captured after the zero audit, recheck
   projection readiness, restore combined CDC readiness, and only then reopen canonical
   mutation admission.

The correction does not advance canonical `ContentVersion`, reset offsets, create a new
topic, or reserve a new binding generation. Consumers replace an equal-`contentVersion`
record at a later partition offset. Ordinary reconciliation continues to treat an existing
equal-version row as fresh; the explicit clear-and-rebuild operation produces the
corrective inserts. Old cache writers remain stopped throughout the rebuild so two
materializer implementations cannot alternate output for one version. If changed bytes
would retain the prior `StreamEtag`, this path is prohibited.

### Offline byte-changing representation correction

When corrected API or stream representation bytes would otherwise retain their prior
strong ETag, operators may use the out-of-band representation-restamp utility only while
the selected data store is explicitly offline and every DMS replica, cache writer, and
external writer has been stopped outside the utility. This is a rare deployment repair,
not a request-path feature, not ad hoc SQL, and not an automated admission gate. It advances
the existing canonical representation stamps rather than adding a projection epoch or
another Kafka ordering field. V1 does not use the result to certify a replacement exact CDC
baseline; projection and connector status after write admission remain eventual.

The offline operation follows this sequence:

1. Stop every affected DMS replica, API reader, canonical writer, projector loop, optional
   direct-fill writer, bulk/seed loader, administrative path, and external writer outside
   the utility. Mark the CDC target not ready when present. The utility requires an explicit
   offline confirmation but does not implement or certify this fence.
2. Deploy the corrected API materializer/composer while the data store remains offline.
   The existing connector may remain registered against the same binding and topic when the
   stream contract remains compatible.
3. Run the out-of-band utility with an explicit affected-document scope and a persisted
   operation manifest. For each current affected document, allocate a fresh unique value
   from the normal change-version sequence, update `dms.Document.ContentVersion` and
   `ContentLastModifiedAt`, and update the concrete resource-root or `dms.Descriptor`
   content-stamp mirror in the same provider transaction. Do not change domain fields,
   identity stamps, keys, or deletion history. The utility uses a captured pre-restamp
   version boundary so a retry resumes without stamping an already completed document
   again.
4. After the utility completes, start only corrected DMS and projector instances. Existing
   affected cache rows are behind and ordinary reconciliation replaces them; missing rows
   are rebuilt. API reads retain relational fallback while projection catches up.
5. Observe eventual projection and connector recovery and verify that affected public
   records have higher `contentVersion` and different `document._etag` values. A later ready
   observation does not certify an exact replacement baseline or control API admission.

The utility is provider-aware and supports both PostgreSQL and SQL Server. It records the
physical-source fingerprint, scope, reason, pre-restamp boundary, counts, and completion
status for audit and safe resume. The higher versions deliberately make affected documents
visible as representation updates to Change Queries and cause conforming Kafka consumers
to replace prior state without a new topic, binding generation, or offset reset. The
dedicated implementation story owns the utility, provider behavior, tests, and operator
examples; general cache and CDC runbooks describe only its offline scope and do not
recreate it with manual SQL.

### In-place record-size increase

Increasing `maxRecordBytes` does not change the binding, any derived topic, keying,
partitioning, ordering, or public message contract. Deployment automation marks the target
not ready, confirms independently operated consumers have raised fetch and deserialization
capacity, raises broker/replica and public-topic limits, then updates producer
`buffer.memory` to at least the new ceiling and `max.request.size` last and restarts or
resumes the connector. It reads back and validates every effective value before restoring
readiness. A partial, out-of-order, or unverifiable rollout remains not ready. If an
over-budget record already failed the connector, the task resumes from its uncommitted
source position after the larger policy is effective.

### Deferred new-topic cutover

Changing the topic partition count or `partitionerAlgorithm` token creates a new binding
generation, public and progress topics, a SQL Server schema-history topic when applicable,
and consumer state namespace because offsets cannot order equal-version records across old
and new partitions. The new public topic may retain `documents.v1` and
`contractVersion: 1` when the public key/value/delete contract is otherwise unchanged.

A change to key encoding, required field names or JSON types, delete semantics, or the
document contract itself requires a new topic contract such as `documents.v2`. A future
deployment workflow must:

1. Enter the deployment-owned maintenance window, block and drain canonical mutations,
   mark the CDC target not ready, and reserve the new binding generation, public and
   progress topics, SQL Server schema-history topic when applicable, ACLs, and consumer
   state namespace without changing the old binding in place.
2. Stop the old connector and verify that all of its tasks are stopped or otherwise fenced
   from the source database. Stop every old-contract cache writer, including projector
   loops and optional direct fill. Neither old publication path may be restarted against
   the shared cache after this point.
3. Deploy the new-contract materializer/composer and connector transform while all cache
   writers remain stopped, then clear `dms.DocumentCache` with the provider-supported
   rebuild operation.
4. Start only new-contract projector writers and completely reproject the cache until an
   exact finishing audit reports zero missing, cache-behind, and cache-ahead rows.
5. Register the new connector against the new binding and topics with a fresh snapshot,
   complete the provider source-position barrier captured after the zero audit, recheck
   projection readiness, and bootstrap consumers in the new state namespace before
   restoring combined CDC readiness and reopening canonical mutation admission.
6. Explicitly retain or retire every old governed topic, including SQL Server schema
   history when applicable, its connector offsets, and consumer state; never restart the
   old connector against the rebuilt cache.

Stopping or fencing the old connector before the cache clear is a required cutover
barrier. Otherwise it could capture rows rebuilt under the new contract and publish them
into the old topic. Schema reprovisioning follows this path when retained consumer state
could otherwise observe reused keys and versions under an incompatible schema.

The new-topic cutover by itself does not advance canonical `ContentVersion`. If the
incompatible change also changes API or fixed-stream representation bytes that would reuse
their prior strong ETag, the maintenance sequence additionally runs the out-of-band
restamp utility before new-contract projection starts. Normal API correctness does not
depend on projection, but the old connector and old-contract projector writers must be
stopped or fenced before the cache is cleared or rebuilt. One cache row cannot supply two
incompatible contracts concurrently; a zero-gap overlap requirement needs separate
versioned projection state and another design decision.

## Enablement and Initial Readiness Sequence

The connector supports an initial snapshot of `dms.DocumentCache`. It may snapshot all
included tables. The operation filter drops every `dms.Document` snapshot record, while a
`dms.CdcHeartbeat` snapshot record routes to the progress topic. A snapshot heartbeat does
not satisfy the streaming source-position barrier; the adapter still rejects snapshot
offsets and waits for the post-audit action-query update.

V1 has one initial-enable path: a new physical database still in its initial provisioning
workflow under the completed E18 schema contract. Write admission has never opened, so the
offline precondition is satisfied without interrupting traffic:

1. Select CDC while creating the physical database and ensure the resulting
   `(tenant key, DataStoreId)` is an explicit DMS `DocumentCache:Targets` entry.
2. Provision and validate the complete current relational schema, including the E18 cache,
   identity, state, trigger, and access-path inventory. Do not alter a legacy cache schema.
3. Resolve the new physical source and atomically create its deployment-owned immutable
   binding record.
4. Apply provider CDC/key setup and create the binding's compact-only public and progress
   topics and their ACLs. For SQL Server, also create its persistent single-partition
   schema-history topic and connector-only ACLs.
5. Register the connector from that exact binding before DMS reconciliation or
   application writes that must be observed.
6. Start or roll out DMS while write admission remains closed, so the resulting
   startup/restart audit is fresh and monotonic cache upserts flow through established
   capture.
7. Wait for that fresh audit to produce DMS projection readiness, capture the provider
   source-position barrier afterward, and wait for connector snapshot/catch-up through
   that barrier using the committed Connect source offset.
8. Recheck DMS projection readiness for the same source fingerprint and advertise
   combined CDC readiness only when connector lag is also acceptable; then open write
   admission.

An already-provisioned physical database is not eligible for v1 initial enablement, even if
it has never had a connector. Bootstrap must reject it before creating a binding, provider
capture artifact, topic, ACL, or connector. Exact current-schema validation remains
required for a retry of the same not-yet-admitted initial workflow, but schema introspection
alone must not turn an unrelated existing database into an eligible target. Operators must
provision a new database; database upgrade, data movement, and CDC retrofit are outside v1.

## Local Bootstrap and CI

Local bootstrap exposes an explicit opt-in such as `-EnableKafkaCdc`.

- `-EnableKafkaUI` starts only Kafka UI.
- First-time CDC opt-in is accepted only when bootstrap reports that it created the selected
  physical database for this initial provisioning workflow; an unbound path that reuses an
  existing data store is rejected. A later invocation may exact-match and validate a binding
  previously created by the supported new-database path. Bootstrap starts Kafka and Kafka
  Connect if needed, verifies that the target is an explicit DMS projection target, and
  generates a connector without hard-coded database, topic, slot/capture, or data-store
  values. The initial eligibility check occurs before binding or external CDC artifacts are
  created.
- The local default binding state root is `eng/docker-compose/.cdc-state`; an explicit
  `-CdcBindingStatePath` may select another persistent deployment-owned location. It is
  never placed in `.bootstrap/bootstrap-manifest.json`.
- Binding reservation and registration are idempotent for an exact binding match and
  fail closed for missing or mismatched state around existing artifacts.
- Topic provisioning applies the explicit durability profile above to the public and
  progress topics and to the SQL Server schema-history topic when applicable. The local
  single-broker default is replication factor one with `min.insync.replicas=1`;
  production-like automation requires at least three replicas and `min.insync.replicas` of
  at least two. It does not rely on broker defaults.
- Public-topic provisioning requires and idempotently validates `cleanup.policy=compact`,
  an explicit per-topic `delete.retention.ms` of at least `604800000`, and
  `max.message.bytes=<maxRecordBytes>` from the current operational policy. It rejects any
  cleanup policy that includes `delete`, a missing topic-level tombstone-retention
  override, or a value below seven days.
  Progress-topic provisioning derives the name from the binding, requires exactly one
  partition and
  `cleanup.policy=compact`, and does not apply the public record-size or consumer-bootstrap
  contract.
  SQL Server schema-history provisioning derives its name from the binding and requires
  exactly one partition, `cleanup.policy=delete`, `retention.ms=-1`, and
  `retention.bytes=-1`; it rejects compaction or any finite time/size retention.
- Before connector registration, bootstrap verifies producer `max.request.size` and
  `buffer.memory` plus the broker request, record-batch, and replica-fetch path against
  `maxRecordBytes`; an unverifiable or smaller limit fails setup rather than relying on
  Kafka defaults. It also requires deployment-provisioned Kafka Connect worker heap beyond
  the configured producer buffer.
- The same workflow provisions and idempotently validates the binding-scoped topic ACLs
  before connector registration. It emits literal public-topic grants for the connector
  and deployment-supplied consumer principals. It grants the connector principal only the
  required producer access to the progress topic and grants no instance-consumer access to
  that topic. For the SQL Server schema-history topic, it grants only that connector
  principal the literal `READ`, `WRITE`, `DESCRIBE`, and `DESCRIBE_CONFIGS` permissions
  required by Debezium's history clients; the deployment principal owns creation and
  deletion, and instance consumers receive no access. It never emits a shared-topic,
  wildcard-topic, or cross-instance consumer grant.
- Bootstrap prints connector name, provider, database, opaque instance key, and public and
  progress topics plus the SQL Server schema-history topic when applicable; secrets are
  excluded.
- It calculates the combined readiness sequence above and reports whether binding,
  new-database/offline eligibility, fresh startup DMS projection,
  heartbeat/capture progress, provider-barrier catch-up, or lag failed. A timeout never
  opens writes as ready.
- E2E setup creates a fresh database, provisions its current schema, and registers capture
  against that same database before issuing writes it expects to consume.
- A normal local stop retains binding, connector, Kafka offsets, and every governed topic.
  Destructive local volume teardown removes the SQL Server history topic and ACLs with the
  connector and offsets, then removes the remaining governed artifacts, and deletes the
  binding record last.

Production-like automation may repeat this workflow only while initially provisioning each
new deployment-selected CDC database and while it can prove that the database has not been
published to any writer. It does not add CDC to an already-provisioned database and does not
implement a later baseline-replacing maintenance window.

## DDL and Query Support

The schema inventory below is create-only and applies to new physical databases. V1 emits
no `ALTER`/migration path for an older `dms.DocumentCache`. Provisioning reruns may validate
and preserve an already-current schema created by the same initial workflow, but encountering
legacy `Etag`, the obsolete cache UUID constraint, or any missing required E18 object makes
the database ineligible rather than triggering an in-place repair.

V1 provisions no durable projection queue, cursor, retry record, or backfill workflow.
Core DMS DDL provisions one `dms.DocumentCacheState` singleton solely to durably latch the
cache-ahead invariant; this safety bit is neither work inventory nor completeness evidence
and does not replace the deployment-owned CDC binding record. Supporting DDL is limited to
that latch, the projection, measured access paths, and the opt-in internal CDC heartbeat:

- `DocumentCache(DocumentId)` remains the primary/foreign key with cascade deletion.
- `DocumentCache.DocumentUuid` is a non-indexed denormalized connector-key column.
  Provider-specific insert/update triggers reject any value that differs from
  `Document.DocumentUuid` for the same `DocumentId`; no composite parent index or cache UUID
  unique index is provisioned.
- `DocumentCache.StreamEtag` stores the DMS-computed opaque ETag for the fixed CDC
  representation; it is not used by API reads.
- Provider-specific constraints ensure `DocumentJson` is a JSON object.
- `dms.DocumentCacheState` contains the singleton
  `CacheAheadRecoveryRequired` bit, initialized false. Detection only sets it true; the
  provider-supported explicit recovery transaction is the only operation that clears it.
  It is not a connector capture source.
- `dms.DataStoreIdentity` is an always-provisioned singleton containing the random
  `SourceIdentity`, stable during ordinary operation, used by the physical-source
  fingerprint contract.
- Provider CDC setup creates and seeds the singleton `dms.CdcHeartbeat` table only when
  CDC is selected. PostgreSQL uses `smallint`, `bigint`, and `timestamp with time zone` for
  `HeartbeatId`, `HeartbeatSequence`, and `HeartbeatAt`; SQL Server uses `smallint`,
  `bigint`, and `datetime2(7)`. Both enforce `HeartbeatId = 1` and
  `HeartbeatSequence >= 0`. The generated
  heartbeat action query increments that row atomically, and provider capture includes
  it only to implement the deployment-owned source-position barrier.
- `dms.Document(ContentVersion, DocumentId)` supports incremental discovery and bounded
  full-audit paging and is always provisioned with `dms.DocumentCache`.
- Add no additional projector/diagnostic index beyond the data-model-defined access
  paths until realistic provider query-plan measurements demonstrate a need.

If realistic steady-state and audit benchmarks remain unacceptable with the incremental
lane and required index, the next design step is a small transactionally maintained
pending-work table or flag. That durable optimization is deferred from v1 and must not
replace full audits as completeness evidence without a separate correctness decision.

PostgreSQL and SQL Server may use different plans while exposing equivalent logical
reconciliation, health, monotonic-upsert, and delete-fence behavior.

## Security, Telemetry, and Operations

Topic-per-instance ACLs are the Kafka authorization boundary. Shared topics requiring
consumer-side instance filtering are not supported. The public stream contains sensitive
data. The binding-scoped CDC progress topic may contain raw connector source metadata and
is available only to the connector principal and deployment control plane; instance
consumer principals receive no access. Kafka Connect worker internal topics, its REST API,
and database credentials are likewise not exposed to third-party consumers. Local
insecure defaults must be replaced in production.

Deployment bootstrap owns ACL provisioning as part of the one-shot binding workflow. For
each binding it idempotently creates and verifies the literal topic grants required by the
connector producer and the deployment-supplied instance consumer principals, plus only
the consumer-group grants required by those consumers. The derived progress topic receives
only the connector's required producer grants; the deployment control plane retains its
administrative access outside the instance-consumer grant set. Repeated execution must
accept an exact ACL match, repair missing required grants, and fail closed when the
effective deployment-managed ACL set would grant a configured instance consumer access to
another instance topic or any progress topic. ACL verification completes before connector
registration and before combined readiness can pass. It does not rely on consumer-side
filtering as an isolation control.

Structured logs and metrics cover:

- incremental source rows examined, candidates, cursor advances, and scan durations,
- full-audit rows examined, finishing counts, observation age, and durations,
- effective projector settings, due/overdue audits, coalesced audit requests, active and
  concurrency-gated targets, and bounded page sizes,
- projection attempts, successes, and failures,
- target-scoped repair-required state, failed-page counts, and backoff duration,
- monotonic already-fresh and superseded-candidate no-op counts,
- unresolved, missing, cache-behind, and cache-ahead-invariant counts plus oldest
  unresolved age,
- durable cache-ahead latch set/clear state, cache hits, misses, stale or latched misses,
  and relational fallback,
- unresolved explicit targets, retryable source-resolution failures, and the current
  opaque projection-source fingerprint.

Deployment-owned CDC status additionally covers binding presence and match, connector
running state, current lag plus Debezium 3.6 P50/P95/P99 source-lag telemetry, last error,
snapshot completion, heartbeat/capture progress, the provider barrier and committed
Connect source offset in sanitized form, existing artifacts without binding state, source
mismatch, and generation migration state.

Use provider, safe project/resource identity, failure category, target-resolution state,
and opaque data-store identity only where cardinality policy permits. Never log
document bodies, raw student data, connection strings, credentials, tenant display
names, or unsanitized physical identifiers.

The deployment status operation identifies binding generation, connector,
provider/source, public and progress topics, the SQL Server schema-history topic when
applicable, PostgreSQL slot or SQL Server capture instance, DMS projection health, snapshot
state, lag, and last error. A failure for one target does not conceal peer status or stop
unrelated DMS API instances.

Runbooks cover connector restart, cache rebuild, same-topic compatible projection repair,
cache-ahead invariant diagnosis and the internal-only/downstream-state recovery split,
ordinary monotonic projection lag, offset reset, resnapshot, topic recreation,
progress-topic diagnosis, SQL Server schema-history recovery, target migration/retirement,
and provider artifact cleanup.
They document the seven-day public-topic tombstone-retention minimum and the 24-hour
consumer-bootstrap deadline, how a consumer captures and completes an end-offset barrier,
how to capacity-test the largest supported retained log rather than only its live-key count,
how to observe cleaner health and earliest-to-end scan volume, and why an over-deadline
reconstruction must be discarded rather than advertised as valid.
They document the shipped projector defaults, how to tune intervals, page size, target
concurrency, and maximum audit age, and how to identify API-resource contention or an
audit that cannot complete within its readiness window.
They cover binding-state backup, fail-closed missing-state recovery, explicit adoption,
cleanup ordering, and new-generation source migration; they never repair a mismatch by
rewriting an immutable binding record. They state that same-topic baseline replacement and
incompatible-contract cutover are deferred until an owned cross-replica/external-writer
fence exists. The representation-restamp utility is documented only for an explicitly
offline data store and does not certify another exact CDC baseline. Offset reset and
resnapshot can replay current document state but remain eventual recovery after first-write
admission.
Topic, offset, ACL, slot, and capture deletion is destructive and always explicit; removal
from configuration is not cleanup authority.

## Verification

Fast contract and transform tests cover every public, progress-routed, and dropped source
operation, serialized key/value bytes, duplicate-tombstone suppression, JSON expansion,
exact copying of the DMS-computed stream ETag, timestamp format, metadata consistency, and
topic routing. Materializer tests prove `StreamEtag` is produced by the shared DMS
served-ETag composer for the fixed stream representation and remains coherent with the
row's `ContentVersion` and effective schema. V1 fixtures pin contractual public fields,
types, selectors, and metadata relationships without independently freezing opaque ETag
bytes. Representative boundary fixtures use the shared materializer across selected
configured schemas, extensions, nested collections, and reference links, then shape them
through the real transform and converters. A broker-backed test publishes, replicates, and
consumes an under-budget record with `max.request.size` set to the operational
`maxRecordBytes` and producer buffer-memory, public-topic, broker, replica-fetch, and
consumer configuration able to carry it; an over-budget variant fails the connector task
and combined readiness instead of being skipped. These fixtures prove enforcement and
aligned configuration, not a universal maximum valid record. Ordering tests prove higher
versions replace, lower versions are ignored, and a later partition offset replaces an
equal version. Contract fixtures retain the equal-version and new-contract consumer rules,
but v1 does not include baseline-replacing correction or cutover integration tests.

Connector template and registration tests require the exact idempotence, acknowledgement,
retry, maximum-in-flight, no-compression, and operational maximum-request/buffer-memory
producer overrides, reject every conflicting value and an override-disallowing worker
policy, and verify the registered connector retains the required configuration. Fixed UUID
key fixtures verify that the generated producer implementation maps exactly to the binding's
`kafka-murmur2-v1` behavior across representative partition counts; missing, unknown, or
changed algorithm tokens and conflicting partitioner configuration fail closed. The tests
do not couple binding state to a Java class or library version. They also require an
explicit `errors.tolerance=none`, reject a
duplicate, missing, or conflicting value, and reject live configuration drift. A
broker-backed retry-ordering test injects a retriable producer failure after an upsert is
submitted and before its canonical tombstone, then proves the public partition contains
the upsert before the tombstone and remains deleted after connector catch-up. One
broker-backed poison-record test supplies a malformed retained record and proves that no
public record is emitted, the connector task fails instead of skipping it, and combined
readiness remains false rather than accepting offset or lag catch-up beyond the poison
record.

Source-position adapter tests pin PostgreSQL `X/Y` and signed Debezium `lsn_proc`
normalization to the same unsigned 64-bit order. SQL Server tests pin binary and
`xxxxxxxx:xxxxxxxx:xxxx` LSN normalization and lexicographic commit/change/event-serial
ordering. Both reject snapshot, null, malformed, wrong-partition, and ambiguous offset
responses. Initial-readiness real-provider tests start from a new offline source, take a
barrier after the fresh startup zero audit, let the configured heartbeat action query advance
capture, and prove combined readiness stays false until
`GET /connectors/{connectorName}/offsets` reaches that barrier.
They also prove a heartbeat is produced and acknowledged in the progress topic, advances
the committed source offset only after it and every earlier retained record complete
processing, and emits no public document record. Returning `null` for the same heartbeat
must fail this test by leaving the committed offset behind the barrier.

Initial combined-readiness sequence tests prove the setup controller created the selected
database and has not published it to a writer, reject a prior audit, force a fresh startup
audit, and keep first-write admission closed until the cache writes and heartbeat are
acknowledged through the provider barrier. Connector `RUNNING`, acceptable lag, timeout,
and setup-controller restart cannot bypass the sequence or publish the database as
CDC-ready. Tests after first-write admission treat a later ready result as eventual health
and never as another exact baseline.

Deployment-state tests cover atomic first creation, exact-match retry, immutable-field
mismatch including attempted partition-count or `partitionerAlgorithm` changes, rejection
of a public topic configured with a cleanup policy that includes
`delete`, a missing topic-level `delete.retention.ms` override, an explicit value below
`604800000`, or conflicting `max.message.bytes`, rejection of a missing, misnamed,
non-single-partition, or non-compacted progress topic, provider aliases that resolve to the
same fingerprint, existing artifacts with missing state, cleanup ordering, normal-stop
retention, destructive-teardown removal, and new-generation source migration.
Multi-controller state backends additionally prove compare-and-set behavior. No test
repairs a mismatch by rewriting a binding, changes a topic's partition count or
`partitionerAlgorithm` in place, or reuses a topic generation for a different source.
Operational-policy tests prove a downstream-first `maxRecordBytes` increase retains the
binding and topics, validates consumer/broker/replica/topic capacity before producer
request/buffer-memory changes, and remains not ready after any partial rollout.

Initial-enable tests prove a freshly created database with the complete current E18 schema
can proceed, a reserved exact binding can retry idempotently, and any unbound
already-provisioned or legacy-schema database is rejected before binding state or provider,
topic, ACL, and connector artifacts are created. A later exact-match validation/restart of a
successfully enabled database remains supported. No test upgrades `DocumentCache.Etag` or
removes the obsolete UUID constraint in place.

Bootstrap integration coverage uses an authorization-enabled broker to prove ACL
provisioning is repeatable and binding-scoped: a consumer principal configured for one
instance can read that instance topic and is denied when it attempts to read a peer
instance topic or either instance's progress topic. The connector principal can write the
progress topic. This focused broker-backed check belongs to bootstrap story 19-04; the
broad API-driven CDC E2E suite does not duplicate an ACL matrix.

SQL Server template tests require the explicit `time.precision.mode=isostring` and
`unavailable.value.placeholder=__debezium_unavailable_value` settings. Transform tests use
realistic `io.debezium.time.IsoTimestamp` strings with zero through seven significant
fractional digits and assert the same whole-second UTC string for every value within that
second, truncation rather than rounding at the upper fractional boundary, exact equality
with `document._lastModifiedDate`, and rejection of unexpected, non-UTC, fractional, raw
numeric, or unavailable-marker public output. Pinned-image provider tests include SQL
Server 2025.

PostgreSQL and SQL Server integration/E2E coverage proves:

- provider key and delete-record prerequisites,
- create/update/snapshot upserts conform to the topic/message ADR,
- canonical deletion emits a tombstone when no cache row exists,
- cache delete/truncate/rebuild emits no domain tombstone,
- with the pinned idempotent-producer settings, a cache upsert committed before canonical
  deletion appears before its tombstone for that key in the routed public topic even when
  the upsert send receives a retriable failure,
- a projector that captured an older version may commit it after a newer canonical version,
  the row remains cache-behind work, and reconciliation converges it to the newer version,
- a source update committed before the final optimistic source-version check produces a
  stale skip, while an update committed after that check may produce the allowed coherent
  lower-version cache row,
- a delayed lower-version candidate never replaces a higher cache row, while a consumer
  that has not yet seen the higher version may temporarily retain the lower monotonic
  projection,
- raw Kafka delivery may replay a lower non-null version after a higher non-null version,
  while the conforming consumer ordering rule keeps applied upsert state monotonic,
- raw at-least-once replay may temporarily place an older upsert after a tombstone, while a
  subsequent replayed tombstone restores deleted state and connector catch-up re-establishes
  convergence,
- projector and direct-fill cache writes request no explicit update/write lock on
  `dms.Document` as a content-version fence and carry no lock from the optimistic source
  check into the cache transaction; ordinary locks acquired by foreign-key enforcement and
  the UUID-validation trigger remain intact and are distinguished from that fence,
- the cache foreign key prevents a delayed candidate from recreating cache state after
  canonical deletion,
- projection selection, empty-cache population, update, restart, backed-off repair, rebuild,
  monotonic upsert, delete fencing, health, cache fallback, and mixed-target isolation,
- ordinary high-version updates are discovered through the incremental lane without a
  full relationship scan,
- a source update committed after the startup audit's finishing observation but before
  incremental scanning begins remains above the pre-audit boundary and is discovered by
  the incremental lane,
- a lower version committed after the incremental cursor advances is repaired by the
  next full audit,
- initial combined readiness starts from a new offline database, rejects a prior zero
  audit, forces a fresh startup audit, waits through the post-audit publication barrier,
  and opens first-write admission only after readiness passes,
- cache-row loss below the incremental cursor is repaired by the next full audit,
- advancing the cursor past a failed candidate retains no document-scoped retry state,
  marks the target repair-required, and lets the next full audit rediscover the database
  difference,
- a cache-ahead row atomically sets the durable database latch, receives no materialization
  or ordinary repair, disables cache reads and writes, keeps projection readiness false
  across restart, and cannot be overwritten with the lower canonical version,
- after a latched ahead row's source advances to exactly the cached `ContentVersion`, the
  row remains ineligible and readiness remains false until explicit recovery,
- internal-only cache-ahead recovery clears the entire cache and latch in one transaction
  before rebuilding from canonical state, while a possibly published higher version
  requires a new downstream state namespace; Kafka CDC uses a new binding generation/topic
  and fresh snapshot,
- projection or connector failure never blocks normal API deletion.

Projector scheduling tests prove one serialized loop per target, process-wide target
concurrency, bounded pages with no document-scoped retry queue, fair progress across a large
and small target, audit-request coalescing, startup/rebuild immediate audits, observational
health reads, interval-based incremental and full-audit eligibility, stale-audit readiness,
and graceful cancellation. A systemic failure across more documents than `PageSize` proves
candidate memory remains bounded by active pages, paging continues, every failed document is
rediscovered from database state, and readiness remains false until an exact-zero audit.
Configuration tests validate all execution settings and pin the implementation-tuned
defaults as documented release behavior rather than stream-contract constants.

Performance qualification compares projector-disabled and projector-enabled source-write
throughput and p95/p99 latency for uniformly distributed writes, a deliberately hot
single document, duplicate projector replicas, and optional direct fill. Rebuild tests
also verify that projection adds no PostgreSQL or SQL Server wait attributable to an
explicit content-version source-row lock; ordinary cache-row, trigger, and foreign-key
contention remains visible. Direct-fill tests bound request-path latency under that
ordinary contention. V1 performs one short monotonic cache transaction per candidate;
future batching remains a separate measured design.

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
