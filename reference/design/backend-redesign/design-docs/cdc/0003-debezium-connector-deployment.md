---
status: proposed
date: 2026-07-06
jira: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1240
  - DMS-1089
---

# Decision Record: Debezium Connector Deployment for Relational CDC

## Decision

The relational CDC reference architecture uses Kafka Connect with Debezium source
connectors to capture `dms.DocumentCache` and publish the document-state topic defined
in [0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md).

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
- the `dms.DocumentCache` projector is enabled and has the CDC guarantees from DMS-1246:
  bounded initial backfill, stale-write fencing, synchronous pre-delete materialization,
  visible health/lag, and retry/dead-letter handling (see
  [../document-cache/](../document-cache/)),
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
physical database containing its `dms.DocumentCache`. CMS may allow multiple data-store
records to reference the same database, but CDC must not register a separate connector and
topic for each alias. `dms.DocumentCache` has no tenant or data-store discriminator, so
those topics would expose duplicate copies of the same physical document set under
potentially different ACLs.

The CDC prerequisite implementation must resolve a provider-specific physical database
identity from each configured connection and reject CDC enablement when two active
`(tenant key, DataStoreId)` entries resolve to the same identity. Comparison must not rely
only on raw connection-string text; semantically equivalent connection strings and server
aliases must be normalized or confirmed after connecting. The diagnostic identifies the
conflicting opaque data-store IDs without logging credentials or tenant display names.

Rejecting the conflict is safer than choosing an arbitrary canonical alias: it avoids
silently changing a topic's security boundary or making topic stability depend on which
alias happens to load first. Ordinary non-CDC DMS operation is unaffected by this CDC
validation failure.

## Fixed Deployment Inventory

The v1 CDC inventory is fixed for the lifetime of a DMS deployment. Deployment/bootstrap
automation enumerates the configured tenant-scoped data stores at startup and performs the
one-shot provisioning and connector-registration workflow explicitly for each CDC target.
V1 does not continuously discover CMS additions, removals, or connection changes and does
not run a background Kafka Connect reconciler.

The fixed-inventory contract is:

- every CDC-enabled data store is selected explicitly during deployment, validated against
  the physical-database uniqueness rule above, and given one connector and one instance
  topic,
- the DMS hosted projector supervisor creates one isolated execution context for every
  configured startup target, but it does not call the Kafka Connect REST API,
- adding or removing a CDC-enabled data store requires an explicit configuration change and
  deployment/restart that runs the provisioning workflow again,
- removing a target requires an explicit operator decision for connector shutdown, topic
  retention/deletion, offset deletion, PostgreSQL slot/publication cleanup, SQL Server CDC
  cleanup, and ACL retirement; absence from a later configuration is not authority for
  destructive cleanup,
- credential, host-name, or server-alias changes that resolve to the same physical database
  may use an explicit connector update/restart while preserving the topic identity,
- changing a `DataStoreId` to a different provider or physical document set is not an
  automatic replacement operation. CDC remains unsupported/not ready until an explicit
  migration chooses a new topic/source generation or deliberately resets the existing
  topic and connector state before resnapshotting,
- route-qualifier-only changes do not affect the connector or topic because request routing
  is outside the CDC source identity.

The one-shot workflow must remain idempotent for repeated deployment of the same logical
connector and physical source. It must reject an attempt to reuse an existing instance
topic for a different physical document set without an explicit migration/reset decision.

## Captured Table

The source connector captures only `dms.DocumentCache`.

The connector must not capture every resource table, `dms.Document`, Change Query
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
  only `dms.DocumentCache`,
- create one replication slot per DMS instance connector,
- configure the connector's table include list to `dms.DocumentCache`,
- configure the connector key columns so the Kafka key is `DocumentUuid`,
- preserve delete tombstones.

PostgreSQL logical replication slots are database-scoped. Therefore, when the DMS
instance isolation model uses one database per instance, the reference PostgreSQL
deployment also uses one source connector per instance database.

### PostgreSQL Delete Key Requirement

The connector must be able to publish a delete tombstone keyed by `DocumentUuid`. Since
`DocumentCache` is physically keyed by `DocumentId`, the PostgreSQL deployment must not
rely on the default table primary key.

Recommended implementation path:

1. Use Debezium key-column configuration to make `DocumentUuid` the Kafka key.
2. Ensure PostgreSQL logical decoding has `DocumentUuid` available for deletes. Prefer a
   replica identity based on the unique `DocumentUuid` index if supported by the emitted
   DDL and PostgreSQL version. Use `REPLICA IDENTITY FULL` only if the unique-index
   approach is not viable.
3. Add connector smoke tests that delete a document and assert the tombstone key is the
   public `DocumentUuid`.

The implementation story must verify the exact Debezium property names and PostgreSQL SQL
syntax against the pinned Kafka Connect/Debezium image.

## SQL Server Deployment

Recommended SQL Server shape:

- use the native Debezium SQL Server connector,
- enable SQL Server CDC for the DMS instance database,
- enable CDC on `dms.DocumentCache` only,
- use a least-privilege connector login with CDC read access,
- configure the connector key columns so the Kafka key is `DocumentUuid`,
- preserve delete tombstones.

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

The connector pipeline must produce the v1 public contract while preserving tombstones.

Recommended logical transform order:

1. Capture `dms.DocumentCache` with a Debezium key containing `DocumentUuid`.
2. Unwrap Debezium create/update values into the current row shape.
3. Preserve delete tombstones; do not rewrite deletes into a `deleted=true` envelope.
4. Rename value fields from database column names to lower camel case:
   - `DocumentUuid` -> `documentUuid`
   - `ProjectName` -> `projectName`
   - `ResourceName` -> `resourceName`
   - `ResourceVersion` -> `resourceVersion`
   - `ContentVersion` -> `contentVersion`
   - `LastModifiedAt` -> `lastModifiedAt`
   - `DocumentJson` -> `document`
5. Compose public `etag` from `contentVersion` and the Kafka document-state `variantKey`.
6. Inject the composed `etag` into `document._etag`.
7. Remove internal or operational fields from the public value:
   - `DocumentId`
   - `ComputedAt`
8. Add `contractVersion = 1`.
9. Expand `document` into structured JSON using the Ed-Fi expand-JSON SMT from DMS-1240
   when Debezium emits the JSON column as an escaped string.
10. Simplify the Kafka key from the Debezium key struct to the lowercase `DocumentUuid`
   string.
11. Route the physical Debezium topic to
   `<topic-prefix>.instance.<instance-key>.documents.v1`.

All value-shaping transforms must pass null tombstone values through unchanged. Key
simplification and topic routing still apply to tombstones.

Connector templates must pin the public wire serialization shape from
[0002-kafka-topic-and-message-contract.md](0002-kafka-topic-and-message-contract.md):

- key converter: Kafka Connect `org.apache.kafka.connect.storage.StringConverter`, yielding UTF-8 lowercase
  `DocumentUuid` text with no JSON quoting,
- value converter: Kafka Connect `org.apache.kafka.connect.json.JsonConverter` with
  `value.converter.schemas.enable=false`,
- create/update/snapshot values: UTF-8 JSON objects without a Kafka Connect
  `schema` / `payload` wrapper,
- deletes: Kafka record-level null values, not JSON `null`.

The connector implementation may use stock Kafka Connect SMTs plus the Ed-Fi expand-JSON
SMT where they are sufficient. If stock SMTs cannot produce this exact envelope and
tombstone behavior cleanly, the implementation should add a small Ed-Fi value-shaping SMT
rather than leaking Debezium's physical row shape as the public contract.

Version-specific connector property names, SMT names, and delete-handling modes must be
verified against the pinned `edfialliance/ed-fi-kafka-connect` image at implementation
time. Tests should assert the published Kafka record shape, not only connector JSON.

## Snapshot and Backfill

Initial connector registration should support an initial snapshot of `dms.DocumentCache`
so existing materialized documents are published to the instance topic.

Recommended CDC enablement sequence for a new instance:

1. Provision relational schema and `dms.DocumentCache`.
2. Apply provider-specific database CDC/key setup and create the instance topic/ACLs.
3. Configure DMS with `Projector:Mode = CdcRequired` and verify that stale-write fencing,
   pre-delete materialization, and the required health/state surfaces are available.
4. Register the connector before allowing write traffic that must be observed by CDC.
5. Start DMS and the projector, run the bounded initial backfill, and let projector writes
   flow through Debezium.
6. Advertise CDC as ready only after the backfill epoch, connector snapshot/catch-up,
   projector lag, and connector lag satisfy their readiness thresholds.

Recommended CDC enablement sequence for an existing instance:

1. Enable `dms.DocumentCache` and the projector in CDC mode, including stale-write
   fencing and pre-delete materialization support.
2. Apply provider-specific database CDC/key setup and create the instance topic/ACLs.
3. Register the connector with initial snapshot behavior before allowing write/delete
   traffic that the host expects Kafka CDC to observe. If that is not possible, quiesce
   writes/deletes until connector registration completes.
4. Run or resume the bounded projector backfill epoch until every still-current
   `dms.Document` row at or below the epoch's captured `BackfillTargetContentVersion`
   has a fresh `dms.DocumentCache` row.
5. Monitor until connector snapshot/catch-up, connector lag, projector lag above the
   completed backfill target, and backfill status reach acceptable thresholds, and only
   then advertise CDC as ready.
   The completed backfill epoch id and target content version are the readiness cutover
   marker for this bootstrap.

DMS-1246 owns the projector-side details that make this safe (see
[../document-cache/](../document-cache/)), especially:

- backfill must not write an older `contentVersion` after a newer one for the same
  `documentUuid`,
- delete must synchronously materialize or verify a cache row before deleting
  `dms.Document`, so a cache row that was missing before the delete request still yields a
  Debezium row-delete and Kafka tombstone,
- older queued projection work must not recreate a cache row after a CDC-mode delete,
- projector failures must be visible through health, metrics, and retry/dead-letter
  signals.

Consumers must tolerate duplicate upserts and replayed snapshot records. `contentVersion`
is the stale-write guard.

## Local Bootstrap and CI Behavior

Local Docker Compose already provides Kafka and Kafka Connect infrastructure. Relational
CDC implementation should add connector registration only behind an explicit CDC flag.

Recommended local behavior:

- `-EnableKafkaUI` starts Kafka UI only.
- `-EnableKafkaCdc` starts Kafka and Kafka Connect if needed, verifies that
  `dms.DocumentCache` and the CDC source guarantees are provisioned, registers the
  instance connector before backfill/test writes, waits for backfill and connector
  catch-up readiness, and prints the target topic.
- E2E setup that opts into CDC registers the connector after database provisioning and
  before the test writes it expects to observe.
- The quarantined KafkaMessaging scenarios should be replaced or updated to assert:
  - topic name follows the v1 instance topic contract,
  - create/update emits a non-null envelope with expanded `document`,
  - delete emits a tombstone keyed by `DocumentUuid`,
  - no legacy `EdFiDoc` or `deleted=true` shape is expected.

Connector JSON templates should be generated or parameterized from the selected data
store context rather than checked in with a single hard-coded database name.

Production-like deployment automation repeats this same one-shot workflow for every
statically configured CDC data store. Runtime discovery, automatic connector retirement,
and automatic physical-source replacement are outside the v1 contract.

## Security

Database connector credentials must be least-privilege:

- PostgreSQL: replication/login privileges plus access required to read the publication
  and `dms.DocumentCache`.
- SQL Server: CDC read access for the target database/table and no broad administrative
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

- DDL changes for `DocumentCache` CDC key/replica identity support,
- CDC readiness checks for `DocumentCache` backfill, projector health, and pre-delete
  materialization support,
- connector template generation for PostgreSQL and SQL Server,
- bootstrap `-EnableKafkaCdc` behavior,
- E2E Kafka scenario replacement,
- operational documentation.

The DMS-1246 decision records in [../document-cache/](../document-cache/) define the
projector behavior this connector design relies on. Connector implementation is not
complete until those source guarantees are implemented and provider-verified.
