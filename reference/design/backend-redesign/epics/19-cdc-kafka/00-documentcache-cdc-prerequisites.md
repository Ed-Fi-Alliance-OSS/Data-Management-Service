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
  retry-classifier, binding-state, binding, and incident models owned by DMS-1319. 19-04
  owns transport and operator command wiring, but consumes and serializes these DTOs
  unchanged rather than defining a second operator-facing shape.
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

### Binding State Store and Incident Latch

- Define one deployment-owned state-store abstraction with these guarded operations:
  create immutable binding if absent, read binding, exact-match existing binding, list
  bindings under a deployment key, latch source-history loss, import an operator-supplied
  verified binding record, and delete state only after caller-proved governed-artifact
  cleanup.
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
- Import/adoption callers must supply an `AdoptionProof` with `contractVersion: 1`,
  operation id, UTC verification time, the complete binding record to import, and a
  bounded verification summary asserting exact live match for the physical-source
  fingerprint, provider artifacts, connector, connector configuration, topics, ACLs,
  offsets, and source-history continuity.
- Delete-after-cleanup callers must supply a `CleanupProof` with `contractVersion: 1`,
  operation id, UTC verification time, binding identity, cleanup mode, and the complete
  binding-derived governed-artifact inventory with each artifact marked `deleted` or
  `notFound`. An explicit retain decision is not a delete-after-cleanup path; if any
  governed artifact is retained, retain binding and incident state with it.
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
- Before initial connector admission, missing, unreadable, or nonconforming SQL Server
  schema history is a non-latching setup/readiness failure. After initial enablement,
  missing history, empty history when retained offsets exist, or proven truncation of
  required history latches `sourceHistoryLost`. Temporary unreadability, ACL drift,
  retention-policy drift, or unavailable evidence without proof of lost required history is
  a non-latching `unknown`, `kafkaPolicyInvalid`, or `connectorConfigInvalid` readiness
  failure until a continuity check proves actual loss.

### Initial Admission and Retry Classification

- Add typed initial-admission and retry classifier models consumed by 19-04. The trusted
  setup controller supplies proof that it created the new physical database and has kept
  canonical write admission closed. DMS-1319 does not accept an operator assertion or an
  existing-database schema inspection as proof of v1 first-time CDC eligibility.
- The classifier receives a trusted `InitialCdcProvisioningProof` object with
  `contractVersion: 1`, normalized target identity, provider, setup-controller run id,
  `databaseCreationMode: "createdForInitialCdcProvisioning"`,
  `writeAdmissionState: "closedNeverOpened"`, and UTC `issuedAt`. DMS-1319 validates the
  structure, enum values, version, non-empty run id, and target/provider match, then still
  performs source resolution and empty canonical/cache/work checks before binding creation.
  It treats proof provenance as trusted input from 19-04.
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
  setup-controller run id, write-admission proof id,
  `consistencyScope: "singleProviderTransaction"`, lifecycle state, cache-ahead latch
  state, `canonicalRowsPresent`, `cacheRowsPresent`, `workRowsPresent`, and an opaque
  provider consistency token for diagnostics. DMS-1319 validates the operation/proof ids,
  target/provider/source match, known lifecycle/latch values, and all three row-presence
  booleans; it rejects before binding creation unless the source is resolved,
  lifecycle/latch observations are authoritative, and canonical, cache, and work rows are
  all absent.
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
  binding-state, persisted binding, and incident DTO shapes, including enum string values,
  required `contractVersion` fields, and exact-match behavior for missing, extra, or
  differently valued binding fields.
- State-store tests cover local path validation and lookup for valid tokens, invalid
  path-like tokens, deterministic lookup, no cross-key collision, adoption proof
  validation, cleanup proof validation, and delete rejection when any governed artifact is
  retained.
- Artifact-name helper tests cover deterministic output, provider-specific identifier
  limits/sanitization, generation isolation, and the complete name inventory consumed by
  19-01, 19-02, and 19-04.
- Artifact-name helper tests include conformance vectors for provider names exactly at
  each limit and one or more characters over each limit, the literal provider hash
  artifact-kind strings, Kafka/Connect 249-character failure behavior, and topic-prefix
  reconstruction from persisted `topicName`.
- PostgreSQL and SQL Server adapter tests cover position, continuity, and failure
  classifications.
- Offset observation tests cover normalized 19-04 offset inputs, PostgreSQL `lsnProc`, SQL
  Server `commitLsn`/`changeLsn`/`eventSerialNo`, source-partition hash generation, and
  exclusion of the sensitive SQL Server database value from serialized state,
  diagnostics, logs, and metrics.
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
