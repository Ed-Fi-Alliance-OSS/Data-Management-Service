---
jira: DMS-1319
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1246
---

# Story: Add Deployment-Owned CDC Binding and Readiness

## Design References

- **Projection health and deployment-owned CDC readiness**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness
- **V1 readiness scope**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope
- **Provider source-position barrier**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#provider-source-position-barrier
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity
- **Deployment-owned physical source binding**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding

The referenced design sections define binding, readiness, continuity, and lifecycle behavior.
This story is only the work package for implementing them.

## Outcome

Add deployment-owned CDC state and status services that combine DMS projection
operational-health and caught-up observations with provider, Kafka, and connector
observations.

## Dependencies

- Consumes target and projection observations from 18-01 and 18-06.
- Integrated readiness scenarios consume the atomic projection path from 18-03 and durable
  queue processing from 18-04.
- Consumes provider artifacts from 19-01 and connector configuration/offset shapes from
  19-02.
- Supplies state and status behavior to 19-04.

## Implementation Scope

- Add CDC target input and validation models.
- Add binding and incident state abstractions plus the local state-store implementation.
- Add the shared deterministic CDC artifact-name helper with the binding model. Given the
  deployment key, opaque instance key, generation, provider, and binding record, it returns
  the provider artifact names consumed by 19-01 and the connector/topic names consumed by
  19-02/19-04. It must not derive names from tenant display names, connection strings,
  server names, database names, or connector JSON.
- Add guarded binding lifecycle operations used by bootstrap and teardown.
- Add provider source-position and source-history adapters.
- Add per-target and aggregate status evaluation with sanitized diagnostics and telemetry.
- Consume lifecycle/latch/process eligibility as projection operational health and indexed
  queue absence as projection caught-up status. Do not consume scan recency, exact-zero
  relationship counts, or process-local completeness cursors.
- Compose initial CDC admission from caught-up status, the provider heartbeat barrier, and
  a second caught-up observation for the same source while canonical write admission is
  closed. After admission, queue growth does not revoke CDC admission or normal API
  routing.

## Resolved CDC Binding and Readiness Scope

This section records the DMS-1319 implementation resolution for binding state,
deployment-owned readiness composition, provider-position adapters, and shared naming.
The linked design documents remain normative for behavior and recovery. If an
implementation detail below needs to change a design contract, update the owning design
document first and keep this story as the work package and evidence owner.

### Component Boundary

- Add one CDC control-plane model and status/classifier implementation consumed by 19-04,
  tests, and later runbooks. Do not create independent bootstrap-only DTOs, provider-only
  DTOs, and status-only DTOs with duplicate readiness logic.
- Put the shared CDC control-plane contracts and pure logic in
  `src/dms/core/EdFi.DataManagementService.Core` under
  `EdFi.DataManagementService.Core.DocumentCache.Cdc`. This includes binding, status,
  admission, retry, and incident DTOs; JSON converters; validators; artifact-name
  generation; source-position parsers and comparers; continuity classifiers; status and
  aggregate evaluators; classifier services; the state-store abstraction; and the local
  filesystem state-store implementation. Provider-specific live database adapters remain
  in the PostgreSQL and SQL Server backend assemblies. Kafka/Connect REST orchestration
  remains in 19-04.
- DMS-1319 does not add a public DMS HTTP endpoint, user-facing CLI command, Kafka Connect
  registration command, topic provisioning command, or provider CDC setup command. 19-04
  wires the local/bootstrap command surface and orchestration around the services defined
  here.
- Do not add CDC binding tables to the DMS data store. Binding and terminal incident state
  are deployment-owned control-plane state outside the canonical database. Runtime DMS
  continues to report only current projection/source observations through E18.
- Consume the stable 18-06 DocumentCache projection status contract, either through the
  in-process service when the caller is inside DMS or through its authorized JSON endpoint
  when deployment automation is out of process. Do not parse logs, infer projection state
  from process liveness, inspect `DocumentProjectionWork` directly from the control plane,
  or add a second projection-health implementation in E19.
- Treat 19-01 provider setup/validation output, 19-02 rendered/live connector validation,
  Kafka/Connect observations, and 18-06 projection status as typed observations into one
  status evaluator. Observation adapters may be faked in DMS-1319 tests; broker-backed
  command execution remains in 19-04 through 19-06.

### Target and Binding Contracts

- Keep two target shapes because they have different owners:
  - E18 projection status and administrative command contracts use the nested
    `"targetKey": { "tenantKey": "", "dataStoreId": 1 }` shape from 18-01/18-06.
  - CDC binding state uses the deployment-owned flat record from the design with
    string-valued `deploymentKey`, `tenantKey`, `dataStoreId`, `instanceKey`, and numeric
    `generation`.
- Map the default E18 tenant key `""` to binding `tenantKey: "default"` only in the CDC
  binding model. Do not copy `"default"` back into E18 target-key DTOs. Non-default tenant
  keys are the already-normalized deployment keys from target selection.
- Validate target input before binding creation:
  `deploymentKey`, `tenantKey`, `dataStoreId`, `instanceKey`, `provider`,
  `topicPrefix`, `generation`, `partitionCount`, and `partitionerAlgorithm`. Reject empty
  strings except the E18 nested target's default tenant key, nonpositive numeric values,
  unsupported provider tokens, missing or non-`kafka-murmur2-v1` partitioner tokens, and
  values that cannot be rendered into all required artifacts.
- Serialize the flat binding `dataStoreId` only as the invariant-culture decimal string of
  the positive E18 numeric `DataStoreId`, with no leading plus sign, whitespace, leading
  zero padding, or provider-specific identifier syntax. Deployment-owned CDC bindings do
  not accept arbitrary string data-store identifiers in v1.
- The immutable binding record stores only the fields defined by the design:
  deployment/target identity, provider, physical-source fingerprint, connector name,
  public topic name, partition count, partitioner algorithm, and contract version. It does
  not store `maxRecordBytes`, connection strings, credentials, connector JSON, provider
  principal names, source UUID, database/catalog/server names, or Kafka security settings.
- Persisted v1 binding JSON keeps both `version: 1` and `contractVersion: 1` to match the
  portable design shape. Immutable exact-match compares every persisted v1 binding field:
  `version`, `deploymentKey`, `tenantKey`, `dataStoreId`, `instanceKey`, `generation`,
  `provider`, `physicalSourceFingerprint`, `connectorName`, `topicName`,
  `partitionCount`, `partitionerAlgorithm`, and `contractVersion`. Missing, extra, or
  differently valued fields fail exact-match rather than being ignored or repaired.
- Use stable `System.Text.Json` DTOs with lower-camel JSON property names and lower-camel
  enum strings for every new JSON-facing contract in this story. Do not serialize numeric
  enum values or rely on implementation type names as wire values.
- Pin one lower-camel v1 contract family for the CDC status, admission,
  retry-classifier, binding-state, binding, incident, normalized observation, and proof
  models owned by DMS-1319. 19-04 owns transport and operator command wiring, but consumes
  and serializes these DTOs unchanged rather than defining a second operator-facing shape.
- The shared CDC target identity object contains `deploymentKey`, `tenantKey`,
  `dataStoreId`, `instanceKey`, `generation`, and `provider`. `cdcComponent` contains
  `state`, `category`, `observedAt`, and `message`; component `state` values are
  `satisfied`, `notSatisfied`, `unknown`, and `notApplicable`, and `category` is `none` or
  one of the blocking categories below.
- The status DTO shape is `contractVersion`, `observedAt`, `readiness`,
  `primaryBlockingCategory`, and `targets[]`; each target contains `targetIdentity`,
  `readiness`, `primaryBlockingCategory`, stable component objects named `binding`,
  `projection`, `providerSetup`, `providerBarrier`, `sourceHistory`, `kafkaPolicy`,
  `connectOffsetStore`, `connectorConfig`, `connectorRuntime`, and `lag`, plus bounded
  `diagnostics`. `readiness` values are `ready`, `notReady`, and `unknown`.
  `sourceHistory` also carries continuity values `healthy`, `unknown`, and `lost`, and
  `incidentLatched`.
- The admission DTO shape is `contractVersion`, `operationId`, `observedAt`,
  `targetIdentity`, `admissionState`, `primaryBlockingCategory`, `steps`, and
  `diagnostics`. `admissionState` values are `admitted`, `notAdmitted`, and `unknown`.
  `steps` contains `binding`, `guardedTrackingActivation`, `providerSetup`,
  `connectorAndTopicValidation`, `firstProjectionCaughtUp`, `providerBarrier`,
  `sourceHistory`, `secondProjectionCaughtUp`, and `lag`.
- The retry DTO shape is `contractVersion`, `operationId`, `observedAt`,
  `targetIdentity`, `retryClassification`, `action`, `primaryBlockingCategory`, and
  `diagnostics`. `retryClassification` values are `retryGuardedActivation`,
  `resumeProviderTopicConnectorSetup`, `rejectUnboundTracking`, `rejectBindingMismatch`,
  `rejectResettingLifecycle`, `rejectRebuildingLifecycle`, `rejectCacheAheadLatch`,
  `rejectUnexpectedRows`, and `rejectNotInitialWorkflow`. `action` values are `proceed`,
  `failClosed`, and `retireUnusedBindingAndReprovision`.
- The binding-state DTO shape is `contractVersion`, `observedAt`, `state`, `binding`,
  and `incident`. `state` values are `bindingPresent`, `bindingMissing`,
  `bindingMismatch`, and `incidentLatched`.

### Normalized Observation Contracts

- DMS-1319 owns stable lower-camel JSON DTOs for every normalized observation it evaluates,
  not the raw acquisition payloads. Pin DTOs for initial provisioning proof, initial
  eligibility, projection-status correlation, provider setup, Kafka policy, Connect
  offset-store policy, connector configuration, connector runtime, connector offset,
  connector lag, provider barrier, and source-history continuity observations.
- 19-01's stable `CdcProviderSetupResult` is the provider-setup source contract. DMS-1319
  may wrap or project it for operation/source correlation but must not redefine provider
  manifests. 19-04 owns raw Kafka, ACL, Connect REST, and operator-command wire shapes, then
  maps them into DMS-1319 observation DTOs before invoking status, admission, retry, or
  continuity logic.
- Every DMS-1319-owned normalized observation uses the common envelope:
  `contractVersion`, `operationId`, `observedAt`, `targetIdentity`, `provider`,
  `physicalSourceFingerprint`, and `diagnostics`. `contractVersion` is required and must be
  `1`; `operationId`, `targetIdentity`, and `provider` are required; `observedAt` is a
  required UTC timestamp; `physicalSourceFingerprint` is required when the source is known
  and otherwise `null`, which maps that observation to `unknown`.
- The projection-correlation observation adds `projectionObservedAt`, `e18TargetKey`,
  `correlationState`, `operationalHealthStatus`, `operationalHealthReason`,
  `caughtUpStatus`, `caughtUpReason`, `queuePresence`, and `enqueueFailureCategories`.
  `correlationState` values are `matched`, `targetMismatch`, `providerMismatch`,
  `sourceMismatch`, `unavailable`, and `invalidPayload`; E18 enum values are copied from
  the 18-06 contract.
- The provider-setup observation adds `setupMode`, `setupOutcome`,
  `artifactInventoryState`, `grantInventoryState`, `sourceInventoryState`,
  `heartbeatState`, and `providerHistoryState`. `setupMode` values are
  `initialCreateOrExactMatch` and `validateOnly`; `setupOutcome` values are `satisfied`,
  `invalid`, and `unknown`; and the state fields use `matched`, `mismatched`, `missing`,
  `unknown`, and `notApplicable`.
- The Kafka-policy observation adds `policyState`, `durabilityProfile`, `publicTopic`,
  `progressTopic`, `schemaHistoryTopic`, `publicTopicAcls`, `progressTopicAcls`,
  `schemaHistoryTopicAcls`, and `recordSizePolicy`. `policyState` values are `satisfied`,
  `invalid`, and `unknown`; PostgreSQL uses `null` for SQL Server-only schema-history
  fields.
- The Connect offset-store policy observation adds `workerKey`, `offsetStorageTopic`,
  `policyState`, `cleanupPolicy`, `replicationFactor`, `minInSyncReplicas`, and
  `aclState`.
- The connector-configuration observation adds `connectorName`, `configurationState`,
  `topicPrefix`, `taskCount`, `transformState`, `converterState`, `producerOverrideState`,
  `heartbeatState`, `sourceIncludeListState`, `offsetState`, and `schemaHistoryState`.
  `configurationState` values are `matched`, `invalid`, and `unknown`.
- The connector-runtime observation adds `connectorName`, `connectorState`, `taskCount`,
  `runningTaskCount`, `soleTaskState`, `snapshotState`, `lastErrorCategory`, and
  `lastErrorObservedAt`. Connector and task state values are `running`, `paused`, `failed`,
  `stopped`, `unassigned`, and `unknown`; `snapshotState` values are `notStarted`,
  `running`, `completed`, `notApplicable`, and `unknown`.
- The connector-lag observation adds `lagState`, `currentLagMilliseconds`,
  `thresholdMilliseconds`, `p50LagMilliseconds`, `p95LagMilliseconds`, and
  `p99LagMilliseconds`. `lagState` values are `withinThreshold`, `exceeded`, and
  `unknown`.
- Required strings are non-empty after validation. Nullable scalar fields are allowed only
  where the field is provider-inapplicable or the observation state is `unknown`.

### Binding State Store and Incident Latch

- Define one deployment-owned state-store abstraction with these guarded operations:
  create immutable binding if absent, read binding, exact-match existing binding, list
  bindings under a deployment key, latch source-history loss, import an operator-supplied
  verified binding record, and delete state only after caller-proved governed-artifact
  cleanup.
- Keep individual state-store operation results as internal strongly typed service results,
  not stable 19-04 JSON contracts. The stable JSON contracts owned by DMS-1319 are
  binding-state, incident, admission, retry, status, normalized observation, adoption proof,
  and cleanup proof DTOs. 19-04 maps internal create/read/exact-match/list/latch/import and
  delete-after-cleanup outcomes into those stable DTOs plus diagnostics.
- The create operation is atomic create-or-exact-match. If the record already exists and
  every immutable field matches, return the existing record. If any immutable field differs,
  return a binding-mismatch result and do not rewrite the record.
- The local implementation uses the design's default persistent root
  `eng/docker-compose/.cdc-state`, with `bindings/` and `incidents/` subtrees. It must be
  outside `.bootstrap/bootstrap-manifest.json`, ignored by Git, and written with
  owner-only file permissions where the platform supports them.
- Use identity encoding for local path components after validation: the path segment is
  the exact normalized `deploymentKey` and exact normalized `instanceKey` accepted by the
  shared Kafka-safe token validator. That validator rejects path separators, path traversal
  values, empty strings, unsupported characters, leading or trailing allowed separator
  characters, and consecutive allowed separator characters before any path is built.
  The local layout is stable:

  ```text
  bindings/{deploymentKey}/{instanceKey}/{generation}.json
  incidents/{deploymentKey}/{instanceKey}/{generation}.json
  ```

- Local writes use create-new semantics for absent binding and incident files, followed by
  read-back exact-match validation. Updating immutable binding files in place is not a
  supported repair. Production implementations expose the same semantics through their
  durable state backend with atomic create or compare-and-set.
- Treat malformed, unreadable, duplicate, or permission-denied local binding or incident
  files as operation-level state-store failures. A read or exact-match for the affected
  identity returns a state-store failure diagnostic with `localStateUnavailable`; status and
  retry map that to `unknown` with `statusObservationUnavailable` and fail closed. Do not
  report `bindingMismatch` unless a valid binding file was parsed and its immutable fields
  differ. Do not report `incidentLatched` unless a valid incident file was parsed for the
  exact binding identity and generation.
- Duplicate JSON properties, duplicate files for the same identity, case-colliding paths,
  symlinks, unexpected non-regular files, and permission-denied paths are local state-store
  failures, not repairable mismatches. A deployment-key list operation fails as a whole when
  any binding or incident file under that deployment key cannot be validated. Returning a
  partial list could hide a binding or terminal incident; a later operator diagnostic
  command may expose partial filesystem details, but that is not the state-store API
  consumed by status, admission, or retry logic.
- The source-history incident file is absent until terminal loss is latched. Latching is
  idempotent false-to-true for one complete binding identity and generation. Later
  validation, provider-artifact recreation, offset mutation, or a healthy-looking lag
  result never clears it.
- Persist one lower-camel incident JSON document with `contractVersion`,
  `incidentType: "sourceHistoryContinuityLost"`, `latchedAt`, `bindingIdentity`,
  `failureCategory`, and `positionMetadata`. `bindingIdentity` contains deployment key,
  tenant key, data store id, instance key, generation, provider, connector name, topic
  name, and physical-source fingerprint.
- Incident `failureCategory` values are `providerArtifactMissing`,
  `providerArtifactRecreated`, `retainedHistoryGap`, `connectOffsetMissing`,
  `connectOffsetMalformed`, `connectSourcePartitionMismatch`, `schemaHistoryMissing`,
  `schemaHistoryEmptyWithRetainedOffset`, and `schemaHistoryRequiredRecordLost`.
  `positionMetadata` contains only safe artifact names, a `connectSourcePartitionHash`,
  provider-normalized committed offset fields, provider-normalized retained-range fields,
  and nullable fields for unavailable facts. It never stores raw source partition JSON,
  database/catalog/server names, source UUIDs, connection strings, credentials, or raw
  exception text.
- Import/adoption callers must supply a lower-camel v1 `AdoptionProof` DTO with
  `contractVersion`, `operationId`, `verifiedAt`, `binding`, and
  `verificationResults[]`. `verificationResults[]` must contain exactly one result for
  each `verificationKind`: `physicalSource`, `providerArtifacts`, `connector`,
  `connectorConfig`, `kafkaTopics`, `kafkaAcls`, `connectOffsets`, and
  `sourceHistoryContinuity`. Each result has `verificationKind`, `state`, and bounded
  `evidenceSummary`; the only accepted state for import is `exactMatch`.
- Delete-after-cleanup callers must supply a lower-camel v1 `CleanupProof` DTO with
  `contractVersion`, `operationId`, `verifiedAt`, `bindingIdentity`,
  `cleanupMode: "retireBindingGeneration"`, and `governedArtifacts[]`. Each artifact has
  `artifactKind`, `artifactName`, `cleanupState`, and bounded `evidenceSummary`.
  `cleanupState` values are `deleted` and `notFound`; retained artifacts are not a
  delete-after-cleanup path.
- Required cleanup `artifactKind` values are `kafkaConnectConnector`,
  `connectSourceOffsets`, `publicTopic`, `progressTopic`, `publicTopicAcls`,
  `progressTopicAcls`, `postgresqlPublication`, `postgresqlLogicalSlot`,
  `sqlServerCdcGatingRole`, `sqlServerCaptureInstanceDocument`,
  `sqlServerCaptureInstanceDocumentCache`, `sqlServerCaptureInstanceCdcHeartbeat`,
  `schemaHistoryTopic`, and `schemaHistoryTopicAcls`, with only the provider-applicable
  artifacts required for a given binding.
- Deleting binding state is permitted only after the caller proves that the connector,
  offsets, topics, ACLs, provider capture artifacts, and provider-history artifacts
  governed by that binding were deleted in the required order or do not exist. If any
  governed artifact is retained intentionally, retain the binding state with it.
  DMS-1319 does not perform those destructive external deletes.
- The state store validates proof structure, version, binding identity, operation id,
  timestamps, and inventory coverage for the binding-derived names. It treats
  authorization, signatures, platform purge evidence, and live provider/Kafka/Connect
  verification as opaque evidence produced by 19-04 or 19-07 and does not query external
  systems itself.
- Proof-validation failure diagnostics use these lower-camel categories:
  `malformedProof`, `invalidContractVersion`, `invalidOperationId`, `invalidTimestamp`,
  `bindingIdentityMismatch`, `verificationIncomplete`, `inventoryIncomplete`,
  `unexpectedArtifact`, `duplicateArtifact`, `artifactNameMismatch`,
  `artifactNotRemoved`, and `unsafeEvidence`.
- A crash may leave an unused binding record. That is a safe retry state, not automatic
  cleanup authority. Automatic deletion of a binding record while any governed artifact may
  survive is prohibited.

### Deterministic Artifact Names

- Add one shared artifact-name helper with the binding model. It accepts
  `deploymentKey`, `topicPrefix`, `instanceKey`, `generation`, `provider`, and the
  immutable binding fields when exact-matching an existing record. It returns one complete
  inventory used by 19-01, 19-02, and 19-04.
- Render common Kafka/Connect artifacts as:
  - `connectorName = <deploymentKey>-<instanceKey>-g<generation>`;
  - `topicName = <topicPrefix>.instance.<instanceKey>-g<generation>.documents.v1`;
  - `progressTopicName = topicName + ".cdc-progress"`; and
  - for SQL Server only, `schemaHistoryTopicName = topicName + ".schema-history"`.
- Validate `deploymentKey`, `topicPrefix`, and `instanceKey` as Kafka-safe administrative
  tokens before rendering common names. They may contain only lowercase ASCII letters,
  digits, dot, underscore, and hyphen; must not start or end with a separator; and must not
  contain consecutive separators. The helper does not repair tenant display names into
  these values.
- Render provider database artifact names from the same deployment key, instance key, and
  generation after converting dot and hyphen to underscore:
  - PostgreSQL publication: `edfi_dms_<deployment>_<instance>_g<generation>_pub`;
  - PostgreSQL logical slot: `edfi_dms_<deployment>_<instance>_g<generation>_slot`;
  - SQL Server CDC gating role: `edfi_dms_<deployment>_<instance>_g<generation>_cdc_reader`;
  - SQL Server capture instances:
    `edfi_dms_<deployment>_<instance>_g<generation>_document`,
    `edfi_dms_<deployment>_<instance>_g<generation>_documentcache`, and
    `edfi_dms_<deployment>_<instance>_g<generation>_cdcheartbeat`.
- Provider artifact names use only lowercase ASCII letters, digits, and underscore and
  always start with `edfi_dms_`. PostgreSQL names must fit in 63 bytes. SQL Server capture
  instance names must fit in 100 characters, and the gating role must fit in 128
  characters. If a rendered provider name is too long, keep the longest valid prefix and
  append `_` plus the first 12 lowercase hex characters of SHA-256 over
  `<artifact-kind>\0<untruncated-name>`. Truncation is deterministic and tested; it is not
  silently different per provider.
- Hash provider truncation with these literal artifact-kind strings:
  `postgresql-publication`, `postgresql-logical-slot`, `sqlserver-cdc-gating-role`,
  `sqlserver-capture-instance-document`, `sqlserver-capture-instance-documentcache`, and
  `sqlserver-capture-instance-cdcheartbeat`.
- Kafka and Connect artifact names are length-limited but never truncated. Validate the
  final rendered `connectorName`, `topicName`, `progressTopicName`, and
  `schemaHistoryTopicName` as ASCII names no longer than 249 bytes/characters, after
  appending `.cdc-progress` or `.schema-history`. If any rendered name exceeds the limit,
  binding input validation fails before state is written or artifacts are created.
  Provider database artifacts remain the only names with deterministic truncation.
- After binding creation, the persisted `topicName` is the source of truth for recovering
  `topicPrefix`. Reconstruct the prefix by stripping the deterministic suffix
  `.instance.<instanceKey>-g<generation>.documents.v1`, validate the recovered prefix with
  the same Kafka-safe token rules, and recompute the full artifact inventory from the
  binding. Retry, status, adoption, and continuity checks require the live connector
  `topic.prefix`, source partition, public topic, progress topic, and SQL Server
  schema-history topic to match that reconstructed inventory. A mismatch is not repaired
  by reading deployment config.
- Name generation never reads tenant display names, connection strings, server names,
  database/catalog names, connector JSON, Kafka broker addresses, principal names, or
  current provider metadata. Existing artifacts whose names differ from this inventory are
  mismatches unless an explicit adoption workflow supplies and verifies a complete binding
  record.

### Status Composition and Readiness Results

- Add one per-target status evaluator and one aggregate evaluator. The per-target result
  has stable component objects for binding, projection, provider setup, provider barrier,
  source-history continuity, Kafka topic/ACL policy, shared Connect offset store,
  connector configuration, connector runtime, lag, and diagnostics. The aggregate result
  is a deterministic reduction over the explicit target results; it does not hide peer
  target failures.
- Use three top-level readiness states: `ready`, `notReady`, and `unknown`. `unknown` is
  fail-closed for admission and connector start/resume, but is not terminal. Terminal
  source-history loss is represented by component state and the incident latch, with the
  top-level target remaining `notReady`.
- Select one primary blocking category by this deterministic per-target precedence:
  `bindingMissing`, `bindingMismatch`, `sourceMismatch`, `sourceHistoryLost`,
  `projectionNonOperational`, `providerSetupInvalid`, `kafkaPolicyInvalid`,
  `connectOffsetStoreInvalid`, `connectorConfigInvalid`, `connectorNotRunning`,
  `snapshotIncomplete`, `projectionBacklog`, `providerHistoryUnknown`,
  `providerBarrierNotReached`, `lagExceeded`, then `statusObservationUnavailable`. Known
  `notReady` categories outrank `unknown`; use `unknown` only when no known `notReady`
  blocker is available. Retain additional facts in bounded diagnostics.
- Aggregate readiness is `notReady` when any target is `notReady`, otherwise `unknown`
  when any target is `unknown`, otherwise `ready`. The aggregate primary category is the
  highest-precedence category across included targets, with normalized target order as the
  tie breaker.
- Projection input is valid for CDC status only when the 18-06 target result is for the
  same normalized target, provider, and physical-source fingerprint as the binding.
  Projection operational-health failure and projection caught-up failure are separate
  categories. Queue presence makes CDC not ready only where readiness requires caught-up;
  it does not make ordinary canonical API health fail.
- Map observations to components with one rule: exact-match affirmative evidence becomes
  component `state: "satisfied"` and `category: "none"`; authoritative invalid or
  mismatched evidence becomes `state: "notSatisfied"` with the most specific blocking
  category; unavailable, stale, mismatched-operation, malformed, or out-of-order evidence
  becomes `state: "unknown"` with `category: "statusObservationUnavailable"` unless the
  evidence proves a more specific binding, source, or source-history failure.
- E18 projection maps to `projection`. A matched source with
  `operationalHealth.status: "operational"` and `caughtUp.status: "caughtUp"` is
  satisfied. Non-operational E18 status maps to `projectionNonOperational`; matched
  operational status with non-empty queue maps to `projectionBacklog` where caught-up is
  required; E18 unknown, invalid payload, operation mismatch, or stale observation maps to
  `statusObservationUnavailable`; provider/source mismatch maps to `sourceMismatch`.
- Represent E18 `enqueueFailures` as projection-component diagnostics only. Do not add a
  separate DMS-1319 component or blocking category for retained enqueue-failure events, and
  do not fold retained enqueue failures into `statusObservationUnavailable`. Authoritative
  E18 operational-health and caught-up values remain the only projection readiness inputs.
  If an enqueue artifact problem also makes E18 report non-operational status, DMS-1319
  maps that status to `projectionNonOperational`; if E18 remains operational and caught up,
  retained enqueue-failure diagnostics do not by themselves change CDC readiness.
- DMS-1320 provider setup maps to `providerSetup`: satisfied setup is satisfied; setup
  mismatches or missing required provider artifacts before admission are
  `providerSetupInvalid`; provider-history unavailability maps to source-history `unknown`
  with `providerHistoryUnknown`; and provider-history loss evidence maps through the
  source-history classifier. Kafka topic, ACL, durability, and record-size failures map to
  `kafkaPolicyInvalid`; shared offset-store policy or ACL failures map to
  `connectOffsetStoreInvalid`; connector live-configuration drift maps to
  `connectorConfigInvalid`; a missing, paused, failed, stopped, or non-sole running task
  maps to `connectorNotRunning`; incomplete snapshot evidence maps to
  `snapshotIncomplete`; connector lag over threshold maps to `lagExceeded`; provider
  barrier not yet crossed maps to `providerBarrierNotReached`; terminal continuity loss or
  a valid incident latch maps to `sourceHistoryLost`.
- Target readiness is `notReady` when any component is `notSatisfied`, otherwise `unknown`
  when any required component is `unknown`, otherwise `ready`. Select
  `primaryBlockingCategory` with the precedence listed above. Aggregate readiness remains
  the deterministic reduction over target results.
- Every typed observation consumed by DMS-1319 includes `contractVersion`, `operationId`,
  UTC `observedAt`, normalized target identity, provider, and physical-source fingerprint
  when the source is known. DMS-1319 enforces same-operation correlation rather than a
  hidden wall-clock maximum age: observations are fresh only when their `operationId`
  matches the current status/admission/retry operation and their step-specific ordering is
  valid. Initial admission may retain earlier evidence from the same operation, but still
  requires the first caught-up observation before barrier capture and the second caught-up
  observation after barrier success for the same source. Regular status evaluation accepts
  only observations gathered for the current polling operation.
- A missing `observedAt`, future `observedAt`, operation mismatch,
  target/provider/source mismatch, reused previous-poll observation, or out-of-order
  initial-admission observation makes that component `unknown` with
  `statusObservationUnavailable`, except where the mismatch is a known binding/source
  failure with a more specific blocking category.
- After initial write admission opens, combined CDC status is observational. A later
  `notReady` result does not close normal DMS API routing, and a later `ready` result does
  not certify a new exact baseline.
- The status evaluator never treats elapsed time, connector task `RUNNING`, Kafka end
  offsets, current lag, progress-topic contents, scan recency, exact-zero relationship
  counts, or process-local completeness cursors as substitutes for the provider barrier
  and second caught-up observation required by initial admission.

### Provider Barrier and Source-History Adapters

- Implement one provider source-position adapter per provider with pure parse/compare
  tests and provider integration tests. The adapter owns barrier capture and committed
  offset comparison only; it does not start connectors, reset offsets, create slots, or
  repair capture artifacts.
- The PostgreSQL adapter captures `pg_current_wal_lsn()` after the selected projection
  caught-up observation, normalizes it to an unsigned 64-bit WAL position, parses the
  committed Debezium `lsn_proc` offset for the exact source partition, and succeeds only
  when `lsn_proc >= barrierLsn`.
- The SQL Server adapter reads the heartbeat sequence after the selected projection
  caught-up observation, waits for the heartbeat capture after-image with a greater
  sequence, normalizes its `__$start_lsn` and `__$seqval`, parses committed Debezium
  `commit_lsn`, `change_lsn`, and `event_serial_no`, and succeeds only at or after the
  heartbeat after-image boundary.
- Both adapters reject missing, malformed, snapshot, null, multiple, or source-partition
  mismatched offsets. They compare decoded provider values, not formatted strings or
  locale-dependent ordering.
- DMS-1319 does not call Kafka Connect or parse the full raw offset REST response. 19-04
  owns the REST call and supplies a normalized `ConnectorOffsetObservation` containing
  `contractVersion`, `operationId`, `observedAt`, connector name, reconstructed topic
  prefix, provider, exact source-partition match result, `sourcePartitionHash`,
  snapshot/null flags, and the provider offset fields needed by the DMS-1319 parsers:
  `lsnProc` for PostgreSQL; `commitLsn`, `changeLsn`, and `eventSerialNo` for SQL Server.
  DMS-1319 parses and compares those provider fields.
- For SQL Server, 19-04 supplies the expected database name only as a sensitive,
  non-persisted match input together with the expected topic prefix. DMS-1319 may compare
  it in memory but must never place it in binding state, status JSON, incident JSON, logs,
  or metrics. The exposed hash is `sha256:` plus lowercase SHA-256 over UTF-8
  `ed-fi-dms-connect-source-partition-v1\0<provider>\0<canonical-source-partition-json>`,
  where the canonical JSON includes the raw SQL Server database value only inside the hash
  input.
- `connectSourcePartitionHash` uses one DMS-1319-owned canonical JSON encoder. The
  canonical source-partition JSON is UTF-8, minified, object-only, and contains no extra
  properties. PostgreSQL renders properties in this exact order:
  `{"server":"<topicPrefix>"}`. SQL Server renders properties in this exact order:
  `{"database":"<rawDatabaseName>","server":"<topicPrefix>"}`. Strings are escaped with
  `System.Text.Json`/`Utf8JsonWriter` default string escaping; the raw SQL Server database
  value is used only as this hash input and must not be serialized elsewhere.
- Source-partition hash conformance vectors are:
  - provider `postgresql`, canonical JSON `{"server":"edfi.dms"}`:
    `sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40`;
  - provider `sqlserver`, canonical JSON
    `{"database":"EdFi_DMS_CDC","server":"edfi.dms"}`:
    `sha256:678792175a93a7e810f3904d8d8e42e654289b147c3313a5c6d6a5c6593beab2`; and
  - provider `sqlserver`, raw database value `EdFi "DMS"\CDC`, canonical JSON
    `{"database":"EdFi \"DMS\"\\CDC","server":"edfi.dms"}`:
    `sha256:d4391b11394929abaabadbf53d9a8c3a9c420f91302573966ceeaf12b591fa2a`.
- Implement one source-history continuity adapter per provider that consumes the exact
  binding-derived artifacts and committed source offset. Its result is `healthy`,
  `unknown`, or `lost`. `unknown` covers temporary inability to prove continuity and keeps
  readiness false without latching. `lost` is a terminal incident candidate for the
  binding generation and is not cleared by later artifact recreation or snapshot.
- Keep the continuity adapters and status evaluator pure. When continuity is `lost`,
  DMS-1319 returns a `SourceHistoryIncidentCandidate` with the binding identity, failure
  category, sanitized position metadata, and observed time. 19-04 calls the DMS-1319
  state-store latch operation explicitly, stops or fences the old connector according to
  the lifecycle workflow, and then recomputes or rereads status so the durable latch is
  reflected. If the latch write fails, status stays fail-closed with a retryable diagnostic
  and must not report `ready`.
- Continuity checks use provider metadata returned by 19-01 where possible and live
  provider/Connect observations where needed. They must not infer continuity from connector
  liveness, lag, matching artifact names alone, or a newly created slot/capture instance.
- Return continuity `healthy` only when the exact committed connector offset for the
  expected source partition is present, non-snapshot, parsed successfully, every
  binding-derived provider/source artifact exact-matches, the provider retained range
  covers that committed offset, SQL Server schema history is valid when applicable, and no
  incident latch exists.
- Return continuity `unknown` for temporary provider, Kafka, or Connect query failure;
  timeout; unavailable provider metadata; unavailable offset-store evidence; unreadable
  SQL Server schema history without proof of loss; retention-policy drift without proof
  that required history was removed; or capture/cleanup job state that is stopped or failed
  while the capture artifacts exist and the retained LSN range still covers the committed
  offset. A stopped or failed SQL Server capture or cleanup job is a separate readiness
  blocker, not a continuity latch, until retained history no longer covers the committed
  offset.
- Return continuity `lost` and propose the matching incident category when authoritative
  evidence proves loss: missing PostgreSQL slot/publication, missing SQL Server capture
  instance/job, or missing required provider artifact uses `providerArtifactMissing`; a
  recreated PostgreSQL slot or SQL Server capture instance under the expected name uses
  `providerArtifactRecreated`; PostgreSQL slot invalidation, retained-WAL loss, or a
  committed `lsnProc` outside the retained slot range uses `retainedHistoryGap`; a SQL
  Server committed LSN outside the retained min/max range uses `retainedHistoryGap`; a
  successful Connect query with no expected offset uses `connectOffsetMissing`; null,
  snapshot, missing required fields, or malformed provider offset fields use
  `connectOffsetMalformed`; multiple matching partitions or an expected source-partition
  mismatch uses `connectSourcePartitionMismatch`; SQL Server schema-history topic absence
  after enablement uses `schemaHistoryMissing`; an empty SQL Server history topic when
  retained offsets exist uses `schemaHistoryEmptyWithRetainedOffset`; and proven removal
  of a required schema-history record uses `schemaHistoryRequiredRecordLost`.
- SQL Server schema-history observations include `enablementPhase` with enum values
  `beforeInitialAdmission` and `afterInitialAdmission`. 19-04 supplies it with the
  schema-history evidence, and DMS-1319 validates that it matches the current
  admission/status workflow.
- Before initial connector admission, missing, unreadable, empty, or nonconforming SQL
  Server schema history is a non-latching setup/readiness failure. After initial
  enablement, missing history, empty history when retained offsets exist, or proven
  truncation of a required history record returns the matching terminal source-history
  incident candidate and latches `sourceHistoryLost`. Temporary unreadability, ACL drift,
  retention-policy drift, or unavailable evidence without proof of lost required history is
  a non-latching `unknown`, `kafkaPolicyInvalid`, or `connectorConfigInvalid` readiness
  failure until a continuity check proves actual loss.

### Initial Admission and Retry Classification

- Add typed initial-admission and retry classifier models consumed by 19-04. The trusted
  setup controller supplies proof that it created the new physical database and has kept
  canonical write admission closed. DMS-1319 does not accept an operator assertion or an
  existing-database schema inspection as proof of v1 first-time CDC eligibility.
- The classifier receives a trusted `InitialCdcProvisioningProof` object with
  `contractVersion: 1`, `proofId`, `operationId`, normalized target identity, provider,
  setup-controller run id, `databaseCreationMode: "createdForInitialCdcProvisioning"`,
  `writeAdmissionState: "closedNeverOpened"`, and UTC `issuedAt`. DMS-1319 validates the
  structure, enum values, version, non-empty `proofId`, non-empty run id, operation id, and
  target/provider match, then still performs source resolution and empty canonical/cache/work
  checks before binding creation. It treats proof provenance as trusted input from 19-04.
- Before binding creation, the classifier rejects an unbound target when canonical,
  cache, or work tables are nonempty, or when the current source cannot be resolved. Do not
  create a binding and then discover that the database is ineligible.
- DMS-1319 consumes typed source-resolution and table-emptiness observations supplied by
  the 19-04 controller and E18/provider services. It does not own a separate pre-binding
  provider-query implementation for `dms.DataStoreIdentity` or canonical/cache/work row
  checks. It validates observation structure, target/provider/source consistency,
  freshness, and empty/nonempty classification, then applies the initial-admission decision
  before binding creation.
- The pre-binding classifier consumes one `InitialCdcEligibilityObservation` captured by
  19-04 from a single provider-consistent statement or read transaction against the
  selected physical database, immediately before classifier execution and before binding
  creation, while the trusted write-admission proof still says writes have never opened.
  That observation contains `contractVersion`, `operationId`, `observedAt`,
  `durableObservedAt`, normalized target identity, provider, physical-source fingerprint,
  setup-controller run id, `writeAdmissionProofId`,
  `consistencyScope: "singleProviderTransaction"`, lifecycle state, cache-ahead latch
  state, `canonicalRowsPresent`, `cacheRowsPresent`, `workRowsPresent`, and an opaque
  provider consistency token for diagnostics. DMS-1319 requires
  `writeAdmissionProofId == proof.proofId`, both DTO `operationId` values equal to the
  current classifier operation, and matching setup-controller run id, target, and provider.
  A mismatch rejects before binding creation as `rejectNotInitialWorkflow` with sanitized
  diagnostics. DMS-1319 also validates source match, known lifecycle/latch values, and all
  three row-presence booleans; it rejects before binding creation unless the source is
  resolved, lifecycle/latch observations are authoritative, and canonical, cache, and work
  rows are all absent.
- Binding reservation precedes guarded tracking activation and all external CDC artifacts.
  The guarded activation itself is the 18-04 command contract; DMS-1319 classifies and
  records state, but does not duplicate the provider table locks or lifecycle mutation.
- On retry before first-write admission:
  - exact binding plus lifecycle `Disabled` and clear latch retries guarded activation;
  - exact binding plus lifecycle `Tracking`, clear latch, and empty canonical/cache/work
    tables resumes provider/topic/connector setup;
  - lifecycle `Tracking` without a binding, binding mismatch, lifecycle `Resetting` or
    `Rebuilding`, a set cache-ahead latch, or unexpected pre-capture rows fail closed; and
  - a caller may retire an unused binding only through the explicit cleanup-proof path.
- Initial admission success requires, in order, binding exact-match, guarded tracking
  activation or recognized retry state, provider setup exact-match, connector/topic
  validation, first projection caught-up observation for the bound source, provider barrier
  catch-up through the committed source offset, source-history continuity `healthy`, a
  second projection caught-up observation for the same source, and acceptable lag.
- The captured provider barrier and first/second caught-up observations are operation
  evidence, not immutable binding fields and not a reusable future baseline.

### Diagnostics, Privacy, and Evidence Boundary

- Every diagnostic and telemetry event uses bounded lower-cardinality categories and
  sanitized messages. Do not log or serialize credentials, connection strings, document
  payloads, raw student data, tenant display names, source UUIDs, raw server/catalog names,
  Kafka security settings, or unsanitized provider exception text.
- Use one bounded lower-camel diagnostic item shape everywhere: `code`, `category`,
  `severity`, `component`, `observedAt`, `message`, `artifactKind`, `artifactName`,
  `expected`, `observed`, and `retryable`. `code`, `category`, `severity`, `component`,
  `observedAt`, `message`, and `retryable` are required. `artifactKind`, `artifactName`,
  `expected`, and `observed` are nullable sanitized strings.
- `severity` values are `info`, `warning`, and `error`. `component` values are `binding`,
  `projection`, `providerSetup`, `providerBarrier`, `sourceHistory`, `kafkaPolicy`,
  `connectOffsetStore`, `connectorConfig`, `connectorRuntime`, `lag`, `stateStore`,
  `proofValidation`, `observationValidation`, `admission`, and `retry`.
- Cap every `diagnostics[]` array at 16 items. Cap `message` at 512 characters and all
  other diagnostic strings at 256 characters after sanitization. Order diagnostics by the
  component precedence used for status components, then by `observedAt`, then by `code`,
  `artifactKind`, and `artifactName`. When truncation is needed, keep the highest-priority
  diagnostics and append a final `diagnosticsTruncated` item whose `observed` value is the
  omitted count.
- Diagnostic `category` values are the blocking categories from the status contract plus
  `malformedProof`, `invalidContractVersion`, `invalidOperationId`, `invalidTimestamp`,
  `bindingIdentityMismatch`, `verificationIncomplete`, `inventoryIncomplete`,
  `unexpectedArtifact`, `duplicateArtifact`, `artifactNameMismatch`, `artifactNotRemoved`,
  `unsafeEvidence`, `malformedObservation`, `staleObservation`, `operationMismatch`,
  `targetMismatch`, `providerMismatch`, `futureObservedAt`, `missingRequiredField`,
  `invalidEnumValue`, `localStateUnavailable`, and `diagnosticsTruncated`. Use specific
  `code` values from the same lower-camel vocabulary when no narrower implementation code
  is needed.
- Metrics may label provider, readiness state, component category, safe deployment key,
  opaque instance key, generation, and outcome. Do not label metrics with `DocumentUuid`,
  `DocumentId`, connector error text, topic consumer group supplied by an external
  consumer, or unbounded resource names.
- DMS-1319 owns unit and focused integration evidence for state-store CAS behavior,
  immutable JSON serialization, artifact-name conformance and truncation, physical-source
  fingerprint conformance vectors, provider barrier parsing/comparison, source-history
  classification and latching, initial-enable retry classification, status aggregation,
  and privacy sanitization.
- Broker-backed topic provisioning, ACL execution, Connect REST registration/start/stop,
  provider CDC object creation, connector-template rendering, public message behavior, and
  API-driven E2E evidence remain assigned to the sibling stories listed below.

## Acceptance Evidence

- State-store and lifecycle tests cover the binding and incident transitions in the
  referenced design sections.
- JSON serialization tests pin the lower-camel v1 status, admission, retry-classifier,
  binding-state, persisted binding, incident, normalized observation, adoption proof,
  cleanup proof, and diagnostic DTO shapes, including enum string values, required
  `contractVersion` fields, and exact-match behavior for missing, extra, or differently
  valued binding fields.
- State-store tests cover local path validation and lookup for valid tokens, invalid
  path-like tokens, deterministic lookup, no cross-key collision, adoption proof
  validation, cleanup proof validation, malformed/unreadable/duplicate local-state
  failures, all-or-fail deployment-key listing, and delete rejection when any governed
  artifact is retained.
- Artifact-name helper tests cover deterministic output, provider-specific identifier
  limits/sanitization, generation isolation, and the complete name inventory consumed by
  19-01, 19-02, and 19-04.
- Artifact-name helper tests include conformance vectors for provider names exactly at
  each limit and one or more characters over each limit, the literal provider hash
  artifact-kind strings, Kafka/Connect 249-character failure behavior, and topic-prefix
  reconstruction from persisted `topicName`.
- PostgreSQL and SQL Server adapter tests cover position, continuity, and failure
  classifications. Use live PostgreSQL only for `pg_current_wal_lsn()` capture,
  logical slot/publication metadata reads, retained-WAL range interpretation, and
  provider-specific continuity observations that cannot be represented without a server.
  Use live SQL Server only for heartbeat CDC after-image barrier capture, 10-byte LSN
  normalization from provider rows, capture-instance/job metadata reads, retained min/max
  LSN interpretation, and provider-specific continuity observations.
- Offset observation tests cover normalized 19-04 offset inputs, PostgreSQL `lsnProc`, SQL
  Server `commitLsn`/`changeLsn`/`eventSerialNo`, source-partition hash generation, and
  exclusion of the sensitive SQL Server database value from serialized state,
  diagnostics, logs, and metrics.
- Pure tests with fake 19-01/19-04 observations cover Connect offset parsing,
  source-partition hash conformance, blocking-category precedence, source-history
  `healthy`/`unknown`/`lost` classification matrices, incident-candidate creation, latch
  behavior, status aggregation, and retry/admission classification. Broker-backed Connect
  REST, topic/ACL, schema-history topic, and full orchestration evidence belongs to 19-04
  through 19-06.
- Status tests cover the complete design-owned readiness input matrix and aggregation.
- Status tests cover the deterministic blocking-category precedence, aggregate
  `ready`/`notReady`/`unknown` reduction, same-operation observation freshness,
  out-of-order initial-admission observations, and stale/reused observation rejection as
  `statusObservationUnavailable`.
- Status tests distinguish projection operational failure, projection backlog, enqueue
  failure, connector failure, continuity failure, and ordinary canonical API health.
- Initial-admission and retry-classifier tests cover `InitialCdcProvisioningProof`,
  `InitialCdcEligibilityObservation`, single-provider-transaction consistency,
  empty/nonempty canonical/cache/work observations, lifecycle/latch classifications, and
  rejection before binding creation.
- Continuity tests cover `healthy`, `unknown`, and `lost` mappings; every incident
  `failureCategory`; pure incident-candidate creation; explicit latch behavior through the
  state store; SQL Server schema-history setup versus post-enablement loss behavior; and
  fail-closed status when a latch write fails.
- API integration tests preserve the separation between deployment status and DMS request
  routing.

## Not Assigned to This Story

- DMS projection implementation is assigned to E18.
- Provider object provisioning, connector rendering, and Connect REST orchestration are
  assigned to 19-01, 19-02, and 19-04.
- DMS-1319 does not own a special v1 source-replacement, generation-reservation, or
  `SourceIdentity` rotation-proof contract. Source replacement orchestration is owned by
  19-04. If 19-04 implements guarded source replacement in v1, it defines the rotation
  proof and generation selection, then calls the existing DMS-1319 binding operation with
  the resulting new fingerprint and generation.
