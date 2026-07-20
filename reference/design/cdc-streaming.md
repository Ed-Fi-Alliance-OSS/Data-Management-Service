# DMS Feature: Change Data Capture to Kafka

> [!NOTE]
> This document is the high-level CDC/Kafka reference. The relational backend-specific
> source, message, and connector decisions are now recorded in:
>
> - `reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-sources.md`
> - `reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md`
> - `reference/design/backend-redesign/design-docs/cdc/0003-debezium-connector-deployment.md`
>
> Older references to legacy `dms.Document` JSON columns, `EdfiDoc`, OpenSearch, or a
> shared `edfi.dms.document` topic are historical and are not active relational CDC
> contracts.

## Overview

DMS CDC uses database-log-based capture to publish document-state changes to Kafka.
The reference implementation uses Kafka Connect with Debezium source connectors for
PostgreSQL and SQL Server.

One relational connector captures two complementary sources. `dms.DocumentCache`
create/update/snapshot events supply document upserts; authoritative `dms.Document`
deletes supply tombstones. Cache deletes/truncates and all other document operations are
ignored. `dms.DocumentCache` remains optional for ordinary DMS correctness but is
required for CDC upserts.

Change Queries are separate from Debezium/Kafka CDC. Change Queries are a polling API
compatibility feature based on `ContentVersion`, `ContentLastModifiedAt`, and
`tracked_changes_*` tables. They are not the Kafka source.

## Why Debezium and Kafka

CDC reads database transaction logs instead of adding DMS request-path dual writes to
Kafka. This avoids coupling API write success to a second write target and keeps DMS
write correctness tied to the relational store.

Debezium remains the preferred CDC tool because it has mature PostgreSQL and SQL Server
connectors and fits the existing Ed-Fi Kafka Connect image and local Docker Compose
infrastructure. Kafka remains the durable stream store for hosts with streaming use
cases.

Debezium Server and embedded Debezium are deferred. Kafka Connect is the relational CDC
v1 reference deployment model.

## Relational Document Stream Contract

Relational DMS publishes a compacted document-state stream:

- one document topic per DMS instance,
- one topic for all resource types in that instance,
- Kafka key is the public `DocumentUuid`,
- Kafka value is a small lower-camel envelope plus the expanded `DocumentJson` payload,
- delete is a Kafka tombstone for the same `DocumentUuid` key.

The v1 wire format uses a Kafka Connect `org.apache.kafka.connect.storage.StringConverter`
key and `org.apache.kafka.connect.json.JsonConverter` value with
`value.converter.schemas.enable=false`: keys are UTF-8 lowercase `DocumentUuid` text,
create/update/snapshot values are UTF-8 JSON objects without a Kafka Connect `schema` /
`payload` wrapper, and deletes are Kafka record-level null values.

Recommended topic pattern:

```text
<topic-prefix>.instance.<instance-key>.documents.v1
```

Default local-only topic prefix:

```text
edfi.dms
```

Example:

```text
edfi.dms.instance.data-store-12.documents.v1
```

Production topic prefixes must include a stable opaque deployment/environment key that
is unique among every DMS/CMS deployment sharing the Kafka cluster. `DataStoreId` alone
is not unique across CMS installations. The short `edfi.dms` prefix is valid only on an
isolated local/test broker (or a broker explicitly dedicated to one deployment).

Create/update/snapshot values have this public shape:

```json
{
  "contractVersion": 1,
  "documentUuid": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
  "projectName": "EdFi",
  "resourceName": "Student",
  "resourceVersion": "5.2.0",
  "contentVersion": 123456,
  "etag": "123456-a1b2c3d4.j._.l.i",
  "lastModifiedAt": "2026-07-06T15:30:45.1234567Z",
  "document": {
    "id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
    "_etag": "123456-a1b2c3d4.j._.l.i",
    "_lastModifiedDate": "2026-07-06T15:30:45.1234567Z"
  }
}
```

The published value does not include `DocumentId`, `ComputedAt`, authorization arrays,
EdOrg hierarchy arrays, API client identity, or readable-profile-specific projections.
The `etag` value is the DMS API `_etag` for the Kafka document-state variant. It is
derived from `contentVersion` and the stream `variantKey`, using the same
`{schemaEpoch}.j._.{linkFlag}.i` variant-key shape as API ETags for the published JSON
document. It is not read from a `dms.DocumentCache.Etag` column.

The `document` field is produced from the caller-agnostic, pre-profile, full API
resource body stored in `dms.DocumentCache.DocumentJson`. The stream shaper injects
`_etag` from the envelope `etag`, so the published document includes top-level `id`,
`_etag`, and `_lastModifiedDate`. If link injection is compiled into the read plan, the
cached document includes reference `link` subtrees. DMS does not maintain a second
link-free Kafka projection. Envelope `documentUuid`, `etag`, and `lastModifiedAt` values
must match the embedded metadata fields in `document`.

Deletes publish:

```text
key = <DocumentUuid>
value = null
```

The relational v1 topic does not publish the legacy `deleted=true` / `EdFiDoc` shape.

The tombstone comes only from the authoritative `dms.Document` delete. The connector
ignores the cascaded cache delete. API deletion therefore does not depend on cache
presence, freshness, projector health, or synchronous pre-delete materialization. Cache
truncation/rebuild does not publish mass domain tombstones.

## Connector Deployment

The source connector captures exactly `dms.DocumentCache` and `dms.Document` in one task.

PostgreSQL reference deployment:

- use the Debezium PostgreSQL connector with `pgoutput`,
- configure PostgreSQL for logical replication,
- create a least-privilege replication user,
- use a publication scoped to both captured tables,
- use one replication slot and one connector per DMS instance database,
- configure `DocumentUuid` as the custom key for both tables,
- set `dms.Document` to `REPLICA IDENTITY FULL` for delete-key capture.

SQL Server reference deployment:

- use the Debezium SQL Server connector,
- enable SQL Server CDC for the DMS instance database,
- enable CDC on both captured tables only,
- use a least-privilege connector login,
- configure `DocumentUuid` as the custom key for both tables.

Debezium SQL Server can process multiple databases from one connector, but the reference
DMS implementation still treats each DMS instance as one logical connector registration
and one topic. SQL Server connector consolidation is an advanced host optimization only
when the same per-instance topic, key, tombstone, ACL, and operational contracts are
preserved.

The CDC topology must also preserve a one-to-one relationship between a logical instance
topic and the physical database containing both captured tables. If two tenant/data-store
records resolve to the same physical database, CDC readiness rejects both aliases rather
than publishing the same document set under independently authorized topics.

## Transform Pipeline

The connector pipeline must produce the relational v1 public contract while separating
cache and domain lifecycles:

1. Capture both tables with Debezium keys containing `DocumentUuid` and one connector task.
2. Before unwrapping/routing, retain cache `c/u/r` as upserts, convert document `d` to a
   Kafka tombstone, and drop cache `d/t` plus document `c/u/r`.
3. Suppress Debezium's automatic extra tombstones so one canonical delete emits exactly
   one public tombstone and cache deletion emits none.
4. Unwrap retained cache values and rename fields to lower camel case.
5. Remove internal fields such as `DocumentId` and `ComputedAt`.
6. Add `contractVersion = 1` and compose/inject the stream `_etag`.
7. Expand `document` into structured JSON using the Ed-Fi expand-JSON SMT when Debezium
   emits `DocumentJson` as an escaped string.
8. Simplify the Kafka key to the lowercase `DocumentUuid` string.
9. Route both physical Debezium topics to the instance document topic.

Provider E2E coverage must prove same-key ordering through the routed topic: a cache
upsert committed before canonical deletion appears before its tombstone.

## Local Bootstrap

Kafka and Kafka UI infrastructure can be useful without CDC. Local bootstrap should use
an explicit CDC opt-in, such as `-EnableKafkaCdc`, before registering DMS source
connectors.

`-EnableKafkaUI` starts Kafka UI only and must not imply connector registration.

Connector registration should occur after the target data store is selected, the target
database is provisioned, provider-specific CDC setup is applied, and
the asynchronous `dms.DocumentCache` projector verifies the required upsert projection
guarantees. Registration also verifies two-table keys, PostgreSQL replica identity, and
source-operation filtering. It establishes capture before the bounded initial backfill and
before writes that must be observed by CDC. CDC is advertised as ready only after the backfill
epoch, connector snapshot/catch-up, connector lag, and projector lag above the completed
backfill target are acceptable. Connector templates must be generated or parameterized
from the selected data-store context instead of using hard-coded database names.

The v1 CDC target list is explicit deployment configuration. Production-like automation
repeats the one-shot provisioning and registration workflow for every listed target. Adding
or removing a target requires an explicit configuration change and coordinated deployment.
Moving a `DataStoreId` to a different
physical document set requires an explicit migration with a new topic/source generation or
a deliberate topic/connector reset; v1 does not automatically replace physical sources or
infer destructive cleanup from target-list changes.

Kafka CDC uses an explicit deployment-configured target list of `(tenant key, DataStoreId)`
values. At startup DMS resolves a provider-specific physical database identity for each
listed target; it does not fingerprint complete connection configuration. Credential,
timeout, pooling, application-name, and equivalent-alias changes are not source drift when
they resolve to the same physical database. A missing configured target or confirmed
physical-source change makes that target's CDC readiness false, and confirmed drift remains
latched as `CdcSourceDriftRequiresDeployment` until the coordinated workflow reruns.

CDC readiness does not change normal CMS-driven request routing or API availability.
Entries outside the target list remain ordinary DMS data stores, and projection/connector
failure never blocks reads or mutations.

## Multitenancy and Security

Topic-per-instance segregation is the recommended Kafka isolation model. Shared topics
with instance filtering are not part of the relational CDC contract because they increase
cross-instance data leakage risk.

Kafka ACLs should grant consumers access only to the instance topics they are authorized
to read. Kafka Connect internal topics, connector REST APIs, and database credentials
must not be exposed to third-party consumers.

The DMS projector and CDC readiness are scoped per `(tenant key, DataStoreId)` and run
independently of request JWT/route selection. Process-wide v1 projector mode may apply to
every loaded data store with a usable connection string; connector registration and CDC
readiness apply only to the explicit deployment-configured targets.

Database connector credentials should be least-privilege. Local development defaults may
use insecure credentials, but production deployments must replace them.

## Historical and Deferred Material

Legacy document-store connector configs targeted JSON columns such as `EdfiDoc`,
`SecurityElements`, authorization EdOrg arrays, and hierarchy JSON. Those columns do not
exist in the relational schema and are not migration targets.

OpenSearch/Elasticsearch read-store support has been dropped. Any prior OpenSearch sink
connector settings in this area are historical.

A domain-event outbox remains a possible future design if DMS needs event shapes that are
different from the materialized document-state stream. It is not part of the relational
CDC v1 contract.
