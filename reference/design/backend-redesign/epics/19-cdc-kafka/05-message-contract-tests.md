---
jira: DMS-1324
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add Kafka Message and Source-Routing Contract Tests

## Design References

- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Connector transformation**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation
- **Contract-to-evidence traceability**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability

The referenced design sections own the normative contracts. This story owns the executable
conformance scenarios and pass evidence without duplicating the contract text.

## Outcome

Add fast serialized-record, provider, broker-backed, and consumer-conformance tests for
the v1 stream.

## Dependencies

- Depends on the 19-03 transform artifact and the 19-01 provider source setup.
- Broker-backed readiness cases also consume 19-00 and 19-04.
- Representative materialized records are supplied by 18-02.

## Implementation Scope

- Add canonical PostgreSQL and SQL Server source-record fixtures.
- Add transform and serialized-record conformance suites, including the exact fixed UTF-8
  progress-key bytes produced by `StringConverter`.
- Add provider key/routing/ordering and broker retry/failure scenarios.
- Prove `DocumentProjectionWork` activity emits neither public nor progress records and
  that an unexpected work-table transform input fails the fixture.
- Add record-size and idle-source readiness fixtures against the real connector.
- Add initial-admission fixtures that require projection caught-up, provider barrier, and
  a second caught-up observation in order.
- Add the reference consumer-continuity fixture assigned to this story by the design
  traceability table.

## Resolved Message Contract Test Scope and Integration Contract

This section records the DMS-1324 implementation resolution for executable message,
routing, readiness, and consumer-contract evidence. The referenced design documents remain
normative for source mapping, message shape, topic policy, readiness, and recovery. If an
implementation needs to change one of those contracts, update the owning design document
first and keep this story as the evidence owner.

### Test Ownership and Handoffs

- DMS-1324 owns DMS-side contract fixtures and evidence in the CDC backend test area. Pure
  fixture canonicalization, expected-envelope assembly, reference-consumer behavior, and
  traceability tests belong in
  `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit`. Docker, provider,
  pinned-image, Kafka Connect, and broker-backed evidence belongs in
  `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration`.
- Reuse and, if necessary, generalize the existing
  `CdcConnectorTemplatePinnedImageFixture` from DMS-1321 for Kafka Connect, broker, provider,
  registration, offset, class-loading, and restart mechanics. Do not create a second
  connector-template renderer, provider-setup path, binding-name generator, or bootstrap
  controller inside these tests.
- The runnable `DocumentState`, `DocumentStateJsonConverter`, and
  `KafkaMurmur2V1Partitioner` artifacts are supplied by DMS-1322's qualified
  Ed-Fi Kafka Connect image. DMS-1324 may add test-only runners or harness code to invoke
  those published classes, but it does not change the transform implementation or add a
  parallel transform in .NET.
- DMS-1324 consumes provider setup evidence from DMS-1320 and rendered connector
  configurations from DMS-1321. It may register a rendered connector directly for focused
  broker-backed assertions, but DMS-1323 owns topic/ACL/offset provisioning orchestration,
  lifecycle commands, and the full initial-enable workflow.
- DMS-1325 owns API-driven E2E scenarios. DMS-1324 fixtures may insert provider rows or
  update the heartbeat singleton directly to exercise source-record and broker behavior;
  they should not prove ordinary API write, projector, lifecycle, or teardown workflows.

### Shared Fixture Inputs

- Treat `src/dms/backend/Fixtures/document-cache/materialized-documents/<case>/` as the
  source of truth for representative materialized cache rows and public document bodies.
  DMS-1324 should load `expected-cache-row.json`, `expected-stream-etag.json`, and
  `expected-public-cdc-document.json` instead of copying those JSON bodies into CDC tests.
- Build the expected Kafka upsert envelope in the message-contract tests from the cache-row
  metadata plus the expected public document body. The fixture's
  `expected-public-cdc-document.json` is the `document` body after `_etag` injection, not a
  complete Kafka envelope. Tests should require envelope `contractVersion`, `documentUuid`,
  `projectName`, `resourceName`, `resourceVersion`, `contentVersion`, and whole-second
  `lastModifiedAt` normalized from the cache row to agree with the embedded document fields.
- Use the existing ordinary link-bearing resource, descriptor/no-link, extension or nested
  collection, and property-absence fixtures as the required positive cases. Add a separate
  synthetic record-size boundary fixture only for below/above `maxRecordBytes` enforcement;
  do not treat it as a materializer golden or proof of the largest valid schema document.
- Add provider raw-record fixture files derived from the shared cache rows under one
  language-neutral CDC fixture root, for example
  `src/dms/backend/Fixtures/cdc/message-contract/<case>/`. These files are fixture inputs
  for test runners, not another public message contract. They should describe provider,
  source topic, source partition/offset, operation, key shape, value shape, headers, and
  timestamp using JSON objects that can be consumed from .NET and Java. DMS-side fixture
  parsing and expected-envelope assembly should use `System.Text.Json`.
- Keep PostgreSQL and SQL Server source-record fixtures provider-specific where Debezium
  shapes differ: UUID key logical type, `DocumentJson` logical type/unavailable marker,
  temporal logical type, SQL Server delete `before` availability, heartbeat source
  partition, and SQL Server database name. Keep expected public message assertions
  provider-neutral after transformation.

### Serialized-Record Contract Evidence

- The fast serialized-record suite should execute the published transform, key converter,
  value converter, and partitioner against fixture records without requiring a full API E2E
  run. It should assert the transformed topic, serialized key bytes, serialized value bytes
  or Kafka null, headers, record timestamp, and partition choice where the partition count
  is fixed by the binding fixture.
- Public upsert assertions should parse the emitted UTF-8 bytes as JSON and compare the
  complete semantic object, including exact JSON number types, property absence, `_etag`
  placement, and absence of Kafka Connect `schema`/`payload` wrappers. Do not make object
  property order a DMS-1324 contract. Separately prove that
  `DocumentStateJsonConverter` passes the transform's named logical-byte public value
  through byte-for-byte.
- Public tombstone assertions should prove that an authoritative `dms.Document` delete
  emits the lowercase UUID string key and Kafka record-level null value, while
  `dms.DocumentCache` delete/truncate and non-delete `dms.Document` operations emit no
  public record.
- Progress assertions should cover both structured heartbeat source keys and null native
  heartbeat source keys. They must prove the output Kafka Connect key schema/value is the
  fixed string progress key and that `StringConverter` produces the exact UTF-8 bytes for
  `cdc-progress`, with no JSON quoting, schema wrapper, provider key pass-through, or null
  key.
- Work-table exclusion needs both sides of the boundary: provider/broker fixtures prove
  `dms.DocumentProjectionWork` activity produces no captured public or progress record, and
  an explicitly injected `DocumentProjectionWork` source-record fixture proves the transform
  fails closed if provider capture or include-list validation is misconfigured.
- Failure fixtures should assert stable failure categories and bounded sanitized metadata
  for malformed retained records. They must not assert provider exception text, log full
  `DocumentJson`, public values, credentials, tenant names, connection strings, or unbounded
  document metadata.

### Provider and Broker-Backed Evidence

- Broker-backed tests use the generated DMS-1321 connector config, the DMS-1320 provider
  setup result, and the qualified pinned image. They should write directly to the minimal
  provider source tables or heartbeat singleton needed for the scenario, then verify records
  by consuming Kafka topics and reading Kafka Connect committed source offsets.
- Provider key/routing tests should prove that PostgreSQL and SQL Server publish
  `DocumentCache` upserts, `Document` deletes, and heartbeat progress to the derived binding
  topics with the expected serialized keys, one routed partition per key, no work-table
  records, and no Debezium automatic second tombstone.
- Ordering and retry evidence should use one document key and one connector task. A
  representative upsert followed by canonical delete must arrive on the same public-topic
  partition and eventually converge to the tombstone after a restart or retried produce
  path. This story does not need exhaustive API mutation ordering; DMS-1325 owns those E2E
  flows.
- Progress acknowledgement evidence should use heartbeat records because an idle source may
  have no public document records. Capture a provider barrier after a caught-up projection
  observation, induce one progress-topic produce failure or broker unavailability before the
  heartbeat is acknowledged, verify the committed Connect source offset does not cross the
  barrier, then allow the keyed progress record to publish and verify the offset crosses the
  barrier.
- Record-size evidence should run through the real transform, converters, partitioner, and
  producer with compression disabled and an intentionally small `maxRecordBytes` policy. The
  below-budget fixture publishes, the above-budget fixture fails the connector task before a
  partial public record appears, and combined-readiness classification remains false until
  the failing condition is corrected.
- Idle-source readiness evidence should prove that a retained heartbeat routes to the
  progress topic and advances the committed provider source offset through the normal
  producer acknowledgement path. Dropped recognized source operations are not readiness
  evidence and should not be used to cross the provider barrier.

### Initial Admission and Consumer Conformance

- Initial-admission fixtures in this story are contract fixtures, not bootstrap workflow
  tests. Use fake or focused observations to prove the evaluator requires, in order:
  current projection caught-up status for the bound source, a provider barrier captured
  after that observation, committed connector source offset at or beyond the barrier, a
  second caught-up observation for the same source fingerprint, and acceptable connector
  lag. Missing, stale, out-of-order, source-mismatched, snapshot, malformed, or multiple
  source-partition observations fail closed.
- Add one reference consumer harness for public contract conformance. It is test-only and
  must not become a supported DMS runtime consumer. The harness should model partition
  assignment, earliest-offset bootstrap, end-offset barriers, durable state writes, a
  controllable clock, and checkpoint loss/corruption.
- Consumer fixtures should prove bootstrap does not advertise valid state until every
  partition barrier is durably applied within the 24-hour deadline; after bootstrap, proof
  must be renewed at least once per 24-hour interval, including idle partitions with
  unchanged end offsets. Missed renewal, uncertain checkpoint, or unexpected partition
  assignment discards local state and restarts from earliest offsets rather than resuming
  incrementally.
- Consumer state application should prove the v1 ordering rules for non-null upserts and
  deletes: higher `contentVersion` replaces, lower and equal non-null versions are ignored
  as stale/duplicate state, byte-different equal-version records are producer contract
  violations rather than corrections, and tombstones remove local state without requiring a
  delete envelope.
- Broker-backed consumer bootstrap evidence may use a small compacted topic with controlled
  partitions and end offsets. It does not need to capacity-test a deployment's largest
  retained topic log; that remains an operator/consumer production qualification
  responsibility under the public contract.

### Traceability and CI Boundary

- Maintain one story-owned traceability manifest or equivalent checked-in mapping from
  stable test identifiers to the applicable `CDC-INV-*` IDs assigned to DMS-1324:
  `CDC-INV-02`, `CDC-INV-06`, `CDC-INV-07`, `CDC-INV-08`, `CDC-INV-09`, `CDC-INV-10`,
  `CDC-INV-13`, and `CDC-INV-14`. Add a test that fails when a DMS-1324 scenario lacks a
  mapping or maps to an unassigned contract ID.
- Mark deterministic unit suites with a dedicated `CdcMessageContract` category. Mark
  Docker/pinned-image suites with `DatabaseIntegration`, `CdcMessageContract`, the relevant
  provider category, and a focused Kafka/Connect category so normal PR CI can run fast
  deterministic evidence while connector qualification lanes run the broker-backed matrix
  with an explicit image digest.
- When a broker-backed suite cannot run because Docker, Kafka, SQL Server 2025, or the
  qualified Ed-Fi Kafka Connect image is unavailable, the skip/failure reason must identify
  the missing prerequisite and sanitized artifact names. Do not silently downgrade the story
  to fixture-only evidence.

## Acceptance Evidence

- Story-owned traceability maps each test identifier to the applicable `CDC-INV-*`
  contract ID.
- Provider and serialized-record suites cover every source and output category assigned to
  this story by the design traceability table.
- Progress-key suites cover structured and null source keys and prove that the resulting
  non-null string key publishes successfully to the compacted progress topic.
- Broker-backed suites cover the delivery, failure, sizing, and progress evidence assigned
  to this story, including committed source-offset advancement only after the keyed
  progress record is acknowledged.
- Consumer-conformance fixtures cover bootstrap and continuity behavior from the public
  contract.

## Not Assigned to This Story

- Full API-driven scenarios are assigned to 19-06.
- Projector completeness and Kafka ACL provisioning are assigned to E18 and 19-04.
