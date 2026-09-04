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
- Provide and register the production
  `IDocumentCacheDownstreamPublicationHistoryProvider` so the E18 DocumentCache administrative
  command gate reads CDC-owned downstream-publication history. What counts as evidence, and
  what each answer authorizes, is owned by
  reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration.
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
- `enable` refuses while another generation of the same target is still bound. The enablement
  sequence is shared, so a replacement enters it naming the generation it just fenced, and that
  one generation is admitted; every other live generation refuses, and an `enable` entered
  directly admits none. The deployment-wide read the sequence already makes for the
  physical-source rule answers this one too. Adoption is not held to it: it reconstitutes the
  record of an artifact set that already exists and registers nothing.
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

How operators are told to use this surface — procedures, worked examples, and the judgements
each confirmation token puts on them — belongs to
reference/design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md rather than here.

### Explicit Projection Target Evidence

The explicit-projection-target proof reads `DataManagement:DocumentCache:Targets` from the
unmodified `IConfiguration` rather than through the CLI's DI graph, which overwrites
`DocumentCacheOptions.Targets` with the invocation's own arguments and would only confirm what
the operator typed. Because the proof is a configuration fact, the entry points configure the
target before DMS starts; infrastructure opt-in alone never implies a projection target. What
the proof must establish is owned by
reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection.

### Downstream Publication History for the E18 Administrative Gate

This story implements the evidence side of the read-acceleration rule owned by
[Relational CDC and Document Projection](../../design-docs/cdc/cdc-streaming.md#cache-backed-reads-and-domain-lifecycle),
and only the evidence side; what each record proves, and which observations reject, is the
owner's and is not restated here. The `Backend.Cdc.Control` provider replaces the default
abstraction that always answered `unknown`, and supplies `active` and `historical` from CDC
records that name the requested target and `unknown` from every other shape. It creates no
production path to `internalOnly`, and specifically none from the absence of a record — the E18
offline commands stay rejected exactly as they were under the default provider, and what this
one adds is the positive proof that keeps a published target out of them.

Three implementation facts are this story's own. The provider is registered by the packaged
administrative host ahead of the DocumentCache runtime services, whose registration of the
`unknown` default is conditional. Target matching maps the E18 empty tenant key onto the
binding `default` token, and every observation carries the currently resolved
physical-source fingerprint, including the rejecting ones, because the E18 evaluator checks the
fingerprint before the status. The deployment key is read from raw configuration rather than
through the bound control options, whose validation the administrative host defers to a `cdc`
verb; binding it here would make every DocumentCache command fail on CDC configuration it does
not use.

### Lag and the Metrics Bridge

Connector lag is Debezium's `MilliSecondsBehindSource` current value plus its P50, P95, and P99
attributes, never derived from the progress topic; the lag observation contract and the
readiness consequences of `Unknown` are owned by
reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness.

This story's resolution is the bridge those values are read over: Jolokia, activated with
`ENABLE_JOLOKIA=true` on the Kafka Connect service, at the port 8778 the Connect image's
entrypoint fixes, so the reader derives the bridge from the Connect host and that port unless
an explicit metrics base URI is supplied. The Prometheus JMX exporter is not an alternative on
this image: its entrypoint branch targets a port whose agent jar the image does not ship. An
unavailable bridge yields `Unknown`.

### Retirement and Teardown Ordering

The removal order, the offsets-before-configuration rule, the read-back that proves the fence,
and what an absent connector does to a retirement are owned by
reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding.
Operator guidance for the confirmation token, `--connector-already-absent`, and reading a
binding record's tenant token back into a verb's `--tenant-key` belongs to
reference/design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md.

This story owns how the controller spends its budgets and classifies its steps. Every provider
pass runs under the configured provider-setup budget — the create pass, the validate-only pass
each verb composes its evidence from, and the retirement's own artifact teardown — because the
CLI adds no wall clock of its own and an unbounded provider call would hold a verb open
indefinitely. A pass that spends its budget is a failed step, and a retirement whose provider
teardown times out ends with no proof and its binding record intact, exactly as any other
failed step there does. The connector-state read-back is bounded by the Connect request timeout
as elapsed time rather than as a number of reads, because each state read carries that timeout
of its own.

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
- The enable phase takes the binding generation from that store rather than fixing it at the
  target's first. A live binding record for the instance key is the generation the control plane
  already holds, so the phase reuses it: an enable that wrote its binding and then failed during
  activation, Kafka setup, connector registration, or readiness is completed by being reissued,
  and only against the generation that record names — asking for the next one makes the rerun a
  first attempt, which the control plane refuses while that generation is still live. More than
  one live binding record for the instance key stops the phase rather than being chosen between.
  With no live record it allocates one past the highest generation the store has ever held for
  the instance key, retirement records included, because retirement removes the binding record it
  retires and leaves the retirement record as the only trace of it. Reading the live bindings
  alone would make a retired generation look unallocated, and this root survives the destructive
  volume removal that destroys the database the generation was bound to - so the next stack would
  ask to bind that same generation to a new physical source, which the control plane refuses and
  v1 never does. Retirements are counted for that reason and are never reused. The governed names
  carry the generation, so a second local cycle publishes under its own connector, topics, and
  consumer state rather than reusing the retired ones. A store that cannot be enumerated, or a
  record not named for a generation, fails the phase rather than allocating over what it could
  not read.
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
- Downstream-publication-history tests prove the shipped composition for both datastores
  resolves the CDC-backed provider rather than the default that always answered `unknown`, and
  that the provider reports `active` for a binding on the resolved physical source,
  `historical` for a binding on an earlier one or a retirement naming the target, and `unknown`
  for every other evidence shape — an unreadable listing, an empty one, and a listing whose
  records all name other targets included.
- Diagnostics tests cover each implementation boundary without exposing secrets.

## Not Assigned to This Story

- Managed-provider-specific deployment automation is deployment work.
- Projector behavior is assigned to E18; message behavior is owned by the ADR and tested in
  19-05.
