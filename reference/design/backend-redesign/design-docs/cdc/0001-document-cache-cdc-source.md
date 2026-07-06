---
status: proposed
date: 2026-07-06
jira: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1089
---

# Decision Record: Relational CDC Source for Kafka

## Decision

When Debezium/Kafka CDC is enabled for the relational DMS backend, `dms.DocumentCache`
is required and is the authoritative database capture source for document-level Kafka
messages.

`dms.DocumentCache` remains optional when CDC/Kafka is not enabled. It is not the
canonical persistence model, and DMS write correctness, authorization, Change Queries,
and normal GET/query behavior must not depend on it.

## Context

The relational backend stores canonical resource state in per-resource relational
tables. `dms.Document` is metadata-only: it carries identity, resource type, ownership,
and representation stamps, but it does not carry the reconstituted JSON document.

The existing CDC/Kafka reference architecture predates the relational backend and still
contains legacy `dms.Document` / `EdfiDoc` assumptions. Those assumptions are not valid
for the relational design.

The backend redesign already defines `dms.DocumentCache` as an optional, eventually
consistent materialized JSON projection intended for read acceleration, downstream
indexing, and CDC streaming. Its `DocumentJson` column contains the caller-agnostic
pre-profile resource document emitted by reconstitution. When link injection is part of
the read plan, this cached document includes `link` subtrees; readable-profile projection
and `ResourceLinks:Enabled` stripping happen after cache retrieval and do not change the
full-resource `_etag`.

Change Queries are a separate polling API surface based on `ContentVersion`,
`ContentLastModifiedAt`, and `tracked_changes_*` tables. They are not the Debezium/Kafka
streaming design.

## Consequences

- Enabling relational CDC/Kafka also enables/provisions `dms.DocumentCache` and its
  projector. A deployment may still disable `dms.DocumentCache` when it does not need
  CDC/Kafka or cache-backed reads.
- Debezium captures `dms.DocumentCache`, not the normalized per-resource tables and not
  `dms.Document` alone.
- Kafka consumers observe the caller-agnostic cached projection, not a profile-filtered
  response and not authorization metadata.
- CDC consumers should treat `DocumentJson` as the document payload and the cache
  metadata as the stream envelope input:
  - `DocumentUuid`
  - `ProjectName`
  - `ResourceName`
  - `ResourceVersion`
  - `ContentVersion`
  - `Etag`
  - `LastModifiedAt`
  - `DocumentJson`
- `DocumentId` may be useful for connector mechanics, ordering diagnostics, and internal
  correlation, but it should not be the public document identity in the Kafka contract.
  The public stable document identifier is `DocumentUuid`.
- Because `dms.DocumentCache` is populated by a projector, CDC observes projection writes,
  not the original resource-table writes. Kafka ordering and lag semantics must therefore
  be documented in terms of projection application and `ContentVersion`.
- Delete messages should be based on `dms.DocumentCache` row deletes and Kafka tombstone
  behavior. DMS-1246 must define the projector/backfill guarantees required so a CDC
  deployment does not miss delete events because cache rows were absent or stale.
- Local Docker Compose, bootstrap, and CI connector registration should target the
  provisioned relational database and `dms.DocumentCache` once the projector and connector
  contract are implemented.

## Alternatives Considered

### Capture `dms.Document`

Rejected. `dms.Document` is metadata-only in the relational design. Capturing it would not
provide the JSON document body and would force downstream consumers or connector transforms
to reimplement DMS reconstitution logic.

### Capture every resource table directly

Rejected. This exposes the physical relational storage model instead of a stable document
contract. It would require consumers to understand per-resource table shapes, child-table
joins, extension tables, descriptors, links, and projection rules. It also creates a large
topic and connector-shape surface that changes with every effective schema.

### Use Change Queries tables as the Kafka source

Rejected. Change Queries are a polling API compatibility surface. They are designed for
`/deletes`, `/keyChanges`, and live resource filters, not for publishing a complete
document payload to Kafka.

### Add a relational outbox as the primary source

Deferred. An outbox may be useful later if DMS needs explicit domain events that differ
from materialized document state. It is a larger contract: event type taxonomy, payload
shape, write/projector transaction boundaries, retention, and replay semantics. For the
relational document stream, `dms.DocumentCache` is the smaller and more direct source
because it already contains the materialized document shape downstream consumers need.

## Follow-up Design Work

DMS-1245 should use this source decision to finalize:

- topic-per-instance strategy and topic naming,
- connector deployment model for PostgreSQL and SQL Server,
- Kafka key/value/delete/tombstone contract,
- field names after Debezium unwrap and expand-JSON transforms,
- local Docker Compose/bootstrap connector registration,
- E2E replacement for the quarantined KafkaMessaging scenarios.

DMS-1246 should finalize the `dms.DocumentCache` implementation design:

- projector lifecycle and enablement,
- initial backfill and rebuild,
- freshness and lag semantics,
- retry, dead-letter, telemetry, and health checks,
- delete coverage when CDC is enabled,
- any additional indexes or projector state needed for reliable capture.
