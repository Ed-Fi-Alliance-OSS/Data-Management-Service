---
jira: DMS-1325
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1232
---

# Story: Add API-Driven Relational Kafka E2E Coverage

## Design References

- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Local bootstrap and CI**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci
- **Contract-to-evidence traceability**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity

The referenced design sections define the supported E2E workflow and observable stream. This
story is only the work package for implementing the scenarios.

## Outcome

Prove the complete relational CDC upsert path on PostgreSQL and SQL Server:

API mutation → durable projection work → cache publication → provider capture
→ routed Kafka record → consumer-observed state.

Also prove that canonical deletion publishes a Kafka tombstone independently of
projection, including when deletion occurs before the first cache publication.

The legacy KafkaMessaging feature and helpers were removed in DMS-1239.
This story creates their relational replacement; there are no remaining legacy
ignore markers to remove.

## Dependencies

- Depends on 19-00 through 19-05 and the completed E18 upsert projection path.

## Existing Foundations

Reuse these implementations and their scoped evidence:

- CDC bootstrap and operational tooling qualified by 19-04 and 19-07.
- Explicit `-EnableKafkaCdc` setup for both providers, fresh-database admission,
  managed lifecycle, governed teardown, and setup-failure diagnostics.
- E18 API integration scenarios for ordinary-resource and descriptor mutations,
  durable enqueue, cache projection, acknowledgement, and enqueue-failure rollback.
- E19-S05 provider/broker fixtures, public-message expectations, bounded consumers,
  provider-position fences, and replay assertions.
- Existing provider/controller source-history and recovery fault mechanisms.

These layers do not replace this story's API-driven Kafka evidence. The current
DMS E2E CDC smoke fixture checks health only. Message-contract fixtures write
provider tables directly and do not run the API/projector path.

## Implementation Scope

The required matrix is eight scenario flows per provider. Use one ordinary resource
type and one descriptor type; the additional lifecycle and failure flows use only
the ordinary resource. These flows may share fixtures and contain multiple test
assertions; they do not require eight independently provisioned stacks.

| Scenario flow | Resource coverage |
| --- | --- |
| Create, update, and delete after a published upsert | One ordinary resource |
| Create, update, and delete after a published upsert | One descriptor |
| Newer API update overlapping an older projection attempt | Ordinary resource |
| Create followed by delete before first projection | Ordinary resource |
| Supported online cache rebuild without domain tombstones | Ordinary resource |
| Projector restart with a multi-page durable backlog | Ordinary resource |
| Unavailable source-history evidence and subsequent recovery | Ordinary resource |
| Proven source-history loss and retained terminal state | Ordinary resource |

### 1. CDC-Safe E2E Harness

- Run against a fresh, admitted database through the existing explicit CDC setup
  workflow, with capture registered before scenario writes.
- Keep scenario isolation from resetting the admitted database, version sequences,
  provider capture artifacts, or connector offsets. The ordinary feature-level
  E2E database reset is not suitable for these scenarios.
- For scenarios requiring paused or synchronized projection, ensure only the
  designated executor can process the tested work and prevent request-path direct
  fill from changing it. Use existing configuration and narrowly scoped test controls.
- Run terminal history loss on an isolated binding or as the final phase of an
  explicitly orchestrated fixture lifecycle. Do not rely on NUnit test ordering
  or attempt to restore the terminal binding for later scenarios.
- Adapt existing topic-consumer helpers and provider-position fences for bounded,
  deterministic assertions, including assertions that an operation emits no record.
- Retain bounded, sanitized setup, restart, teardown, projection, connector,
  provider-position, and consumer-boundary diagnostics on failure.

### 2. API Mutation and Public-Message Scenarios

For PostgreSQL and SQL Server:

- Exercise create, update, and delete requests through the API for the one ordinary
  resource type and one descriptor type selected for the matrix.
- In each CRUD flow, consume and assert the created state before issuing the
  update, then consume and assert the updated state before deletion. Allow
  duplicate deliveries.
- Observe durable work and cache publication, then verify the complete public
  record. Reuse existing contract assertion helpers and representative body
  expectations; derive dynamic expectations from API results and authoritative
  source metadata. Do not derive the expected document body solely from the cache
  or consumed Kafka record.
- Assert the binding-derived topic, UUID key, envelope/body, content version,
  stream ETag, routed partition, and record-level null tombstone.
- Fold projection-work exclusion assertions into these flows. The exhaustive
  source-operation exclusion matrix remains in DMS-1324.
- Avoid assuming one public record per successful API mutation: durable work
  coalesces, and delivery permits replay.

### 3. Conditional Acknowledgement and Ordering

- Exercise an API update overlapping an older projection attempt. Prove the older
  attempt cannot acknowledge away newer unprojected work and that projection and
  consumer state eventually converge. Use one deterministic interleaving with
  bounded synchronization: hold a materialized candidate for version N, commit API
  version N+1, then let the older attempt complete. Before permitting another
  projection attempt, assert that work still requires N+1 and the cache has not
  advanced to N+1. Finally release processing and verify convergence. Do not build
  a stress or exhaustive concurrency matrix.
- Cover API delete after a published upsert in the CRUD flows, plus one ordinary
  resource create followed by delete before projection.
- Complete one supported online cache rebuild of already-published documents.
  Verify cache repopulation and return to `Tracking`, then use a provider fence
  and bounded Kafka scan to verify no domain tombstones were emitted and consumer
  state remains intact. Other administrative lifecycle variants remain with their
  existing evidence owners.
- Preserve the public contract's per-key ordering and replay-convergence semantics;
  do not assert exactly-once delivery or monotonic state across tombstone replay.

### 4. Projector Restart and Backlog Recovery

- Accumulate a small API-created durable backlog exceeding the configured page
  size while projection processing is stopped. No lengthy outage or volume
  benchmark is required.
- Resume after an actual projection-executor restart and drain a backlog spanning
  multiple bounded pages.
- Prove recovery uses retained work without a full canonical/cache inventory or
  reconciliation scan at startup, using focused observable execution evidence
  rather than elapsed time alone. Normal per-document materialization and
  source/cache version lookups remain necessary; detailed query-plan coverage may
  reference existing E18 evidence.
- Prevent request-path direct fill from satisfying projector recovery assertions.
  Observe durable work, cache state, and executor activity without repairing the
  tested backlog through resource reads.
- Verify cache and Kafka convergence, and that post-admission projection backlog
  does not gate ordinary API traffic.
- Keep projector recovery distinct from existing Kafka Connect worker-restart tests.

### 5. Source-History Failure Scenarios

- Exercise one unavailable-evidence fault and one proven-history-loss fault per
  provider, with API-created ordinary-resource state and an observing Kafka
  consumer. Reuse existing fault mechanisms; the comprehensive failure matrix
  remains in the provider/controller suites.
- Establish successful API-to-Kafka publication and affirmative continuity evidence
  before injecting each fault. Assert the specific continuity outcome and rejection
  reason rather than combined readiness alone.
- For unavailable evidence, verify combined readiness is false and managed
  restart/resume is rejected without latching history loss; fresh affirmative
  evidence permits recovery. After removing the fault, verify successful publication
  of a subsequent API mutation on the same binding.
- For proven history loss, verify durable terminal state, connector containment,
  and managed restart/resume rejection despite later healthy-looking observations.
  Alter one fixture-owned capture artifact or committed-offset prerequisite and let
  the production observation/controller path detect and persist the incident.
  Directly seeding terminal state is not sufficient evidence.
  After verified containment, verify an API mutation still succeeds while managed
  restart remains rejected.
- Verify neither outcome changes ordinary API routing.
- Respect the documented native-recovery boundary: publication before controller
  revalidation is possible and must not be presented as strictly fenced.

### 6. Qualification and Traceability

- Prefer shared scenario logic and existing helpers. Keep provider-specific setup
  and faults explicit; allow small duplication where it avoids a new abstraction.
  Extend existing qualification and artifact handling. Keep necessary test seams
  internal and narrowly scoped.
- Add stable scenario identifiers mapped to the applicable portions of
  `CDC-INV-03`, `CDC-INV-06`, `CDC-INV-07`, and `CDC-INV-11`.
- Wire repeatable PostgreSQL and SQL Server API-driven CDC test selections into
  the existing nightly/manual CDC qualification workflow. Changing PR gating is
  outside this story.
- Qualify one supported setup/test/teardown entry point per provider end to end;
  both direct and build-based paths are not required. If the build-based path is
  selected, exercise it rather than relying on existing direct-setup evidence.
- Fail qualification for missing or skipped required scenarios and retain
  diagnostic artifacts when setup, execution, restart, or teardown fails.

## Acceptance Evidence

- Both provider lanes use real API traffic, durable projection, provider capture,
  the qualified connector image, routed topics, and Kafka consumption.
- Scenario assertions establish the complete API-to-consumer behavior; direct
  provider writes are limited to explicit fault injection or administrative test
  arrangements.
- Story-owned traceability identifies each executable scenario and the contract
  behavior it proves without claiming complete coverage of an entire invariant.
- Both lanes pass their required scenarios with no skipped acceptance cases.
- Retained evidence identifies provider/runtime inputs and includes setup,
  recovery, teardown, and failure outcomes.

## Not Assigned to This Story

- Exhaustive resource coverage, connector scaling, and the broader ACL matrix.
- Reimplementation of bootstrap, transforms, provider adapters, or consumer helpers
  already supplied by earlier stories.
- Duplication of exhaustive message-format, consumer-bootstrap, writer-concurrency,
  or administrative-repair matrices owned by other stories.
- A production projector-control API, generic fault-injection framework, new
  diagnostics platform, or long-running outage/volume benchmark.
- Strict pre-consumption fencing across native recovery, exactly-once delivery,
  or same-topic recovery after terminal source-history loss.
