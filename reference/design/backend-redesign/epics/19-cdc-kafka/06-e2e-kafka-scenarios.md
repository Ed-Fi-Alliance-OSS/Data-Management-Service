---
jira: DMS-1325
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1232
---

# Story: Add API-Driven Relational Kafka E2E Coverage

## Design References

- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)
- [Local bootstrap and CI](../../design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci)
- [Contract-to-evidence traceability](../../design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability)
- [Source-history continuity](../../design-docs/cdc/cdc-streaming.md#source-history-continuity)

These documents own the contracts; this story owns the remaining API-driven evidence.

## Outcome

Prove the complete relational CDC path on PostgreSQL and SQL Server:

API mutation → durable projection work → cache publication → provider capture
→ routed Kafka record → consumer-observed state.

Also prove canonical deletion publishes a tombstone independently of projection.
Legacy KafkaMessaging scenarios and helpers were removed in DMS-1239.

## Dependencies and Reuse

Build on completed E18 projection and E19 stories 19-00 through 19-05 and 19-07:

- Extend the [DMS E2E CDC area](../../../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/Cdc/), whose current fixture checks health only, using existing explicit CDC setup and governed teardown.
- Adapt [E18 API mutation scenarios](../../../../../src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/DocumentCacheCompletedProjectionScenario.cs) and [hosted projector restart mechanics](../../../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/DocumentCache/DocumentCacheHostedProjectorResumeTests.cs).
- Reuse DMS-1324's [public-message assertions](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractProviderAssertions.cs), [bounded consumers](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.MessageContract.cs), and provider fences.
- Reuse [managed lifecycle](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) and [recovery fault mechanisms](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcNativeRecoveryTests.cs).

E18 API-to-cache and E19 provider-to-Kafka evidence supply foundations, but neither
proves this complete path.

## Required Scenarios

Run these eight flows per provider using one ordinary resource type and one descriptor
type. All other flows use the ordinary resource. Flows may share fixtures and stacks.

| Flow | Required observable result |
| --- | --- |
| Ordinary-resource CRUD | Consume and assert created state before update, updated state before deletion, then the keyed null tombstone. |
| Descriptor CRUD | Establish the same results through the descriptor API path. |
| Overlapping update and projection | Older projection cannot acknowledge newer unprojected work; cache and consumer eventually converge. |
| Delete before first projection | Canonical deletion emits a tombstone; subsequent projection does not resurrect the document. |
| Online cache rebuild | After consuming published documents, run the supported online cache rebuild. Verify cache repopulation and return to `Tracking`; consumer state remains intact without domain tombstones. |
| Projector restart | After an actual executor restart, drain retained work across multiple pages and converge cache/Kafka state while ordinary API traffic remains available. |
| Unavailable continuity evidence | Report `unknown` and not ready; reject managed restart/resume without a terminal latch. API writes succeed during the fault. Fresh affirmative evidence permits recovery and a subsequent API mutation publishes on the same binding. |
| Proven history loss | Detect and durably retain `lost`, remain not ready, contain the connector, and reject managed restart/resume despite later healthy-looking observations. API writes succeed after containment. |

## Test Constraints

- Admit a fresh database with capture registered before API mutations. Direct provider
  writes are limited to fault injection or administrative arrangements. Do not reset
  the database, version sequences, capture artifacts, or offsets between flows.
- Observe durable work and cache publication. Assert the complete public record:
  binding-derived topic, UUID key, envelope/body, content version, stream ETag,
  partition, and record-level null tombstone. Derive expected bodies independently
  of cache/Kafka output, using API inputs/results and authoritative source metadata.
  Fold work-table exclusion assertions into these flows.
- Allow duplicates and coalesced work. Use bounded consumers and provider fences for
  absence assertions, preserving per-key ordering and replay-convergence semantics.
- Control the designated projector and prevent direct fill from satisfying paused,
  overlap, or recovery assertions. Keep test seams internal and narrowly scoped.
- For the single overlap interleaving, hold materialized version N, commit API N+1,
  and complete N. Before another attempt, assert work still requires N+1 and cache
  has not advanced to N+1; then release processing and verify convergence.
- Use a small backlog exceeding a deliberately small page size. Require focused
  execution evidence of retained-work recovery without a startup canonical/cache
  inventory scan; reference E18 for detailed paging/query-plan evidence.
- Inject exactly one unavailable-evidence fault and one actual history-loss fault per
  provider, after successful API-to-Kafka publication and affirmative continuity.
  Assert continuity outcomes and rejection reasons. Production observation/controller
  processing must detect and persist loss from a changed capture artifact or
  committed-offset prerequisite; directly seeding terminal state is insufficient.
- Isolate terminal loss to its own binding or an explicitly orchestrated final phase;
  do not rely on NUnit ordering. Native recovery may publish before revalidation.

## Completion Evidence

All eight flows pass on both providers through real API traffic, projection, capture,
the qualified connector image, and Kafka consumption. Extend the existing
[qualification runner](../../../../../eng/ci/Invoke-CdcQualification.ps1) and nightly/manual
workflow with one exercised setup/test/teardown path per provider. Missing or skipped
required scenarios fail qualification. Map stable scenario IDs to the applicable
portions of `CDC-INV-03`, `CDC-INV-06`, `CDC-INV-07`, and `CDC-INV-11`. Retain runtime/provider
inputs and bounded, sanitized setup, projection, connector, consumer-boundary, recovery,
and teardown diagnostics, including failures.

## Out of Scope

Exhaustive resource, message-format, concurrency, consumer-bootstrap, ACL, and
administrative-repair matrices remain with E18 and sibling stories. This story adds
no production projector-control API, generic fault framework, diagnostics platform,
volume benchmark, or PR gating change. Exactly-once delivery, strict native-recovery
fencing, and same-topic recovery after terminal history loss remain excluded.

## Clarifying Questions and Answers

### Questions 1

1. For deterministic pause, overlap, and restart scenarios, must the designated projector run inside the wrapper-started DMS container, or may the E2E fixture own a separate production projection runtime against the admitted database while disabling competing projection and direct fill in DMS? This determines where the internal materialization gates and executor-lifecycle controls must be introduced.
2. Does “ordinary API traffic remains available” require successful requests throughout the executor restart itself, or only while projection is paused and while retained work drains after restart? The referenced `DocumentCacheHostedProjectorResumeTests` recreates the DMS container and waits for health before checking API availability, so uninterrupted availability would require a different restart topology.

### Answers 1

1. Use a fixture-owned, non-HTTP production projection runtime against the wrapper-admitted database as the designated executor. Prefer this same topology across all eight flows per provider. Reuse `CdcProjectionRuntimeFactory` and the shared E18 runtime composition. This follows [process-local target selection](../../design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection) and reuses [19-04's non-HTTP runtime composition](04-bootstrap-enable-kafka-cdc.md#reuse-and-command-ownership); it adds no production projector-control API or new deployment mode.

   Complete normal wrapper admission first, then hand off projection ownership before scenario mutations. Roll out the HTTP DMS host with an effectively empty `DataManagement:DocumentCache:Targets` list and read acceleration disabled. Remove inherited indexed target environment entries from the generated Compose configuration; an empty array in another configuration source does not remove those entries. Retain the admitted target settings for the fixture/controller runtime. Await shutdown of previous executors and preserve lifecycle, durable work, binding, capture artifacts, and offsets.

   Pass the designated runtime to production controller status and lifecycle services, and serialize their operations with controlled test phases. Status observation does not start processing; managed connector start/restart/resume starts its supplied runtime. Reuse that runtime rather than creating a competing executor or suppressing production recovery behavior. Read projection status from the designated runtime.

   The factory currently builds its service provider internally. Add only a narrow internal composition seam for test decorators and execution observation. Gates must retain real materialization and cache-write/acknowledgement behavior: hold the actual materialized N candidate before publication, commit API N+1, complete the N attempt, and block the next attempt until the required intermediate assertions finish. Make gate waits bounded and cancellation-aware. Extract only the existing consumer/assertion helpers needed to attach to the wrapper-created stack, preserving the shared setup and teardown path. If flows share a binding, orchestrate terminal loss explicitly as the final phase.

2. Require successful ordinary API reads and writes across the executor restart, including while the old executor is stopped and while the replacement drains retained work. Keep the HTTP DMS host running throughout the measured scenario. Stop/dispose the designated runtime completely, then create/start a fresh instance; releasing a gate or changing a poll interval alone is insufficient. An OS-process crash is not required.

   Use bounded requests coordinated with executor lifecycle gates to prove availability before shutdown, after disposal and before replacement start, and during multi-page recovery. Verify writes made during the outage remain queued and subsequently converge in cache and Kafka. This tests the [projection/API-readiness separation](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md#projection-operational-health-caught-up-status-and-cdc-admission), without requiring continuous traffic or a general uptime or latency SLA. Adapt the referenced hosted-resume fixture's backlog and convergence assertions, replacing its whole-DMS-container restart with the independent runtime restart. Complete the initial HTTP configuration rollout before this measured interval; waiting for HTTP health after recreating DMS does not supply the required availability evidence.
