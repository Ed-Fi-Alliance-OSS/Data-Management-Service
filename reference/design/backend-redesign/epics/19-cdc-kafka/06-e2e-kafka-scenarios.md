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

## Not Assigned to This Story

- Exhaustive resource coverage, connector scaling, and the broader ACL matrix are outside
  this E2E work package.
