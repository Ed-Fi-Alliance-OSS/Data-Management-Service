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
source. V1 initial enablement is restricted to new physical databases provisioned with the
completed E18 schema before DMS writes are admitted; retrofitting an existing database is
outside the epic. Exact combined readiness exists only in that initial offline workflow.
After first-write admission, status is observational and eventually consistent. This epic
does not implement a production cross-replica/external-writer gate or an exact
baseline-replacing repair/cutover. It monitors provider source-history continuity and fails
closed when continuity is unknown; proven loss durably terminates that v1 binding and has no
v1 recovery workflow.

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
  partitioner behavior token so keyed upserts and versionless tombstones remain in one
  ordered compaction domain without binding state depending on a Java class/version.
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
  metadata relationships while treating `StreamEtag` bytes as opaque DMS output. They pin
  the consumer rule that only a higher `contentVersion` replaces retained non-null state;
  equal versions are byte-identical duplicates. The epic does not implement an exact
  baseline-replacing producer workflow or contract cutover.
- Local and E2E setup creates a fresh selected database, provisions the current E18 schema,
  retains positive evidence that it has not been published to a writer, and registers
  against it without hard-coded instance values. It rejects first-time use of an unbound
  already-provisioned database before creating governed CDC artifacts; later exact-match
  validation and restart of a successfully enabled binding remain supported.
- Binding state survives DMS and connector restarts, fails closed around missing or
  mismatched state, and prevents a topic generation from changing physical source. A
  guarded adoption operation accepts only an operator-supplied complete record after live
  verification of the physical source and every governed artifact; it never infers binding
  fields, is not a first-time enablement path, and changes nothing on failure. Destructive
  retirement removes the connector,
  offsets, topics, ACLs, provider capture artifacts, and every other governed artifact
  before terminal incident and binding state.
- Guarded source replacement of a database previously enabled through the v1 new-database
  path fences the old connector, rotates source identity through 19-00, and creates a new
  binding generation, connector, topics, provider artifacts, consumer namespace, and fresh
  snapshot. It never reuses old-generation artifacts, reports eventual status rather than
  another exact baseline, and cannot recover terminal source-history loss or a possibly
  published cache-ahead latch.
- DMS exposes only per-database projection health; deployment automation combines it
  with binding, new-database/offline eligibility for first-time enablement, provider
  source-position catch-up, and lag status. Initial readiness rejects prior audit evidence
  and keeps first-write admission closed through a fresh startup audit and the later
  publication barrier. PostgreSQL and SQL Server adapters compare a barrier captured after
  that zero audit with the connector's committed Debezium offset, and an internal captured
  heartbeat advances idle sources. After admission opens, the same inputs produce eventual
  operational status rather than another exact baseline. A durable cache-ahead latch keeps
  combined readiness false across later source equality and process restart. V1 clears it
  only for a proven internal-only projection; possibly published state remains latched and
  publication remains stopped because new-namespace recovery is deferred.
- Deployment status proves source-history continuity before connector start/resume and on
  every status interval. PostgreSQL checks the binding-derived slot/publication and retained
  WAL range; SQL Server checks every capture instance/job, retained LSN range, and remaining
  cleanup margin. Unavailable evidence reports `unknown` and prevents start/resume. Proven
  loss durably latches `SourceHistoryContinuityLost`, stops the connector, survives restart,
  and cannot be cleared by artifact recreation, offset mutation, or resnapshot. V1 never
  resnapshots the existing public topic after history loss and implements no replacement-
  namespace cutover.
- Connector templates and live registration pin `errors.tolerance=none`; a malformed
  retained record fails the task and combined readiness instead of being skipped as
  caught-up progress.
- SQL Server templates pin `time.precision.mode=isostring` and the Debezium 3.6
  unavailable-value marker. The transform validates `IsoTimestamp` input, rejects an
  unavailable required `DocumentJson`, and emits only the existing whole-second public
  timestamp. They also pin the binding-derived internal schema-history topic, same-cluster
  bootstrap/security configuration, durable history producer, and
  `include.schema.changes=false`. Provisioning gives the single-partition,
  infinite-retention history topic the active durability profile and connector-only ACLs;
  restart recovers retained history and offsets, while destructive cleanup removes both
  before binding state.
- Connector templates pin `statistics.metrics.enabled=true`, and deployment telemetry
  exposes Debezium 3.6 P50/P95/P99 source lag without treating it as a substitute for the
  provider position barrier.
- Every target has one mutable operational `maxRecordBytes` ceiling enforced by the real
  producer before publication. Producer request/buffer memory, topic, broker/replication,
  and consumer limits align to it; representative boundary tests prove acceptance and
  rejection without claiming a universal schema maximum; and a downstream-first increase
  reuses the binding and topic.
- Every public topic explicitly retains tombstones for at least seven days. Topic
  provisioning and live validation reject a missing topic-level `delete.retention.ms`
  override or a lower value, and conforming consumers must prove that a complete
  earliest-offset scan through captured partition barriers becomes durable within the
  fixed 24-hour bootstrap deadline. An over-deadline partial reconstruction is discarded
  and never advertised as valid; each independently operated consumer owns capacity
  evidence for the largest retained topic log it claims to support, not only its live-key
  count. Incremental consumers renew durable per-partition end-offset-barrier evidence at
  least every 24 hours; stale, missing, corrupt, or uncertain evidence invalidates all local
  state and requires another complete bounded bootstrap.
- API deletion remains correct when projection is absent or failing.
- Operator documentation covers supported initial offline setup, later eventual status,
  security, observation, exact-match restart, guarded adoption and source replacement,
  cache-ahead containment, and explicit destructive cleanup. It identifies
  exact baseline replacement and incompatible-contract cutover as deferred rather than
  presenting an unimplemented write gate as an operator procedure, and identifies provider
  source-history loss as terminal and unrecoverable in v1. Sensitive-data disclosure
  response fences publication, revokes consumer access, and requires recorded platform
  purge evidence for destructive retirement of the affected topic generation; it never
  treats compaction or corrective republication as purge and leaves CDC unavailable.

## Out of Scope

- A production cross-replica/external-writer admission gate or transaction drain.
- Exact baseline-replacing repair or contract cutover after first-write admission.
- Recovery from provider source-history loss, including offset reset, same-topic resnapshot,
  or new-generation/topic/consumer-namespace cutover.
- Possibly published cache-ahead new-generation/topic/consumer-namespace recovery.

Anything excluded or deferred by the authoritative design is outside this epic unless a
new decision record changes that design.
