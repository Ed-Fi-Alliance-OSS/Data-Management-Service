# Serialized SourceRecord runner

MC-02's test-only Java adapter runs the published `DocumentState`, `StringConverter`,
`DocumentStateJsonConverter`, and `KafkaMurmur2V1Partitioner` from the configured Connect
image. It uses public Connect APIs and artifact failure accessors. It contains no
transformation, ETag, or partition algorithm. The shared CDC loader expands E18 references
before passing JSON to Java; Java uses the image's Jackson libraries to read that JSON.

Run from the repository root, after making the qualified image available to Docker:

```bash
export CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE='<qualified-image>@sha256:<64-lowercase-hex-digits>'
export CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration \
  --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractRunner'
```

Only the Connect image is required. There is one isolated, network-disabled, short-lived
container per batch, with its entrypoint overridden to run Java. No broker, provider,
API, or Connect worker starts. The digest reference used to create the container is
returned with the observations. Images are inspected locally; an unavailable image is
reported instead of implicitly pulling a replacement. Existing template qualification's
local-skip versus fail-fast policy applies to missing prerequisites.

Build and publish outputs contain the Java source and shared fixture resources; execution
resolves resources from `AppContext.BaseDirectory` and does not require the checkout.
The classpath discovery and required-class probe are shared with the existing template
fixture, as is the Docker process adapter. Execution is limited to two minutes and
cleanup uses a separate 20-second token, including cancellation and failed startup.
Temporary input and output files are deleted; a cleanup failure is explicit.

The request contains ordered `scenarios`, each with `scenarioId`, an expanded
`sourceRecord`, `transformConfig`, and positive `partitionCount`. Schema descriptors use
the shared fixture vocabulary. Null schemas allow schemaless/native heartbeat inputs.
BYTES descriptors contain base64 physical bytes. For the standard Connect Decimal
logical schema, the runner uses Connect's `Decimal.toLogical` to construct the required
`BigDecimal`; its observed value is a JSON number. This lets progress tests distinguish
`decimal.format=NUMERIC` delegation from base64 encoding without implementing a converter.
The result contains format version 1 and matching, ordered observations:

- `retained` has a `record` with topic, recursive key/value schemas, Connect key/value,
  source partition/offset, headers, Connect timestamp, partition/count, and converter bytes.
- `dropped` has no record, bytes, or failure.
- `failed` has no record and includes only failure stage, category, artifact reason, and
  bounded metadata. Only recognized provider/operation values remain readable; identities
  are redacted. Exception text, causes, stack traces, and partial output are excluded.

Each serialized byte observation is either `{"kind":"kafka-null"}` or
`{"kind":"bytes","base64":"...","length":N}`. In particular, an empty base64
string with length zero is an empty byte array, and `bnVsbA==` with length four is the
UTF-8 bytes for JSON `null`; neither is a Kafka tombstone or a dropped record.
For byte-valued transform outputs, equality and reference identity observations separately
show whether the converter returned equal bytes in a defensive copy.

Plugin stdout/stderr is suppressed; observations travel through a dedicated result file.
Prerequisite diagnostics expose fixed reasons rather than Docker/compiler output or
exception details. A missing class is distinguishable from an incompatible runtime or
classpath. Negative prerequisite tests inject secret sentinels and cover failed startup,
class loading, caller cancellation, execution timeout, and cleanup failure. The image
smoke runs both providers through upsert, tombstone, drop, failure, and native progress,
plus an actual missing-class probe. Broader message assertions belong to MC-03–MC-06.

MC-13 can opt into `measureProducerSize=true`. For public records with empty headers,
the runner additionally observes the pinned Kafka client's framing-aware
`AbstractRecords.estimateSizeInBytesUpperBound` with current magic and no compression,
and its client version. This uses the same Kafka API as the producer's local size check;
.NET does not reproduce the framing calculation. The broker suite separately exercises
the measured below/above boundary through the actual source producer. The size estimate
is not a measurement of the complete network request.
