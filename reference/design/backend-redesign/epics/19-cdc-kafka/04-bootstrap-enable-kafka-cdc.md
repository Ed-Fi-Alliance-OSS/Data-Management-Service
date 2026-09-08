---
jira: DMS-1323
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Design References

- **Enablement and initial readiness sequence**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence
- **V1 readiness scope**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope
- **Local bootstrap and CI**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci
- **Connector topology and provider setup**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup
- **Local and CI connector telemetry**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry
- **Deployment-owned physical source binding**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding
- **V1 deployment-state continuity and adoption deferral**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity
- **Managed lifecycle and native recovery boundary**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary
- **Bootstrap phase ownership**: reference/design/backend-redesign/design-docs/bootstrap/command-boundaries.md
- **Projection administrative serialization**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#administrative-serialization-and-state-row-fencing

The referenced design sections define eligibility, sequencing, topic policy, registration,
readiness, and lifecycle operations. This story is only the work package for implementing
them.

## Outcome

Add the explicit local/bootstrap CDC workflow and the deployment-controller operations
needed to provision, validate, start, stop, and retire a target.

## Dependencies

- Depends on 19-00 through 19-03 and the E18 projection/status inputs consumed by 19-00.

## Implementation Scope

- Add the local/bootstrap command surface and controller orchestration.
- While canonical write admission is closed, integrate new-database evidence, reject a
  nonempty canonical/cache/work database rather than attempting CDC retrofit, atomically
  create or exact-match the immutable binding, and then invoke or recognize the completed
  guarded `Disabled -> Tracking` transition before the first seed/API write according to
  the retry classification.
- Configure and validate the matching DMS target, start queue processing, wait for
  projection caught-up status, cross the provider heartbeat barrier, and require a second
  caught-up observation before opening admission.
- Integrate provider setup, binding lifecycle, and connector rendering. DMS startup itself
  never enables tracking, and mutable projection/CDC state stays outside the bootstrap
  manifest.
- Wire CDC-owned downstream-publication-history evidence into the E18 DocumentCache
  administrative command gate by providing and registering the production
  `IDocumentCacheDownstreamPublicationHistoryProvider` bridge. The bridge must report
  `internalOnly` only when durable CDC binding/source-history evidence proves the same
  normalized target key and physical-source fingerprint were internal-only; `active`,
  `historical`, `possible`, `unknown`, missing, or mismatched evidence must keep the E18
  commands rejected with no mutation.
- Add cluster-scoped Kafka Connect offset-store provisioning/validation and binding-scoped
  Kafka topic, durability, record-size, and ACL provisioning/validation.
- Add Kafka Connect registration, live validation, status polling, restart, guarded
  source replacement, and teardown operations.
- Expose the same workflow to the E2E harness.

## Resolved Bootstrap and Controller Scope

The following choices define the implementation seams for this story. The linked design
documents continue to own eligibility, readiness, continuity, and recovery behavior; the
controller composes those contracts rather than introducing another set of rules.

### Reuse and Command Ownership

- Put reusable controller orchestration and its transport interfaces in the existing
  `EdFi.DataManagementService.Backend.Cdc` library. Add a thin `cdc` command group to
  SchemaTools and a shared PowerShell phase used by the local/published bootstrap wrappers
  and E2E setup. Do not implement independent workflows in each script or add Kafka control
  to the DMS HTTP application.
- Reuse Core's `AddDmsCdcControlPlane`, `ICdcBindingLifecycleService`, artifact-name
  generator, retry/admission/status evaluators, and provider-position contracts. Reuse
  `ICdcProviderSetupService` from `Backend.Ddl` and `ICdcConnectorTemplateService` from
  `Backend.Cdc`. Their typed results are the handoff; scripts do not reconstruct bindings,
  provider identifiers, connector properties, source-position comparisons, or diagnostics.
- Reuse E18 target resolution, effective-schema initialization, administrative commands,
  supervisor, and projection-status services. Factor the necessary non-HTTP composition
  currently in `DocumentCacheAdmin` into a shared runtime composition helper if needed;
  do not reference one CLI executable from another or copy its initialization pipeline.
  Lifecycle mutations still execute through the E18 command and its provider mutex.
- Expose explicit enable, validate, status/watch, restart, stop, source-replacement,
  record-size increase, and retire operations. Validate inspects without repairing;
  status/watch also performs the design-required incident latching and connector containment.
  Stop retains artifacts; retire requires an explicit generation and destructive-cleanup
  intent. Configuration
  removal is not an invocation of either operation.
- Use one typed deployment request for normalized target identity, binding inputs, DMS
  settings, provider setup context, Connect endpoint, Kafka/worker policy, externalized
  connector credentials, consumer principals/groups, and bounded timeout/poll settings.
  Reuse existing provider-token conversion at CLI/configuration boundaries. Keep secrets
  out of binding and workflow JSON, command output, and redacted template manifests.
  JSON mode emits one structured result on stdout and diagnostics/progress on stderr.

### Bootstrap Integration and the Offline Window

- Add `-EnableKafkaCdc` and `-CdcBindingStatePath` to the wrapper handoff without changing
  schema/claims preparation or target selection ownership. Use the structured selected
  IDs from `configure-local-data-store.ps1`; extend the physical-database creation path
  to return authoritative created-versus-reused evidence. A newly created CMS record,
  successful `ddl provision`, empty database, or matching schema hash is not that evidence.
- For the first local implementation, use a dedicated new DMS database and the existing
  `-SeparateConfigDatabase` topology. Reject an initial CDC request that would reuse the
  shared CMS database, a DMS database outside the proven initial workflow, or an already running
  DMS/IDE endpoint. Validate unsupported flag combinations before starting infrastructure.
  Ordinary non-CDC and Kafka-UI-only flows retain their existing phase behavior.
- Insert the CDC phase after ordinary schema provisioning and before the wrapper's
  `-DmsOnly` start and optional seed phase. During this phase, host the existing DMS
  projection runtime without an HTTP listener in the controller process. Resolve and
  initialize the explicitly configured target, perform activation and connector setup,
  then start its supervisor and read the shared projection-status service. This supplies
  DMS-owned observations without exposing a canonical writer during the offline window.
- Require explicit `DocumentCache:Targets` membership in the supplied DMS settings and
  carry that same configuration into the eventual DMS host. CDC opt-in does not manufacture
  target membership. Dispose the temporary supervisor cleanly after initial readiness;
  public DMS startup resumes the same durable queue through the existing runtime path.
- The controller must exclusively own this initial database and its publication to writers.
  Local setup checks that no existing application instance can resolve/use the selected
  database; connecting a shared CMS to running replicas does not meet that condition.
  External deployment adapters must supply equivalent ownership evidence. This story adds
  no cross-replica request gate and does not accept a caller's `writesClosed=true` as proof.
- Start public DMS, print actionable writer/IDE continuation guidance, and invoke seed or
  E2E API writes only after the CDC phase succeeds. A failed or cancelled phase ends the
  wrapper without those actions. Suppress the existing provision/start phase's early
  writer/IDE continuation hints while CDC admission is pending. An `-InfraOnly` preparation
  may remain offline, but an arbitrary `-DmsBaseUrl` health check cannot complete initial
  CDC admission.

### Durable Workflow Evidence and Retry Boundaries

- Extend the deployment-owned local state infrastructure with a small, versioned workflow
  journal alongside the existing binding/incident files under `.cdc-state`. Record the
  controller creation receipt, normalized target and source fingerprint, original workflow
  identity, binding generation, external-operation intent/completion, provider creation
  evidence, and whether writer publication has been authorized. Do not add these fields to
  the immutable binding, `EffectiveSchema`, or `.bootstrap/bootstrap-manifest.json`.
- Serialize controller mutations with one exclusive local controller lock for the state
  root; retain the existing atomic binding operations and owner-only file protections.
  This remains a single-controller filesystem implementation, not a distributed lease or
  a generic workflow engine. Persist intent before an external side effect and confirm
  completion from live state after a crash; a journal step alone does not validate an
  artifact. Missing, corrupt, or contradictory provenance fails closed. A watch loop releases
  the controller lock between observation/containment passes rather than holding it forever.
- Build fresh `InitialCdcProvisioningProof` and eligibility observations from that trusted
  journal plus current provider/runtime inspection. Reuse the 19-00 pre-binding and retry
  classifiers. Persist the binding before invoking E18 activation. If database creation
  committed but its receipt was not durably recorded, require cleanup/reprovisioning;
  do not adopt the database into the initial workflow by inference.
- Preserve the PostgreSQL initial slot-creation proof returned by 19-01 before registration.
  An existing slot with lost creation evidence cannot be relabeled newly created. Use
  `InitialCreateOrExactMatch` only for authorized unfinished provider creation; once the
  connector has started consuming, obtain fresh `ValidateOnly` results so a legitimate
  active/advanced slot is not sent back through the pre-registration creation guard.
- Distinguish an initial connector awaiting its first streaming offset from an established
  connector whose offset disappeared. Persist establishment evidence and retained provider
  artifact identity needed by 19-00 continuity checks. A lost HTTP response is an unknown
  outcome to reconcile, not permission to recreate history or resubmit a different config.
- On an interrupted readiness wait, reobserve projection, capture a new barrier, and obtain
  the second projection observation through the existing evaluator. Do not persist a
  reusable ready flag or barrier. Before any writer handoff, durably mark the initial
  workflow ineligible for further initial-enable retries; a crash around that handoff
  therefore routes to validation/restart, never an empty-table enablement shortcut.

### Provider, Kafka, and Connect Adapters

- Supply the ordinary emitted source inventory to 19-01; supply its fresh typed result and
  the exact binding to 19-02 rendering and validation. Provider setup does not perform
  activation. Configure local SQL Server projection prerequisites while the new database
  is offline and let E18 validate them; retain 19-01's ownership of capture setup and its
  prohibition on silently repairing established capture history.
- Separate Kafka broker startup from Connect worker startup in the existing Compose path.
  Pre-create/validate the configured shared offset store before launching the qualified
  digest-pinned worker, including when `-EnableKafkaUI` is also selected. Replace the legacy
  floating connector-image selection on the CDC path; consume the 19-03 image and 19-02
  qualification fixtures rather than rebuilding
  plugin behavior in this repository. UI-only startup never registers a connector.
- Implement narrow Kafka administration and Connect REST adapters behind the controller
  interfaces. Kafka administration observes actual topic configs, replicas, broker limits,
  and effective deployment-managed ACLs. Worker configuration/deployment inspection supplies
  offset-topic identity, override-policy, image, and heap evidence that connector REST
  config alone cannot prove. Unavailable evidence remains unavailable, not a default value.
- Apply the topic, durability, sizing, and ACL policies from the linked design through one
  shared policy builder/validator. Create missing binding artifacts only in eligible setup;
  exact-match existing topics and reject incompatible configuration rather than silently
  changing partition counts or repairing history. Repair missing required ACL grants only
  within the design's allowed ACL reconciliation. Keep the shared worker offset topic out
  of binding cleanup inventories. Authorization-disabled local fixtures must identify that
  profile explicitly; production-like ACL evidence requires an authorization-enabled broker.
- Render the current registration payload in memory, run registration preflight against
  the worker, and create only an absent connector. Read an existing connector's effective
  config and use 19-02 live validation; do not use unconditional config upsert to hide drift.
  Read back and live-validate a newly registered connector as well. A create conflict or
  request timeout triggers read-back reconciliation. Wait for the connector and sole task,
  then read committed offsets through the supported REST surface.
  Reuse the provider offset parser and source-partition hash; do not consume progress-topic
  records or substitute topic offsets for source-position evidence.
- Implement the direct JMX Exporter HTTP adapter and single-worker endpoint wiring from
  the design's [local/CI telemetry contract](../../design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry).
  Consume DMS-1322's qualified exporter-enabled image and DMS-1321's reusable metric
  qualification fixtures. Extend the typed deployment request with the configured worker
  metrics endpoint and maximum observation age; reuse the Core lag observation and
  evaluator contracts, extending their typed handoff only where required for the
  design-owned statistics and identity evidence. Implement collection-time tracking,
  worker/task correlation, freshness validation, and readiness integration in the adapter
  and controller. Bound each external call and the complete wait, propagate cancellation,
  and return the failed component with sanitized diagnostics. Connection/authentication
  failure and an authoritative missing artifact must remain distinguishable inputs to
  continuity classification.
- Keep an explicit record-size increase separate from ordinary validation/retry. Compose
  broker/topic policy changes and regenerated connector config through the design's
  [coordinated increase procedure](../../design-docs/cdc/cdc-streaming.md#in-place-record-size-increase);
  do not rewrite the binding or make ordinary drift validation a configuration repair loop.
  Add its structured deployment-operator consumer-capacity acknowledgement to the command
  input, with scope validation, workflow-journal persistence, and resumed-invocation
  confirmation handling. Add command help and invocation examples explaining the operator's
  responsibility for consumer inventory and capacity evidence.

### Publication-History Bridge to E18 Administration

- Implement and register the production `IDocumentCacheDownstreamPublicationHistoryProvider`
  in the DocumentCache administrative host as well as any controller host invoking those
  commands. Reuse the E18 observation and proof evaluator. Normal HTTP DMS needs neither
  Kafka credentials nor CDC controller registration to perform projection.
- Binding absence cannot prove `internalOnly`. Add a controller-owned source-history
  record for a newly created, exclusively managed database, keyed by normalized target and
  physical-source fingerprint. The ordinary managed provisioning path must be able to
  establish this record when CDC is not selected; a CDC-only setup path cannot supply the
  successful internal-only production case required by this story. Existing/unmanaged
  databases receive no retrospective internal-only attestation.
- Record loss of internal-only eligibility durably before binding reservation or any other
  managed downstream exposure. Serialize this update with the controller's binding workflow
  and keep E18 history-gated administration from racing that workflow, using the same lock
  order in both hosts before entering the E18 administrative command. Preserve exposure
  history across stop, target removal, and binding retirement when the database survives.
  A failed reservation may conservatively leave history `possible`; cleanup does not
  promote it back to `internalOnly`.
- Return `internalOnly` only from complete trusted ownership/history evidence with no
  downstream exposure for that same target/source. Active or historical bindings, possible
  exposure, incomplete ownership/history, absent/unreadable state, and mismatched sources
  use the existing rejecting statuses. Do not add an operator assertion, override switch,
  or empty-directory shortcut. If no trusted state backend is configured, preserve E18's
  default `unknown` behavior.

### Post-Enablement Operations and Delivery Evidence

- After admission, use the 19-00 observational status/continuity services and 19-01
  validate-only inspection before controller-issued start/restart/resume and on each watch
  interval. Persist any terminal incident and stop the affected connector; report failures to persist or stop
  without concealing the incident. Never restart on unknown continuity or reuse initial
  readiness evidence to claim another exact baseline.
- Implement the design's managed shutdown/startup ordering through the existing Connect
  stop/resume and offset APIs. Journal verified connector shutdown before worker stop and
  reconcile retained stopped state on startup. Route incomplete or unverified shutdown to
  the native recovery boundary. On observed worker/task recovery or reassignment, invalidate
  prior readiness evidence and collect fresh observations through the shared services.
  DMS-1323 owns this orchestration and its provider/image qualification; reuse sibling
  fixtures without adding a worker or task interception mechanism.
- Enforce the design's v1 deployment-state continuity and adoption deferral before
  validation/restart. Return sanitized diagnostics identifying unavailable or contradictory
  provenance without reconstructing it from operator input or healthy artifacts. Do not
  expose an adoption command or call `ImportVerifiedBindingAsync` from the controller.
  The existing lower-level import capability may remain unused; removing it is not required
  by this story. Preserve intact interrupted-initial-workflow retries through 19-00.
- Source replacement uses the design's guarded
  previously-enabled-source path, with an explicit old/new generation request and durable
  crash checkpoints around connector fencing, source-identity rotation, and new artifacts.
  Factor any missing rotation/state-operation seam into the existing control-plane/provider
  abstractions, including an explicitly guarded new-generation provider-creation path;
  do not mislabel replacement as an initial-empty retry to bypass 19-01's mode guards.
  It is not recovery from lost deployment provenance, terminal history loss, or a published
  cache-ahead latch, and it does not implement the deferred baseline-replacing cutover.
- Teardown keeps infrastructure reachable while stopping the connector, removing its own
  committed offsets through the supported stopped-connector API, and deleting/verifying
  the connector and remaining governed artifacts. Use 19-00's typed cleanup inventory and
  `DeleteStateAfterVerifiedCleanupAsync` only after complete live absence evidence. A failed
  cleanup retains binding/incident state and is resumable. Normal stack stop retains all
  state; destructive volume teardown must complete governed cleanup before deleting state
  files. Per-binding retirement never deletes shared worker topics or another binding's
  principals, grants, or artifacts.
- Put controller and transport-contract tests in the existing CDC unit/integration test
  projects and wrapper ordering tests under `eng/docker-compose/tests`. Use real PostgreSQL
  and SQL Server capture plus the qualified Connect image for admission, interrupted setup,
  intact-state restart, state-loss rejection, containment, and retirement evidence. Include
  an authorization-enabled broker profile for the shared-offset and cross-instance ACL cases.
  Qualification CI fails on missing prerequisites rather than counting skipped provider/broker
  tests as evidence.
- Exercise the history bridge through the packaged DocumentCacheAdmin composition, including
  durable positive internal-only evidence and rejection after binding retirement, state
  loss, source change, and concurrent CDC reservation. Include crash injection at creation
  receipt, binding/activation, provider proof, registration, barrier, writer handoff, and
  cleanup boundaries. Reuse sibling fixtures; detailed public-message and API-driven Kafka
  scenarios remain in 19-05/19-06, and full operator runbooks remain in 19-07. Add command
  help and local invocation examples here so those stories have a concrete shipped surface.

## Acceptance Evidence

- Script and integration tests cover the setup, retry, rejection, timeout, restart,
  guarded lifecycle, and teardown cases defined by the integration design.
- Partial/retry tests prove the binding is durable before guarded activation: an exact
  binding with lifecycle `Disabled` and a clear latch retries activation; an exact binding
  with lifecycle `Tracking`, a clear latch, and empty tables resumes setup; and a set
  cache-ahead latch, unbound `Tracking`, any other lifecycle, a binding mismatch, or
  unexpected pre-capture rows fail closed and require cleanup/reprovisioning as applicable.
  They also cover queue drain, provider barrier, and second caught-up observation
  interruptions.
- Broker-backed tests cover the shared Connect offset store's compaction, durability, and
  worker-only ACLs plus binding-topic policy, record-size, connector, offset, heartbeat, and
  image validation.
- Record-size increase tests cover the design's consumer-attestation contract: accepted
  acknowledgement, explicit no-consumers declaration, incomplete or mismatched evidence,
  changed consumer deployments and requested ceilings, durable acknowledgement ordering,
  and interrupted rollout with renewed confirmation and live reconciliation. Include crash
  boundaries before external changes and before readiness restoration, and prove missing
  confirmation prevents advancement while a partial rollout remains not ready.
- Provider tests cover the initial readiness and post-enablement lifecycle paths for
  PostgreSQL and SQL Server.
- Managed lifecycle tests for both providers use the qualified image to prove that verified
  stopped connectors remain stopped across worker restart, expose committed offsets through
  REST, and perform no source consumption or offset advancement before successful validation
  and controller resume. Cover healthy continuity, unavailable observations, missing
  provenance, and retained or newly detected terminal history loss. Wrapper tests prove
  connector shutdown verification precedes worker shutdown; an acknowledgement without
  verified completion is insufficient.
- Native recovery tests exercise worker crash, task recovery/reassignment, and incomplete
  or unverified managed shutdown using the same evidence cases. Assert readiness invalidation,
  fresh observation, rejection of unauthorized controller restart/resume, and terminal
  incident retention/latching and containment. These scenarios allow consumption before
  revalidation and must not claim prevention or retrospective continuity certification from
  eventual containment, current healthy offsets, or later ready status. Include a case with
  observed pre-validation consumption to make that limitation executable.
- Telemetry adapter and controller tests consume the sibling qualification fixtures and
  cover fresh successful collection, configured age boundaries and expiry before writer
  handoff, missing/duplicate/malformed metrics, exporter failure, timeout/cancellation,
  connector or worker mismatch, task/worker restart, reassignment, and rejection of
  previous-pass evidence. Both provider admission paths prove that current lag and the
  provider barrier remain independent requirements. Unsupported worker topology and
  unavailable identity evidence cannot pass readiness.
- Controller tests cover the design's deployment-state continuity boundary: intact-state
  validation/restart and interrupted initial retries succeed when otherwise eligible;
  missing binding, journal, establishment/provider-identity evidence, or source-history
  record, unreadable, corrupt, or contradictory provenance, and reported incident-history deletion or
  state rollback reject without mutation. Healthy artifacts and operator-supplied binding
  JSON do not bypass rejection. Retained terminal incidents prevent restart; normal incident
  file absence remains valid for an intact workflow. Tests also reject attempts to route
  state loss through initial enablement or source replacement and prove retirement does not
  restore a surviving database's initial eligibility or internal-only publication history.
- Production-path tests prove the E18 `activate-offline`, `deactivate-offline`, and
  `recover-cache-ahead` commands no longer receive the default `unknown` downstream
  history when trusted CDC evidence proves `internalOnly`, and still reject active,
  historical, possible, unknown, missing, or mismatched evidence without mutation.
- Diagnostics tests cover each implementation boundary without exposing secrets.

## Not Assigned to This Story

- Managed-provider-specific deployment automation is deployment work.
- Projector behavior is assigned to E18; message behavior is owned by the ADR and tested in
  19-05.
- Missing-state adoption and recovery from deployment-state rollback are deferred by the
  owning integration design; this story adds no replacement provenance mechanism.
- Distributed-worker metrics-endpoint discovery is deferred to a later deployment adapter.
  Exporter packaging/image publication belongs to DMS-1322, and reusable image/metric
  qualification fixtures belong to DMS-1321.
- Strict pre-consumption fencing across native worker/task recovery is deferred by the
  owning design; custom worker startup hooks, task interceptors, and infrastructure fences
  are not part of this story.
- Automated inspection or certification of independently operated consumers is deferred;
  this story implements the design-owned deployment-operator attestation contract.

## Clarifying Questions and Answers

### Questions 1

1. For explicit adoption, which durable provenance must survive besides the operator-supplied binding and live artifacts? Define the supported cases when the workflow journal, connector-establishment/provider-identity evidence, source-history record, or incident state is missing, and how adoption avoids reviving a previously terminal generation when live artifacts currently look healthy.
2. Which concrete telemetry transport must the shipped local/CI controller use to obtain current connector lag and the required lag statistics, and who owns any exporter or qualified-image changes it needs? What observation freshness and connector/task identity checks must that adapter satisfy before its lag value can participate in readiness?
3. For the explicit record-size increase operation, what evidence confirms that independently operated consumers have raised fetch and deserialization capacity, and how is that confirmation scoped and persisted for crash-safe retries? Is an operator-supplied acknowledgement sufficient, or must this story deliver a consumer/deployment verification adapter before broker/topic and producer changes can proceed?


### Answers 1

1. **Resolved: defer missing-state adoption from v1.** Implement the owning design's
   [deployment-state continuity and adoption deferral](../../design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral).
   Ship intact-state validation/restart, interrupted initial-workflow retry, and independently
   guarded retirement, with rejection diagnostics and evidence for unsupported state-loss
   recovery. Remove adoption from the command surface and successful integration scenarios;
   do not invoke `ImportVerifiedBindingAsync` from the controller. The lower-level capability
   may remain unused. The design retains the normally absent incident file and explicitly
   excludes recovery from suspected incident-history deletion or state rollback; this story
   does not add an always-present generation ledger or infer historical proof from live health.

2. **Resolved: use direct worker-local JMX Exporter HTTP telemetry for local/CI.** Implement
   the owning design's
   [local/CI telemetry contract](../../design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry)
   for collection, connector/task and worker identity, observation age, restart invalidation,
   and fail-closed readiness. DMS-1322 owns the pinned standard exporter, fixed mappings,
   and qualified image publication; DMS-1321 owns reusable image/metric qualification
   fixtures; DMS-1323 owns the configured endpoint, HTTP adapter, typed observation handoff,
   and controller integration tests. The shipped adapter supports the explicitly configured
   single-worker local/CI topology; distributed-worker endpoint discovery is deferred.
   Preserve the independent current-lag threshold and provider barrier requirements.

3. **Resolved: use deployment-operator consumer-capacity attestation for v1.** Implement the
   owning design's attestation contract in the
   [coordinated increase procedure](../../design-docs/cdc/cdc-streaming.md#in-place-record-size-increase)
   using the existing administrative trust boundary. The deployment operator supplies a
   structured acknowledgement based on every affected consumer owner's capacity evidence
   and attests to inventory completeness; an explicit no-consumers declaration is supported.
   DMS-1323 owns the command input, operation-scope validation, durable journal integration,
   renewed confirmation on resumed invocations, live rollout reconciliation, command help,
   and acceptance tests. Follow the design's rejection and partial-rollout readiness rules.
   Automated inspection or certification of independently operated consumers is deferred.

### Questions 2

1. Does the requirement to validate continuity before every connector start/resume also cover retained connectors restarting with the Connect worker after a clean stop or crash? If so, what shipped mechanism keeps their tasks from consuming until the controller can read offsets through the worker REST API and validate provenance/provider history; if automatic worker/task recovery is outside that guarantee, what explicit support boundary and restart tests apply?
2. For the supported source-replacement operation, what is the concrete successful input scenario: does the controller receive an already prepared replacement database, or must it create/restore/copy the database and update CMS connection metadata? Specify who owns those steps and what old-source continuity and replacement projection-state evidence must be available before source-identity rotation and new-generation capture, so the success tests distinguish this path from deferred history-loss or baseline-replacing recovery.


### Answers 2

1. **Resolved: scope pre-start continuity validation to controller-managed lifecycle operations.**
   Implement the owning design's
   [managed lifecycle and native recovery boundary](../../design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary).
   DMS-1323 owns managed shutdown/startup orchestration, durable shutdown evidence, REST
   observation and resume wiring, readiness invalidation, and provider/image qualification.
   Managed shutdown verifies stopped connectors before stopping the worker; managed startup
   validates retained stopped connectors before resuming them. Native worker recovery, task
   reassignment/internal recovery, and startup after an incomplete or unverified shutdown
   receive observational monitoring and containment, with no pre-consumption guarantee or
   retrospective continuity certification. Controller-issued starts/restarts/resumes remain
   prohibited without intact provenance, no terminal incident, and affirmative continuity
   evidence. Use the existing Connect APIs, shared classifiers/provider adapters, and sibling
   pinned-image fixtures. Strict fencing across native recovery is deferred. The acceptance
   suites above distinguish prevention during managed startup from observation and containment
   after native recovery for both providers.

2. **Requires human decision:** Define one supported source-replacement input and preparation
   handoff, including ownership of database creation/restore/copy, CMS connection changes,
   and writer/projector fencing. The owning
   [binding lifecycle](../../design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
   specifies the rotation and new artifacts, but does not establish an admissible prepared
   replacement state or assign those preparation steps. Ordinary bootstrap phase ownership
   does not define a replacement migration workflow. The settled constraints are a source
   previously enabled through the v1 new-database path, intact deployment provenance, no
   terminal source-history loss or published cache-ahead latch, verified old-connector
   fencing before rotation, and entirely new generation artifacts and consumer namespace.
   Source replacement cannot reconstruct missing provenance, clear published projection
   state, or certify another exact baseline. The decision must define when and against
   which source the old committed-offset/provider-history proof is obtained and retained,
   how it is tied to the replacement, and what replacement canonical/cache/work integrity
   evidence is required before rotation and capture. E18 requires `Tracking` with a clear
   latch for operational health and an explicit integrity scrub after suspected restore
   before relying on queue-empty status; queue emptiness alone cannot prove a valid restored
   projection. Update the owning design and story with the concrete successful scenario,
   preparation owner, evidence handoff, and crash boundaries, then use that same scenario
   for both provider success tests. Keep state-loss, terminal-history-loss, cache-ahead,
   and baseline-replacing recovery scenarios as rejections.
