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

# Decision Record: Kafka Topic and Message Contract for Relational CDC

## Decision

Relational DMS CDC publishes a compacted document-state stream sourced from
`dms.DocumentCache`.

The v1 public Kafka contract is:

- one document topic per DMS instance,
- one topic for all resource types in that instance,
- Kafka key = `DocumentUuid`,
- Kafka value = an envelope containing `dms.DocumentCache` metadata and the expanded
  `DocumentJson` payload,
- delete = Kafka tombstone for the same `DocumentUuid` key.

The stream is an upsert/delete state stream, not an immutable event log and not the
Change Queries API.

## Topic Naming

Recommended topic pattern:

```text
<topic-prefix>.instance.<instance-key>.documents.v1
```

Default local topic prefix:

```text
edfi.dms
```

Example:

```text
edfi.dms.instance.data-store-12.documents.v1
```

`<instance-key>` is a deployment-controlled, Kafka-safe, opaque identifier for one DMS
instance. The recommended default is `data-store-<DataStoreId>` when CMS is the
instance registry. Hosted deployments may use a different stable opaque alias, but the
topic name must not include district names, tenant display names, school years, or other
human-readable sensitive identifiers.

The `<topic-prefix>` may include deployment, environment, or tenant scoping when a host
needs another namespace boundary, for example `edfi.prod.us-east`.

## Topic Shape

Use one compacted document topic per instance rather than topic-per-resource.

Rationale:

- topic-per-instance matches the multitenancy data-segregation guidance,
- Kafka ACLs can grant a consumer access to exactly one instance stream,
- one topic per instance avoids multiplying topics by every Ed-Fi resource type,
- resource type is already present in each value through `projectName` and `resourceName`,
- consumers can still route to resource-specific downstream stores from the envelope.

Recommended topic configuration:

```text
cleanup.policy=compact
```

Hosts may add delete retention or time retention settings for operational replay windows,
but consumers must not depend on this topic preserving every historical change. It
preserves final document state by key, plus tombstones during the Kafka delete retention
window.

## Message Key

The public Kafka key is the lowercase `D`-format `DocumentUuid` string:

```json
"f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
```

`DocumentUuid` is the stable public document identity. `DocumentId` is an internal
surrogate and is not part of the public key or value contract.

Connector and database setup must guarantee that deletes are keyed by `DocumentUuid`, not
by `DocumentId`. For PostgreSQL this may require replica-identity or connector-key
configuration for `dms.DocumentCache`; for SQL Server this may require equivalent
connector key-column configuration. The connector deployment design owns the exact
engine-specific mechanism.

The connector must not rely on deriving the key only from the unwrapped value, because
delete tombstones have a null value. `DocumentUuid` must be available in the Debezium key
path so the tombstone can carry the correct public key.

## Message Value

For creates, updates, and initial snapshots, the value is a JSON object:

```json
{
  "contractVersion": 1,
  "documentUuid": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
  "projectName": "EdFi",
  "resourceName": "Student",
  "resourceVersion": "5.2.0",
  "contentVersion": 123456,
  "etag": "4d967b6c8c9e2fd5c8a47e3f0a6db9d0f4b9bb5c7c30c1a4e31dd4c21f2a0123",
  "lastModifiedAt": "2026-07-06T15:30:45.1234567Z",
  "document": {
    "id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
  }
}
```

Field meanings:

| Field | Source | Contract |
| --- | --- | --- |
| `contractVersion` | connector/static transform | Integer contract version for this topic value shape. |
| `documentUuid` | `dms.DocumentCache.DocumentUuid` | Stable public API document id and Kafka key. |
| `projectName` | `dms.DocumentCache.ProjectName` | MetaEd project name, such as `EdFi`. |
| `resourceName` | `dms.DocumentCache.ResourceName` | Resource name, such as `Student`. |
| `resourceVersion` | `dms.DocumentCache.ResourceVersion` | Project/schema resource version copied from `dms.ResourceKey`. |
| `contentVersion` | `dms.DocumentCache.ContentVersion` | Representation version applied by the projector. |
| `etag` | `dms.DocumentCache.Etag` | Full-resource `_etag` value for the cached representation. |
| `lastModifiedAt` | `dms.DocumentCache.LastModifiedAt` | Full-resource `_lastModifiedDate` source value. |
| `document` | `dms.DocumentCache.DocumentJson` | Expanded structured JSON object, not an escaped JSON string. |

The published field names are lower camel case even though the database column names are
Pascal case. Connector transforms should rename columns after Debezium unwrap.

`document` is exactly the cached caller-agnostic pre-profile projection. It is not
profile-filtered and does not include authorization arrays or EdOrg hierarchy JSON. When
the read plan includes link injection, `document` contains `link` subtrees. DMS does not
maintain a second link-free Kafka projection.

The envelope values for `etag` and `lastModifiedAt` are authoritative. Consumers should
not require `_etag` or `_lastModifiedDate` to be embedded inside `document`.

## JSON Expansion

`document` must be published as structured JSON.

When Debezium emits `DocumentJson` as an escaped string, the connector uses the Ed-Fi
owned expand-JSON SMT from DMS-1240 after renaming:

```text
sourceFields=document
```

If a future connector/runtime can publish the JSON column as a structured object without
an SMT, it must still publish the same value contract.

## Create and Update Semantics

A non-null value is an upsert for the message key.

Consumers should apply the value only when it is newer than the state they already hold
for that `documentUuid`. `contentVersion` is the recommended idempotency and stale-write
guard. Duplicate messages and replays are allowed.

Kafka ordering is guaranteed only per partition. Because the key is `DocumentUuid`,
ordering is guaranteed per document, not globally across all documents. Consumers must
not assume global `contentVersion` ordering unless a deployment intentionally uses one
partition and documents that operational tradeoff.

## Delete and Tombstone Semantics

Delete is represented by a Kafka tombstone:

```text
key = <DocumentUuid>
value = null
```

The v1 document-state topic does not publish a separate `deleted=true` envelope and does
not promise a deleted document body. Consumers that maintain resource-specific downstream
stores must retain enough local state from prior upserts to route the tombstone.

This replaces the legacy KafkaMessaging scenario shape that expected `deleted=false` /
`deleted=true` and an `EdFiDoc` body. DMS-1232 should update or replace those scenarios
to assert the relational v1 contract.

DMS-1246 must define the `dms.DocumentCache` projector guarantees needed for reliable
delete capture when CDC is enabled. In particular, CDC-enabled deployments must not miss
a delete because the cache row was never materialized.

## Security and Authorization

Topic-per-instance isolation is the primary Kafka authorization boundary. Consumer ACLs
should grant access at the instance-topic level.

The message value does not include:

- tenant display name,
- API client identity,
- authorization strategy output,
- EdOrg hierarchy arrays,
- readable-profile-specific projections.

The stream is still sensitive student data. Shared topics with instance filtering are not
part of this contract.

## Alternatives Considered

### Keep `edfi.dms.document`

Rejected for the relational public contract. It is a single shared topic name and does
not express instance isolation. Local test environments may use simpler aliases during
transition, but implementation stories should target the v1 topic-per-instance naming
contract.

### Topic per resource per instance

Rejected. It gives consumers convenient subscription granularity, but it multiplies topic
count by the full resource inventory for every instance and makes provisioning, ACLs, and
connector transforms more complex. The envelope already carries resource metadata.

### Shared topic with `instanceId` in the value

Rejected. It relies on every consumer filtering correctly and raises cross-instance data
leakage risk. This conflicts with the multitenancy analysis.

### Include `DocumentId` in the public value

Rejected. `DocumentId` is an internal storage surrogate. Including it would invite
consumers to depend on a non-API identifier that can vary across backfills,
reprovisioning, and database restores.

### Publish delete envelopes instead of tombstones

Rejected for v1. The document topic is a compacted state stream. Tombstones are the
standard delete representation for compacted topics and keep sink behavior simple.
Future outbox/event topics can carry richer delete events if needed.

## Follow-up Design Work

DMS-1245 should next define the connector deployment model:

- PostgreSQL and SQL Server source connector settings,
- table include list for `dms.DocumentCache`,
- key-column and delete-key guarantees for `DocumentUuid`,
- transform order for unwrap, field renames, static `contractVersion`, JSON expansion,
  key simplification from the Debezium key, tombstone preservation, and topic routing,
- snapshot mode and backfill behavior,
- local Docker Compose/bootstrap registration.

DMS-1246 should define the projector guarantees this contract depends on:

- cache materialization before delete when CDC is enabled,
- lag and health signals for projection,
- retry and dead-letter behavior,
- rebuild/backfill semantics that do not create stale lower-`contentVersion` messages
  after newer values for the same `documentUuid`.
