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

This document owns configuration, integration, deployment, readiness, and operations for
relational DMS change data capture (CDC) and the `dms.DocumentCache` projection that
supplies its upsert payloads. It consumes, but does not redefine, the physical schema,
projector semantics, or public Kafka contract.

Normative ownership is intentionally exclusive:

| Owner | Normative subject |
| --- | --- |
| [`data-model.md`](backend-redesign/design-docs/data-model.md) | Physical tables, columns, constraints, indexes, and triggers |
| [Relational CDC projector and sources](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md) | Projection/source choice, cached projection semantics, freshness, reconciliation, and cache/domain lifecycle |
| [Kafka topic and message contract](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md) | Public topic, key, value, tombstone, consumer behavior, transform behavior, and compatibility contract |
| This document | Configuration, integration, deployment, readiness, and operations |
| Epics and stories | Implementation scope and acceptance evidence |

Supporting documents may state facts local to their subject and link to the owner. They
must not create a second normative contract for a subject in this table.

Epics and stories may name implementation components, dependencies, test suites, and
delivery artifacts. Their acceptance evidence links to the applicable owner and records
the evidence produced; it does not repeat algorithms, message fields, fixed values,
readiness conditions, recovery rules, or other normative requirements.

Older references to legacy `dms.Document` JSON columns, `EdfiDoc`, OpenSearch, a shared
`edfi.dms.document` topic, or `deleted=true` messages are historical and are not active
relational CDC contracts.

### Documentation Audit and Disposition

For `dms.DocumentCache` projection and relational CDC/Kafka subjects, normative authority
is distributed across `data-model.md`, the two decision records, and this integration
design according to the table above. If another document conflicts with an owner on its
subject, the owner prevails. The classifications below apply only to each artifact's
DocumentCache or CDC/Kafka content, not to unrelated material in the same artifact.

| Existing material | Classification | Disposition |
| --- | --- | --- |
| This `cdc-streaming.md` document | Current | Normative for configuration, integration, deployment, readiness, and operations only. |
| [`0001-relational-cdc-projector-and-sources.md`](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md) and [`0002-kafka-topic-and-message-contract.md`](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md) | Current | Normative for their focused projector/source and public streaming subjects; this document supplies integration and deployment context. |
| [`data-model.md`](backend-redesign/design-docs/data-model.md) | Current | Normative for physical relational objects only; runtime projection and streaming behavior links to its owning ADR or this document. |
| [`transactions-and-concurrency.md`](backend-redesign/design-docs/transactions-and-concurrency.md), [`link-injection.md`](backend-redesign/design-docs/link-injection.md), and [`update-tracking.md`](backend-redesign/design-docs/update-tracking.md) | Current | Supporting descriptions of local transaction, representation, and ETag facts. They defer to the owning ADR for projection and streaming behavior. |
| [`ddl-generation.md`](backend-redesign/design-docs/ddl-generation.md) and [`flattening-reconstitution.md`](backend-redesign/design-docs/flattening-reconstitution.md) | Current | Supporting DDL and API materialization context. JSON response streaming in reconstitution is not CDC/Kafka streaming. |
| [`expandjsonsmt-replacement.md`](backend-redesign/design-docs/expandjsonsmt-replacement.md) | Current | Owns the implemented generic expand-JSON transform only. Legacy DMS record-shape discussion is context; the relational source contract is owned by ADR 0001 and the `DocumentState` transform/topic/message contract by ADR 0002. |
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
use the guarded source-replacement recovery defined below. Those operations are not another
initial enablement, never modify the core E18 schema, and expose only eventual status after
first-write admission. Guarded source replacement does not provide an exact baseline or
clear a possibly published cache-ahead latch.

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

A SQL Server data store selected in `DocumentCache:Targets` must have
`READ_COMMITTED_SNAPSHOT ON`. This is a projection and cache-use prerequisite, not a
global SQL Server DMS requirement: an unlisted relational-only data store may continue to
use locking `READ COMMITTED`. DMS validates the option when it resolves or replaces a
target execution context and before a source/cache comparison on a newly opened target
connection; it never changes the database option at runtime. A false or unreadable
`sys.databases.is_read_committed_snapshot_on` result leaves that target resolved but
projection-ineligible and unhealthy. DMS performs no scan, audit, direct fill, cache-backed
read, or cache-ahead latch update for that target and continues canonical relational API
processing with relational reads. The bounded supervisor rechecks the prerequisite so an
operator can complete the required offline database step, enable RCSI, and restore
eligibility without changing target membership. `ALLOW_SNAPSHOT_ISOLATION` remains optional
because v1 does not use an explicit SQL Server `SNAPSHOT` transaction for projection.

Deployment automation selects CDC targets separately and must configure every CDC target
on at least one designated DMS projector host as a `DocumentCache:Targets` entry. Kafka
infrastructure, `-EnableKafkaUI`, and `-EnableKafkaCdc` do not implicitly select DMS
projection targets. All projection and CDC health remains observational and never changes
`IDataStoreSelection` or normal request routing by itself. Only the initial setup controller
delays publishing a new offline database to writers while establishing first readiness;
DMS health polling does not activate or release a runtime gate.

## Cached Document Contract

The physical row shape, constraints, indexes, and validation triggers are owned by
[data-model.md](backend-redesign/design-docs/data-model.md#6-dmsdocumentcache-always-provisioned-optional-projection).
The cached projection semantics and materialization invariants are owned by the
[projector/source ADR](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract).
The public Kafka representation derived from that projection is owned by the
[topic/message ADR](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#upsert-value).

## Freshness and Reconciliation

The [projector/source ADR](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation)
owns the freshness predicate, discovery and audit algorithms, monotonic cache write,
failure policy, bounded execution model, cache-ahead handling, and recovery rules.
This integration design consumes the resulting projection-health evidence for deployment
readiness and operations; it does not define another reconciliation contract.

## Cache-Backed Reads and Domain Lifecycle

The [projector/source ADR](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-backed-reads-and-domain-lifecycle)
owns cache-read eligibility, relational fallback, direct fill, and the distinction between
cache maintenance and canonical document deletion. Deployment procedures in this document
treat that lifecycle contract as an input.

## Projection Health and Deployment-Owned CDC Readiness

Projection health is evaluated for each explicit `(tenant key, DataStoreId)` execution
context. It reports at least:

- target resolution and required-table existence,
- provider prerequisites, including the current RCSI validation result for a SQL Server
  projection target,
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
recent exact-zero finishing audit, satisfied provider prerequisites, a clear durable
cache-ahead latch, no active unresolved incremental candidate, and no target-scoped
repair-required observation.

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
- a Kafka Connect worker whose shared offset store satisfies the cluster-scoped durability,
  cleanup-policy, and authorization prerequisites below,
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
- deployment-owned source-history continuity status that is currently proven `healthy` and
  has no terminal loss latch for the binding generation,
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

Both adapters use the opt-in singleton `dms.CdcHeartbeat` table defined by
[`data-model.md`](backend-redesign/design-docs/data-model.md#8-dmscdcheartbeat-opt-in-cdc-integration-object).
It contains no document or tenant data. Connector setup includes this table in the
PostgreSQL publication or SQL Server CDC capture and configures a positive
`heartbeat.interval.ms`. Its fixed provider `heartbeat.action.query` atomically
increments `HeartbeatSequence` and updates `HeartbeatAt`. The default interval is 5,000
ms. Deployments may lower it or raise it within their readiness timeout, but template and
live-configuration validation reject zero, a negative value, a missing/conflicting action
query, or, for SQL Server, `poll.interval.ms > heartbeat.interval.ms`.

`dms.CdcHeartbeat` is an internal progress source. Its snapshot, create, update, and delete
records and Debezium heartbeat records are routed by the `DocumentState` transform to the
binding-scoped CDC progress topic before public-record validation; they are never routed to
the instance document topic. Before routing, the transform replaces every source key,
whether structured, scalar, or null, with the fixed non-null
[internal progress key](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#internal-progress-key)
required by the shared `StringConverter` and compacted progress topic. The progress topic
is a transport acknowledgement boundary, not a status store: deployment automation does
not consume it and continues to read the connector's committed source offsets through the
REST API.

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

### Source-History Continuity

The provider source-position barrier proves catch-up only while the source history needed
to resume from the committed connector offset still exists. Connector `RUNNING` state,
current or quantile lag, a recreated provider artifact with the expected name, and a new
snapshot do not prove that no source changes were skipped. Deployment automation therefore
checks source-history continuity before every connector start or resume after initial
enablement and on every combined-status polling interval.

The PostgreSQL check requires the binding-derived logical replication slot and publication
to exist with their exact expected database, plug-in, and captured-table configuration. It
compares the committed Debezium offset with the retained range of that same slot and rejects
a missing, recreated, invalidated, or lost slot, a retained-WAL gap, or any state from which
exact resume at the committed position cannot be proved. The monitor reads the provider's
replication-slot status rather than assuming that automatic slot creation preserves
history. Debezium documents that a newly created slot cannot supply the historical position
of the prior slot and can therefore skip changes without reconstructing them.

The SQL Server check requires every binding-derived capture instance and its capture and
cleanup jobs to exist with the expected captured columns. For each capture instance it
compares the committed Debezium position with the provider's retained minimum and maximum
LSN range and rejects an expired position, missing or re-created capture instance, or any
other unprovable gap. A stopped or failed capture/cleanup job is independently not ready but
does not latch history loss while the exact retained range still covers the committed
position. The monitor also reports the configured cleanup retention and remaining position
margin; SQL Server's default cleanup retention is only 4,320 minutes (72 hours). SQL Server
schema-history and Connect offset integrity remain additional required checks rather than
substitutes for retained CDC rows. See
[`sys.sp_cdc_add_job`](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sys-sp-cdc-add-job-transact-sql?view=sql-server-ver17)
and the Debezium
[PostgreSQL connector history-loss guidance](https://debezium.io/documentation/reference/3.6/connectors/postgresql.html).

The deployment-owned status has three continuity outcomes:

- `healthy`: the exact resume position is currently proved for every required provider
  source artifact;
- `unknown`: a provider or Connect query is temporarily unavailable, times out, or returns
  no authoritative result without disproving continuity. Combined readiness is false,
  automation does not start or resume the connector, and an already failed/stopped connector
  is not automatically restarted. A later check may return to `healthy` only with complete
  affirmative evidence; and
- `lost`: a required artifact was removed or re-created, the committed position fell
  outside retained history, or a successful Connect query proves the established binding's
  expected offset missing, malformed, or source-mismatched. Deployment state durably
  latches `SourceHistoryContinuityLost` for that binding generation, stops the old connector,
  and keeps combined readiness false. The latch cannot be cleared by later artifact
  recreation, offset mutation, a healthy-looking lag value, or a snapshot.

This latch affects CDC publication readiness only and does not change DMS request routing.
A failure for one binding does not stop unrelated bindings.

Source-history loss is unrecoverable in v1. Resetting or removing Connect offsets,
re-creating a PostgreSQL slot or SQL Server capture instance, or snapshotting current rows
into the existing public topic is prohibited: a current-state snapshot cannot emit
tombstones for documents deleted before the snapshot and can therefore preserve stale
state in that topic's consumers. Safe recovery would require a new binding generation,
public and progress topics, SQL Server schema-history topic when applicable, fresh consumer
state namespace, and snapshot. That baseline-replacing cutover is deferred from v1. The old
binding remains terminal; provisioning or migrating to a replacement database and namespace
requires a separately designed workflow.

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

Every provisioned database contains one UUID, stable during ordinary operation, in the
singleton `dms.DataStoreIdentity` row. DMS reads that UUID through the active target
connection and computes the physical-source fingerprint as follows:

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
  "contractVersion": 1
}
```

The record contains no connection string, credential, or source UUID. Credential, timeout,
pooling, application-name, host-alias, or equivalent connection changes produce the same
fingerprint when they reach the same `dms.DataStoreIdentity` row. `partitionCount` is a
positive topic-creation value and is immutable within the binding generation so keyed
upserts and versionless tombstones remain in one ordered partition and one compaction
domain.
`partitionerAlgorithm` is the immutable named behavior token
`kafka-murmur2-v1`; it is not a Java class or library version. For non-null serialized key
bytes `K` and partition count `N`, that token means
`(KafkaMurmur2(K) & 0x7fffffff) % N`, byte-for-byte matching Kafka's Java-client Murmur2
key partitioning. V1 rejects a missing or different token. Template generation maps the
token to a compatible implementation in the pinned connector image, and validation uses
fixed serialized-key/partition fixtures so an image or implementation change cannot
silently change the mapping. A different algorithm requires a new token and binding
generation; an implementation change that preserves every token-defined result does not.

`SourceHistoryContinuityLost` is deliberately not an immutable binding field and is not
stored by DMS. It is mutable deployment-owned incident state keyed by the complete binding
identity and generation in the same durable backend. The state supports only idempotent
false-to-true latching during the binding's lifetime; no ordinary validation, setup retry,
artifact recreation, or status poll clears it. Explicit binding retirement removes it only
after the connector and every governed artifact are retired in the required cleanup order.

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
topic has one partition and `cleanup.policy=compact`. Every progress record uses the fixed
non-null [internal progress key](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#internal-progress-key),
so compaction can retain the latest published progress record without accepting a null or
provider-specific structured key. It contains no public document state, and its retained
contents are not part of the public bootstrap contract.

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

After bootstrap, the consumer renews durable continuity evidence at least once every 24
hours by capturing a new end-offset barrier for every partition and durably applying through
it. An idle partition may renew evidence with an unchanged end offset. Failure to renew,
loss or corruption of the checkpoint, an unexpected partition assignment, or any other
uncertainty immediately invalidates the consumer's entire local state. It stops advertising
that state, discards it, and repeats the complete earliest-offset bootstrap within the same
24-hour deadline; it never resumes incrementally from the uncertain checkpoint. This rule
prevents an incremental consumer that was absent beyond tombstone retention from retaining
a document whose tombstone Kafka has already compacted away.

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
eng/docker-compose/.cdc-state/incidents/
  {filesystemSafeDeploymentKey}/{instanceKey}/{generation}.json
```

`instanceKey` is the deployment-controlled Kafka-safe opaque identifier from the topic
contract; it does not contain a tenant or other display name. Path components are
filesystem-safe encodings rather than unsanitized administrative values. `.cdc-state`
must be ignored by Git and is separate from
`.bootstrap/bootstrap-manifest.json`; the bootstrap manifest remains prepared-input
handoff, not mutable CDC control-plane state. The incident file is absent until terminal
source-history loss is latched and contains only binding identity, latch time, provider-safe
failure category, and sanitized position metadata. JSON files use owner-only permissions
where the platform supports them. The local implementation is supported only when one
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
   source and every retained artifact. E19's binding-state operation owns guarded atomic
   record creation, and bootstrap owns the live provider, connector, topic, offset, ACL, and
   configuration verification. Adoption repairs missing deployment state around an already
   complete governed-artifact set; it is not a first-time enablement path. A failed or
   incomplete adoption changes nothing.
4. On retirement, either retain the binding record with every retained governed
   artifact, or delete the connector, every governed topic, offset, ACL, PostgreSQL
   slot/publication, SQL Server capture artifact, and other governed artifact before
   deleting the terminal incident state and binding record. A normal process restart or
   stack stop deletes neither.

The binding record lives at least as long as any governed artifact. Local teardown may
remove it only in the same destructive volume-removal workflow that removes all of those
artifacts. A crash may leave an unused record, which safely supports an idempotent retry;
it must never leave surviving artifacts reusable after automatic record deletion.

V1 never reassigns an existing topic or connector generation to a different physical
database. Guarded source replacement is supported only for a database previously enabled
through the v1 new-database path. It fences the old connector, rotates `SourceIdentity`
through the binding-state operation, and creates a new binding generation, connector,
public topic, progress topic, SQL Server schema-history topic when applicable, consumer
state namespace, and snapshot. The old generation is retained or explicitly retired; none
of its governed artifacts is reused. The new generation reports eventual operational status
rather than another exact baseline. It cannot clear a published cache-ahead latch or recover
a binding whose source-history loss is terminal. Removing a target requires explicit
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

### Kafka Connect Offset Store

The Kafka Connect worker's configured `offset.storage.topic` is shared cluster-scoped
source-position state for every binding registered with that worker. It is not derived from
a binding, is not a binding field, and is not created, replaced, or deleted as part of one
binding's lifecycle.

Production deployments pre-create this topic before starting the worker with
`cleanup.policy=compact`, a replication factor of at least three, and an explicit
topic-level `min.insync.replicas` of at least two. Local development and CI may use
replication factor one and `min.insync.replicas=1`. Deployment automation resolves the
configured topic name and validates its actual cleanup policy, replica count, and
topic-level override before accepting a worker, before connector registration or
start/resume, and during live status checks. It never relies on Connect topic auto-creation
or broker defaults.

On this topic, an authorization-enabled deployment grants the Kafka Connect worker service
principal only literal `READ`, `WRITE`, and `DESCRIBE` access. The deployment control plane
retains topic/configuration and ACL administration, and instance consumer principals receive
no access. Provisioning and live validation verify the effective deployment-managed grants.

An unavailable or nonconforming shared offset store makes every binding assigned to that
worker not ready. Inability to obtain authoritative evidence is nonterminal and fail-closed;
a successful query proving an established binding's expected offset absent remains the
per-binding terminal source-history loss defined above. Replication and authorization
reduce the risk of offset loss but are not a recovery mechanism after loss.

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
connector not ready; automation never recreates it silently around retained offsets. After
initial enablement, missing or inconsistent history or offsets participates in the
source-history continuity check and may terminally latch the binding. V1 provides no
destructive same-binding recovery or controlled resnapshot for that condition. Binding
retirement removes the history topic and its ACLs with the connector and offsets before
deleting binding state.

Provider CDC/key setup is opt-in and does not run during ordinary relational provisioning
when CDC is not selected.

## Connector Transform Integration

Connector templates invoke the required Ed-Fi `DocumentState` SMT defined by the
[topic/message ADR](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation).
That ADR owns source and operation classification, transform configuration, public
key/value/tombstone shaping, progress routing, malformed-record behavior, and compatibility.
Templates and live registration use those fixed values with the pinned connector runtime;
verification asserts final published bytes, routing, and failure behavior rather than
treating generated connector JSON alone as evidence.

## Contract Change and Repair Operations

The [topic/message ADR](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes)
owns the compatibility boundary, ordering rules, and conditions that require a new topic
contract or binding generation. The procedures below implement those decisions for
offline representation correction, sensitive-data containment, record-capacity changes,
and a deferred new-topic cutover. They do not establish another message contract or an
exact post-admission CDC baseline. The deferred cutover still requires the writer fence
excluded by [V1 readiness scope](#v1-readiness-scope).

### Offline byte-changing representation correction

For every correction that changes API or stream representation bytes, operators use the
out-of-band representation-restamp utility only while the selected data store is explicitly
offline and every DMS replica, cache writer, and external writer has been stopped outside
the utility. This is a rare deployment repair, not a request-path feature, not ad hoc SQL,
and not an automated admission gate. It advances the existing canonical representation
stamps rather than adding a projection epoch or another Kafka ordering field. V1 does not
use the result to certify a replacement exact CDC baseline; projection and connector status
after write admission remain eventual.

The offline operation follows this sequence:

1. Stop every affected DMS replica, API reader, canonical writer, projector loop, optional
   direct-fill writer, bulk/seed loader, administrative path, and external writer outside
   the utility. Mark the CDC target not ready when present. The utility requires an explicit
   offline confirmation but does not implement or certify this fence.
2. Deploy the corrected API materializer/composer while the data store remains offline.
   The existing connector may remain registered against the same binding and topic when the
   stream contract remains compatible and no previously published bytes require purging.
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
status for audit and safe resume. For corrections that do not require prior-record purging,
the higher versions deliberately make affected documents visible as representation updates
to Change Queries and cause conforming Kafka consumers to replace prior state without a new
topic, binding generation, or offset reset. The dedicated implementation story owns the
utility, provider behavior, tests, and operator examples; general cache and CDC runbooks
describe only its offline scope and do not recreate it with manual SQL.

### Sensitive-data disclosure correction

When corrected representation bytes remove or mask sensitive data that should never have
been published and the old Kafka values must be purged, the same-topic restamp path above is
prohibited. A higher-version upsert, tombstone, or compaction establishes eventual current
state but is not evidence that Kafka has destroyed superseded bytes.

The v1 response is deliberately destructive and favors containment over continued CDC
availability:

1. Mark the target not ready, stop and verify every task of the affected connector is
   fenced, and revoke consumer access to the public topic before restamping or rebuilding
   can publish corrected state.
2. Correct the materializer and use the restamp utility as needed while the data store
   remains offline.
3. Explicitly retire the affected binding generation using the governed cleanup order.
   Delete the public topic and remove its connector, offsets, ACLs, progress topic, and SQL
   Server schema-history artifacts when applicable before removing binding state.
4. Record the operation/restamp identifier, binding generation, topic name, containment
   time, deletion request, and broker or managed-platform purge confirmation. Completion
   requires platform evidence that the public topic and any platform-governed remote or
   tiered copies covered by its deletion guarantee no longer retain the topic. A successful
   delete request, configuration removal, corrective record, tombstone, compaction request,
   or temporary metadata lookup failure alone is not purge evidence. If the deployment
   cannot obtain its platform's required evidence, the incident remains open.
5. Do not recreate or restart the old binding or topic. CDC remains unavailable for the
   target. Re-enablement requires the deferred new-generation topic, consumer namespace,
   fresh snapshot, and publication-barrier workflow.

Independently operated consumer stores and exports are outside the broker cleanup boundary
and remain part of the deployment's disclosure-response scope; DMS documentation must not
claim that deleting the Kafka topic purges those downstream copies.

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
and consumer state namespace. Compaction and versionless tombstone ordering do not cross
partitions, so an existing key cannot safely move between old and new mappings in one topic.
The new public topic may retain `documents.v1` and `contractVersion: 1` when the public
key/value/delete contract is otherwise unchanged.

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
- After initial enablement, bootstrap/status automation checks provider source-history
  continuity before every connector start/resume and on each status interval. It leaves an
  `unknown` connector stopped until affirmative evidence returns and durably terminates a
  binding whose continuity is `lost`; it never resets offsets or resnapshots the existing
  public topic.
- Before bootstrap starts local Kafka Connect, it pre-creates and validates the configured
  shared offset topic and its worker-only ACLs using the cluster-scoped contract above. For
  an already-running or externally managed worker, it requires equivalent authoritative
  validation before registering, starting, or resuming a connector. The shared topic is not
  a binding-governed artifact and is never removed by per-binding teardown.
- Binding-topic provisioning applies the explicit durability profile above to the public and
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
- A normal local stop retains the binding, connector, Kafka offsets, ACLs, provider capture
  artifacts, and every governed topic. Destructive local volume teardown removes the
  connector; its offsets; public, progress, and SQL Server schema-history topics and ACLs;
  the PostgreSQL slot/publication or SQL Server capture instances/jobs; and any other
  governed artifact before deleting terminal incident state and the binding record last.

Production-like automation may repeat this workflow only while initially provisioning each
new deployment-selected CDC database and while it can prove that the database has not been
published to any writer. It does not add CDC to an already-provisioned database and does not
implement a later baseline-replacing maintenance window.

## Schema and Query Integration

Schema integration is create-only and applies to new physical databases. V1 emits
no `ALTER`/migration path for an older `dms.DocumentCache`. Provisioning reruns may validate
and preserve an already-current schema created by the same initial workflow, but encountering
legacy `Etag`, the obsolete cache UUID constraint, or any missing required E18 object makes
the database ineligible rather than triggering an in-place repair.

Supported SQL Server create-database provisioning enables `READ_COMMITTED_SNAPSHOT`; an
externally created database must satisfy the same prerequisite before it is selected for
projection. Provisioning or deployment automation may enable the option while the data
store is offline. Runtime DMS only validates it and never attempts `ALTER DATABASE`.

The physical projection, singleton-state, source-identity, heartbeat, trigger, constraint,
and access-path inventory is owned by
[`data-model.md`](backend-redesign/design-docs/data-model.md). Deployment validation checks
that inventory rather than redefining it here. Provider CDC setup provisions and captures
the opt-in heartbeat only when CDC is selected; ordinary relational provisioning does not.
The generated provider action query and capture configuration implement the
[source-position barrier](#provider-source-position-barrier).

The [projector/source ADR](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation)
owns the v1 decision to use the current database difference instead of durable projection
queues, cursors, retry records, or a backfill workflow.

Provider queries and plans must implement the ADR-owned logical reconciliation,
monotonic-upsert, and delete-fence behavior; provider-specific SQL does not create a
second contract.

## Security, Telemetry, and Operations

Topic-per-instance ACLs are the Kafka authorization boundary. Shared topics requiring
consumer-side instance filtering are not supported. The public stream contains sensitive
data. The binding-scoped CDC progress topic may contain raw connector source metadata and
is available only to the connector principal and deployment control plane; instance
consumer principals receive no access. The shared Kafka Connect offset topic follows the
worker-only ACL and validation contract above. Other Kafka Connect worker internal topics,
its REST API, and database credentials are likewise not exposed to third-party consumers.
Local insecure defaults must be replaced in production.

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
  opaque projection-source fingerprint,
- SQL Server projection-target RCSI validation failures and recovery.

Deployment-owned CDC status additionally covers binding presence and match, connector
running state, current lag plus Debezium 3.6 P50/P95/P99 source-lag telemetry, last error,
snapshot completion, heartbeat/capture progress, the provider barrier and committed
Connect source offset in sanitized form, existing artifacts without binding state, source
mismatch, shared Connect offset-store durability and ACL health, source-history continuity
outcome and remaining provider-retention margin, the durable terminal loss latch, and
guarded source-replacement state.

Use provider, safe project/resource identity, failure category, target-resolution state,
and opaque data-store identity only where cardinality policy permits. Never log
document bodies, raw student data, connection strings, credentials, tenant display
names, or unsanitized physical identifiers.

The deployment status operation identifies binding generation, connector,
provider/source, public and progress topics, the SQL Server schema-history topic when
applicable, PostgreSQL slot or SQL Server capture instance, DMS projection health, snapshot
state, lag, source-history continuity outcome and last successful proof time, terminal loss
latch, and last error. A failure for one target does not conceal peer status or stop
unrelated DMS API instances.

Runbooks cover connector restart, cache rebuild, same-topic compatible projection repair,
cache-ahead invariant diagnosis, supported internal-only recovery, and containment of
possibly published state pending the deferred downstream-state reset,
ordinary monotonic projection lag, source-history continuity monitoring, progress-topic
diagnosis, shared Connect offset-store durability and ACL diagnosis, SQL Server
schema-history diagnosis, target migration/retirement, and provider artifact cleanup. They
also cover sensitive-data disclosure containment and destructive
binding-generation retirement, require recorded platform purge evidence, and leave CDC
unavailable rather than republishing into or recreating the affected topic.
They distinguish the projection-scoped SQL Server RCSI prerequisite from ordinary
relational-only DMS support, show how to inspect and enable it during an offline maintenance
step, state that DMS never changes it at runtime, and include row-version-store capacity and
health monitoring.
They document the seven-day public-topic tombstone-retention minimum and the 24-hour
consumer-bootstrap deadline, how a consumer captures and completes an end-offset barrier,
how to capacity-test the largest supported retained log rather than only its live-key count,
how to observe cleaner health and earliest-to-end scan volume, and why an over-deadline
reconstruction or stale incremental-continuity proof must be discarded rather than
advertised as valid.
They document the shipped projector defaults, how to tune intervals, page size, target
concurrency, and maximum audit age, and how to identify API-resource contention or an
audit that cannot complete within its readiness window.
They cover binding-state backup, fail-closed missing-state recovery, guarded explicit
adoption, cleanup ordering, and guarded new-generation source replacement; they never infer
or repair a binding by rewriting an immutable record. They state that same-topic baseline
replacement and incompatible-contract cutover are deferred until an owned cross-replica/
external-writer fence exists. The representation-restamp utility is documented only for an
explicitly offline data store and does not certify another exact CDC baseline. They state
that source-history `unknown` is fail-closed, source-history `lost` durably terminates the v1
binding, and offset reset, provider-artifact recreation, or resnapshot into the existing
topic is not a recovery path. A replacement generation/topic/consumer namespace is a
deferred future workflow, not a v1 runbook.
Connector, topic, offset, ACL, slot, and capture deletion is destructive and always
explicit; removal from configuration is not cleanup authority.

## Contract-to-Evidence Traceability

The design documents linked below own the normative invariants. The listed stories own the
executable scenarios, test identifiers, and pass evidence. A contract ID is stable shorthand
for its linked design section; it does not restate or replace that section. Test layers show
where evidence belongs without prescribing duplicate test cases here.

| Contract ID | Design owner | Evidence-owning stories | Test layers |
| --- | --- | --- | --- |
| `CDC-INV-01` | [Configuration and target selection](#configuration-and-projection-target-selection) and [projection readiness](#projection-health-and-deployment-owned-cdc-readiness) | [E18-S01](backend-redesign/epics/18-document-cache/01-documentcache-configuration-and-target-selection.md), [E18-S06](backend-redesign/epics/18-document-cache/06-documentcache-health-readiness-and-telemetry.md) | Configuration/unit; PostgreSQL and SQL Server provider integration; mixed-target isolation |
| `CDC-INV-02` | [Cached document contract](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract) and [upsert value](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#upsert-value) | [E18-S00](backend-redesign/epics/18-document-cache/00-documentcache-schema-and-provider-ddl.md), [E18-S02](backend-redesign/epics/18-document-cache/02-document-materializer-service.md), [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md) | DDL snapshot/DB-apply; materializer unit/provider integration; serialized-record contract |
| `CDC-INV-03` | [Freshness and reconciliation](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation) | [E18-S03](backend-redesign/epics/18-document-cache/03-monotonic-cache-upsert-and-delete-fencing.md), [E18-S04](backend-redesign/epics/18-document-cache/04-async-projector-reconciliation-loop.md), [E18-S07](backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md), [E19-S06](backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md) | Provider concurrency/integration; performance qualification; cross-feature integration; API-driven Kafka E2E |
| `CDC-INV-04` | [Bounded in-process execution policy](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#bounded-in-process-execution-policy) and [projection readiness](#projection-health-and-deployment-owned-cdc-readiness) | [E18-S04](backend-redesign/epics/18-document-cache/04-async-projector-reconciliation-loop.md), [E18-S06](backend-redesign/epics/18-document-cache/06-documentcache-health-readiness-and-telemetry.md), [E18-S07](backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md) | Scheduling/unit; bounded-load and memory; multi-target/provider integration; health and restart integration |
| `CDC-INV-05` | [Cache-backed reads and domain lifecycle](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-backed-reads-and-domain-lifecycle) | [E18-S05](backend-redesign/epics/18-document-cache/05-cache-backed-read-path.md), [E18-S07](backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md) | API/provider integration; authorization; fallback and concurrency integration |
| `CDC-INV-06` | [Connector topology and provider setup](#connector-topology-and-provider-setup) and [schema integration](#schema-and-query-integration) | [E19-S01](backend-redesign/epics/19-cdc-kafka/01-cdc-ddl-support.md), [E19-S02](backend-redesign/epics/19-cdc-kafka/02-connector-template-generation.md), [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md), [E19-S06](backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md) | DDL/DB-apply; provider integration; pinned-image connector integration; API-driven Kafka E2E |
| `CDC-INV-07` | [Topic](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic), [record size](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size), [public and internal keys](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#key), [upsert](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#upsert-value), and [delete](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#delete) | [E19-S03](backend-redesign/epics/19-cdc-kafka/03-document-state-transform.md), [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md), [E19-S06](backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md) | Transform unit; serialized-record contract; broker-backed integration; consumer conformance; API-driven Kafka E2E |
| `CDC-INV-08` | [Connector transformation](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation) | [E19-S02](backend-redesign/epics/19-cdc-kafka/02-connector-template-generation.md), [E19-S03](backend-redesign/epics/19-cdc-kafka/03-document-state-transform.md), [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md) | Rendering/unit; provider-record fixtures; plugin-loading/pinned-image; serialized-record contract |
| `CDC-INV-09` | [Pinned connector runtime](#pinned-connector-runtime) and [connector topology](#connector-topology-and-provider-setup) | [E19-S02](backend-redesign/epics/19-cdc-kafka/02-connector-template-generation.md), [E19-S04](backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md), [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md) | Template validation; pinned-image integration; Connect REST integration; broker-backed fault injection |
| `CDC-INV-10` | [Provider source-position barrier](#provider-source-position-barrier), [internal progress key](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#internal-progress-key), and [enablement sequence](#enablement-and-initial-readiness-sequence) | [E19-S00](backend-redesign/epics/19-cdc-kafka/00-documentcache-cdc-prerequisites.md), [E19-S04](backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md), [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md) | Position-adapter unit; provider integration; controller integration; broker-backed heartbeat/readiness |
| `CDC-INV-11` | [Source-history continuity](#source-history-continuity) | [E19-S00](backend-redesign/epics/19-cdc-kafka/00-documentcache-cdc-prerequisites.md), [E19-S02](backend-redesign/epics/19-cdc-kafka/02-connector-template-generation.md), [E19-S04](backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md), [E19-S06](backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md) | Adapter/unit; real-provider integration; pinned-image lifecycle; API-driven failure E2E |
| `CDC-INV-12` | [Deployment-owned binding](#deployment-owned-cdc-target-and-physical-source-binding), [local bootstrap](#local-bootstrap-and-ci), and [security](#security-telemetry-and-operations) | [E19-S00](backend-redesign/epics/19-cdc-kafka/00-documentcache-cdc-prerequisites.md), [E19-S04](backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md) | State-store/CAS unit; controller/script integration; broker-backed topic and ACL integration; destructive-lifecycle integration |
| `CDC-INV-13` | [Public consumer bootstrap](#public-consumer-bootstrap) and [topic retention](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic) | [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md) | Consumer conformance; controllable-clock/partition-barrier; broker-backed bootstrap |
| `CDC-INV-14` | [Contract change and repair](#contract-change-and-repair-operations), [v1 compatibility](backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes), and [cache-ahead recovery](backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-ahead-invariant-recovery) | [E18-S08](backend-redesign/epics/18-document-cache/08-representation-restamp-utility.md), [E19-S00](backend-redesign/epics/19-cdc-kafka/00-documentcache-cdc-prerequisites.md), [E19-S04](backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md), [E19-S05](backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md) | Administrative-command/provider integration; lifecycle integration; contract/consumer conformance |
| `CDC-INV-15` | [Security, telemetry, and operations](#security-telemetry-and-operations) | [E18-S06](backend-redesign/epics/18-document-cache/06-documentcache-health-readiness-and-telemetry.md), [E18-S07](backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md), [E19-S00](backend-redesign/epics/19-cdc-kafka/00-documentcache-cdc-prerequisites.md), [E19-S07](backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md) | Status/telemetry unit and integration; diagnostics sanitization; exercised runbook/documentation checks |

## Historical and Deferred Material

Legacy document-store connector configurations referenced JSON columns such as
`EdfiDoc`, security elements, authorization EdOrg arrays, and hierarchy JSON that do not
exist in the relational schema. OpenSearch/Elasticsearch read-store support has been
dropped.

A relational outbox remains a future option if DMS needs explicit domain events rather
than a compacted document-state stream. Debezium Server and provider-specific managed
Kafka deployment guides are also outside v1.
