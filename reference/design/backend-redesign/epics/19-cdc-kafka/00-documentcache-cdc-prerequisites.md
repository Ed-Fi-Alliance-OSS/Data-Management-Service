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
- The immutable binding record stores only the fields defined by the design:
  deployment/target identity, provider, physical-source fingerprint, connector name,
  public topic name, partition count, partitioner algorithm, and contract version. It does
  not store `maxRecordBytes`, connection strings, credentials, connector JSON, provider
  principal names, source UUID, database/catalog/server names, or Kafka security settings.
- Use stable `System.Text.Json` DTOs with lower-camel JSON property names and lower-camel
  enum strings for every new JSON-facing contract in this story. Do not serialize numeric
  enum values or rely on implementation type names as wire values.

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
- Local writes use create-new semantics for absent binding and incident files, followed by
  read-back exact-match validation. Updating immutable binding files in place is not a
  supported repair. Production implementations expose the same semantics through their
  durable state backend with atomic create or compare-and-set.
- The source-history incident file is absent until terminal loss is latched. Latching is
  idempotent false-to-true for one complete binding identity and generation. Later
  validation, provider-artifact recreation, offset mutation, or a healthy-looking lag
  result never clears it.
- Retiring a binding is a state-store operation only after the caller proves that the
  connector, offsets, topics, ACLs, provider capture artifacts, and provider-history
  artifacts governed by that binding were retained intentionally or deleted in the
  required order. DMS-1319 does not perform those destructive external deletes.
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
- Select one primary blocking category by deterministic precedence, with additional facts
  retained in bounded diagnostics. At minimum cover:
  `bindingMissing`, `bindingMismatch`, `sourceMismatch`,
  `projectionNonOperational`, `projectionBacklog`, `providerSetupInvalid`,
  `providerHistoryUnknown`, `sourceHistoryLost`, `kafkaPolicyInvalid`,
  `connectOffsetStoreInvalid`, `connectorConfigInvalid`, `connectorNotRunning`,
  `snapshotIncomplete`, `providerBarrierNotReached`, `lagExceeded`, and
  `statusObservationUnavailable`.
- Projection input is valid for CDC status only when the 18-06 target result is for the
  same normalized target, provider, and physical-source fingerprint as the binding.
  Projection operational-health failure and projection caught-up failure are separate
  categories. Queue presence makes CDC not ready only where readiness requires caught-up;
  it does not make ordinary canonical API health fail.
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
- Implement one source-history continuity adapter per provider that consumes the exact
  binding-derived artifacts and committed source offset. Its result is `healthy`,
  `unknown`, or `lost`. `unknown` covers temporary inability to prove continuity and keeps
  readiness false without latching. `lost` latches the binding generation and is not
  cleared by later artifact recreation or snapshot.
- Continuity checks use provider metadata returned by 19-01 where possible and live
  provider/Connect observations where needed. They must not infer continuity from connector
  liveness, lag, matching artifact names alone, or a newly created slot/capture instance.

### Initial Admission and Retry Classification

- Add typed initial-admission and retry classifier models consumed by 19-04. The trusted
  setup controller supplies proof that it created the new physical database and has kept
  canonical write admission closed. DMS-1319 does not accept an operator assertion or an
  existing-database schema inspection as proof of v1 first-time CDC eligibility.
- Before binding creation, the classifier rejects an unbound target when canonical,
  cache, or work tables are nonempty, or when the current source cannot be resolved. Do not
  create a binding and then discover that the database is ineligible.
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
- Artifact-name helper tests cover deterministic output, provider-specific identifier
  limits/sanitization, generation isolation, and the complete name inventory consumed by
  19-01, 19-02, and 19-04.
- PostgreSQL and SQL Server adapter tests cover position, continuity, and failure
  classifications.
- Status tests cover the complete design-owned readiness input matrix and aggregation.
- Status tests distinguish projection operational failure, projection backlog, enqueue
  failure, connector failure, continuity failure, and ordinary canonical API health.
- API integration tests preserve the separation between deployment status and DMS request
  routing.

## Not Assigned to This Story

- DMS projection implementation is assigned to E18.
- Provider object provisioning, connector rendering, and Connect REST orchestration are
  assigned to 19-01, 19-02, and 19-04.
