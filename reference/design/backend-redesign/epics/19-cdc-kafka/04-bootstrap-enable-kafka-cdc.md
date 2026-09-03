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
- **Deployment-owned physical source binding**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity

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
  `IDocumentCacheDownstreamPublicationHistoryProvider` implementation. It reports
  `internalOnly` only when durable CDC binding evidence proves the same normalized target key
  and physical-source fingerprint were internal-only; `active`, `historical`, `possible`,
  `unknown`, missing, or mismatched evidence keeps the E18 commands rejected with no mutation.
- Add cluster-scoped Kafka Connect offset-store provisioning/validation and binding-scoped
  Kafka topic, durability, record-size, and ACL provisioning/validation.
- Add Kafka Connect registration, live validation, status polling, restart, guarded
  adoption/source replacement, and teardown operations.
- Expose the same workflow to the E2E harness.

## Resolved Bootstrap CDC Scope and Integration Contract

This section records the DMS-1323 implementation resolution for where the deployment-owned
CDC control plane lives, which contracts it reuses, and how the local/bootstrap and E2E
entry points drive it. The owning design documents remain normative for eligibility,
sequencing, topic policy, readiness, continuity, and operations; this story owns the
controller, its adapters, and the entry points that invoke them.

### Placement and Boundary

- The adapters and the setup controller live in a new `Backend.Cdc.Control` library rather
  than in `Backend.Cdc` or the CLI. `Backend.Cdc`'s dependency guardrail forbids the broker
  and Connect clients they need, runtime DMS must not gain a control-plane dependency, and
  the CLI project is excluded from coverage assembly-wide, which would silently exempt the
  orchestration.
- Runtime DMS never enables tracking. It receives only operator-configured
  `DataManagement:DocumentCache:Targets` entries and exposes per-database projection health;
  every enablement, restart, adoption, replacement, and retirement is an external
  administrative command.
- The controller returns only contracts that already exist - `CdcAdmission`, `CdcStatus`,
  `CdcRetry`, `CdcAdoptionProof`, and `CdcCleanupProof`. No new result shape is introduced,
  so the JSON surface stays the one the operator contracts already define.
- Provider CDC artifact setup (19-01), binding lifecycle and admission evaluation (19-00),
  and connector rendering and property comparison (19-02) are consumed, not reimplemented.
  The Connect adapter supplies live read-back evidence and maps diagnostics;
  `ICdcConnectorTemplateService` remains the only place connector property rules live.

### Command Surface and Operator Evidence

- The command surface extends `dms-document-cache` with a `cdc` verb group - `enable`,
  `status`, `restart`, `adopt`, `replace-source`, `retire` - rather than adding a third CLI.
  That project already owns target resolution, `DocumentCache:Targets` binding, provider
  runtime services, the guarded activation command, shared JSON output, exit-code mapping,
  and log sanitization. Verb identity is scoped, because `cdc status` is not the
  DocumentCache `status` command even though the two share a leaf name.
- Provisioning evidence is caller-supplied and never inferred. `enable` and
  `replace-source` require two exact tokens -
  `--database-creation-mode created-for-initial-cdc-provisioning` and
  `--write-admission closed-never-opened` - mirroring the existing
  `--offline-writer-admission closed-and-drained` convention. A caller that cannot support
  either claim omits it and is refused, which is the correct outcome.
- `maxRecordBytes` has no default. It drives topic configuration, producer overrides, and
  broker verification, so an absent value fails closed rather than inheriting a broker
  default.
- Destructive verbs require their own exact confirmation token, and `replace-source` names
  the generation it supersedes explicitly rather than inferring it from what exists.
- Consumer principals come from configuration, and an empty list is valid: local and
  no-consumer deployments grant no instance-consumer access, and ACL items report
  `NotApplicable` when the broker has no authorizer.
- Both database principals — the setup principal and the connector principal — are required for
  every cdc verb whether or not the broker has an authorizer, and options validation refuses at
  start-up rather than mid-sequence when either is absent. Every verb runs a provider-setup pass
  as the setup principal, and that pass reports the source grants held by the connector
  principal, so neither is conditional on Kafka. Only the Connect worker principal is
  ACL-conditional: nothing outside the Kafka grants names it.
- `status` and `restart` observe the governed artifacts; they never provision them. Both
  read the Kafka policy and the shared Connect offset store through the describe pass, so an
  absent topic is reported absent and a missing grant reported missing. Only `enable` and
  `replace-source` create. A status that provisioned would report artifacts it had just
  created itself, and would put back what an interrupted retirement had already removed -
  leaving the next retirement's cleanup proof describing something other than what the
  failed one left behind.
- A source-history loss proved on a status interval is latched and the connector carrying it
  is fenced. A fence the worker refuses is reported as its own diagnostic on the connector
  runtime: the loss is latched either way, and a status that reported a contained incident
  while the connector kept committing offsets would leave no evidence that it had not.
- Against affirmative continuity, `restart` resumes a connector the worker is holding
  `STOPPED` or `PAUSED` and restarts any other. Those two are worker-owned target states a
  restart does not clear - it re-creates connector and task instances, and a stopped connector
  has no tasks to re-create - so a restart-only verb could start nothing, and a generation
  fenced by an abandoned source replacement would be unreachable from every operator command.
  Resume is the only operation that clears either state, and like a start it is issued only
  after continuity is proved.
- A connector lifecycle request the worker refuses - a fence, a restart, or a resume - is
  reported as its own diagnostic on the connector runtime rather than left to be inferred from
  the state read back afterwards. A connector still not running reads identically whether the
  worker applied the request and it failed anyway or never accepted it at all, and only the
  second is worth reissuing.

### Explicit Projection Target Evidence

- The explicit-projection-target proof reads `DataManagement:DocumentCache:Targets` from the
  unmodified `IConfiguration`, not through the CLI's DI graph. The CLI overwrites
  `DocumentCacheOptions.Targets` with the invocation's own arguments, so resolving the proof
  through the target resolver or registry would only confirm what the operator typed.
- Because the proof is a configuration fact, the entry points must configure the target
  before DMS starts: the CDC opt-in writes the `(tenant key, DataStoreId)` entry and the
  status endpoint's required role into the DMS runtime settings, and infrastructure opt-in
  alone never implies a projection target.

### Downstream Publication History for the E18 Administrative Gate

The rule itself — what proves a projection internal-only, what each record means, and which
observations reject — is owned by
[Relational CDC and Document Projection](../../design-docs/cdc/cdc-streaming.md) under the
read-acceleration toggle. It is not restated here.

This story's scope is the production implementation of that rule:

- The `Backend.Cdc.Control` provider replaces the default abstraction that always answered
  `unknown`, reading the deployment's durable binding and retirement records.
- Retirement writes its retirement record before it deletes the binding record, so the two
  are never both absent for a generation this deployment published.
- The provider is registered by the packaged administrative host ahead of the DocumentCache
  runtime services, whose registration of the `unknown` default is conditional.
- Target matching maps the E18 empty tenant key onto the binding `default` token, and every
  observation carries the currently resolved physical-source fingerprint, including the
  rejecting ones, because the E18 evaluator checks the fingerprint before the status.
- The deployment key is read from raw configuration rather than through the bound control
  options, whose validation the administrative host defers to a `cdc` verb; binding it here
  would make every DocumentCache command fail on CDC configuration it does not use.

The bootstrap lifecycle configures exactly one target, so its store holds only that target's
own records. `internalOnly` is therefore unreachable across the bootstrap lifecycle and the
E18 offline commands stay rejected for the whole of it, which is the fail-closed side of the
owning rule rather than a gap in this story.

### Lag and the Metrics Bridge

- Connector lag is Debezium's `MilliSecondsBehindSource` current value plus its P50, P95,
  and P99 attributes, read over a JMX-to-HTTP bridge. It is never derived from the progress
  topic: the lag observation contract requires all five values whenever `LagState` is not
  `Unknown`, and the Debezium quantiles are named by the owning design as deployment-owned
  CDC status.
- The bridge is Jolokia, activated with `ENABLE_JOLOKIA=true` on the Kafka Connect service.
  Its port is fixed at 8778 by the Connect image's entrypoint and is not configurable, so it
  is a property of the image rather than a deployment setting; the reader derives the bridge
  from the Connect host and that port unless an explicit metrics base URI is supplied.
- The Prometheus JMX exporter is not an alternative on this image: its entrypoint branch
  targets a port whose agent jar the image does not ship. Jolokia also returns per-attribute
  JSON, where the exporter would require flattening rules to be authored and maintained.
- An unavailable bridge yields `Unknown`, which keeps readiness false. A timeout never opens
  writes as ready.

### Retirement and Teardown Ordering

- Retirement stops the connector, deletes its committed offsets while it is stopped and
  still exists, then deletes the connector. Connect accepts an offsets `DELETE` only for an
  existing stopped connector, and a configuration delete leaves committed offsets in the
  shared store, so deleting the connector first would orphan offsets permanently and break
  later registrations. The worker applies a stop asynchronously, so the control plane reads
  the connector's state back until it reports `STOPPED` rather than treating the accepted
  request as the fence; a connector that never settles is reported unavailable and
  retryable. The wait is bounded by the Connect request timeout as elapsed time rather than
  as a number of reads, because each state read carries that timeout of its own. Stopping
  the connector is also how source replacement fences the outgoing generation.
- Every provider pass runs under the configured provider-setup budget - the create pass, the
  validate-only pass each verb composes its evidence from, and the retirement's own artifact
  teardown. A pass that spends its budget is reported as a failed step rather than waited on:
  the CLI adds no wall clock of its own, so an unbounded provider call would hold a verb open
  indefinitely. A retirement whose provider teardown times out ends with no proof and its
  binding record intact, exactly as any other failed step there does.
- A connector the worker does not have ends the retirement. Because the offsets outlive the
  configuration and the worker answers the same `404` whether the connector never existed or
  was deleted out from under the record, a missing connector is never read as proof that the
  committed offsets are gone. Retirement refuses, issues no proof, and keeps the binding
  record naming what an operator must reconcile by hand.
- An operator may take that judgement on themselves with `--connector-already-absent`, which
  is how a generation whose connector was never registered, or whose earlier retirement
  removed the connector before being interrupted, is still retirable. The retirement then
  proceeds and its proof records the offsets as the operator's assertion rather than the
  worker's observation, because the worker was never in a position to make one. Neither named
  case can leave committed offsets behind: a connector that was never registered committed
  none, and a retirement that removed a connector had already deleted its offsets first, in
  the order above. What the switch actually covers is a connector deleted outside this control
  plane, which is the judgement it puts on the operator by name.
- After the connector, retirement removes the binding's public, progress, and SQL Server
  schema-history topics and their ACLs, then the provider capture artifacts, then the
  terminal incident state and the binding record last, and only against a validated cleanup
  proof that accounts for every artifact the record governs. The shared Connect offset store
  is cluster-scoped and is never removed by per-binding teardown.
- A normal stop retains all of it. Local destructive volume removal is the only workflow
  that may remove a binding record, and only in the same pass that removes every artifact
  the record governs.

### Local Bootstrap and E2E Entry Points

- The local Kafka and Kafka Connect infrastructure is engine-neutral. SQL Server is a
  first-class CDC provider, so neither the compose set nor the start sequence branches on
  the database engine, and one Connect worker service hosts whichever Debezium connector is
  registered for either provider.
- The Kafka Connect image is operator-supplied and identified by immutable digest. The CDC
  path takes it from an environment variable and fails closed when the value is not
  digest-qualified rather than falling back to a tag; a moving tag makes a registered
  connector's runtime unreproducible and leaves live read-back validation comparing against
  an unknown image. The non-CDC Kafka UI path keeps its tag default.
- The local entry points expose the workflow as an explicit opt-in - `-EnableKafkaCdc`, with
  an optional binding-state-store root - on the start script, the bootstrap wrapper, and the
  DMS E2E setup wrapper. Infrastructure opt-in alone starts Kafka and Kafka Connect and
  nothing else; the CDC opt-in additionally configures the projection target and status role
  before DMS starts and then runs the enable workflow.
- The opt-in refuses the shapes a single binding cannot cover, before any Docker or
  Configuration Service state exists: a run that starts no DMS for the workflow to observe,
  more than one configured data store, route-qualified data stores, a data store the run did
  not create, and an identity provider whose clients cannot carry the DocumentCache status
  role.
- The control-plane verbs run as a one-shot container on the local compose network rather
  than on the host. The instance database is registered in the Configuration Service under
  its container alias and the broker advertises a container-internal listener, so a
  host-side process resolves neither. The container is independent of the DMS image, so
  nothing in the control plane ties it to the local stack.
- The `-EnableKafkaCdc` opt-in itself is local-only. `start-published-dms.ps1` declares
  neither it nor the binding-state root, and the wrapper refuses the switch on the published
  path rather than half-running it. A published deployment that wants projection sets the
  three DocumentCache variables in its own environment file - which is why
  `published-dms.yml` reads them the same way `local-dms.yml` does - and drives the
  control-plane verbs itself. Blank is the default on both stacks and binds to no target.
- Before local Kafka Connect starts, the start script pre-creates the configured shared
  offset topic with `cleanup.policy=compact` and an explicit topic-level
  `min.insync.replicas`, and sets those values on a topic that already exists. A worker that
  reaches the broker first creates the topic itself and leaves `min.insync.replicas` to the
  broker default; the control plane validates an existing store rather than repairing it, and
  a broker default is not a topic-level override, so a Connect-first store would leave every
  verb refusing. The local values are the `local` durability profile's own and are read from
  the same shared resolver the verbs' arguments come from.
- The durable binding state store is a persistent root outside the bootstrap manifest. The
  manifest is prepared-input handoff, while a binding record outlives any one bootstrap run
  and lives at least as long as every artifact it governs.
- That root reaches the control-plane container as a host bind mount, and the setup image
  clears the group- and other-write bits from it before the tool runs. Docker Desktop presents
  any bind mount as world-writable whatever the host's own permissions are - including a
  directory the host itself created - and the state store refuses a group- or world-writable
  root, so every cdc verb would otherwise fail at its first binding read. The mount point is
  the one directory in the store's tree the store never creates for itself. Only the two
  rejected bits are cleared: an already-private root keeps the mode it has, and the owner bits
  a retirement's host-side binding discovery reads the records through are never touched.
- E2E setup creates a fresh database, provisions its current schema, and registers capture
  against that same database before the suite issues any write, so the initial enablement is
  admitted with write admission still closed.

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
- Provider tests cover the initial readiness and post-enablement lifecycle paths for
  PostgreSQL and SQL Server.
- Downstream-publication-history tests prove the E18 `activate-offline`, `deactivate-offline`,
  and `recover-cache-ahead` commands no longer receive the default `unknown` history: the
  shipped composition for both datastores resolves the CDC-backed provider, and the provider
  reports `internalOnly` only for a complete listing that holds a readable binding and none for
  the target, reporting `active`, `historical`, or `unknown` for every other evidence shape, an
  empty listing included.
- Diagnostics tests cover each implementation boundary without exposing secrets.

## Not Assigned to This Story

- Managed-provider-specific deployment automation is deployment work.
- Projector behavior is assigned to E18; message behavior is owned by the ADR and tested in
  19-05.
