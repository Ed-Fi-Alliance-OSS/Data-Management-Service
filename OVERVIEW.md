# DocumentCache and Relational CDC/Kafka Planning Overview

Prepared for scrum planning from the E18/E19 story files, the central
[dependency analysis](reference/design/backend-redesign/epics/DEPENDENCIES.md), and
read-only Jira review on July 28, 2026. The recommended points below are planning
recommendations; all target stories were unpointed in Jira at review time, and neither
Jira nor the epic/story source files were changed.

## Epic introductions

| Epic | Brief explanation |
| --- | --- |
| [DMS-1308](https://edfi.atlassian.net/browse/DMS-1308) — `dms.DocumentCache` Projection | Adds an optional, durable document projection that asynchronously materializes relational data into `dms.DocumentCache`, serves only provably fresh cache rows, and falls back to the canonical relational path for correctness. It also supplies lifecycle administration, recovery, health, telemetry, provider parity, and operator guidance without making normal API availability depend on projection health. |
| [DMS-1309](https://edfi.atlassian.net/browse/DMS-1309) — Relational CDC/Kafka Streaming | Builds PostgreSQL and SQL Server CDC/Kafka publication on top of the E18 projection, including provider capture, a DMS-specific Kafka Connect transform, connector/bootstrap tooling, source binding and readiness, contract/E2E evidence, and runbooks. Initial admission waits for projection catch-up and a provider heartbeat barrier while writes are closed; later projection backlog remains observational and does not gate normal API routing. |

Epics are not separately pointed; their story estimates are the planning units.

## E18 story introductions and estimates

| Story | Brief explanation | Points | Immediate implementation dependency |
| --- | --- | ---: | --- |
| [DMS-1310](https://edfi.atlassian.net/browse/DMS-1310) — Finalize DocumentCache schema and provider DDL | Defines and provisions the cache, durable work queue, lifecycle/safety state, data-store identity, indexes, and transactional enqueue triggers for PostgreSQL and SQL Server. It also proves rerun safety, restricted-writer trigger behavior, rollback on enqueue failure, and provider-equivalent schema/security behavior. | **3** | E02 DDL/provisioning and E10 stamps; both are complete. |
| [DMS-1311](https://edfi.atlassian.net/browse/DMS-1311) — Add configuration and target selection | Adds typed configuration, target discovery/replacement, lifecycle command contracts, provider prerequisite checks, and target-scoped diagnostics. Configuration scaffolding can proceed independently, while durable-state, trigger, and activation-preflight integration waits for DMS-1310. | **2** | DMS-1310 for integrated validation; scaffolding can start now. |
| [DMS-1312](https://edfi.atlassian.net/browse/DMS-1312) — Add reusable document materialization | Creates a caller-independent service that reconstructs the latest canonical document through existing compiled read plans and returns validated materialization results for the projector, direct fill, and CDC fixtures. | **2** | DMS-1310 plus the completed E08 read/reconstitution and E10 update-tracking services. |
| [DMS-1313](https://edfi.atlassian.net/browse/DMS-1313) — Implement monotonic cache upsert and delete fencing | Implements the shared atomic cache-write/conditional-ack component, suppressing stale writes and preserving newer work across races, deletes, crashes, and duplicate workers. Its dual-provider concurrency and lock-order guarantees make this a high-complexity correctness story. | **3** | DMS-1310, DMS-1312, E10 stamps, and E11 delete behavior. |
| [DMS-1314](https://edfi.atlassian.net/browse/DMS-1314) — Add the asynchronous reconciliation loop | Adds bounded, fair, restart-safe queue processing plus serialized activation, deactivation, rebuild, scrub, baseline, and cache-ahead recovery workflows. It owns the database-scoped administrative mutex and the most extensive scheduling, failure, recovery, and concurrency behavior in E18. | **3** | DMS-1310 through DMS-1313. |
| [DMS-1315](https://edfi.atlassian.net/browse/DMS-1315) — Add cache-backed reads with fallback | Integrates cache lookup into GET/query response assembly, using a cache row only when lifecycle, safety latch, and content version prove it fresh; every other state uses the existing relational path. It also adds optional direct fill through the shared materializer/writer and preserves authorization behavior. | **2** | DMS-1310 through DMS-1314. |
| [DMS-1316](https://edfi.atlassian.net/browse/DMS-1316) — Add health, readiness, and telemetry | Exposes separate projection operational-health and caught-up status with bounded, sanitized lifecycle, queue, worker, backlog, and failure observations. Health polling must use indexed observations rather than scanning canonical/cache data, and it remains independent of canonical API routing. | **2** | DMS-1310, DMS-1311, DMS-1314, and DMS-1315. |
| [DMS-1317](https://edfi.atlassian.net/browse/DMS-1317) — Add integration coverage and runbooks | Exercises the completed E18 feature across both providers, including queueing, races, crashes, recovery, activation, rebuild, scrub, direct fill, performance, and no-scan guarantees, then documents the shipped operational workflows. This is a broad cross-story qualification package rather than a narrow test-only change. | **3** | DMS-1310 through DMS-1316. |
| [DMS-1318](https://edfi.atlassian.net/browse/DMS-1318) — Add representation-restamp utility | Adds a non-interactive, resumable PostgreSQL/SQL Server administration command for byte-changing representation corrections, with preview/confirmation, manifests, progress, mutex protection, lifecycle-mode validation, transactional enqueue, and final verification. | **3** | DMS-1310, DMS-1311, DMS-1312, DMS-1314, DMS-1316, and completed E10 behavior. |

## E19 story introductions and estimates

| Story | Brief explanation | Points | Immediate implementation dependency |
| --- | --- | ---: | --- |
| [DMS-1319](https://edfi.atlassian.net/browse/DMS-1319) — Add deployment-owned CDC binding and readiness | Adds durable deployment-owned source binding and incident state, provider position/history adapters, and status aggregation across projection, database, connector, and Kafka observations. It implements the guarded initial-readiness sequence while keeping later backlog and failures distinct from normal API health. | **3** | DMS-1320, DMS-1321, DMS-1311, DMS-1313, DMS-1314, and DMS-1316. |
| [DMS-1320](https://edfi.atlassian.net/browse/DMS-1320) — Emit/provision provider CDC support | Provisions the PostgreSQL publication and SQL Server CDC/key/heartbeat inventory, least-privilege connector access, binding-aware validation, and continuity metadata queries. It explicitly proves that projection-work rows are neither captured nor granted to the CDC reader. | **3** | DMS-1310. |
| [DMS-1321](https://edfi.atlassian.net/browse/DMS-1321) — Generate connector templates | Generates validated PostgreSQL and SQL Server connector configurations with exact capture lists, binding identity, transform, heartbeat, topic, offset, and runtime settings. Renderer code and tests can start early, but live image/provider qualification needs DMS-1320 and the transform from DMS-1322. | **2** | DMS-1320 and DMS-1322 for completion; rendering scaffolding can start now. |
| [DMS-1322](https://edfi.atlassian.net/browse/DMS-1322) — Add the `DocumentState` transform | Implements and packages the DMS-specific Kafka Connect transform that classifies provider records, emits the public document/progress shapes, normalizes heartbeat keys, and fails closed if projection-work data appears. | **2** | No unfinished E18/E19 implementation dependency; the DMS-1245 design prerequisite is complete. |
| [DMS-1323](https://edfi.atlassian.net/browse/DMS-1323) — Add bootstrap connector registration | Orchestrates the complete local/deployment-controller workflow: new-database admission, immutable binding, guarded projection activation, provider/Kafka provisioning, connector registration, heartbeat/caught-up barriers, restart/adoption, and teardown. Its multi-system sequencing and retry/fail-closed behavior make it one of the largest E19 stories. | **3** | DMS-1319 through DMS-1322 plus the E18 activation, queue, and status inputs. |
| [DMS-1324](https://edfi.atlassian.net/browse/DMS-1324) — Add message contract tests | Adds serialized-record, provider, broker-backed, routing/ordering, failure, sizing, progress, and reference-consumer conformance suites for both providers. The fast/provider portions can begin once source setup and the transform exist; readiness cases also require DMS-1319 and DMS-1323. | **3** | DMS-1320 and DMS-1322; DMS-1319/DMS-1323 for broker readiness; DMS-1312 is soft. |
| [DMS-1325](https://edfi.atlassian.net/browse/DMS-1325) — Replace legacy Kafka E2E expectations | Replaces quarantined legacy messaging tests with real API-to-projection-to-provider-CDC-to-Kafka scenarios for PostgreSQL and SQL Server, including restart and long-backlog recovery. | **3** | DMS-1319 through DMS-1324 and the completed E18 upsert/projection path. |
| [DMS-1326](https://edfi.atlassian.net/browse/DMS-1326) — Add CDC runbooks | Publishes verified setup, monitoring, security, sizing, troubleshooting, recovery, containment, replacement, and retirement guidance for both providers. This is larger than a documentation-only story because its commands and destructive procedures must be exercised against the shipped implementation. | **2** | DMS-1317 and completed DMS-1319 through DMS-1325. |

## Parallel-start plan

All external epic prerequisites named by E18/E19—E02, E08, E10, E11, and E16—were
`Done` in Jira at review time. Within these two epics, the useful parallel lanes are:

| Stage | Work that can proceed in parallel | Boundary before the next stage |
| --- | --- | --- |
| **Now** | **DMS-1310** (already In Progress); **DMS-1311 configuration/contract scaffolding**; **DMS-1322** in full; and **DMS-1321 renderer/template scaffolding**. | DMS-1311 integrated validation and DMS-1321 live qualification remain blocked on their named dependencies. |
| **After DMS-1310** | Finish DMS-1311 integration, implement **DMS-1312**, and implement **DMS-1320** concurrently. | DMS-1313 needs DMS-1312; DMS-1321 completion needs DMS-1320 and DMS-1322. |
| **Two independent middle lanes** | Projection lane: **DMS-1313**, followed by DMS-1314. Kafka lane: finish **DMS-1321** and begin the non-readiness portions of **DMS-1324** once DMS-1320/DMS-1322 are ready. | The E18 projection spine then remains intentionally sequential: DMS-1314 → DMS-1315 → DMS-1316. |
| **After DMS-1316** | **DMS-1317** and **DMS-1318** can run together. **DMS-1319** can also run in parallel once its DMS-1320/DMS-1321 inputs are ready; it does not require DMS-1317 or DMS-1318. | DMS-1323 waits for DMS-1319 and the connector/provider/transform foundation. |
| **Kafka integration finish** | Implement **DMS-1323**; complete the broker-readiness portion of **DMS-1324** as bootstrap becomes available. | **DMS-1325** requires DMS-1319 through DMS-1324. |
| **Closeout** | Run **DMS-1325**, then complete **DMS-1326** after DMS-1317 and all earlier E19 tooling/scenarios are complete. | DMS-1326 is the terminal documentation/verification story. |

The critical path is therefore approximately
`1310 → 1312 → 1313 → 1314 → 1315 → 1316 → 1319 → 1323 → 1325 → 1326`,
with DMS-1311 joining before DMS-1314, DMS-1320/DMS-1322/DMS-1321 forming the
parallel Kafka foundation, DMS-1324 joining before DMS-1325, and DMS-1317 joining
before DMS-1326.

## Jira sizing calibration

The comparison used completed DMS tickets with known Jira story points:

| Jira scale | Representative completed tickets | Calibration used here |
| ---: | --- | --- |
| **1** | [DMS-943](https://edfi.atlassian.net/browse/DMS-943), focused descriptor DDL; [DMS-1289](https://edfi.atlassian.net/browse/DMS-1289), extension of an existing scheduled-smoke workflow; [DMS-1195](https://edfi.atlassian.net/browse/DMS-1195), documentation-only workflow guidance | A narrow change at an established seam with limited new orchestration or integration behavior. |
| **2** | [DMS-937](https://edfi.atlassian.net/browse/DMS-937), core DDL/triggers; [DMS-978](https://edfi.atlassian.net/browse/DMS-978), configuration/fail-fast behavior; [DMS-1105](https://edfi.atlassian.net/browse/DMS-1105), document load/reconstitution; [DMS-1240](https://edfi.atlassian.net/browse/DMS-1240), Kafka Connect SMT; [DMS-809](https://edfi.atlassian.net/browse/DMS-809), Kafka create/update/delete E2E; [DMS-1228](https://edfi.atlassian.net/browse/DMS-1228), structured logging | A bounded component or end-to-end slice with substantial tests but one dominant implementation concern. |
| **3** | [DMS-961](https://edfi.atlassian.net/browse/DMS-961), dual-provider DB-apply infrastructure; [DMS-1124](https://edfi.atlassian.net/browse/DMS-1124), complex cross-provider persistence/concurrency; [DMS-1243](https://edfi.atlassian.net/browse/DMS-1243), end-to-end CMS SQL Server backend; [DMS-860](https://edfi.atlassian.net/browse/DMS-860), new route-context E2E project | Broad cross-provider or cross-subsystem work with difficult concurrency/orchestration, several acceptance-evidence layers, or a new operational workflow. |

No E18/E19 story is recommended as **1 point**. Even the narrowest stories add a new
runtime/provider component plus meaningful integration evidence; the runbook story also
requires exercised two-provider procedures rather than prose alone.
