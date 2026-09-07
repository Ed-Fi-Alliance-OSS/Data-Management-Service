---
jira: DMS-1325
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1232
---

# Story: Replace Legacy Kafka E2E Expectations

## Design References

- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Local bootstrap and CI**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci
- **Contract-to-evidence traceability**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity

The referenced design sections define the supported E2E workflow and observable stream. This
story is only the work package for implementing the scenarios.

## Outcome

Replace the quarantined legacy KafkaMessaging scenarios with API-driven relational CDC
coverage.

## Dependencies

- Depends on 19-00 through 19-05 and the completed E18 upsert projection path.

## Implementation Scope

- Update DMS-1232 fixtures and assertions to consume the relational public contract.
- Integrate E2E setup/teardown with the explicit bootstrap CDC workflow.
- Add topic-consumer helpers and failure diagnostics.
- Drive API mutations into durable projection work and verify conditional
  acknowledgement, cache publication, and the unchanged public record.
- Add projector stop/restart and long-backlog recovery scenarios that resume from durable
  work without a startup canonical/cache scan on PostgreSQL and SQL Server.
- Add the cache lifecycle, ordering, and source-history scenarios
  assigned to this story by the design traceability table.
- Remove legacy ignore markers after the relational lanes are stable.

## Acceptance Evidence

- PostgreSQL and SQL Server lanes use real provider capture, connectors, routed topics,
  and API traffic.
- Story-owned traceability maps each E2E test identifier to the `CDC-INV-*` contract ID it
  proves.
- Setup, restart, teardown, and failure artifacts are retained by the test harness for
  diagnosis.

### Explicit Acceptance Criteria: DMS-1326 T14 Fixture Handoff

The PostgreSQL operator exercise in DMS-1326 T14 depends on the reusable E2E fixture
delivered by this story. The criteria below make that handoff executable; they supplement
the existing two-provider acceptance evidence and do not replace the design contracts.
The consuming procedures are the [API smoke](../../../../cdc-documentation/operations-runbook.md#local-api-smoke),
[planned stop/restart](../../../../cdc-documentation/operations-runbook.md#local-stop-restart),
and [source replacement](../../../../cdc-documentation/operations-runbook.md#replace-physical-source)
exercises.

1. **Executable selection.** Deliver API-to-Kafka scenarios in
   `src/dms/tests/EdFi.DataManagementService.Tests.E2E` and reusable database/consumer
   helpers. Publish the concrete scenario/fixture identifiers, exact PostgreSQL
   `dotnet test --filter` selections, required configuration names, and starting
   directories in the E2E README. Include test discovery and execution commands with
   TRX output and nonzero expected case counts for each selected exercise. No placeholder
   filter, ignored scenario, skipped run, or zero-case selection satisfies this handoff.
2. **Attach to the admitted wrapper target.** Demonstrate the fixture after
   `pwsh ./setup-local-dms.ps1 -EnvironmentFile ./.env.e2e -DatabaseEngine postgresql -EnableKafkaCdc`
   from the E2E directory, using a disposable database, self-contained identity provider,
   and digest-qualified `DMS_CDC_CONNECT_IMAGE`. Reuse the wrapper's generated DDL,
   including `dms."EffectiveSchema"`, and its admission result. Document how the fixture
   receives the effective environment file, API/authentication configuration, database
   connection, actual CMS data-store/tenant selection, physical-source identity, binding
   generation, shared state root, connector, and generated public topic. Resolve these
   from the provisioned target and binding; do not assume data-store ID or generation `1`.
   Set test-process `AppSettings__DataStoreDatabaseName` to `E2E_DATABASE_NAME` and use
   `env -u NODE_OPTIONS dotnet test` on Linux. Document reachable broker/Connect addresses
   from the consumer's execution context, including Kafka advertised-address resolution.
   Ordinary `build-dms.ps1 E2ETest` is not the CDC opt-in entry point.
3. **Prerequisites and lifecycle preservation.** Before API seeding or mutation, verify
   that DMS, the projector, provider capture, connector, and consumer refer to that same
   admitted target and generation, and collect current readiness/continuity evidence.
   Missing provider/image/broker/configuration, an unreadable binding tree, or a target
   mismatch is an explicit unmet prerequisite. Respect owner-only state permissions and
   the host/container identity mapping. The attached fixture must not rerun destructive
   setup or let ordinary E2E reset hooks truncate/reprovision the bound database, change
   its source identity, reset offsets, or discard binding state between observations.
   Run shared `dms-local` exercises sequentially.
4. **Real API upsert/delete evidence.** Use synthetic resources created and updated
   through authenticated DMS API calls, then deleted through the API. Capture responses
   and correlate document UUIDs/versions with durable projection work, its conditional
   acknowledgement, cache publication, real PostgreSQL capture, the generated connector,
   and records consumed from the binding's public topic. Assert the public key/envelope
   and resulting consumer state against the message contract, including the same-key
   record-level null tombstone from canonical deletion. Observe each expected upsert
   before the next update or delete of that resource so asynchronous projection cannot
   legitimately coalesce away the intended observation. Use bounded waits with failure
   diagnostics; direct SQL mutations, direct Kafka production, and mocked records do not
   supply this evidence.
5. **Reusable public consumer.** Reuse the E19-05 contract/consumer components and expose
   connection, topic, group/state namespace, timeout, and artifact configuration for the
   provisioned environment. Capture partition/offset and key/value evidence and applied
   state. When claiming bootstrap or checkpoint continuity, use the design's earliest-
   offset scan, per-partition end barriers, durable application, and checkpoint rules.
   Keep those consumer barriers distinct from provider-source readiness barriers. Support
   repeated observation of the same generation without silently discarding checkpoints
   or changing topics; no separate runbook shell consumer is required.
6. **Planned stop/restart handoff.** Allow T14 to retain the fixture's target and consumer
   context while invoking shipped `cdc stop`, restarting the existing Connect worker,
   verifying that its persisted stop survives, and invoking guarded `cdc restart`.
   Supply an executable API/consumer observation after successful restart, with continued
   delivery on the same generation/topic and retained offsets/state. Preserve the stop,
   worker read-back, restart, and status outcomes separately; a command exit code alone
   does not establish readiness or resumed API publication.
7. **Successful new-database replacement handoff.** Supply reusable provisioning and
   configuration helpers for a distinct disposable database created for initial CDC
   provisioning, with generated DDL, a different physical-source identity, and no prior
   canonical writes. Make the CMS/DMS/projector target handoff and external write-admission
   sequencing explicit so T14 can invoke shipped `cdc replace-source` with the outgoing
   and higher new generation. Demonstrate admitted replacement, new-source capture, and
   API upsert/delete delivery using a new topic and independent consumer namespace/bootstrap.
   Retain the outgoing binding, connector/offset evidence, and original-source connection
   reference for guarded retirement. A replacement refusal or mocked admission does not
   satisfy this criterion; no restore, identity-rotation, or same-topic reset workflow is
   introduced.
8. **Diagnostics, evidence, and cleanup.** Retain repository revision, tool/provider/image
   versions and digests, sanitized configuration/commands and JSON, native exit codes,
   exact test identifiers and pass/fail/skip counts, API/consumer observations, and artifact
   paths. Expose bounded provider and status observations for T14 to inspect capture/grant
   exclusion of `DocumentProjectionWork`, heartbeat/source barriers, and lag; label local
   ACL-disabled results and link separate authorizer-backed evidence. On failure capture
   bounded DMS/projector, provider capture/lag, connector/task, and consumer diagnostics
   plus relevant Docker logs. Keep credentials out of artifacts.
   Provide the matching engine/effective-environment teardown handoff, preserve shared
   state during per-binding retirement, and retain cleanup outcomes and host retirement
   history after disposable teardown, including on partial failure. Map each claimed
   behavior to its owning `CDC-INV-*` ID and evidence layer so T14 can link actual results.

T14 owns running and reconciling the PostgreSQL runbook exercises. Its host-state ownership
documentation, bounded capacity observations, and operator assertions owned by DMS-1326
T12/T13/T17 remain there; this story supplies the reusable E2E handoff and successful API
publication evidence. Reuse existing E18 recovery/rollback evidence and existing CDC
control/lifecycle fixtures. This handoff does not require a second bootstrap, controller,
state store, or an implementation of deferred recovery workflows. Adding these criteria
does not itself satisfy T14's missing-harness prerequisite.

### Explicit Acceptance Criteria: DMS-1326 T15 Fixture Handoff

The SQL Server operator exercise in DMS-1326 T15 requires the following reusable
fixture handoff. These criteria supplement the two-provider acceptance evidence and
the PostgreSQL handoff above. The consuming procedures are the
[SQL Server setup](../../../../cdc-documentation/operations-runbook.md#local-sqlserver),
[API smoke](../../../../cdc-documentation/operations-runbook.md#local-api-smoke),
[planned stop/restart](../../../../cdc-documentation/operations-runbook.md#local-stop-restart),
and [source replacement](../../../../cdc-documentation/operations-runbook.md#replace-physical-source)
exercises. T15's [prerequisite review](../../../../cdc-documentation/cdc-inv-evidence.md#t15-sqlserver-prerequisite-review)
records the resolved wrapper input and the still-missing API harness.

1. **Executable SQL Server selection.** Deliver scenarios in
   `src/dms/tests/EdFi.DataManagementService.Tests.E2E` with reusable bootstrap, database,
   connector, broker, and public-consumer helpers. Publish concrete fixture/scenario
   identifiers, exact SQL Server `dotnet test --filter` selections, starting directories,
   configuration names, discovery commands, TRX output paths, and expected nonzero case
   counts for each exercise in the E2E README. Report executed fully qualified names and
   per-case pass/fail/skip results. The existing CLI selection
   `Category=MssqlIntegration&(Category=Runbook|Category=CdcShippedComposition)` is
   separate operator evidence: `CdcShippedComposition` is currently PostgreSQL-only,
   so passing SQL Server Runbook cases cannot establish wrapper/API coverage. Skipped,
   ignored, zero-case, or provider-independent results do not satisfy this handoff.
2. **Consume the E19-04 policy handoff before destructive setup.** Require the owning
   [bootstrap story](04-bootstrap-enable-kafka-cdc.md) to deliver
   `DataManagement:DocumentCache:Cdc:SqlServerPollInterval` into the one-shot control-plane
   container for enable, subsequent status, and guarded restart. Document its supported
   input and capture that all three receive the same positive value, no greater than the
   effective heartbeat interval. The shared wrapper now supplies the local policy through
   `docker compose run -e`; verify this delivery before database recreation. Direct
   deployments that omit the interval still receive the renderer diagnostic
   `CDC_TEMPLATE_SQLSERVER_POLL_INTERVAL_REQUIRED`; a host export alone is not container
   delivery. The fixture consumes the shared handoff rather than
   adding a shell override or its own connector-policy implementation.
3. **Attach to the actual admitted SQL Server target.** Demonstrate the fixture after
   `pwsh ./setup-local-dms.ps1 -EnvironmentFile ./.env.e2e -DatabaseEngine mssql -EnableKafkaCdc`
   from the E2E directory, with a prepared disposable SQL Server 2025 environment,
   self-contained identity provider, and digest-qualified `DMS_CDC_CONNECT_IMAGE`.
   Verify intended admin connectivity, capture infrastructure, separate connector login,
   image availability and qualification before setup. Retain the resolved engine-overlay
   environment path printed by setup, `E2E_DATABASE_NAME`, the distinct
   `E2E_SNAPSHOT_DATABASE_NAME`, and generated DDL/`dms.EffectiveSchema` evidence. Attach
   to the admitted primary database, not its snapshot derivative or another test database.
   Resolve the actual CMS tenant/data-store selection, physical-source fingerprint,
   generation, connector, public/progress/schema-history topics, and shared binding-state
   root from the provisioned target and binding; do not assume ID or generation `1`.
   Document API/authentication and provider connections, host/container broker and Connect
   reachability, advertised-address resolution, and owner-only state access. Set
   `AppSettings__DataStoreDatabaseName` to the effective `E2E_DATABASE_NAME` and use
   `env -u NODE_OPTIONS dotnet test` on Linux. Ordinary `build-dms.ps1 E2ETest` does not
   supply CDC opt-in.
4. **SQL Server prerequisite evidence and safe fixture lifecycle.** Before API seeding,
   retain initialization and activation evidence for `READ_COMMITTED_SNAPSHOT ON`,
   server `nested triggers` with `value_in_use = 1`, and `ALLOW_SNAPSHOT_ISOLATION ON`
   for the connector's `snapshot.isolation.mode=snapshot`. Link exact existing E18 provider
   results for Disabled-only initialization
   correction/restart and activation correction/retry; document that post-validation
   changes on active targets and prerequisite-failure recovery in other lifecycles are
   unsupported. Do not introduce settings changes as an admitted-target repair.
   Verify DMS, projector, capture, connector, and consumer use the same admitted source
   and generation. Prevent ordinary E2E reset hooks from truncating/reprovisioning it or
   resetting source identity, offsets, or binding state between observations. Run shared
   `dms-local` exercises sequentially and preserve the environment across lifecycle steps.
5. **Real SQL Server API upsert/delete publication.** Create, update, and delete synthetic
   resources through authenticated DMS API calls. Capture responses and correlate document
   UUIDs/versions with durable projection work, conditional acknowledgement, cache
   publication, real SQL Server capture, the generated connector, and consumed public
   records. Assert the contract key/envelope, applied consumer state, and same-key
   record-level null tombstone from canonical deletion. Await each expected upsert before
   the next update or delete of that resource to avoid legitimate projection coalescing.
   Use bounded waits with per-layer diagnostics. Direct SQL mutations, handcrafted Kafka
   records, mocks, and ordinary API tests without consumer observations cannot substitute.
6. **Reusable consumer and SQL Server continuity observations.** Reuse E19-05 consumer
   components with explicit connection, topic, group/state namespace, timeout, and artifact
   configuration. Retain key/value, partition/offset, applied-state, and checkpoint evidence.
   Bootstrap/checkpoint claims follow the design's earliest-offset scan, per-partition end
   barriers, durable application, and checkpoint rules. Expose the shipped SQL Server
   readiness/continuity evidence: post-caught-up heartbeat capture after-image, barrier
   commit/change LSNs, and the matching Connect source partition and committed
   `commit_lsn`, `change_lsn`, `event_serial_no` tuple. Reuse the owning adapter's comparison
   and retained-range validation; snapshot/null offsets do not prove streaming readiness.
   Keep provider-source barriers distinct from consumer end barriers and preserve the
   same-generation consumer namespace/checkpoints across repeated observations.
7. **Planned stop and guarded restart.** Retain target, policy, state, and consumer context
   while T15 invokes shipped `cdc stop`, verifies the registered connector is `STOPPED`,
   restarts the existing Connect worker, proves that the persisted stop survived, and
   invokes guarded `cdc restart`. Capture each command, worker read-back, continuity and
   status result separately. Supply executable post-restart API upsert/delete observations
   on the same generation/topic with retained offsets and consumer state. Successful
   command exit alone does not prove readiness or resumed publication. Do not rerun setup,
   reset offsets, recreate capture/history, or directly resume through Connect to pass.
8. **Successful new-database replacement and operator-fixture reuse.** Supply reusable
   SQL Server provisioning/configuration helpers for a distinct fresh database with
   generated DDL, satisfied provider prerequisites, a different physical-source identity,
   and no prior canonical writes. Make CMS/DMS/projector reconfiguration and external
   write-admission sequencing explicit so T15 can run shipped `cdc replace-source` with
   the outgoing and higher new generation. Demonstrate admitted replacement, the outgoing
   persisted fence, new-source capture, and API upsert/delete delivery on the new public
   topic with an independent consumer namespace/bootstrap. Preserve outgoing records,
   offsets, governed artifacts and original-source connection reference for retirement.
   Replacement refusals and mocked admissions do not establish successful cutover.
   Publish reusable lifecycle fixture entry points or exact existing SQL Server results
   for controlled missing-state adoption, guarded retirement, partial-retirement retry,
   default-tenant selection, and packaged downstream-history rejection. Map each to the
   same documented operator path and identify whether it actually opens a SQL Server
   connection. Reuse T12/T13/T17 assertions; do not delete live deployment state to stage
   adoption or duplicate their controllers and fault-injection machinery.
9. **Bounded provider inspection and diagnostics.** Expose generated identities and
   bounded observations for all three capture instances (`dms.Document`, `dms.DocumentCache`,
   `dms.CdcHeartbeat`), capture/cleanup jobs, retained LSN ranges, connector lag,
   and exclusion of `DocumentProjectionWork` from capture and connector grants. Retain
   schema-history identity/policy and connector-only access evidence; label local
   ACL-disabled results and link separate authorizer-backed evidence. Supply T15 the
   connections, identities, and artifact hooks for the runbook's bounded retention and
   capacity observations, including capture polling/cleanup retention and applicable
   tempdb/ADR version-store observations. Distinguish capture-job polling from connector
   `SqlServerPollInterval`; unavailable metadata or access is unproved evidence. Capture
   bounded DMS/projector, SQL Server/Agent, connector/task, consumer, and Docker diagnostics
   on failure without changing retention or provider configuration to make a test pass.
10. **Reviewable evidence and governed cleanup.** Retain repository revision,
    tool/provider/image versions and digests, sanitized configuration/commands and JSON,
    native exit codes, exact test results, API/consumer observations and output locations.
    Map claimed behaviors and reused results to their owning `CDC-INV-*` IDs and evidence
    layers, keeping credentials out of artifacts. Supply the matching `mssql` teardown
    command with the setup-printed effective environment and shared state root. Preserve
    state during per-binding retirement, record partial-failure/retry and cleanup outcomes,
    and retain host retirement history and protected backups after disposable teardown.
    Missing provider, image, broker, policy input, or executable harness remains an unmet
    prerequisite, not a passing or silently skipped SQL Server exercise.

T15 owns executing the SQL Server runbook, collecting bounded operational observations,
and reconciling examples and actual results in the evidence index. E19-04 owns the
poll-interval wrapper fix; E19-06 supplies the executable API/bootstrap/database/consumer
handoff. T12/T13/T17 retain missing operator assertions, and E18 supplies prerequisite,
reset/rebuild crash-recovery, scrub-guard, and restart-without-source-scan evidence.
Reuse those results with their actual scope; this handoff requires no second bootstrap,
controller, state store, shell consumer, or deferred recovery implementation. Adding
these criteria does not itself deliver the executable harness or complete T15.

## Not Assigned to This Story

- Exhaustive resource coverage, connector scaling, and the broader ACL matrix are outside
  this E2E work package.
