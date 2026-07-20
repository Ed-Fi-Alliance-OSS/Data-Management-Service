---
status: proposed
date: 2026-07-20
jira:
  - DMS-1245
  - DMS-1246
related:
  - DMS-1232
  - DMS-1240
  - DMS-1089
---

# Relational CDC and Document Projection

## Authority and Document Ownership

This is the authoritative integration and deployment design for relational DMS change
data capture (CDC) and the `dms.DocumentCache` projection that supplies its upsert
payloads. It owns configuration, target selection, projection mechanics, readiness,
provider deployment, bootstrap, operations, and verification.

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

DMS publishes a compacted document-state stream by reading database transaction logs
with Debezium in Kafka Connect. CDC is not a request-path dual write: API write
correctness remains tied to the relational database even when Kafka or projection is
unavailable.

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

Capabilities select projection; there is no independently configured projector mode and
no process-wide Kafka CDC boolean.

```text
DataManagement:DocumentCache:Enabled = false | true
DataManagement:DocumentCache:ReadAcceleration:Enabled = false | true
DataManagement:KafkaCdc:Targets = [{ TenantKey, DataStoreId }, ...]
```

The defaults are `false`, `false`, and an empty target list. The settings are additive:

| Selector | Projection scope | Additional behavior |
| --- | --- | --- |
| `DocumentCache:Enabled` | Every loaded data store with a usable connection string | Standalone projection for indexing, diagnostics, or another consumer |
| `ReadAcceleration:Enabled` | Every loaded data store with a usable connection string | Fresh cache rows may accelerate GET/query body assembly |
| `KafkaCdc:Targets` | Only the listed `(tenant key, DataStoreId)` entries | Connector provisioning, registration, and CDC readiness for those targets |

The effective projection target set is the de-duplicated union of those selections. DMS
runs one logical reconciliation loop for each selected data store. A Kafka target is
projected when both process-wide settings are false, and an unlisted data store does not
acquire CDC infrastructure because another data store uses it.

`KafkaCdc:Targets` is the sole runtime CDC enablement contract. An empty list disables
CDC. Entries must be unique after applying the same case-insensitive tenant-key
normalization as `IDataStoreProvider`. Kafka infrastructure and `-EnableKafkaUI` do not
select targets or register connectors.

The effective set is bound at startup. DMS does not infer projection or CDC membership
from HTTP requests, route qualifiers, JWT `DataStoreIds`, the most recent request, or the
complete CMS inventory. Adding or removing a target requires configuration change and a
coordinated deployment. V1 has no dynamic target discovery or automatic connector
retirement.

All projection and CDC health is observational. It never changes `IDataStoreSelection`,
normal request routing, or API read/mutation availability.

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

`DocumentId` is the internal primary key and an `ON DELETE CASCADE` foreign key to
`dms.Document`. `DocumentUuid` is unique and is the public identity used by CDC.

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
DocumentJson.id == DocumentUuid
DocumentJson._lastModifiedDate == formatted LastModifiedAt
StreamEtag == DMS served-etag composition for the fixed stream representation
```

An invariant failure is a materialization failure and produces no cache write.
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

Candidate discovery is separate from completeness verification. For each selected data
store, the projector has two cooperating lanes:

1. A frequent incremental lane keyset-pages current `dms.Document` rows after a
   process-local `(ContentVersion, DocumentId)` cursor, ordered by those columns. Each
   page left-joins `dms.DocumentCache` by `DocumentId`, and only missing or
   version-mismatched rows become materialization candidates. The cursor advances to the
   last source row examined whether the cache row was stale or already fresh; failed
   candidates remain in the in-memory retry set described below.
2. A periodic full-audit lane evaluates the complete source/cache anti-join above. It
   repairs every mismatch it finds, including rows below the incremental cursor, and
   finishes with one exact aggregate observation of total, missing-row, and
   version-mismatched-row counts plus the oldest mismatch timestamp. Startup, restart,
   cache rebuild, and any attempt to establish readiness require a completed full audit.

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
prefix. An audit may race with writes; guarded upserts make its repairs safe, and a
nonzero finishing aggregate causes repair to continue. The full-audit interval is
bounded so it also bounds discovery latency for work that the incremental lane cannot
see.

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
are continuously successful. On startup the projector establishes its process-local
cursor from the maximum current source key observed at or after the required finishing
audit; restart may repeat work but cannot lose it.

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
   same-version result is already fresh and does not need another captured update.
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
projector-state rows, failure rows, retry classifications, dead-letter transitions,
requeue APIs, or manual repair workflows in v1. The process-local incremental and audit
cursors are disposable scan positions only. Empty-cache population,
truncation/rebuild, recovery, and completeness all derive from the current database
difference. A maximum scanned or projected version cannot prove completeness because a
lower current version may still be missing.

Failures use bounded in-memory exponential backoff with jitter keyed by opaque data-store
identity, `DocumentId`, and current `ContentVersion`. A deferred candidate does not
starve other work. Its entry is removed when the version changes, the document is
deleted, or the cache becomes fresh. Restart loses only the delay and rediscovers work
from database state. V1 has no retry budget or persisted attempt count; a persistent
failure remains visible until its underlying data, mapping, or service cause is fixed.

The hosted supervisor creates an isolated, non-HTTP service scope for each startup
target and explicitly selects its data store. It does not depend on
`ResolveDataStoreMiddleware` or reuse request-scoped `IDataStoreSelection`. One
unavailable data store does not stop peers. Multiple DMS replicas may perform duplicate
scans safely because candidate discovery is read-only and writes are idempotently fenced.
Deployments may designate projector hosts to avoid redundant work; correctness does not
require a distributed lease.

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

## Projection Health and CDC Readiness

Projection health is evaluated for each explicit `(tenant key, DataStoreId)` execution
context. It reports at least:

- selection reason and required-table existence,
- whether the in-process loop is running,
- whether an incremental scan or full audit is in progress,
- the latest completed full audit's observation time, duration, and age,
- that audit's exact total mismatch, missing-row, and version-mismatched-row counts,
- its oldest mismatch source timestamp and age, derived from
  `dms.Document.ContentLastModifiedAt`,
- currently known unresolved incremental candidates and retry deferrals, identified as
  process-local observations rather than exact database counts.

Optional counts by project/resource may be exposed when operationally safe. Process-local
incremental cursor, last scan, successful upsert, and last error are diagnostic only.
`LastScannedContentVersion`, `LastProjectedContentVersion`, and last-success timestamps
are never completeness evidence.

Health reads return the latest audit snapshot; they do not synchronously execute a full
anti-join. Configurable audit-age, mismatch-count, and oldest-mismatch-age thresholds
distinguish a fresh zero observation or brief asynchronous lag from a stale audit or
sustained degradation. A nonzero finishing audit invalidates completeness until a later
exact finishing aggregate returns zero. Incremental discovery makes readiness false
while that known candidate or retry remains unresolved, but successful repair does not
force another full scan; the last exact-zero audit retains its original observation time
and must still satisfy the audit-age threshold. A same-version timestamp comparison is
not another freshness or completeness test; embedded metadata consistency is enforced
when a row is materialized and written.

Connector registration prerequisites are the subset available before completeness:

- explicit target membership,
- provisioned `dms.Document` and `dms.DocumentCache`,
- a startable reconciliation execution context,
- the guarded upsert,
- provider CDC/key prerequisites and the source-operation transform.

End-to-end CDC readiness additionally requires:

- the configured target still resolves to its startup provider/physical source binding,
- a sufficiently recent completed full audit whose exact finishing mismatch count is
  zero,
- no currently known unresolved incremental candidate or retry deferral,
- connector snapshot/catch-up through a database source position observed at or after
  the zero-mismatch observation,
- connector lag within its configured threshold.

There is no backfill epoch, completed backfill target, or maximum projected version in
this readiness calculation. Connector/source checks belong to CDC; the projector does
not call Kafka Connect.

Readiness and health are per data store. A deployment aggregate may be not ready while
retaining each target's result. No failure gates normal API traffic.

## CDC Target and Physical Source Binding

Each logical instance topic maps to exactly one physical database containing the
captured tables. Registering separately authorized topics for multiple CMS aliases of
the same physical document set is rejected because the rows contain no tenant or
data-store discriminator.

The logical connector identity is `(deployment key, tenant key, DataStoreId)`. Tenant
identity remains deployment/administrative state and is not published in the topic or
message. Route-qualifier-only changes do not affect connector or topic identity.

Deployment resolves a provider-specific physical database identity for every configured
target. Comparison does not rely only on raw connection-string text: semantically
equivalent strings and server aliases must be normalized or confirmed after connection.
Diagnostics identify conflicting opaque data-store IDs without credentials, tenant
display names, or unsanitized physical identifiers.

At successful provisioning DMS records an immutable binding from the configured target
to provider and provider-resolved physical database identity. Credential, timeout,
pooling, application-name, host-alias, or equivalent connection changes are not drift
when the provider and physical database are unchanged.

After a successful CMS refresh, DMS reevaluates source identity only for configured CDC
targets. A missing target or confirmed provider/physical-source change makes that target
not ready. Confirmed drift is latched as `CdcSourceDriftRequiresDeployment` until a
coordinated deployment reruns the one-shot workflow, even if CMS later returns to the
original source. A transient identity-resolution failure is retryable and is not latched.

Source observation is not source reconciliation. Runtime DMS does not move projectors,
call Kafka Connect, stop connectors, or delete topics, offsets, ACLs, PostgreSQL
slots/publications, or SQL Server capture state in response to CMS changes. Moving a
`DataStoreId` to another physical document set requires an explicit migration choosing a
new topic/source generation or deliberately resetting the existing topic and connector
state before resnapshotting. Removing a target requires explicit decisions for every
retained or deleted artifact.

The provisioning workflow is idempotent for the same logical connector and physical
source and rejects reuse of an existing instance topic for a different physical document
set without an explicit migration/reset decision.

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

## Stream Contract Compatibility and Version Cutover

The [topic/message ADR](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-stream-representation-immutability)
defines the v1 stream representation and served-ETag composition as immutable for
unchanged inputs. V1 adds no projection-generation column or public ordering field.
`ContentVersion` therefore remains the sole cache freshness value and the sole consumer
stale-write value within `documents.v1`.

Every implementation of the shared served-ETag composer must carry fixed conformance
fixtures for the v1 stream context. Refactoring the composer, materializer, or connector
is allowed only when the same semantic inputs retain the exact `StreamEtag` and the same
contractual public fields and values. Updating those fixtures to bless a changed value
without also defining a new contract version is forbidden.

If an intentional change or bug fix would change the v1 ETag for unchanged inputs, the
fixed stream selectors, or the projected document representation for unchanged canonical
document/schema state, operators perform this coordinated cutover:

1. Pause the v1 connector, stop every old-contract projector writer, and report the CDC
   target not ready before new-contract cache rows can be written.
2. Provision a new versioned topic and ACLs and prepare the new DMS
   materializer/composer and connector contract, but keep new-contract projector writers
   stopped. The topic suffix and value `contractVersion` advance together.
3. While all projector writers remain stopped, truncate `dms.DocumentCache`, or provision
   an equivalently empty cache, so the same-`ContentVersion` guarded-upsert rule cannot
   preserve old-contract rows.
4. Deploy and start only new-contract projector writers, then run ordinary full
   reconciliation until an exact finishing audit reports zero missing or
   version-mismatched rows.
5. Register the new connector with a fresh initial snapshot of the completely
   reprojected cache, catch it up through the required post-audit source position, and
   advertise readiness only after the ordinary readiness checks pass.
6. Bootstrap consumers from the new topic's independent state namespace. Retain or
   retire the now-frozen old topic only through explicit operator policy.

The cutover does not advance canonical `ContentVersion`. Republishing changed derived
values into the existing topic is unsupported because live v1 consumers apply only a
newer `contentVersion`. Cache deletes/truncation still have no domain meaning, but the
cutover is intentionally replay-producing and destructive to projected state. Normal API
correctness does not depend on projection, so a host may resume API traffic on
new-contract nodes while their cache is rebuilding; old-contract API/projector nodes must
already be drained. Otherwise the host uses a coordinated maintenance window. CDC remains
not ready during the cutover.

One cache row cannot supply two ETag algorithms or document representations at once.
Consequently, v1 and a successor contract are not both kept live by this procedure. A
zero-gap overlap requirement needs separate versioned projection state and a new design
decision rather than another field added implicitly to `DocumentCache`.

## Enablement and Initial Readiness Sequence

The connector supports an initial snapshot of `dms.DocumentCache`. It may snapshot both
included tables, but the operation filter drops every `dms.Document` snapshot record.

For a new instance:

1. Provision and validate the relational schema and cache table.
2. Apply provider CDC/key setup and create the instance topic and ACLs.
3. Add the target to `KafkaCdc:Targets`, making the projector execution context and
   fencing available.
4. Register the connector before reconciliation or application writes that must be
   observed.
5. Start DMS reconciliation so guarded cache upserts flow through established capture.
6. Complete a full audit with an exact finishing count of zero mismatches and observe a
   database source position at or after that audit observation; advertise readiness only
   after snapshot/catch-up reaches that position, no known projection work remains, and
   connector lag is acceptable.

For an existing instance, perform the same one-shot sequence before write/delete traffic
that the host expects CDC to observe. If capture cannot be registered first, quiesce
traffic until it is registered. Existing cache rows are handled by the connector's
initial snapshot; remaining mismatches are repaired by ordinary reconciliation.

## Local Bootstrap and CI

Local bootstrap exposes an explicit opt-in such as `-EnableKafkaCdc`.

- `-EnableKafkaUI` starts only Kafka UI.
- CDC opt-in starts Kafka and Kafka Connect if needed, resolves the selected configured
  target, verifies registration prerequisites, and generates a connector without
  hard-coded database, topic, slot/capture, or data-store values.
- Registration is idempotent for the same connector and source.
- Bootstrap prints connector name, provider, database, opaque instance key, and topic;
  secrets are excluded.
- It waits for the readiness sequence above and reports which prerequisite or readiness
  condition failed.
- E2E setup registers capture against the same provisioned database used by the DMS test
  process before issuing writes it expects to consume.
- Local teardown removes connector and Kafka state with the local stack and volumes.

Production-like automation repeats this one-shot workflow for each explicit target.

## DDL and Query Support

No `DocumentCacheProjectionState`, `DocumentCacheProjectionFailure`, or other durable
workflow table is provisioned. Supporting DDL is limited to the projection and measured
access paths:

- `DocumentCache(DocumentId)` remains the primary/foreign key with cascade deletion.
- `DocumentCache.DocumentUuid` remains unique for connector keys.
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
- projection attempts, successes, and failures,
- retry deferrals and backoff duration,
- guarded stale-write skips,
- guard lock-wait duration, timeout, deadlock, and serialization-retry counts,
- mismatch count and oldest mismatch age,
- cache hits, misses, stale misses, and relational fallback,
- connector running state, lag, last error, and snapshot completion,
- missing targets, retryable source-resolution failures, and latched drift.

Use provider, safe project/resource identity, failure category, projection-selection
reason, and opaque data-store identity only where cardinality policy permits. Never log
document bodies, raw student data, connection strings, credentials, tenant display
names, or unsanitized physical identifiers.

Operator status identifies connector, provider/source, topic, PostgreSQL slot or SQL
Server capture instance, snapshot state, lag, and last error. A failure for one target
does not conceal peer status or stop unrelated DMS API instances.

Runbooks cover connector restart, cache rebuild, guard-lock contention and timeout
diagnosis, offset reset, resnapshot, topic recreation, target migration/retirement, and
provider artifact cleanup. They also cover the coordinated immutable-contract cutover:
freeze v1 publication, completely reproject the cache, snapshot into a new versioned
topic, and bootstrap consumer state without advancing canonical `ContentVersion`. Offset
reset and resnapshot can replay current document state. Topic/offset/ACL/slot/capture
deletion is destructive and always explicit; removal from configuration is not cleanup
authority.

## Verification

Fast contract and transform tests cover every retained and dropped source operation,
serialized key/value bytes, duplicate-tombstone suppression, JSON expansion, exact
copying of the DMS-computed stream ETag, timestamp format, metadata consistency, and
topic routing. Materializer tests prove `StreamEtag` is produced by the shared DMS
served-ETag composer for the fixed stream representation and remains coherent with the
row's `ContentVersion` and effective schema. Golden v1 fixtures prove unchanged composer
inputs retain the exact ETag and contractual public fields and values across refactors.
An intentional fixture change requires a new topic suffix, matching `contractVersion`,
and version-cutover coverage; tests reject blessing an output-changing composer change
as v1.

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
- a lower version committed after the incremental cursor advances is repaired by the
  next full audit,
- cache-row loss below the incremental cursor is repaired by the next full audit,
- advancing the cursor past a failed candidate retains that candidate for bounded
  in-memory retry,
- a higher projected `ContentVersion` cannot hide a missing lower current version,
- projection or connector failure never blocks normal API deletion.

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
