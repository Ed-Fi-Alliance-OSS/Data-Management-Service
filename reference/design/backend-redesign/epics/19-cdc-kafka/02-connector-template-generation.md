---
jira: DMS-1321
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1232
---

# Story: Generate PostgreSQL and SQL Server Connector Templates

## Design References

- **Connector transformation**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#connector-transformation
- **Connector topology and provider setup**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup
- **Provider source-position barrier**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#provider-source-position-barrier
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity
- **Pinned connector runtime**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#pinned-connector-runtime

The referenced design sections define connector inputs, generated configuration, image
qualification, and lifecycle constraints. This story is only the work package for
implementing them.

## Outcome

Generate and validate provider connector configurations for the deployment binding and
the published `DocumentState` transform.

## Dependencies

- Depends on 19-01 for provider setup and 19-03 for the published transform artifact.
- Template code and rendering tests may proceed before the transform image is available.

## Implementation Scope

- Add typed connector-template inputs and validation.
- Add PostgreSQL and SQL Server configuration renderers.
- Render exact include lists for `Document`, `DocumentCache`, and heartbeat sources and
  reject any connector configuration that includes `DocumentProjectionWork`.
- Integrate binding-derived identity, provider setup, Kafka policy, transform, heartbeat,
  metrics, and source-offset settings owned by the design.
- Generate and validate the exact key converter and Debezium delete-tombstone settings
  required by the public document and progress key contracts:
  `key.converter=org.apache.kafka.connect.storage.StringConverter` and
  `tombstones.on.delete=false`.
- Generate and validate the exact value-converter settings required for public document
  state publication:
  `value.converter=org.edfi.kafka.connect.converters.DocumentStateJsonConverter`,
  `value.converter.schemas.enable=false`, and
  `value.converter.decimal.format=NUMERIC`.
- Emit and validate the fixed Debezium topic naming settings required by `DocumentState`
  native-heartbeat classification for PostgreSQL and SQL Server:
  `topic.delimiter=.`,
  `topic.naming.strategy=io.debezium.schema.SchemaTopicNamingStrategy`,
  `topic.heartbeat.prefix=__debezium-heartbeat`, and an unset or empty
  `topic.heartbeat.name`.
- Add pinned-image loading, rendering, restart, and provider smoke fixtures.

## Resolved Connector Template Scope and Integration Contract

This section records the DMS-1321 implementation resolution for connector-template
rendering, validation, and sibling-story integration. The owning design documents remain
normative for source selection, message shape, readiness, continuity, and operations; this
story owns the typed renderer and evidence that the generated connector configuration
implements those contracts.

### Boundary and Handoffs

- DMS-1321 delivers a provider-neutral template service plus PostgreSQL and SQL Server
  renderers. It may produce the flat Kafka Connect config map and a registration payload
  shape for callers, but it does not call Kafka Connect REST, create or delete Kafka
  topics/ACLs/offsets, mutate CDC binding state, create provider CDC artifacts, or repair
  source history. Those orchestration operations remain in 19-04 and 19-07.
- The renderer consumes the immutable binding and binding-derived names from 19-00. Use
  `connectorName` as both the Kafka Connect connector name and Debezium `topic.prefix`.
  The public topic comes from the binding, the progress topic is always
  `topicName + ".cdc-progress"`, and the SQL Server schema-history topic is always
  `topicName + ".schema-history"`. None of those values is operator-configurable in the
  template request.
- The renderer consumes observed provider metadata from 19-01's `CdcProviderSetupResult`,
  not a second provider-inspection implementation. It requires a successful
  `CreatedOrMatched` or `ExactMatch` result for the same provider and bound physical source
  fingerprint, then uses that result's source-table inventory, artifact names, expected
  `DocumentUuid` message-key columns, and generated heartbeat action query.
- The renderer consumes the transform and converter class names published by 19-03 as fixed
  contract values. It does not split behavior into a stock SMT chain, use the completed
  generic `ExpandJson` transform, or expose a source/table/value mapping language.
- The renderer consumes deployment/runtime policy inputs owned by 19-04: Kafka bootstrap
  servers, connector database endpoint/security values, Kafka client security values,
  `maxRecordBytes`, optional `producerBufferBytes`, heartbeat interval, and SQL Server
  poll interval. These are inputs to a connector config, not binding fields.

### Typed Request, Output, and Override Rules

- Add an explicit typed request and result model. The request separates binding identity,
  provider setup result, operational policy, provider connection properties, Kafka security
  properties, and artifact output options. Do not accept raw connector JSON as the source of
  truth and do not merge arbitrary operator property bags into the generated config.
- The rendered connector config is a deterministic flat string map. Snapshot artifacts and
  tests use a stable key order. A caller may wrap that map with the binding connector name
  for Kafka Connect REST registration, but 19-04 owns posting it and reading it back.
- Use an allow-list for deployment-supplied connection and security fragments. Optional
  fragments may supply provider endpoint properties, externalized credential references,
  TLS/SASL/security protocol settings, and Debezium/Kafka security client settings needed by
  the pinned runtime. They must not set or override any reserved contract key or key prefix,
  including connector class, task count, source/table include lists, message keys,
  transforms, converters, producer overrides, heartbeat settings, topic naming, schema
  history, error tolerance, or snapshot mode.
- Reject duplicate, missing, or conflicting values before rendering. Treat a duplicate
  reserved key as invalid even when it repeats the generated value; otherwise later config
  serialization or REST read-back could hide which source supplied the effective value.
- Connector configs necessarily contain endpoint and credential references that Kafka
  Connect needs. Diagnostics, manifests, snapshots, logs, and test failure messages must
  redact raw secrets, connection strings, document payloads, tenant display names, and
  unsanitized physical identifiers. Tests should include sentinel secret values to prove
  they do not leak through the renderer or validator.
- Emit no `topic.creation.*`, dead-letter queue, or connector-managed topic policy. Topic,
  ACL, offset-store, and SQL Server schema-history-topic provisioning and validation remain
  deployment/controller work in 19-04.

### Common Generated Connector Contract

- Every connector renders the Debezium connector class for exactly one provider, sets
  `tasks.max=1`, and configures exactly one bound physical database. Multi-database,
  cross-instance, topic-per-resource, or shared-topic rendering is not supported.
- Every connector sets the transform and converter contract exactly:

  ```properties
  transforms=documentState
  transforms.documentState.type=org.edfi.kafka.connect.transforms.DocumentState
  transforms.documentState.provider=<postgresql|sqlserver>
  transforms.documentState.target.topic=<binding topicName>
  transforms.documentState.progress.topic=<binding topicName>.cdc-progress
  key.converter=org.apache.kafka.connect.storage.StringConverter
  value.converter=org.edfi.kafka.connect.converters.DocumentStateJsonConverter
  value.converter.schemas.enable=false
  value.converter.decimal.format=NUMERIC
  tombstones.on.delete=false
  ```

- Every connector sets `errors.tolerance=none` and emits no dead-letter queue properties.
  Expected dropped records are handled only by `DocumentState` returning `null` for
  recognized excluded operations.
- Every connector sets the source-producer durability and sizing overrides from the design:

  ```properties
  producer.override.enable.idempotence=true
  producer.override.acks=all
  producer.override.retries=2147483647
  producer.override.max.in.flight.requests.per.connection=5
  producer.override.max.request.size=<maxRecordBytes>
  producer.override.buffer.memory=<producerBufferBytes>
  producer.override.compression.type=none
  ```

  `producerBufferBytes` defaults to the greater of `33554432` and `maxRecordBytes`. The
  renderer maps the binding's `kafka-murmur2-v1` `partitionerAlgorithm` token to the
  compatible pinned-image producer partitioner configuration; operators do not provide a
  partitioner class or algorithm.
- Every connector sets positive heartbeat settings. `heartbeat.interval.ms` defaults to
  `5000` when the caller does not supply another positive value. `heartbeat.action.query`
  is the exact generated query returned by 19-01. SQL Server rendering rejects
  `poll.interval.ms > heartbeat.interval.ms`.
- Every connector sets:

  ```properties
  topic.delimiter=.
  topic.naming.strategy=io.debezium.schema.SchemaTopicNamingStrategy
  topic.heartbeat.prefix=__debezium-heartbeat
  statistics.metrics.enabled=true
  snapshot.mode=initial
  ```

  It leaves `topic.heartbeat.name` unset or empty. Rendering and live validation reject
  any non-empty heartbeat name or a missing/conflicting delimiter, naming strategy,
  heartbeat prefix, metrics setting, or snapshot mode.

### PostgreSQL Rendering

- Render `connector.class=io.debezium.connector.postgresql.PostgresConnector`,
  `plugin.name=pgoutput`, the binding-derived `topic.prefix`, and the deployment-supplied
  single-database connection properties.
- Use the 19-01 publication and logical replication slot names. Set
  `publication.autocreate.mode=disabled` and do not request destructive slot behavior. The
  provider setup and source-history checks own publication/slot creation, exact-match
  validation, and continuity classification.
- Render the Debezium include list from the 19-01 emitted source-table inventory and include
  exactly `dms.DocumentCache`, `dms.Document`, and `dms.CdcHeartbeat`. Escape identifiers
  for the pinned Debezium PostgreSQL include-list syntax through one provider helper rather
  than hand-coding `dms.DocumentCache` casing. `dms.DocumentProjectionWork` and every other
  DMS-managed table are rejected if present in the rendered or live effective include list.
- Render `message.key.columns` only for `dms.DocumentCache` and `dms.Document`, with
  `DocumentUuid` as the only key column for each. Do not configure a custom message key for
  `dms.CdcHeartbeat`; the transform normalizes heartbeat output to the fixed progress key.
- Set `unavailable.value.placeholder=__debezium_unavailable_value` explicitly. Do not rely
  on a Debezium default for TOAST/unavailable-value handling.

### SQL Server Rendering

- Render `connector.class=io.debezium.connector.sqlserver.SqlServerConnector`, the
  binding-derived `topic.prefix`, and deployment-supplied connection properties for exactly
  one database. The renderer rejects any request that would set more than one database name
  or reuse one connector across databases.
- Render the Debezium include list from the 19-01 emitted source-table inventory and include
  exactly `dms.DocumentCache`, `dms.Document`, and `dms.CdcHeartbeat`. Do not include
  `dms.DocumentProjectionWork`, descriptors, authorization tables, tracked-change tables,
  or generated resource tables.
- Render `message.key.columns` only for `dms.DocumentCache` and `dms.Document`, with
  `DocumentUuid` as the only key column for each. SQL Server capture-instance names remain
  provider metadata used for validation and diagnostics; the template does not invent a
  second capture-name mapping.
- Set `time.precision.mode=isostring` and
  `unavailable.value.placeholder=__debezium_unavailable_value` explicitly.
- Configure the required internal schema-history store exactly:

  ```properties
  schema.history.internal.kafka.bootstrap.servers=<deployment Kafka bootstrap servers>
  schema.history.internal.kafka.topic=<binding topicName>.schema-history
  schema.history.internal.producer.enable.idempotence=true
  schema.history.internal.producer.acks=all
  schema.history.internal.producer.retries=2147483647
  schema.history.internal.producer.max.in.flight.requests.per.connection=1
  include.schema.changes=false
  ```

  Apply the connector principal's externalized Kafka security settings to both
  `schema.history.internal.producer.*` and `schema.history.internal.consumer.*` clients.
  The renderer rejects missing, duplicate, or conflicting history properties.

### Validation and Pinned-Image Evidence

- Use the same reserved-key validator for rendering tests, registration preflight, and live
  config read-back. Live validation compares the effective connector config returned by
  Kafka Connect to the expected generated values, while treating masked secret values as
  presence/redaction evidence rather than raw-string equality.
- Live validation also verifies that the provider setup result still exact-matches the
  binding, that effective include lists contain the three and only three source tables, that
  work-table capture is absent, that expected message-key columns are present, that the
  progress and SQL Server schema-history topics are the derived binding topics, and that
  the source partition shape uses the configured `topic.prefix` and, for SQL Server, the
  single configured database name.
- Pinned-image fixtures must load the Ed-Fi transform and value converter classes, validate
  both provider connector configs with the Debezium 3.6/Kafka Connect 4.3 image, prove the
  `kafka-murmur2-v1` partitioner mapping with fixed serialized-key/partition vectors, and
  execute a minimal provider smoke path that observes heartbeat/offset progress. Detailed
  public record assertions remain in 19-03, 19-05, and 19-06.
- Restart tests for this story restart an already rendered and registered connector only to
  prove the same template remains valid against retained provider and Connect state. They do
  not reset offsets, recreate slots/capture instances, resnapshot an admitted database, or
  clear source-history incident state.

## Acceptance Evidence

- Rendering tests cover every generated and rejected configuration category in the design
  references.
- Typed request/result tests prove that renderer inputs come from the binding, the 19-01
  provider result, and deployment policy instead of tenant names, connection strings,
  raw connector JSON, or duplicated provider inspection.
- Rendering and live-validation tests require the exact `StringConverter` key-converter
  path and `tombstones.on.delete=false` for PostgreSQL and SQL Server connector templates,
  and reject missing, duplicate, or conflicting values.
- Rendering and live-validation tests require the exact `DocumentStateJsonConverter`
  value-converter path and the `schemas.enable=false` and `decimal.format=NUMERIC`
  delegate settings, and reject missing, duplicate, or conflicting converter properties.
- Rendering and live-validation tests reject missing or conflicting `topic.delimiter`,
  `topic.naming.strategy`, or `topic.heartbeat.prefix` values and reject any non-empty
  `topic.heartbeat.name`.
- Provider-specific rendering tests cover PostgreSQL slot/publication use,
  `publication.autocreate.mode=disabled`, SQL Server schema-history properties,
  `time.precision.mode=isostring`, explicit unavailable-value placeholders, exact include
  lists, exact message-key columns, and work-table exclusion.
- Security/redaction tests prove rendered artifacts, diagnostics, and validation failures
  do not leak credentials, raw connection strings, document payloads, tenant display names,
  or unsanitized physical identifiers.
- Live connector validation confirms the work table is absent from effective capture.
- Pinned-image tests cover transform loading, producer/partition behavior, heartbeat and
  offset visibility, and provider restart integration.
- SQL Server image coverage includes the qualified database/runtime combination identified
  by the integration design.

## Not Assigned to This Story

- Bootstrap command wiring and Connect REST lifecycle are assigned to 19-04.
- Detailed transform behavior and public-record assertions are assigned to 19-03 and
  19-05.

## Clarifying Questions and Answers

### Questions 1

1. Which assembly/project should own the reusable connector-template service, typed request/result contracts, reserved-key validator, and DI registration: the existing `Backend.Ddl` provider-setup area, a new CDC-focused backend project, SchemaTools, or a split between runtime library and CLI/bootstrap wiring?
2. What exact allow-list of deployment-supplied provider connection keys and Kafka security keys is in scope for PostgreSQL and SQL Server templates, including externalized credential reference syntax and the keys that must be replicated onto SQL Server schema-history producer/consumer clients?
3. What stable validation result and diagnostic contract should DMS-1321 expose for render/preflight/live-validation failures, including severity/category/code values and whether it should mirror `CdcProviderSetupResult` diagnostics?
4. What exact pinned-image producer partitioner configuration implements the binding token `kafka-murmur2-v1`, and should the renderer emit an explicit partitioner property even when the pinned Kafka client default currently matches the token?
5. Does DMS-1321 own a persisted connector-template artifact or redacted snapshot output, and if so what are the exact file names, JSON shape, registration-payload shape, and relationship to 19-04's bootstrap artifacts?
6. For live Kafka Connect config read-back, which generated or supplied properties are classified as secrets, what masked values are accepted as presence evidence, and which non-secret security settings still require raw equality?
7. What is the expected pinned-image/provider smoke-test harness boundary for this story: an in-process config validation fixture only, a local Docker Kafka Connect plus provider fixture owned here, or shared harness setup owned by 19-04 with DMS-1321 contributing reusable assertions?

### Answers 1

1. Create a new CDC-focused backend library,
   `src/dms/backend/EdFi.DataManagementService.Backend.Cdc`, and put the connector
   template service there. That project should own the shared CDC template-facing surface
   that is not provider-DDL or bootstrap orchestration: typed connector render/validation
   request and result models, reserved-key validation, redaction helpers, deterministic
   artifact serialization, PostgreSQL and SQL Server renderers, and DI registration such
   as `AddCdcConnectorTemplates`. It should consume the single binding/name contracts from
   19-00 and the single `CdcProviderSetupResult` contract from 19-01; if those types need
   to live in the new library for dependency reasons, move or factor them once rather than
   defining parallel DMS-1321 copies. `Backend.Ddl` should
   continue to own ordinary and CDC provider DDL execution, SchemaTools should only expose
   command-line/bootstrap surfaces that call the CDC services, and 19-04 should consume the
   library for orchestration rather than owning template rules itself.
2. The renderer should accept only these deployment-supplied provider connection keys.
   PostgreSQL: `database.hostname`, `database.port`, `database.user`,
   `database.password`, `database.dbname`, `database.sslmode`, `database.sslrootcert`,
   `database.sslcert`, `database.sslkey`, and `database.sslpassword`. SQL Server:
   `database.hostname`, `database.port`, `database.user`, `database.password`,
   `database.names`, `driver.encrypt`, `driver.trustServerCertificate`,
   `driver.trustStore`, `driver.trustStorePassword`, `driver.trustStoreType`, and
   `driver.hostNameInCertificate`. SQL Server
   validation must require `database.names` to contain exactly one database name. Secret
   values must be Kafka Connect config-provider references matching either
   `${env:NAME}` or `${file:/absolute/path:property}`; the renderer must not resolve them.
   The Kafka client security input should be an unprefixed map with only
   `security.protocol`, `sasl.mechanism`, `sasl.jaas.config`,
   `sasl.client.callback.handler.class`, `sasl.login.callback.handler.class`,
   `sasl.login.class`, `sasl.kerberos.service.name`, `ssl.truststore.location`,
   `ssl.truststore.password`, `ssl.truststore.type`, `ssl.truststore.certificates`,
   `ssl.keystore.location`, `ssl.keystore.password`, `ssl.key.password`,
   `ssl.keystore.type`, `ssl.keystore.certificate.chain`, `ssl.keystore.key`,
   `ssl.endpoint.identification.algorithm`, `ssl.protocol`, and
   `ssl.enabled.protocols`. The renderer writes those keys to
   `producer.override.<key>` for every connector and, for SQL Server only, also writes the
   same values to both `schema.history.internal.producer.<key>` and
   `schema.history.internal.consumer.<key>`. No caller-supplied raw connector config or
   property prefix is accepted.
3. Expose one stable `CdcConnectorTemplateResult` contract for render, preflight, and live
   read-back validation. It should include provider, connector name, public topic, progress
   topic, SQL Server schema-history topic when applicable, outcome, deterministic config
   map, optional Kafka Connect registration payload, optional redacted artifact payload,
   config hash, and `diagnostics[]`. Each diagnostic should carry stable `code`,
   `category`, `severity`, `propertyName`, safe artifact/object name, expected value,
   observed value, provider, source phase (`Render`, `Preflight`, `LiveReadBack`, or
   `PinnedImageSmoke`), and redaction classification. Use the same shape as
   `CdcProviderSetupResult` diagnostics. Required categories are
   `BindingIdentityFailure`, `ProviderSetupResultFailure`, `MissingRequiredInput`,
   `ReservedKeyViolation`, `ConnectionPropertyViolation`, `KafkaSecurityPropertyViolation`,
   `ProducerPolicyViolation`, `HeartbeatConfigurationViolation`,
   `TopicNamingConfigurationViolation`, `TransformConfigurationViolation`,
   `ConverterConfigurationViolation`, `IncludeListViolation`, `MessageKeyViolation`,
   `SchemaHistoryConfigurationViolation`, `LiveReadBackMismatch`, and
   `SecretRedactionViolation`. All contract violations are `Error`; redacted successful
   secret presence is result evidence, not a warning.
4. Emit an explicit Ed-Fi partitioner class instead of relying on Kafka client or Connect
   defaults:

   ```properties
   producer.override.partitioner.class=org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner
   ```

   That class should be packaged in the qualified Ed-Fi Kafka Connect image with the
   transform/converter runtime support and must implement the binding token exactly:
   `(KafkaMurmur2(serializedKeyBytes) & 0x7fffffff) % partitionCount` for every non-null
   key. It may delegate to Kafka's murmur2 utility inside the pinned image, but the public
   contract is the Ed-Fi class and the fixed key/partition vectors. Rendering rejects a
   missing, duplicate, or different partitioner property.
5. DMS-1321 should not persist connector template state. It should return the registration
   payload in memory as:

   ```json
   {
     "name": "<binding.connectorName>",
     "config": {
       "connector.class": "...",
       "name": "<binding.connectorName>"
     }
   }
   ```

   where `config` is the complete deterministic flat string map. When artifact output is
   requested, write one redacted deterministic snapshot named
   `cdc-connector-template.<provider>.<connectorName>.manifest.json`, where
   `connectorName` is the validated binding connector name. Including the connector name
   keeps two same-provider bindings that write to one artifact directory from overwriting
   each other's redacted manifest. Its JSON shape should be:

   ```json
   {
     "version": 1,
     "provider": "postgresql",
     "connectorName": "...",
     "publicTopicName": "...",
     "progressTopicName": "...",
     "schemaHistoryTopicName": null,
     "configSha256": "sha256:...",
     "redactedConfig": {},
     "reservedKeys": [],
     "generatedAt": "<omitted from snapshot tests>"
   }
   ```

   For SQL Server, `schemaHistoryTopicName` is the derived topic. Snapshot tests should
   omit or fix `generatedAt` so the artifact is deterministic. 19-04 may include the
   artifact hash/path in bootstrap diagnostics, but it must render the current in-memory
   payload and post that to Kafka Connect; `.bootstrap/bootstrap-manifest.json` remains
   prepared input handoff, and `.cdc-state` remains the mutable binding/incident state.
6. Treat these rendered properties as secret-bearing for live read-back:
   `database.password`, `database.sslpassword`, `database.sslkey`,
   `driver.trustStorePassword`, every emitted key ending in `.password`, and every
   emitted key ending in `sasl.jaas.config`, `ssl.keystore.key`,
   `ssl.keystore.password`, or `ssl.key.password` under `producer.override.`,
   `schema.history.internal.producer.`, or `schema.history.internal.consumer.`. If Kafka
   Connect reads back the exact externalized reference, require raw equality. If it reads
   back a masked value, accept only `[hidden]` or a non-empty all-asterisk value as
   presence/redaction evidence. Missing, empty, differently named, or unmasked different
   secret values fail validation. Non-secret connection and security properties, including
   host, port, database name, user/principal name, TLS mode, truststore/keystore location,
   certificate chain, security protocol, SASL mechanism, callback handler class, endpoint
   identification algorithm, SSL protocol, and enabled protocols require raw equality with
   the generated expected config, while diagnostics redact unsafe physical identifiers.
7. DMS-1321 owns a narrow local Docker pinned-image smoke fixture, not the 19-04 bootstrap
   controller. The fixture should start the qualified Ed-Fi Kafka Connect image, a broker,
   and the selected PostgreSQL or SQL Server provider; create only the minimal topics and
   provider objects required by the already-rendered config; register the rendered connector
   directly with Kafka Connect; prove transform/converter/partitioner class loading, config
   validation, provider heartbeat offset progress, and restart validity; then expose the
   reusable render/live-validation assertions for 19-04. It should not test binding
   reservation, topic/ACL provisioning policy, offset-store lifecycle, initial readiness
   sequencing, teardown orchestration, or Connect REST workflow behavior beyond the direct
   registration needed for the smoke path.

### Questions 2

1. Which story and repository own implementing, publishing, and versioning `org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner`: should DMS-1322's Ed-Fi-Kafka-Connect work be expanded to include it, or does DMS-1321 include a cross-repo plugin change before its pinned-image smoke tests can pass?
2. For DMS-1321 live validation, should the service accept Kafka Connect read-back config plus a fresh 19-01 `CdcProviderSetupResult` supplied by 19-04, or may it invoke the 19-01 validate-only/provider inspection service itself?
3. What test project/category and CI gating should own DMS-1321's local Docker pinned-image smoke fixtures, especially when the qualified Ed-Fi Kafka Connect image or SQL Server 2025 provider is unavailable?
4. What canonical input and serialization produce `configSha256`: the complete generated config map, the redacted config, or the Kafka Connect registration payload, and is the hash computed before or after stable key ordering/redaction?

### Answers 2

1. DMS-1322 in `Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect` should own implementing,
   publishing, and versioning `org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner`
   because the class is part of the qualified connector image alongside
   `DocumentState` and `DocumentStateJsonConverter`. DMS-1322's story should later be
   updated to include this partitioner in its plugin/image scope. DMS-1321 should not
   make a separate cross-repo plugin change; it should consume the qualified image digest,
   render the explicit `producer.override.partitioner.class` property, and validate the
   fixed key/partition vectors against that image. If the DMS-1322 image does not contain
   the class yet, DMS-1321's pinned-image smoke evidence is blocked on that published
   artifact.
2. DMS-1321 live validation should accept the Kafka Connect read-back config plus a fresh
   19-01 `CdcProviderSetupResult` supplied by 19-04. The validator should require that
   result to be successful, for the same provider, binding generation, physical-source
   fingerprint, source-table inventory, message-key inventory, and heartbeat action query
   used to render the expected config. DMS-1321 must not invoke the 19-01 validate-only
   provider inspection service itself; 19-04 owns orchestration and supplies the observed
   provider result immediately before registration, restart, or status validation.
3. Put the Docker pinned-image smoke fixtures in
   `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration`, with pure
   renderer and canonicalization tests remaining in the CDC unit test project. Mark smoke
   tests with `DatabaseIntegration`, `CdcConnectorTemplateSmoke`, and the provider-specific
   `PostgresqlIntegration` or `MssqlIntegration` category. Normal PR CI should run the unit
   and deterministic rendering tests; a separate CDC connector qualification lane should
   run `CdcConnectorTemplateSmoke` with an explicit qualified Ed-Fi Kafka Connect image
   digest and the required provider prerequisites. Local runs may `Assert.Ignore` with a
   clear diagnostic when Docker, the image digest, PostgreSQL logical replication, or SQL
   Server 2025 is unavailable, but the qualification CI lane must fail fast for those
   missing prerequisites instead of reporting skipped evidence.
4. Compute `configSha256` from the complete unredacted generated flat connector config map,
   exactly as emitted under the Kafka Connect registration payload's `config` object,
   including the connector `name` entry if the renderer emits it. Exclude the outer
   registration payload, redacted manifest fields, artifact path, and `generatedAt`.
   Canonicalize first by ordering keys with ordinal string comparison and serializing the
   string-only map as compact UTF-8 JSON with `System.Text.Json`; then compute SHA-256 and
   format it as `sha256:<lowercase-hex>`. Redaction happens only after this canonical hash
   is computed, so changing an externalized credential reference or any generated value
   changes the hash even when the manifest redacts that property.
