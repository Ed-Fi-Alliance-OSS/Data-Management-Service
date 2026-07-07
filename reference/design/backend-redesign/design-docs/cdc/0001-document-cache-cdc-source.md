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

CDC mode adds a reliability invariant to the otherwise optional/eventual cache:

- upsert materialization may be asynchronous, subject to projector lag and stale-write
  guards,
- delete materialization is synchronous enough that DMS must not delete `dms.Document`
  unless a corresponding `dms.DocumentCache` source row exists in the same delete
  transaction.

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
indexing, and CDC streaming. Its `DocumentJson` column contains the caller-agnostic,
pre-profile, full API resource body emitted by reconstitution, including top-level
`id`, `_etag`, and `_lastModifiedDate`. When link injection is part of the read plan,
this cached document includes reference `link` subtrees; readable-profile projection and
`ResourceLinks:Enabled` stripping happen after cache retrieval and do not change the
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
- Kafka consumers observe the caller-agnostic cached API body, not a profile-filtered response
  and not authorization metadata.
- CDC consumers should treat `DocumentJson` as the document payload and the cache
  metadata as the stream envelope input:
  - `DocumentUuid`
  - `ProjectName`
  - `ResourceName`
  - `ResourceVersion`
  - `ContentVersion`
  - `Etag` (base64-encoded `SHA-256` API `_etag`)
  - `LastModifiedAt`
  - `DocumentJson`
- `DocumentId` may be useful for connector mechanics, ordering diagnostics, and internal
  correlation, but it should not be the public document identity in the Kafka contract.
  The public stable document identifier is `DocumentUuid`.
- Because `dms.DocumentCache` is populated by a projector, CDC observes projection writes,
  not the original resource-table writes. Kafka ordering and lag semantics must therefore
  be documented in terms of projection application and `ContentVersion`.
- Delete messages should be based on `dms.DocumentCache` row deletes and Kafka tombstone
  behavior. In CDC mode, the delete path must synchronously ensure a `dms.DocumentCache`
  row exists for the target `DocumentId` before deleting the resource row and
  `dms.Document`. If the row is missing or stale, DMS must materialize/upsert the current
  pre-delete representation under the same per-document write/projector fence, then
  delete `dms.Document` so `ON DELETE CASCADE` removes the cache row and Debezium can
  publish the tombstone.
- If CDC is enabled and the delete path cannot materialize or verify the cache source row,
  the API delete must fail with a retryable server-side error rather than silently
  deleting the only row Debezium can use for the tombstone.
- Projector and backfill writes must be fenced by `(DocumentId, ContentVersion)` or an
  equivalent monotonic guard. A lower-`ContentVersion` retry/backfill must not overwrite a
  newer cache row or recreate a cache row after a CDC-mode delete has removed the
  document.
- CDC readiness must require completion of a bounded initial `dms.DocumentCache`
  backfill epoch for existing `dms.Document` rows, no known projector dead-letter
  failures, and projector lag above the completed backfill target within the configured
  operational threshold. Deployments may run below that threshold for ordinary
  cache-backed reads, but should not advertise Kafka CDC as ready. The completed
  backfill epoch id and target content version are the CDC readiness cutover marker.
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

DMS-1246 owns the `dms.DocumentCache` implementation design. The proposed decision
records are in [../document-cache/](../document-cache/) and cover:

- projector lifecycle and enablement,
- initial backfill and rebuild,
- freshness and lag semantics,
- retry, dead-letter, telemetry, and health checks,
- CDC-mode synchronous pre-delete materialization and failure behavior,
- stale-write fencing for projector retries/backfill and post-delete races,
- any additional indexes or projector state needed for reliable capture.
