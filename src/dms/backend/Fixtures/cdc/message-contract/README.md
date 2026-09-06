# Message-contract fixture inputs

`catalog.json` assigns stable scenario IDs and references E18 cases relative to the
backend `Fixtures` directory. Shared cache rows, opaque stream ETags, and public
bodies remain in `document-cache/materialized-documents`; do not copy them here.
The shared public fixture's `document` property contains the body, not an envelope.

Provider files describe test-only SourceRecord inputs, not the public wire contract.
`MessageContractFixtureCatalog` expands each template into a self-contained JSON
record for the Java runner. The expectation is independently assembled from
E18 metadata and its public body, never from transformed output.

Template vocabulary (format version 1):

- `{"$cacheRow":"field"}` substitutes a cache-row field with its JSON token kind intact.
  `"encoding":"json-string"` serializes the selected JSON as a Connect string, used
  for `DocumentJson`. No public document body is stored in the provider templates.
- `{"$schema":"row"}` expands a schema from the file's `schemas` dictionary. The
  expanded runner record omits that dictionary. References are depth bounded.
- Schema descriptors use uppercase Connect `type`, explicit boolean `optional`,
  optional logical `name` and integer `version`, `fields` for STRUCT, and `items`
  for ARRAY. Primitive values retain their JSON kind; BYTES uses a base64 string.
  Missing optional struct properties remain absent; explicit null remains null.
- `keySchema`/`key`, `valueSchema`/`value`, and each ordered header's `schema`/`value`
  are schema/value pairs. `sourcePartition`, `sourceOffset`, `sourceTopic`,
  `timestamp` (INT64 milliseconds or null), and `operation` describe source metadata.
- Provider source structs carry the required `io.debezium.connector.<provider>.Source`
  name. Debezium UUID/JSON/timestamp logical schemas explicitly carry version 1.
- PostgreSQL uses logical UUID/JSON/ZonedTimestamp strings and a server partition.
  SQL Server uses plain UUID/JSON strings, IsoTimestamp, and server/database partition
  identity. Both pin `__debezium_unavailable_value`. The baseline PostgreSQL delete
  supplies an available before UUID; SQL Server supplies its unavailable marker.
  These inputs retain that distinction; interpreting it belongs to the real transform.

`MessageContractFixtures.props` copies inputs and shared E18 expectations into both
unit and integration build/publish outputs. The loader first resolves `Fixtures`
from the supplied artifact directory, then supports checkout-relative resolution.
The integration project links the test helpers; no production project consumes them.
The integration project's `MessageContractRunner/README.md` describes the MC-02
image-only runner, its resource packaging, observations, and prerequisite diagnostics.

`upsertVariant` optionally names a file under this root. The loader validates the
shared E18 files first, then applies only the variant's signed `contentVersion`,
paired source/expected timestamps, and additive `documentProperties`. Additions
cannot overwrite shared document fields or reserved metadata. Expected timestamps
are explicit whole-second values checked against the cache timestamp, so rounding
across the year boundary is not accepted. Variant files are test inputs, not new
materializer goldens. The shared opaque ETag is deliberately unchanged even when a
synthetic variant changes the version or body: these scenarios test copying, not
ETag composition or materializer consistency for synthetic data.

MC-03's `Given_MessageContractUpsert` executes 42 scenarios (both providers, seven
cases, create/update/read) in two image-only runner batches. Stable runner IDs use
`MC-UPSERT-{PG|SQL}-{case}-{C|U|R}`. The four baseline case names match the catalog;
the additional cases are `EXACT-NUMBERS`, `SIGNED-INT64-MIN`, and
`FRACTIONAL-SECOND`. PostgreSQL supplies six fractional digits; SQL Server supplies
seven. Numeric data includes adjacent integers beyond IEEE-754 and INT64 precision,
exact decimal/exponent values, mixed nested arrays, Unicode strings, and absent
versus explicitly null sibling properties. Updates also supply uppercase source
UUIDs and a distinct before row to detect stale metadata or body selection.
Provider upsert templates include `ComputedAt`
distinct from `LastModifiedAt` to prove that computation metadata does not leak.

Each scenario independently asserts the complete semantic public envelope, derived
topic and unquoted UTF-8 key, exact required logical BYTES schema/version and
converter defensive copy, whole-second document time and cleared Connect metadata,
and opaque ETag placement/internal-field exclusion. Public JSON object order and
equivalent numeric spelling/scale are not golden byte contracts. Byte equality is
asserted separately only across the transform-to-converter boundary.

Run with the qualified immutable `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` configured:

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractUpsert'
```

This suite needs no broker/provider image settings or running Connect worker. It
uses `DatabaseIntegration`, `CdcMessageContract`, `CdcMessageContractSerialized`,
and the applicable `PostgresqlIntegration`/`MssqlIntegration` categories. Set
`CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true` in qualification to fail on missing
prerequisites instead of skipping.

Schema/value validation checks representability as Connect data, not transform
acceptance: a plain string under an incorrect logical schema can be structurally
valid and still be a negative transform test. Diagnostics expose fixed reason codes
without record contents or parser exception details.

`MessageContractJson` compares complete objects and ordered arrays, preserving
property absence, nulls, and token kinds. Numbers compare using significant digits
and an arbitrary-precision base-10 exponent, without double/decimal conversion.
Object ordering and equivalent number spelling/scale are not contract differences.

## Serialized routing (MC-04)

`Given_MessageContractRouting` derives provider inputs from the shared ordinary
upsert and canonical-delete fixtures. Each image-only batch runs deletes before
upserts, with a fresh transform per record. Missing `before` schema/value, explicit
null, and a before row lacking `DocumentUuid` all preserve the authoritative key.
Available matching UUIDs normalize case; conflicts fail with the artifact's stable
reason. The unavailable marker succeeds only in the pinned SQL Server delete
before field. PostgreSQL, optional/counterfeit logical before schemas, and cache
upsert keys/rows cannot use that exception.

The complete excluded-operation matrix is cache delete/truncate and canonical
create/update/read/truncate. Each runs with valid, malformed, and null keys,
proving classification drops the record before retained-key validation. A dropped
observation has neither a public/progress record nor a failure. Tombstones assert
exact `kafka-null` observations, null value/schema, empty headers, null Connect
timestamp, and the same lowercase UTF-8 UUID bytes/partition as an upsert.

`partition-vectors.json` records four UUIDs at 1, 3, 7, 10, and 17 partitions.
Expectations were calculated independently with a standalone Python implementation
of Kafka's Murmur2 (seed `0x9747b28c`, multiplier `0x5bd1e995`, little-endian words,
32-bit overflow, positive mask `0x7fffffff`, then partition-count remainder).
Unsigned hashes for TEMPLATE/MIXED/MAX/VERSIONED are respectively `2849317978`,
`1821684602`, `3935097074`, and `210376136`; this includes both sign-bit cases.
The calculation also reproduced all four existing template-probe vectors.
TEMPLATE at 10 partitions is the existing
`CdcConnectorTemplatePinnedImageFixture.AssertKafkaMurmur2PartitionerVectorsAsync`
UUID expectation (partition 0). Tests load fixed expectations; they never derive
expected partitions from the published partitioner. Every UUID/count runs through
both real upsert and delete transformation/conversion, including uppercase source
UUIDs. These synthetic identity variants retain the shared opaque ETag.

Stable runner IDs use `MC-ROUTING-{PG|SQL}-DELETE-{case}`,
`MC-ROUTING-{PG|SQL}-UPSERT-UNAVAILABLE-{KEY|ROW}`,
`MC-ROUTING-{PG|SQL}-DROP-{DOCUMENTCACHE|DOCUMENT}-{operation}-{VALID|INVALID|NULL}`,
`MC-ROUTING-{PG|SQL}-VECTOR-{vector-id}-{count}-{UPSERT|DELETE}`, and
`MC-ROUTING-{PG|SQL}-BASELINE-UPSERT`. The executable case sources enumerate the
suffixes; MC-18 owns their invariant mapping. Both provider suites use the same
categories/prerequisites as MC-03 and start no provider, broker, or Connect worker.

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractRouting'
```

## Reference consumer ordering (MC-14)

`MessageContractConsumer.cs` lives in the CDC unit test project and is linked into
its integration test project for later broker conformance scenarios. It accepts
serialized UTF-8 public key/value bytes with an explicit Kafka-null flag. The
ordering tests serialize the shared expected envelopes at runtime; this is
deterministic consumer evidence, not additional transform or broker evidence.

The harness retains the original value bytes and exact signed INT64 version for
each key. Higher versions replace, lower versions are ignored, and equal versions
are ignored while byte differences report `ProducerContractViolation`. This byte
comparison applies to duplicate delivery of one key/version, independently of the
semantic JSON comparison used for transform goldens. ETags stay opaque. A keyed
Kafka null removes state, including when no upsert was seen; replay can restore a
lower version after deletion until a replayed tombstone arrives.

`Stage`, `CompleteApply`, and `CompleteCheckpoint` independently model delivery,
durable state application, and durable partition next-offset persistence. Ignored
records and reported producer violations leave state unchanged but can complete a
delivery checkpoint; the harness reports violations without prescribing an operator
recovery policy. State contains no per-document offset or permanent delete watermark.
`Assign` and `CaptureEndOffsets` expose earliest/exclusive-end observations;
`CompleteScan` accepts actual transport scan positions across empty/compacted gaps.
End-offset capture alone never advances durable progress. The caller must supply
transport observations, not use an end barrier as proof that a scan completed.

`AdvanceTime`, `LoseCheckpoints`, `CorruptCheckpoints`, and `DiscardState` provide
controlled hooks for bootstrap/continuity coordination. The low-level MC-14 harness
does not advertise valid consumer state or implement those policies.

Stable fixture scenario properties use `MC-CONSUMER-ORDERING-<shared-case>`,
`MC-CONSUMER-ORDERING-INT64`, `MC-CONSUMER-ORDERING-DURABILITY`, and
`MC-CONSUMER-ORDERING-WIRE-BOUNDARY`. Test method names identify the individual
assertions. Correction scenarios provide ordering evidence for `CDC-INV-14`;
MC-18 will include these IDs in the story traceability manifest.

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/EdFi.DataManagementService.Backend.Cdc.Tests.Unit.csproj --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractConsumerOrdering'
```

## Reference consumer bootstrap (MC-15)

`MessageContractConsumerBootstrap.cs` coordinates the MC-14 state application
harness and is also linked into the integration project. Its bounds callback must
observe the current assignment, earliest offsets, and exclusive end offsets on
every attempt. Each attempt freezes those barriers. `StartPartitionScan` starts
the 24-hour wall-clock budget with the first partition; later partition starts,
retries, stalls, and delayed persistence share that deadline. Completion at exactly
24 hours is permitted; one tick later an unfinished attempt is discarded.

Validity requires every partition's durable application and checkpoint to reach
its captured end. `CompleteScan` consumes an independent transport position for
empty partitions or compacted gaps; capturing a barrier does not prove scanning.
No record at the exclusive end is required, and concurrent writes beyond it do not
move the barrier. Incremental consumption continues at durable next offsets once
bootstrap succeeds. Already durable writes beyond the barrier need not be replayed
within the same attempt, even if checkpoint persistence lags those later writes.

`FailBootstrap` and deadline expiry discard all documents, pending writes, and
checkpoints and observe fresh bounds for a complete scan from current earliest
offsets. Scan handles carry the attempt identity so delayed callbacks cannot apply
or checkpoint a discarded attempt. If fresh bounds are unavailable, no scan can
start using the previous observations. The next attempt gets its own budget when
its first partition starts scanning.

Stable scenario properties are `MC-CONSUMER-BOOTSTRAP-DURABILITY`,
`MC-CONSUMER-BOOTSTRAP-OFFSETS`, `MC-CONSUMER-BOOTSTRAP-DEADLINE`, and
`MC-CONSUMER-BOOTSTRAP-RECOVERY` (`CDC-INV-14`; manifest mapping belongs to MC-18).
These deterministic timelines use shared serialized envelopes and no wall-clock
sleeping. They establish bootstrap behavior, not production retained-log capacity.
Recurring validity renewal and checkpoint/assignment invalidation are covered by
MC-16 below; real Kafka transport evidence remains MC-17.

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/EdFi.DataManagementService.Backend.Cdc.Tests.Unit.csproj --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractConsumerBootstrap'
```

## Reference consumer continuity (MC-16)

The same `MessageContractConsumerBootstrap` coordinator maintains a durable proof
completion time and a separate renewal deadline after bootstrap. `BeginRenewal`
accepts freshly observed exclusive ends for the entire assignment and freezes that
vector. Capturing ends, starting/retrying scans, or ordinary incremental checkpoints
cannot extend validity. Each partition must complete a scan or durable apply through
its barrier, followed by checkpoint persistence, before the entire proof renews.
An idle partition explicitly scans its unchanged position and checkpoints it; no
new record is required. A partial renewal preserves valid state within the previous
proof's interval. Completion at exactly 24 hours is permitted; one tick later the
proof expires, even during downtime or when no renewal was started.

Start new partition scan handles after each capture. Handles include a proof
generation so old callbacks can finish incremental work without certifying a newer
proof. Apply and checkpoint completion remain separately controlled; an unflushed
write or checkpoint through a captured barrier cannot count as completion. Work
beyond an already completed barrier does not move that barrier.

`LoseCheckpoints`, `CorruptCheckpoints`, `ReportUncertainProgress`, unexpected
`ObserveAssignment` results, invalid barrier observations, and expired proofs all
use the existing full-bootstrap restart path. It revokes validity and discards all
documents, pending writes, checkpoints, and renewal evidence before observing fresh
assignment/earliest/end bounds. A failed observation cannot fall back to old bounds.
Recovery timelines reconstruct empty retained logs after tombstones have disappeared,
proving stale documents are removed by discarding state, not by waiting for another
delete. Healthy state and checkpoints can continue incrementally within the interval;
the controllable clock includes downtime, and no uncertain checkpoint is resumed.
This models durable state in the harness; it does not implement process persistence
or a supported runtime consumer.

Stable scenario properties are `MC-CONSUMER-CONTINUITY-DURABILITY`,
`MC-CONSUMER-CONTINUITY-IDLE`, `MC-CONSUMER-CONTINUITY-DEADLINE`,
`MC-CONSUMER-CONTINUITY-RECOVERY`, and `MC-CONSUMER-CONTINUITY-OBSERVATIONS`
(`CDC-INV-14`; manifest mapping remains MC-18). These tests use deterministic clocks
and serialized shared envelopes without sleeps or broker capacity claims.

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/EdFi.DataManagementService.Backend.Cdc.Tests.Unit.csproj --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractConsumerContinuity'
```

## Initial admission (MC-11)

MC-11's admission fixtures use the production `CdcInitialAdmissionEvaluator`,
`ICdcConnectorObservationMapper`, and both `ICdcProviderSourcePositionAdapter`
implementations. Fixed provisioning, binding, and healthy source-history evidence
is supplied directly; only the synchronous observation methods execute. No database,
Kafka client, Connect worker, or provisioning controller runs. The CDC unit project
references the control library to resolve these implementations through public DI.

`Given_MessageContractAdmission` varies one admission requirement from a separately
asserted admitted baseline. The sequence is first caught-up projection, barrier
capture, committed offset, barrier success, healthy source history, second caught-up
projection, and acceptable lag. Stale evidence means a prior operation or an invalid
sequence; these tests introduce no observation-age policy. A missing barrier cannot
be replaced by RUNNING status, low lag, or elapsed time. A failed task blocks admission
even when the previously observed barrier and lag remain healthy.

`Given_MessageContractAdmissionOffset` maps raw committed offset descriptors and
passes their observations through the real provider adapters into admission. It
checks PostgreSQL unsigned `lsn_proc` boundaries and SQL Server's lexicographic
`commit_lsn/change_lsn/event_serial_no` tuple against the heartbeat after-image ending
in `2`. Fixed normalized expectations include equality, dominance of each tuple
member, and positions behind the barrier. Malformed/scalar/null/snapshot offsets,
wrong or ambiguous source partitions, mismatched observation envelopes, topic end
offsets, and heartbeat values cannot stand in for committed source progress. Every
other admission step is asserted satisfied in these offset scenarios.

Stable fixture `ScenarioId` properties are
`MC-ADMISSION-{Postgresql|SqlServer}-{case}` and
`MC-ADMISSION-OFFSET-{Postgresql|SqlServer}-{case}`; their executable `Scenarios()`
sources enumerate the cases for MC-18 traceability to `CDC-INV-10`. These are
observation-contract tests; broker acknowledgement remains MC-12 evidence.

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/EdFi.DataManagementService.Backend.Cdc.Tests.Unit.csproj --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractAdmission'
```

## Provider and Kafka fixture (MC-07)

`CdcConnectorTemplatePinnedImageFixture` remains the resource owner. Its message
contract partial adds parameterized canonical/cache writes, transactional paired
writes, cache/canonical deletes, work insertion, and heartbeat advancement. Both
providers now use the full cache/canonical column inventory. PostgreSQL uses
uuid/jsonb/timestamptz; SQL Server uses uniqueidentifier/nvarchar(max)/datetime2(7).
Cache `DocumentId` is its primary key and cascading foreign key to `Document`;
cache `DocumentUuid` has no index. The fixture supplies IDs explicitly and omits
unrelated DMS tables/triggers. Provider setup creates the heartbeat and capture
artifacts and validates all source column types, ordinals and nullability against
the expanded inventory. Live catalog assertions additionally verify the cache keys.
The control integration fixture's existing PostgreSQL SQL/inventory helpers now
receive these same expanded definitions.

`CaptureKafkaBoundariesAsync` observes each partition's earliest and exclusive end
offsets. `ConsumeThroughAsync` accepts those frozen bounds (or caller-supplied start
offsets) and returns records only in `[start, end)`, plus completed scan bounds. It
uses explicit assignment, byte deserializers, and consumer positions/EOF to scan
through gaps. Empty ranges complete without requiring a record. All partitions
must finish; timeout, cancellation, unavailable bounds, or Kafka errors cannot
be interpreted as absence. Records after a frozen end are excluded. The observer
commits no consumer offsets. `MessageContractKafkaBytes.IsNull` distinguishes a
Kafka null from empty bytes and from bytes spelling JSON `null`; headers preserve
order, duplicate names and null values. Broker timestamps remain broker metadata.

A loopback-only external broker listener supports the .NET byte client; Connect
continues using its internal Docker listener. `TryReadCommittedSourceOffsetAsync`
uses the existing Connect REST offset parser and provider-specific normalization
and comparison contracts. `ReadConnectorStatusAsync` exposes bounded state names,
without task traces. `RestartRegisteredConnectorAsync` extracts the existing REST
restart operation for focused scenarios; the original smoke restart still checks
retained offsets and template read-back. Topic ends and progress values are **not**
provider positions. A negative source-routing test must establish the appropriate
committed provider barrier before capturing Kafka ends; a completed Kafka scan
alone cannot prove that Connect has processed a particular source mutation.

`MC-KAFKA-HARNESS-PG` and `MC-KAFKA-HARNESS-SQL` qualify this shared harness with the
four E18 rows, live schema/key inspection, connector status/offset/restart calls,
and a controlled three-partition byte-observation topic. That topic covers null,
empty, JSON-null and binary values, duplicate/null/binary headers, an empty
partition, frozen-end exclusion, invalid retained bounds, and cancellation. Shared
row availability permits snapshot/capture replay; provider routing and ordering
assertions remain MC-08/09. Categories are `DatabaseIntegration`,
`CdcMessageContract`, `CdcMessageContractKafka`, and the relevant provider category.

Set `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` to the qualified immutable sha256 image,
`CDC_CONNECTOR_TEMPLATE_REDPANDA_IMAGE`, and the relevant
`CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` or
`CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE`. Set
`CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true` in qualification lanes. Missing local
prerequisites retain the sanitized skip policy. Containers/networks are isolated;
cleanup attempts every resource with independent timeouts, including assertion or
cancellation failures during startup. The existing explicit keep-containers option
continues to support debugging.

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration --filter 'Category=CdcMessageContractKafka'
```

## Serialized progress (MC-05)

`Given_MessageContractProgress` derives provider source schemas, partitions, and offsets
from the existing catalog and executes one image-only batch per provider. It replaces
cache rows with heartbeat singleton descriptors; no materialized document golden is copied.
The 114 stable runner scenario IDs use `MC-PROGRESS-{PG|SQL}-` followed by:

- `RELATIONAL-{C|U|R|D|T}-{STRUCTURED|NULL|STRING|INVALID-PUBLIC-UUID}`:
  every heartbeat operation replaces the source key, preserves the complete value/schema,
  ordered duplicate and nullable headers, source partition/offset, and Connect timestamp.
- `NATIVE-{STRUCT|STRING|SCHEMALESS|NULL|TYPED-NULL|DECIMAL}-{STRUCTURED|NULL|STRING|INVALID-PUBLIC-UUID}`:
  exact native topic recognition, non-null value preservation, and native-null substitution
  with required STRING `native-heartbeat`. Decimal uses physical bytes `MDk=` (unscaled
  12345, scale 2) and independently expects numeric JSON `123.45`, proving NUMERIC delegation.
- `COLLISION-{HEARTBEAT|UPSERT|DROP}`: a relational prefix starting with
  `__debezium-heartbeat` still follows relational metadata and operation routing.
- `SERVER-{MISSING|EMPTY|NULL|NUMBER|OBJECT}` and
  `TOPIC-{EMPTY-SUFFIX|MISMATCH|EXTRA-SUFFIX|CASE|DELIMITER}`: malformed native-looking
  identities fail at the transform boundary with stable artifact reasons and no output.

Every retained progress scenario asserts the binding-derived `.cdc-progress` topic,
required STRING schema/key, literal hex bytes for `cdc-progress`, and partition zero with
one partition. Full serialized JSON comparison proves `schemas.enable=false`; progress
values do not use the public logical-byte handshake. Native and negative variants use the
runner's supported null-schema vocabulary directly, beyond the catalog's schema-backed
materialized-record validator. These tests establish serialized behavior; broker produce
acknowledgement and committed provider offsets remain the provider/broker suites' evidence.

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractProgress'
```

Only the qualified `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` is required; use
`CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true` in qualification lanes.
