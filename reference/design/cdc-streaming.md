# DMS Feature: Change Data Capture to Kafka

> [!NOTE]
> This document is the high-level CDC/Kafka reference. The relational backend-specific
> source, message, and connector decisions are now recorded in:
>
> - `reference/design/backend-redesign/design-docs/cdc/0001-document-cache-cdc-source.md`
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

The relational backend source is `dms.DocumentCache`, not the normalized resource
tables and not `dms.Document` alone. `dms.DocumentCache` is optional for ordinary DMS
correctness, but it is required when relational Debezium/Kafka CDC is enabled.

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

Because the tombstone comes from a captured `dms.DocumentCache` row delete, CDC mode
requires a stronger projector guarantee than ordinary cache-backed reads. Upsert
projection may be asynchronous, but DMS must not delete `dms.Document` unless a
corresponding `dms.DocumentCache` row exists in the same transaction. If the cache row is
missing or stale at delete time, DMS materializes the current pre-delete projection first;
if it cannot, the API delete fails rather than losing the Kafka tombstone.

## Connector Deployment

The source connector captures only `dms.DocumentCache`.

PostgreSQL reference deployment:

- use the Debezium PostgreSQL connector with `pgoutput`,
- configure PostgreSQL for logical replication,
- create a least-privilege replication user,
- use a publication scoped to `dms.DocumentCache`,
- use one replication slot and one connector per DMS instance database,
- ensure delete tombstones are keyed by `DocumentUuid`.

SQL Server reference deployment:

- use the Debezium SQL Server connector,
- enable SQL Server CDC for the DMS instance database,
- enable CDC on `dms.DocumentCache` only,
- use a least-privilege connector login,
- ensure delete tombstones are keyed by `DocumentUuid`.

Debezium SQL Server can process multiple databases from one connector, but the reference
DMS implementation still treats each DMS instance as one logical connector registration
and one topic. SQL Server connector consolidation is an advanced host optimization only
when the same per-instance topic, key, tombstone, ACL, and operational contracts are
preserved.

The CDC topology must also preserve a one-to-one relationship between a logical instance
topic and a physical `dms.DocumentCache`. If two tenant/data-store records resolve to the
same physical database, CDC readiness rejects both aliases rather than publishing the same
document set under independently authorized topics.

## Transform Pipeline

The connector pipeline must produce the relational v1 public contract while preserving
tombstones:

1. Capture `dms.DocumentCache` with a Debezium key containing `DocumentUuid`.
2. Unwrap Debezium create/update values into the current row shape.
3. Preserve delete tombstones.
4. Rename value fields to lower camel case.
5. Remove internal fields such as `DocumentId` and `ComputedAt`.
6. Add `contractVersion = 1`.
7. Expand `document` into structured JSON using the Ed-Fi expand-JSON SMT when Debezium
   emits `DocumentJson` as an escaped string.
8. Simplify the Kafka key to the lowercase `DocumentUuid` string.
9. Route the physical Debezium topic to the instance document topic.

All value-shaping transforms must pass null tombstone values through unchanged. Key
simplification and topic routing still apply to tombstones.

## Local Bootstrap

Kafka and Kafka UI infrastructure can be useful without CDC. Local bootstrap should use
an explicit CDC opt-in, such as `-EnableKafkaCdc`, before registering DMS source
connectors.

`-EnableKafkaUI` starts Kafka UI only and must not imply connector registration.

Connector registration should occur after the target data store is selected, the target
database is provisioned, provider-specific CDC setup is applied, and
`dms.DocumentCache` verifies that the required projector/delete source guarantees are
available. Registration establishes capture before the bounded initial backfill and before
writes that must be observed by CDC. CDC is advertised as ready only after the backfill
epoch, connector snapshot/catch-up, connector lag, and projector lag above the completed
backfill target are acceptable. Connector templates must be generated or parameterized
from the selected data-store context instead of using hard-coded database names.

The v1 CDC inventory is fixed for the lifetime of a deployment. Production-like
automation repeats the one-shot provisioning and registration workflow for every
statically configured CDC data store. Adding or removing a target requires an explicit
configuration change and deployment/restart. Moving a `DataStoreId` to a different
physical document set requires an explicit migration with a new topic/source generation or
a deliberate topic/connector reset; v1 does not automatically replace physical sources or
infer destructive cleanup from inventory changes.

## Multitenancy and Security

Topic-per-instance segregation is the recommended Kafka isolation model. Shared topics
with instance filtering are not part of the relational CDC contract because they increase
cross-instance data leakage risk.

Kafka ACLs should grant consumers access only to the instance topics they are authorized
to read. Kafka Connect internal topics, connector REST APIs, and database credentials
must not be exposed to third-party consumers.

The DMS projector and CDC readiness are scoped per `(tenant key, DataStoreId)` and run
independently of request JWT/route selection. Process-wide v1 enablement applies to every
loaded data store with a usable connection string; connector registration and status
remain per data store.

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
