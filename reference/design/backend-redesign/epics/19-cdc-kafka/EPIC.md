---
jira: TBD
source_spike: DMS-1245
related:
  - DMS-1246
  - DMS-1232
  - DMS-1089
  - DMS-1279
---

# Epic: Relational CDC/Kafka Streaming

## Design References

- [Authoritative relational CDC design](../../../cdc-streaming.md)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Implement the relational Debezium/Kafka CDC capability defined by the design references,
including deployment-owned durable binding state, provider setup, connector generation
and registration, combined readiness, contract verification, API-driven E2E coverage,
and operator guidance. This epic owns connector-side lifecycle capture;
`18-document-cache` owns explicit projection targets and the reusable projected upsert
source.

## Stories

- `TBD` — `00-documentcache-cdc-prerequisites.md` — Add deployment-owned CDC binding and readiness
- `TBD` — `01-cdc-ddl-support.md` — Emit/provision provider CDC key and database support
- `TBD` — `02-connector-template-generation.md` — Generate PostgreSQL and SQL Server connector templates
- `TBD` — `03-document-state-transform.md` — Add the DMS-specific relational record transform
- `TBD` — `04-bootstrap-enable-kafka-cdc.md` — Add explicit local/bootstrap connector registration
- `TBD` — `05-message-contract-tests.md` — Add message and source-routing contract tests
- `TBD` — `06-e2e-kafka-scenarios.md` — Replace legacy Kafka E2E expectations
- `TBD` — `07-ops-docs-runbooks.md` — Add setup, monitoring, recovery, and security runbooks

## Delivery Dependencies

The story and cross-epic dependency graph is maintained once in
[DEPENDENCIES.md](../DEPENDENCIES.md). Story files identify only their immediate
implementation inputs.

## Completion Evidence

- The published Ed-Fi connector image is built from the exact
  `quay.io/debezium/connect:3.6.0.Final@sha256:6f3fe6407bae8f2a7714b9fc174d545d52d81044b4f4add1565854f020943d47`
  base, includes the required transforms, and is selected by immutable digest rather than
  a floating tag. Image tests run on its Kafka Connect 4.3.0 runtime.
- Both providers pass database CDC/key smoke tests and real routed-topic ordering tests.
  SQL Server provider qualification includes SQL Server 2025 as a known-working Ed-Fi
  combination.
- Generated and published records pass the topic/message contract suite.
- Each binding fixes its topic partition count and the named `kafka-murmur2-v1`
  partitioner behavior token so a document's later Kafka offset remains a valid
  equal-version tie-breaker without binding state depending on a Java class/version.
- Cache-row transitions and conforming consumer-applied non-null upserts are monotonic, and
  the stream is eventually convergent rather than linearizable to each canonical commit.
  Raw at-least-once delivery may contain duplicates or lower-version replays. A consumer may
  temporarily retain an older projection until the newer canonical version is projected;
  across a tombstone, replay may temporarily restore an older upsert until the replayed
  tombstone arrives. Optional projection requests no explicit update/write source-row lock
  as a content-version fence and carries no lock from the optimistic source check into the
  cache transaction. Ordinary integrity locks acquired there by foreign-key enforcement and
  the UUID-validation trigger remain required.
- Connector transforms copy the DMS-projected opaque stream ETag and contain no schema,
  link-configuration, or ETag-composition rules.
- Contract fixtures pin the v1 key, fields/types, tombstones, document semantics, and
  metadata relationships while treating `StreamEtag` bytes as opaque DMS output.
  Compatible projection corrections rebuild into the existing topic and replace an equal
  `contentVersion` at the later Kafka offset; incompatible contract changes use a new
  versioned topic.
- Local and E2E setup registers against selected provisioned data stores without
  hard-coded instance values.
- Binding state survives DMS and connector restarts, fails closed around missing or
  mismatched state, and prevents a topic generation from changing physical source.
- DMS exposes only per-database projection health; deployment automation combines it
  with binding, migration, provider source-position catch-up, and lag status. PostgreSQL
  and SQL Server adapters compare a barrier captured after the zero audit with the
  connector's committed Debezium offset, and an internal captured heartbeat advances idle
  sources. A durable cache-ahead latch keeps combined readiness false across later source
  equality and process restart until explicit recovery.
- Connector templates and live registration pin `errors.tolerance=none`; a malformed
  retained record fails the task and combined readiness instead of being skipped as
  caught-up progress.
- SQL Server templates pin `time.precision.mode=isostring` and the Debezium 3.6
  unavailable-value marker. The transform validates `IsoTimestamp` input, rejects an
  unavailable required `DocumentJson`, and emits only the existing whole-second public
  timestamp.
- Connector templates pin `statistics.metrics.enabled=true`, and deployment telemetry
  exposes Debezium 3.6 P50/P95/P99 source lag without treating it as a substitute for the
  provider position barrier.
- Every binding pins one `maxRecordBytes` derived from the maximum supported fully
  materialized link-bearing envelope. Producer, topic, broker/replication, and consumer
  limits align to it, and a broker-backed boundary test publishes and consumes that
  maximum record without relying on compression.
- API deletion remains correct when projection is absent or failing.
- Operator documentation covers supported setup, security, observation, same-topic
  compatible repair, incompatible-contract migration, and explicit destructive cleanup.

Anything excluded or deferred by the authoritative design is outside this epic unless a
new decision record changes that design.
