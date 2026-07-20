---
status: proposed
date: 2026-07-20
jira: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1240
  - DMS-1089
---

# Decision Record: Debezium Connector Deployment for Relational CDC

## Decision

The relational CDC reference architecture uses Kafka Connect with one Debezium source
connector per DMS instance to capture `dms.DocumentCache` and `dms.Document` and publish
the document-state topic defined in
[0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md).

The tables have distinct roles in the connector pipeline: cache create/update/snapshot
events become document upserts, `dms.Document` deletes become tombstones, and cache
deletes plus all other document operations are dropped before final topic routing.

Kafka Connect is the v1 deployment model. Debezium Server and embedded Debezium are not
the reference path for DMS v1 relational CDC.

The connector deployment is instance-oriented:

- PostgreSQL uses one source connector per DMS instance database.
- SQL Server may technically capture multiple databases from one connector, but the
  reference implementation still treats each DMS instance as one logical connector
  registration and one document topic.
- Advanced SQL Server deployments may consolidate connector registrations only if they
  preserve the same per-instance topic, key, tombstone, ACL, and operational contracts.

The logical connector identity is `(deployment key, tenant key, DataStoreId)`. The tenant
key is retained only in deployment configuration and administrative state; it is not
published in the Kafka topic or message. The topic is derived from the deployment-unique
topic prefix and opaque instance key defined in
[0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md).

## Preconditions

CDC can be enabled only after these conditions are true for the target DMS instance:

- the relational schema is provisioned and validated,
- `dms.DocumentCache` is provisioned,
- the instance is present in `DataManagement:KafkaCdc:Targets`, which selects its
  asynchronous `dms.DocumentCache` reconciliation, and the projector has the DMS-1246
  guarantees: current-state reconciliation, stale-write fencing, mismatch-derived
  health, and bounded in-memory retry (see
  [../document-cache/](../document-cache/)),
- `dms.Document` is configured as the authoritative delete source and both captured
  tables support a `DocumentUuid` connector key,
- Kafka and Kafka Connect are reachable,
- the connector principal has the least database permissions required for CDC,
- the target Kafka topic and ACLs are configured or can be created by the deployment,
- no other CDC-enabled data-store record in the deployment resolves to the same physical
  database.

`-EnableKafkaUI` or equivalent local UI flags do not enable CDC. Bootstrap should add a
separate explicit CDC opt-in, such as `-EnableKafkaCdc`, before registering source
connectors.

## One Stream per Physical Document Set

The v1 contract requires a one-to-one mapping between a logical instance topic and the
physical database containing its `dms.Document` and `dms.DocumentCache`. CMS may allow
multiple data-store records to reference the same database, but CDC must not register a
separate connector and
topic for each alias. The captured tables have no tenant or data-store discriminator, so
those topics would expose duplicate copies of the same physical document set under
potentially different ACLs.

The CDC prerequisite implementation must resolve a provider-specific physical database
identity for each entry in `DataManagement:KafkaCdc:Targets` and reject the target set
when two listed `(tenant key, DataStoreId)` entries resolve to the same identity. Comparison must not rely
only on raw connection-string text; semantically equivalent connection strings and server
aliases must be normalized or confirmed after connecting. The diagnostic identifies the
conflicting opaque data-store IDs without logging credentials or tenant display names.

Rejecting the conflict is safer than choosing an arbitrary canonical alias: it avoids
silently changing a topic's security boundary or making topic stability depend on which
alias happens to load first. Ordinary non-CDC DMS operation is unaffected by this CDC
validation failure.

## Explicit Deployment Target List

The v1 CDC target set is an explicit deployment configuration, represented by
`DataManagement:KafkaCdc:Targets` entries containing `(tenant key, DataStoreId)`.
Deployment/bootstrap automation performs the one-shot provisioning and
connector-registration workflow explicitly for each listed target. It does not treat the
complete CMS inventory as CDC-enabled, continuously discover CMS additions, or run a
background Kafka Connect reconciler. The target list is the runtime CDC enablement
contract: an empty list disables CDC, and each entry also selects DocumentCache projection
for only that data store.

The target-list contract is:

- every CDC-enabled data store appears in the target list, is resolved explicitly during
  deployment, is validated against the physical-database uniqueness rule above, and is
  given one connector and one instance topic,
- DMS captures an immutable source binding for each listed target using the provider and
  provider-resolved physical database identity; tenant keys use the same case-insensitive
  normalization as `IDataStoreProvider`,
- DMS does not fingerprint the complete connection configuration. Credential, timeout,
  pooling, application-name, and equivalent-alias changes are not source drift when they
  resolve to the same provider and physical database,
- CDC readiness reads the corresponding isolated projector execution context for every
  deployment-configured target, but the projector does not call the Kafka Connect REST API,
- after a successful CMS refresh/reload, DMS reevaluates physical source identity for only
  the listed targets. A missing target or confirmed provider/physical-database mismatch is
  not reconciled and makes that target's CDC readiness false,
- CMS entries outside the target list remain ordinary DMS routing data and do not become CDC
  targets or CDC drift,
- adding or removing a CDC-enabled data store requires an explicit configuration change and
  coordinated deployment that runs the provisioning workflow again,
- removing a target requires an explicit operator decision for connector shutdown, topic
  retention/deletion, offset deletion, PostgreSQL slot/publication cleanup, SQL Server CDC
  cleanup, and ACL retirement; absence from a later configuration is not authority for
  destructive cleanup,
- credential, host-name, server-alias, or other connection-setting changes may preserve the
  topic and source binding when provider-resolved physical-database identity is unchanged;
  connector credential updates remain an explicit deployment concern,
- changing a `DataStoreId` to a different provider or physical document set is not an
  automatic replacement operation. CDC remains unsupported/not ready until an explicit
  migration chooses a new topic/source generation or deliberately resets the existing
  topic and connector state before resnapshotting,
- route-qualifier-only changes do not affect the connector or topic because request routing
  is outside the CDC source identity.

Source-binding comparison observes drift without becoming a reconciler. It marks the
affected target not ready, but it does not alter normal request routing, call Kafka Connect,
change a projector context, stop a connector, or delete topics, offsets, ACLs, or database
CDC artifacts. A missing configured target is reported as not ready and handled by the
explicit operator/deployment procedure above. Unrelated targets remain independent.
Confirmed physical-source drift is latched until a coordinated deployment reruns the
one-shot workflow, even if CMS later returns to the original source binding. A transient failure to
resolve physical identity is retryable and is not latched as drift.

CDC readiness is observational. Connector/projector failures and source drift do not
affect DMS routing, relational write correctness, or API mutation availability.

The one-shot workflow must remain idempotent for repeated deployment of the same logical
connector and physical source. It must reject an attempt to reuse an existing instance
topic for a different physical document set without an explicit migration/reset decision.

## Captured Tables and Operation Filter

The source connector captures exactly `dms.DocumentCache` and `dms.Document`:

| Table | Accepted operations | Output |
| --- | --- | --- |
| `dms.DocumentCache` | create (`c`), update (`u`), snapshot/read (`r`) | Shaped document upsert |
| `dms.Document` | delete (`d`) | Kafka tombstone |

The pipeline drops `dms.DocumentCache` deletes/truncates and every other `dms.Document`
operation, including its initial snapshot. It must classify by source table and operation
before routing both physical table topics to the public instance topic. Cache maintenance
must therefore never appear as domain deletion.

The connector must not capture normalized resource tables, Change Query
`tracked_changes_*` tables, authorization tables, or EdOrg hierarchy tables.

Implementation stories must verify the exact case-sensitive table identifier emitted by
the generated PostgreSQL and SQL Server DDL. PostgreSQL generated DDL may expose the table
as a quoted identifier such as `dms."DocumentCache"`, while connector include-list syntax
is Debezium-version-specific.

## PostgreSQL Deployment

Recommended PostgreSQL shape:

- use the native Debezium PostgreSQL connector with `pgoutput`,
- configure PostgreSQL for logical replication,
- create a least-privilege replication user, not a superuser,
- create one publication per connector or a narrowly scoped publication that includes
  only `dms.DocumentCache` and `dms.Document`,
- create one replication slot per DMS instance connector,
- configure the connector's table include list to both captured tables,
- configure `DocumentUuid` as the connector message key for both tables,
- set `dms.Document` to `REPLICA IDENTITY FULL`,
- configure the source-aware operation filter and authoritative-delete tombstone conversion.

PostgreSQL logical replication slots are database-scoped. Therefore, when the DMS
instance isolation model uses one database per instance, the reference PostgreSQL
deployment also uses one source connector per instance database.

### PostgreSQL Delete Key Requirement

The connector must publish a `dms.Document` delete tombstone keyed by `DocumentUuid`.
`dms.Document` is physically keyed by `DocumentId`, so connector setup must not rely on
the default primary key.

Recommended implementation path:

1. Use Debezium `message.key.columns` to make `DocumentUuid` the key for both captured
   tables.
2. Set `dms.Document` to `REPLICA IDENTITY FULL`. Debezium's PostgreSQL connector
   requires full replica identity when a delete key uses a non-primary-key column; this
   makes `DocumentUuid` available in the delete record.
3. Do not give `dms.DocumentCache` delete semantics in the transform pipeline. Its
   deletes are dropped regardless of whether they are explicit, cascaded, or part of a
   rebuild.
4. Add connector smoke tests that delete a canonical document and assert the routed
   tombstone key is the public `DocumentUuid`.

The implementation story must verify the exact Debezium property names and PostgreSQL SQL
syntax against the pinned Kafka Connect/Debezium image.

## SQL Server Deployment

Recommended SQL Server shape:

- use the native Debezium SQL Server connector,
- enable SQL Server CDC for the DMS instance database,
- enable CDC on `dms.DocumentCache` and `dms.Document` only,
- use a least-privilege connector login with CDC read access,
- configure `DocumentUuid` as the connector message key for both tables,
- configure the source-aware operation filter and authoritative-delete tombstone conversion.

Debezium SQL Server can process multiple databases on the same server. The reference DMS
deployment still registers connectors per DMS instance because it keeps offsets, topic
routing, failure isolation, and operational runbooks aligned with the PostgreSQL model.

Advanced hosts may consolidate SQL Server connector registrations across databases when
they can prove:

- every instance still publishes to its own topic,
- tombstones are keyed by `DocumentUuid`,
- offsets and failure recovery do not allow one instance's failure to block unrelated
  instance operations beyond the host's accepted operational boundary,
- Kafka ACLs remain topic-per-instance.

## Transform Pipeline

The connector pipeline must produce the v1 public contract while keeping cache and domain
lifecycles separate. Both physical source topics are processed by the same connector task
and routed to one public topic.

Recommended logical transform order:

1. Capture `dms.DocumentCache` and `dms.Document` with Debezium keys containing
   `DocumentUuid` and `tasks.max = 1` for the instance connector.
2. Before unwrapping or topic routing, classify the original Debezium record by source
   table and `op`:
   - retain cache `c`, `u`, and `r` records as upsert inputs,
   - convert document `d` records to Kafka record-level null tombstones,
   - drop cache `d`/`t` records and document `c`, `u`, and `r` records.
3. Suppress or drop Debezium's additional automatic tombstone records so each canonical
   document delete produces one public tombstone and cache deletes produce none. A clean
   implementation may set `tombstones.on.delete=false` and convert the retained document
   delete envelope to a tombstone.
4. Unwrap retained cache upsert values into the current row shape.
5. Rename value fields from database column names to lower camel case:
   - `DocumentUuid` -> `documentUuid`
   - `ProjectName` -> `projectName`
   - `ResourceName` -> `resourceName`
   - `ResourceVersion` -> `resourceVersion`
   - `ContentVersion` -> `contentVersion`
   - `LastModifiedAt` -> `lastModifiedAt`
   - `DocumentJson` -> `document`
6. Compose public `etag` from `contentVersion` and the Kafka document-state `variantKey`.
7. Inject the composed `etag` into `document._etag`.
8. Remove internal or operational fields from the public value:
   - `DocumentId`
   - `ComputedAt`
9. Add `contractVersion = 1`.
10. Expand `document` into structured JSON using the Ed-Fi expand-JSON SMT from DMS-1240
   when Debezium emits the JSON column as an escaped string.
11. Simplify the Kafka key from the Debezium key struct to the lowercase `DocumentUuid`
   string.
12. Route both physical Debezium topics to
   `<topic-prefix>.instance.<instance-key>.documents.v1`.

All value-shaping transforms apply only to retained cache upserts. Key simplification and
topic routing apply to both upserts and authoritative document tombstones.

Connector templates must pin the public wire serialization shape from
[0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md):

- key converter: Kafka Connect `org.apache.kafka.connect.storage.StringConverter`, yielding UTF-8 lowercase
  `DocumentUuid` text with no JSON quoting,
- value converter: Kafka Connect `org.apache.kafka.connect.json.JsonConverter` with
  `value.converter.schemas.enable=false`,
- create/update/snapshot values: UTF-8 JSON objects without a Kafka Connect
  `schema` / `payload` wrapper,
- deletes: Kafka record-level null values, not JSON `null`.

The connector implementation may use stock Kafka Connect/Debezium SMTs plus the Ed-Fi
expand-JSON SMT where they are sufficient. If stock predicates and transforms cannot
classify both tables, drop unwanted operations, and emit exactly one document tombstone
without scripting-engine risk, the implementation should add a small Ed-Fi
document-state routing/shaping SMT. It should not leak Debezium's physical row shape or
cache deletes into the public contract.

Version-specific connector property names, SMT names, predicates, and delete-handling
modes must be verified against the pinned `edfialliance/ed-fi-kafka-connect` image at
implementation time. Tests should assert the published Kafka record shape and ordering,
not only connector JSON.

## Snapshot and Projection Reconciliation

Initial connector registration should support an initial snapshot of `dms.DocumentCache`
so existing materialized documents are published to the instance topic. The connector may
snapshot both included tables, but the operation filter drops every `dms.Document`
snapshot/read record.

Recommended CDC enablement sequence for a new instance:

1. Provision relational schema and `dms.DocumentCache`.
2. Apply provider-specific database CDC/key setup and create the instance topic/ACLs.
3. Add the instance to `KafkaCdc:Targets`; this selects asynchronous DocumentCache
   reconciliation. Verify that stale-write fencing and the mismatch-health surface are
   available.
4. Register the connector before allowing write traffic that must be observed by CDC.
5. Start DMS and let the ordinary reconciliation loop populate every current missing or
   version-mismatched cache row while its guarded upserts flow through Debezium.
6. Observe a zero projection mismatch count, then advertise CDC as ready only after the
   connector has caught up through a database source position at or after that
   observation and connector lag satisfies its threshold.

Recommended CDC enablement sequence for an existing instance:

1. Add the instance to `KafkaCdc:Targets`; this selects asynchronous DocumentCache
   reconciliation, including stale-write fencing.
2. Apply provider-specific database CDC/key setup and create the instance topic/ACLs.
3. Register the connector with initial snapshot behavior before allowing write/delete
   traffic that the host expects Kafka CDC to observe. If that is not possible, quiesce
   writes/deletes until connector registration completes.
4. Run the ordinary projector reconciliation loop until the exact current mismatch count
   is zero. Do not infer completeness from a maximum scanned or projected version.
5. Record a connector/source position at or after the zero-mismatch observation. Monitor
   until connector snapshot/catch-up reaches that position and connector lag is within
   threshold, and only then advertise CDC as ready.

DMS-1246 owns the projector-side details that make this safe (see
[../document-cache/](../document-cache/)), especially:

- reconciliation must not write an older `contentVersion` after a newer one for the same
  `documentUuid`,
- a stale materialization candidate must not recreate a cache row after canonical
  `dms.Document` deletion,
- current failures remain visible through mismatch count/age, structured logs, and
  bounded retry metrics.

Canonical deletes are captured independently from `dms.Document` and remain valid while
the cache is missing, stale, rebuilding, or unhealthy. No projector-side pre-delete
operation participates in the API transaction.

Consumers must tolerate duplicate upserts and replayed snapshot records. `contentVersion`
is the stale-write guard.

## Local Bootstrap and CI Behavior

Local Docker Compose already provides Kafka and Kafka Connect infrastructure. Relational
CDC implementation should add connector registration only behind an explicit CDC flag.

Recommended local behavior:

- `-EnableKafkaUI` starts Kafka UI only.
- `-EnableKafkaCdc` starts Kafka and Kafka Connect if needed, verifies that both captured
  tables, key/replica setup, `dms.DocumentCache`, and projector guarantees are
  provisioned, registers the instance connector before reconciliation/test writes, waits
  for zero projection mismatches and connector catch-up, and prints the target topic.
- E2E setup that opts into CDC registers the connector after database provisioning and
  before the test writes it expects to observe.
- The quarantined KafkaMessaging scenarios should be replaced or updated to assert:
  - topic name follows the v1 instance topic contract,
  - create/update emits a non-null envelope with expanded `document`,
  - delete emits a tombstone keyed by `DocumentUuid`,
  - cache deletion/rebuild emits no tombstone,
  - no legacy `EdFiDoc` or `deleted=true` shape is expected.

Connector JSON templates should be generated or parameterized from the selected data
store context rather than checked in with a single hard-coded database name.

Production-like deployment automation repeats this same one-shot workflow for every target
in `DataManagement:KafkaCdc:Targets`. Runtime discovery, automatic connector retirement,
and automatic physical-source replacement are outside the v1 contract.

## Security

Database connector credentials must be least-privilege:

- PostgreSQL: replication/login privileges plus access required to read the publication
  and the two captured tables.
- SQL Server: CDC read access for the target database/tables and no broad administrative
  permissions beyond setup-time operations performed by deployment automation.

Kafka ACLs should grant consumers access to only the instance topics they are authorized
to read. Kafka Connect internal topics, connector REST APIs, and database credentials
must not be exposed to third-party consumers.

Local development defaults may use insecure credentials, but production guidance must
explicitly replace them.

## Operational Expectations

Each connector registration must expose enough status for operators to answer:

- is the connector running,
- which DMS instance and database does it capture,
- which topic does it publish,
- what replication slot or CDC capture instance does it use,
- what is the current connector lag,
- what was the last error,
- has the initial snapshot completed.

Status and readiness are per logical data store. An aggregate deployment signal may report
not ready when any CDC-enabled data store is not ready, but one instance's connector or
projector failure must not stop unrelated DMS API instances or conceal their individual
status.

Status also reports missing configured targets, retryable physical-identity resolution
failures, and latched source drift using only the opaque data-store key and a sanitized
reason. These conditions do not alter request routing or API mutation availability.

Connector failures should not corrupt DMS writes. DMS write correctness remains tied to
the relational store and `dms.Document` stamps, not to Kafka delivery.

Operational runbooks should document connector restart, offset reset, resnapshot, and
topic recreation behavior. Offset reset and resnapshot are destructive operational acts
because they can replay the current document state.

## Alternatives Considered

### Debezium Server

Deferred. Debezium Server can be useful for non-Kafka deployments, but Kafka Connect fits
the existing Ed-Fi Kafka Connect image, REST-based connector registration, SMT pipeline,
and local Docker Compose infrastructure.

### Embedded Debezium in a Custom DMS Worker

Rejected. It adds a custom Java service boundary and makes DMS responsible for connector
lifecycle that Kafka Connect already provides.

### DMS Dual Writes to Kafka

Rejected. The CDC architecture intentionally reads database logs instead of adding a
second write target to the DMS request transaction. Dual writes introduce distributed
transaction and retry semantics that are outside DMS write correctness.

### One Shared PostgreSQL Connector Across Instance Databases

Rejected. PostgreSQL replication slots are scoped to one database, so this does not work
for the database-per-instance isolation model.

### Always Consolidate SQL Server Instances in One Connector

Rejected for the reference implementation. It is possible in some SQL Server deployments,
but it complicates routing, runbooks, and failure isolation. It remains an advanced host
optimization when the public contract is preserved.

## Follow-up Design Work

DMS-1245 should next define the implementation epic and stories for relational CDC:

- DDL changes for two-table CDC capture, `DocumentUuid` keys, and PostgreSQL
  `dms.Document` replica identity,
- CDC readiness checks for `DocumentCache` mismatch-derived completeness/health and
  authoritative `dms.Document` delete capture,
- connector template generation for PostgreSQL and SQL Server,
- bootstrap `-EnableKafkaCdc` behavior,
- E2E Kafka scenario replacement,
- operational documentation.

The DMS-1246 decision records in [../document-cache/](../document-cache/) define the
upsert projector behavior this connector design relies on. The DMS-1245 implementation
itself owns provider verification for `dms.Document` delete capture, source-operation
filtering, routed tombstones, and same-key ordering.
