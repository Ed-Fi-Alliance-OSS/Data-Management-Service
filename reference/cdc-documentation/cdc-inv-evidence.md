# CDC Procedure Evidence Index

[Entry point](README.md) · [Runbook](operations-runbook.md)

## Scope and Ownership

DMS-1326 owns exercised runbooks and documentation checks contributing to
[CDC-INV-14 and CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability).
This index starts with **pending** rows. Existing test fixtures are reuse candidates;
their presence is not proof that a runbook snippet ran. No live deployment, secured
Kafka exercise or documentation-snippet test is claimed by T01.

Projection evidence remains in the [E18 matrix](../document-cache-documentation/cdc-inv-evidence.md).
For other invariants, follow the [design's contract-to-evidence map](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability)
and sibling-owned acceptance evidence:
[binding/readiness](../design/backend-redesign/epics/19-cdc-kafka/00-documentcache-cdc-prerequisites.md),
[provider DDL](../design/backend-redesign/epics/19-cdc-kafka/01-cdc-ddl-support.md),
[templates](../design/backend-redesign/epics/19-cdc-kafka/02-connector-template-generation.md),
[transform](../design/backend-redesign/epics/19-cdc-kafka/03-document-state-transform.md),
[lifecycle/controller](../design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md),
[message/consumer conformance](../design/backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md),
and [API E2E](../design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md).
Controller-only exercises do not complete DMS-1325 API-driven scenarios.

## Recording Results

For each exercise, retain the exact snippet ID, repository revision, stable fixture
and method identifier with provider/case arguments, qualification lane/profile,
resolved image tag and digest, sanitized artifact reference, and actual outcome.
Use the existing [qualification entry point](../../eng/ci/Invoke-CdcQualification.ps1)
and [matrix](../../eng/ci/Get-CdcQualificationMatrix.ps1); documentation checks run in
Contract/PR through T13–T15, and live cases reuse provider/nightly and secured Kafka lanes.

Split each `Both` placeholder into separate PostgreSQL and SQL Server result rows
when evidence arrives. Also split local and authorization-enabled Kafka evidence.
Record `pending`, `passed`, `failed`, `skipped`, or `blocked` with the actual reason;
missing prerequisites and skipped cases cannot count as passing acceptance. A parser
or mocked-result test does not qualify a live operator procedure. Destructive/fault
cases must identify the fixture-owned disposable artifacts. Unsupported workflows
remain unsupported even if a lower-level fixture can mutate the underlying state.

Keep credentials, connection strings, document bodies and response payloads out of
this index and its artifacts. Use sanitized controller results and the existing
qualification exporter under the [security/diagnostics owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).
Record a bounded result, not an inference of continuity, purge, consumer correctness
or production capacity from a narrower success.

## Procedure Evidence

`Both` means PostgreSQL and SQL Server; each provider result is recorded separately. Image/profile values
remain pending until an actual run captures them; the supported local profile is
described in the [entry point](README.md#supported-deployment).

| Procedure | Design link / invariant contribution | Provider | Stable test identifiers (reuse candidates; method/case wiring pending) | Snippet ID (documented or reserved) | Qualification profile/image | Sanitized artifact reference | Actual result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| [PostgreSQL local setup](operations-runbook.md#postgresql-setup) | [CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence) | PostgreSQL | `CDC-DOC cdc-pg-bootstrap-local` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1); [behavioral cases](#postgresql-setup-qualification-t16) | `cdc-pg-infrastructure`, `cdc-pg-settings`, `cdc-pg-bootstrap-local`, `cdc-pg-status`, `cdc-pg-watch` | LocalSingleBroker / AuthorizationDisabledLocal; [images/substitutions](evidence/t16-postgresql-setup/run-details.json) | [Live outcomes](evidence/t16-postgresql-setup/cdc-runbook-live-setup.json), [qualification](evidence/t16-postgresql-setup/qualification.json) | Passed T16; observation exits 1 with projection Unknown. Interactive role prompt and published alternative not exercised. |
| [SQL Server local / published setup](operations-runbook.md#sql-server-setup) | [CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server) | SQL Server | `CDC-DOC cdc-sqlserver-bootstrap-local`, `CDC-DOC cdc-sqlserver-bootstrap-published` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1); [E18 prerequisite evidence](#sql-server-setup-qualification-t17) | `cdc-sqlserver-infrastructure`, `cdc-sqlserver-settings`, `cdc-sqlserver-bootstrap-local`, `cdc-sqlserver-bootstrap-published`, `cdc-sqlserver-status`, `cdc-sqlserver-watch` | LocalSingleBroker / AuthorizationDisabledLocal; [images/substitutions](evidence/t17-sqlserver-setup/run-details.json) | [Live outcomes](evidence/t17-sqlserver-setup/cdc-runbook-live-setup.json), [assertions](evidence/t17-sqlserver-setup/run-details.json) | Both passed T17, zero skips; production user mapping, narrow grants and writer publication from an absent target database. Published images are fixture-packaged branch builds. |
| [DMS E2E opt-in](operations-runbook.md#dms-e2e-setup) | [CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci) | PostgreSQL | `CDC-DOC cdc-pg-e2e-setup` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1); both `Given_CdcE2ESetup` smoke tests | `cdc-pg-e2e-setup`, `cdc-pg-e2e-test`; build alternative unexercised | LocalSingleBroker / AuthorizationDisabledLocal; [images/substitutions](evidence/t16-postgresql-setup/run-details.json) | [Live outcomes](evidence/t16-postgresql-setup/cdc-runbook-live-setup.json), [smoke details](evidence/t16-postgresql-setup/run-details.json) | PostgreSQL direct setup passed T16, smoke 2 passed/0 skipped. Build alternative not exercised. No API-driven message certification. |
| [DMS E2E opt-in](operations-runbook.md#sql-server-e2e-variant) | [CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci) | SQL Server | `CDC-DOC cdc-sqlserver-e2e-setup` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1); both `Given_CdcE2ESetup` smoke tests | `cdc-sqlserver-e2e-setup`, shared `cdc-pg-e2e-test` with documented SQL Server inputs | LocalSingleBroker / AuthorizationDisabledLocal; [images/substitutions](evidence/t17-sqlserver-setup/run-details.json) | [Live outcomes](evidence/t17-sqlserver-setup/cdc-runbook-live-setup.json), [snapshot/smoke assertions](evidence/t17-sqlserver-setup/run-details.json) | Direct setup passed T17; distinct snapshot, smoke 2 passed/0 skipped. Build alternative unexercised. No API-driven message certification. |
| [Preserve deployment state](operations-runbook.md#deployment-state) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral) | PostgreSQL | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_rejects_missing_corrupt_and_incomplete_provenance_without_authorizing_resume` (missing/corrupt/unsafe-permissions/contradictory bindings, workflows and source-history; incomplete completions; integrity reports); `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-state-inventory` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t18-postgresql-lifecycle/run-details.json) | [Live assertions](evidence/t18-postgresql-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t18-postgresql-lifecycle/qualification.json) | Passed T18; [exact outcomes and limits](#postgresql-lifecycle-qualification-t18) |
| [Preserve deployment state](operations-runbook.md#deployment-state) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral) | SQL Server | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_rejects_missing_corrupt_and_incomplete_provenance_without_authorizing_resume` (missing/corrupt/unsafe-permissions/contradictory bindings, workflows and source-history; incomplete completions; integrity reports); `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-state-inventory` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t19-sqlserver-lifecycle/run-details.json) | [Live assertions](evidence/t19-sqlserver-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t19-sqlserver-lifecycle/qualification.json) | Passed T19; [exact outcomes and limits](#sql-server-lifecycle-qualification-t19) |
| [Interrupted initial-enable retry](operations-runbook.md#initial-enable-retry) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence) | PostgreSQL | [Given_Cdc_command_enable_retry](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcCommandEnableRetryTests.cs) — `It_resumes_the_same_command_after_a_stage_interruption` (provider/broker-start/worker-start/preflight/post-after/barrier/metrics); `It_rejects_ineligible_original_evidence_before_provider_or_Kafka_effects`; `It_preserves_combined_initial_containment_failures_in_command_json`; unit reuse candidates, live snippet wiring pending | `cdc-enable-retry` | Pending | Pending — no artifact | Exact snippet unexercised; intact wrapper retry evidence in [T16](#postgresql-setup-qualification-t16) |
| [Interrupted initial-enable retry](operations-runbook.md#initial-enable-retry) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence) | SQL Server | [Given_Cdc_command_enable_retry](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcCommandEnableRetryTests.cs) — `It_resumes_the_same_command_after_a_stage_interruption` (provider/broker-start/worker-start/preflight/post-after/barrier/metrics); `It_rejects_ineligible_original_evidence_before_provider_or_Kafka_effects`; `It_preserves_combined_initial_containment_failures_in_command_json`; unit reuse candidates, live snippet wiring pending | `cdc-enable-retry` | Pending | Pending — no artifact | Exact snippet unexercised; intact wrapper retry evidence in [T17](#sql-server-setup-qualification-t17) |
| [Established validation and restart preflight](operations-runbook.md#established-validation) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity) | PostgreSQL | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start`; `It_restarts_an_intact_running_connector_with_fresh_ready_evidence`; `It_rejects_unavailable_live_evidence_while_stopped` (offset/provider); `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-validate` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t18-postgresql-lifecycle/run-details.json) | [Live assertions](evidence/t18-postgresql-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t18-postgresql-lifecycle/qualification.json) | Passed T18; [exact outcomes and limits](#postgresql-lifecycle-qualification-t18) |
| [Established validation and restart preflight](operations-runbook.md#established-validation) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity) | SQL Server | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start`; `It_restarts_an_intact_running_connector_with_fresh_ready_evidence`; `It_rejects_unavailable_live_evidence_while_stopped` (offset/provider); `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-validate` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t19-sqlserver-lifecycle/run-details.json) | [Live assertions](evidence/t19-sqlserver-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t19-sqlserver-lifecycle/qualification.json) | Passed T19; [exact outcomes and limits](#sql-server-lifecycle-qualification-t19) |
| [Missing provenance and source mismatch](operations-runbook.md#unsupported-provenance) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral) | PostgreSQL | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_rejects_missing_corrupt_and_incomplete_provenance_without_authorizing_resume`; `It_rejects_an_independent_empty_or_populated_source_without_mutating_either` (false/true); `It_retains_a_terminal_incident_despite_healthy_current_provider_and_offset_evidence`; `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-provenance-rejection` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t18-postgresql-lifecycle/run-details.json) | [Live assertions](evidence/t18-postgresql-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t18-postgresql-lifecycle/qualification.json) | Passed T18; [exact outcomes and limits](#postgresql-lifecycle-qualification-t18) |
| [Missing provenance and source mismatch](operations-runbook.md#unsupported-provenance) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral) | SQL Server | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_rejects_missing_corrupt_and_incomplete_provenance_without_authorizing_resume`; `It_rejects_an_independent_empty_or_populated_source_without_mutating_either` (false/true); `It_retains_a_terminal_incident_despite_healthy_current_provider_and_offset_evidence`; `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-provenance-rejection` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t19-sqlserver-lifecycle/run-details.json) | [Live assertions](evidence/t19-sqlserver-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t19-sqlserver-lifecycle/qualification.json) | Passed T19; [exact outcomes and limits](#sql-server-lifecycle-qualification-t19) |
| [Managed shutdown and startup](operations-runbook.md#managed-lifecycle) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary) | PostgreSQL | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start`; `It_waits_for_retained_projection_work_in_the_single_start_invocation` (persistent false/true). [CdcLifecycleOrdering.Tests.ps1](../../eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1) — `CDC-DOC cdc-managed-stop` (both connectors, retained custom roots); `does not shutdown the worker after delayed/unverified final task status`; `blocks DMS and a later unmanaged startup retry after one resume fails`; `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-managed-stop`, `cdc-managed-start` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t18-postgresql-lifecycle/run-details.json) | [Live assertions](evidence/t18-postgresql-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t18-postgresql-lifecycle/qualification.json) | Passed T18; [exact outcomes and limits](#postgresql-lifecycle-qualification-t18) |
| [Managed shutdown and startup](operations-runbook.md#managed-lifecycle) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary) | SQL Server | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start`; `It_waits_for_retained_projection_work_in_the_single_start_invocation` (persistent false/true). [CdcLifecycleOrdering.Tests.ps1](../../eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1) — `CDC-DOC cdc-managed-stop` (both connectors, retained custom roots); `does not shutdown the worker after delayed/unverified final task status`; `blocks DMS and a later unmanaged startup retry after one resume fails`. Live snippet wiring pending | `cdc-managed-stop`, `cdc-managed-start` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t19-sqlserver-lifecycle/run-details.json) | [Live assertions](evidence/t19-sqlserver-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t19-sqlserver-lifecycle/qualification.json) | Passed T19; [exact outcomes and limits](#sql-server-lifecycle-qualification-t19) |
| [Intact connector restart and resume](operations-runbook.md#intact-restart) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary) | PostgreSQL | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_restarts_an_intact_running_connector_with_fresh_ready_evidence`; `It_rejects_unavailable_live_evidence_while_stopped` (offset/provider). [Given_CdcManagedLifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcManagedLifecycleTests.cs) — `It_does_not_infer_a_restart_from_unchanged_running_status_after_a_lost_reply` (unit); `CDC-DOC cdc-managed-start` in [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) | `cdc-intact-restart`, `cdc-intact-resume` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t18-postgresql-lifecycle/run-details.json) | [Live assertions](evidence/t18-postgresql-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t18-postgresql-lifecycle/qualification.json) | Passed T18; [exact outcomes and limits](#postgresql-lifecycle-qualification-t18) |
| [Intact connector restart and resume](operations-runbook.md#intact-restart) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary) | SQL Server | [Given_Cdc_Controller_Managed_Lifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_restarts_an_intact_running_connector_with_fresh_ready_evidence`; `It_rejects_unavailable_live_evidence_while_stopped` (offset/provider). [Given_CdcManagedLifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcManagedLifecycleTests.cs) — `It_does_not_infer_a_restart_from_unchanged_running_status_after_a_lost_reply` (unit). Live snippet wiring pending | `cdc-intact-restart`, `cdc-intact-resume` | LocalSingleBroker / AuthorizationDisabledLocal; [immutable images](evidence/t19-sqlserver-lifecycle/run-details.json) | [Live assertions](evidence/t19-sqlserver-lifecycle/managed-lifecycle-runbook.json), [required cases](evidence/t19-sqlserver-lifecycle/qualification.json) | Passed T19; [exact outcomes and limits](#sql-server-lifecycle-qualification-t19) |
| [Native recovery and incomplete shutdown](operations-runbook.md#native-recovery) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary) | PostgreSQL | [Given_Cdc_Controller_Native_Recovery](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcNativeRecoveryTests.cs) — `It_observes_publication_before_crash_revalidation_and_rejects_prior_process_metrics`; `It_detects_failed_task_recovery_on_the_same_worker_without_certifying_the_gap`; `It_routes_incomplete_or_acknowledged_but_unverified_shutdown_to_native_recovery` (false/true); `It_contains_recovered_connectors_and_retains_terminal_history_loss` (false/true). [Given_Cdc_command_configuration](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcCommandContainmentTests.cs) — `It_contains_retained_incidents_through_the_request_builder_despite_journal_failure`; `It_returns_terminal_containment_after_the_command_deadline_but_honors_caller_cancellation` (unit). [Given_CdcControllerStatus](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcControllerStatusTests.cs) — `It_reports_failed_latch_and_still_attempts_containment` (false/true; unit persistence/stop faults). Packaged marked commands execute against the same owned services; all seven methods/nine outcomes are required | `cdc-native-recovery-watch`, `cdc-incomplete-shutdown-status` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in [run details](evidence/t21-postgresql-recovery/run-details.json) | [T21 qualification](#postgresql-native-recovery-qualification-t21), [marked commands](evidence/t21-postgresql-recovery/marked-commands.json) | Passed T21: 9 live cases, 11 marked commands; unit failure layers separately labeled |
| [Native recovery and incomplete shutdown](operations-runbook.md#native-recovery) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary) | SQL Server | [Given_Cdc_Controller_Native_Recovery](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcNativeRecoveryTests.cs) — `It_observes_publication_before_crash_revalidation_and_rejects_prior_process_metrics`; `It_detects_failed_task_recovery_on_the_same_worker_without_certifying_the_gap`; `It_routes_incomplete_or_acknowledged_but_unverified_shutdown_to_native_recovery` (false/true); `It_contains_recovered_connectors_and_retains_terminal_history_loss` (false/true). [Given_Cdc_command_configuration](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcCommandContainmentTests.cs) — `It_contains_retained_incidents_through_the_request_builder_despite_journal_failure`; `It_returns_terminal_containment_after_the_command_deadline_but_honors_caller_cancellation` (unit). [Given_CdcControllerStatus](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcControllerStatusTests.cs) — `It_reports_failed_latch_and_still_attempts_containment` (false/true; unit persistence/stop faults). Packaged marked commands execute against the same owned services; all seven methods/nine outcomes are required | `cdc-native-recovery-watch`, `cdc-incomplete-shutdown-status` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in [run details](evidence/t22-sqlserver-recovery/run-details.json) | [T22 qualification](#sql-server-native-recovery-qualification-t22), [marked commands](evidence/t22-sqlserver-recovery/marked-commands.json) | Passed T22: 9 live cases, 11 marked commands; unit failure layers separately labeled |
| [Projection troubleshooting and administration handoff](operations-runbook.md#projection-handoff) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration) | PostgreSQL | [Given_CdcPublicationHistory_packaged_administration](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/CdcPublicationHistoryTests.cs) — `It_allows_all_three_commands_from_managed_non_CDC_creation`; `It_rejects_untrusted_or_exposed_history_without_any_mutation` (all 15 scenarios); `It_preserves_historical_rejection_after_retiring_possible_exposure_with_a_surviving_source`; `It_rejects_after_reservation_wins_without_entering_the_provider_mutex_early`; `It_holds_the_controller_lock_through_E18_mutation_when_administration_wins` (all three commands). Exact snippet qualified T25 | `cdc-history-internal-only`, `cdc-history-rejected` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T25 | [T25 results](#postgresql-history-and-retirement-qualification-t25) | Documented T06; PostgreSQL T25 qualified |
| [Projection troubleshooting and administration handoff](operations-runbook.md#projection-handoff) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration) | SQL Server | [Given_CdcPublicationHistory_packaged_administration](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/CdcPublicationHistoryTests.cs) — `It_allows_all_three_commands_from_managed_non_CDC_creation`; `It_rejects_untrusted_or_exposed_history_without_any_mutation` (all 15 scenarios); `It_preserves_historical_rejection_after_retiring_possible_exposure_with_a_surviving_source`; `It_rejects_after_reservation_wins_without_entering_the_provider_mutex_early`; `It_holds_the_controller_lock_through_E18_mutation_when_administration_wins` (all three commands). Exact snippet qualified T26 | `cdc-history-internal-only`, `cdc-history-rejected` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T26 | [T26 results](#sql-server-history-and-retirement-qualification-t26) | Documented T06; SQL Server T26 qualified |
| [Monitoring and provider retention](operations-runbook.md#monitoring-retention) | [CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations), [telemetry](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry), [PostgreSQL](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql) | PostgreSQL | [Given_CdcConnectorTelemetryQualification.It_qualifies_the_pinned_exporter_with_real_streaming_and_replaces_task_and_worker_metrics](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTelemetryQualificationTests.cs); [Given_PostgresqlCdcRunbookRetention.It_executes_marked_slot_disk_and_progress_inspections_with_unavailable_actions](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTelemetryQualificationTests.Runbook.cs) | `cdc-telemetry-inspect`, `cdc-pg-retention-inspect`, `cdc-provider-disk-inspect`; `cdc-status`/`cdc-watch` reuse T16 | Qualified local single-worker/broker, PostgreSQL 16.8; [immutable images](evidence/t27-postgresql-telemetry/run-details.json); `aclIsolationProven: false` | [11 exact observations](evidence/t27-postgresql-telemetry/inspection-observations.json), [live results/required guard](evidence/t27-postgresql-telemetry/qualification.json), [layered details](#postgresql-telemetry-and-retention-qualification-t27) | T27 passed 2/2 live cases, no skips; task/worker replacement, retention/progress, bounded capacity and unavailable actions. No pressure injection or consumer-progress claim |
| [Monitoring and provider retention](operations-runbook.md#monitoring-retention) | [CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations), [SQL Server](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server) | SQL Server | [Given_CdcConnectorTelemetryQualification.It_qualifies_the_pinned_exporter_with_real_streaming_and_replaces_task_and_worker_metrics](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTelemetryQualificationTests.cs) (SqlServer); [Given_MssqlCdcSourcePositionAdapterTests.It_reads_capture_job_and_retained_lsn_metadata_for_healthy_continuity](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcSourcePositionAdapterTests.cs); exact inspection wiring pending T28 | `cdc-status`, `cdc-watch`, `cdc-telemetry-inspect`, `cdc-sqlserver-retention-inspect`, `cdc-provider-disk-inspect` | Pending live run; qualified local single-worker/broker, SQL Server 2025 | Pending — no live snippet artifact | T07 source/contract review only; exact snippets pending T28 |
| [Topic policy observations](operations-runbook.md#security-consumer-evidence) | [CDC-INV-12/15; offset store](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#kafka-connect-offset-store), [topic contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic) | Both; Kafka policy is provider-neutral, history applies to SQL Server | [Given_authorized_three_broker_cdc_policy](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcKafkaPolicyTests.cs) — `It_rejects_live_topic_drift_without_repair`; `It_requires_explicit_isr_even_when_the_broker_default_is_two`; `It_retains_tombstones_with_the_public_policy_and_accepts_stronger_retention`; `It_rejects_changed_partition_identity_and_actual_weak_replica_assignments`; exact local status snippet wiring pending T20 | `cdc-topic-policy-inspect` | Pending live snippet; local CLI reports ACL isolation false; secured fixtures separately test three-broker policy | Pending — no live snippet artifact | T08 source/contract review only; live exercise pending T20 |
| [Effective-access qualification](operations-runbook.md#security-consumer-evidence) | [CDC-INV-12/15; security](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations) | Provider-neutral Kafka fixture, including SQL Server history artifact | [Given_authorized_three_broker_cdc_policy](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcKafkaPolicyTests.cs) — `It_prepares_live_worker_only_offsets_before_the_qualified_worker_starts`; `It_allows_only_the_configured_public_topic_and_consumer_group` (a/b); `It_denies_cross_binding_and_internal_reads` (all topic/group cases); `It_denies_consumer_writes_and_connector_access_to_worker_offsets`; `It_preserves_unavailable_acl_authority_when_description_is_denied`; `It_fails_closed_on_unsafe_effective_grants_even_with_a_missing_required_grant` (all six cases). Same file: `Given_explicit_authorization_disabled_local_kafka_policy.It_labels_local_policy_without_claiming_acl_or_production_durability_proof` | `cdc-access-inspect` | Pending; authorization-enabled three-broker and explicitly separate authorization-disabled local profiles; exact images recorded by runner | Pending — no live snippet artifact | T08 source/contract review only; live exercise pending T20. Fixture proof never certifies operator resources |
| [Consumer-owner reference evidence](operations-runbook.md#consumer-owner-proof-and-invalidation-handoff) | [CDC-INV-13/15; public consumer bootstrap](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap) | Provider-neutral consumer assertions hosted by PostgreSQL fixture | [Given_MessageContractConsumerBroker](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractConsumerBrokerTests.cs) — `MC-CONSUMER-BROKER-BOOTSTRAP-DURABILITY`, `MC-CONSUMER-BROKER-ORDERING`, `MC-CONSUMER-BROKER-IDLE-RENEWAL`, `MC-CONSUMER-BROKER-CONTINUATION`, `MC-CONSUMER-BROKER-CHECKPOINT-MISSING`, `MC-CONSUMER-BROKER-CHECKPOINT-CORRUPT`; [bootstrap](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerBootstrapTests.cs) and [continuity](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerContinuityTests.cs) unit deadline/fault cases | `cdc-consumer-evidence` | Pending broker snippet; real Kafka transport, synthetic public values, simulated durable store/clock | Pending — no live snippet artifact | T08 source review and unit checks only; exact MessageContract lane pending T20. Independent stores need their own durable/capacity evidence |
| [Coordinated record-size increase](operations-runbook.md#record-size-increase) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase); [CDC-INV-07 sizing](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size) | PostgreSQL | [Given_Cdc_Controller_Record_Size_Increase](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.cs) — `It_orders_a_confirmed_increase_and_preserves_identity` (false/true inventories); `It_rejects_incomplete_or_mismatched_confirmation_without_mutation`; `It_requires_renewed_confirmation_at_every_interrupted_boundary`; [producer case](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.Producer.cs) — `It_replays_the_uncommitted_over_budget_materialized_record_after_capacity_alignment`. [Given_CdcRecordSizeAcknowledgement](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcRecordSizeAcknowledgementTests.cs) — `It_requires_updated_evidence_for_changed_consumer_deployments`; `It_requires_new_capacity_evidence_for_a_later_higher_ceiling` (unit). [Given_CdcRecordSizeIncrease](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcRecordSizeIncreaseTests.cs) — `It_keeps_fully_aligned_interrupted_rollout_blocking_ordinary_commands`; `It_rejects_out_of_order_or_unknown_limits_without_effects` (unit). [Marked-command fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.Runbook.cs) — `It_executes_marked_record_size_increase_with_explicit_inventory` (false/true); `It_executes_marked_record_size_retry_after_partial_broker_change` | `cdc-size-no-consumers`, `cdc-size-consumers`, `cdc-size-increase`, `cdc-size-retry` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in [run details](evidence/t23-postgresql-record-size/run-details.json) | [T23 qualification](#postgresql-record-size-qualification-t23), [marked commands](evidence/t23-postgresql-record-size/marked-commands.json), [rollout evidence](evidence/t23-postgresql-record-size/rollout-observations.json) | Passed T23: 20 live cases, 9 marked invocations; no independent consumer/ACL certification |
| [Coordinated record-size increase](operations-runbook.md#record-size-increase) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase); [CDC-INV-07 sizing](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size) | SQL Server | [Given_Cdc_Controller_Record_Size_Increase](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.cs) — `It_orders_a_confirmed_increase_and_preserves_identity` (false/true inventories); `It_rejects_incomplete_or_mismatched_confirmation_without_mutation`; `It_requires_renewed_confirmation_at_every_interrupted_boundary`; [producer case](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.Producer.cs) — `It_replays_the_uncommitted_over_budget_materialized_record_after_capacity_alignment`. [Given_CdcRecordSizeAcknowledgement](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcRecordSizeAcknowledgementTests.cs) — `It_requires_updated_evidence_for_changed_consumer_deployments`; `It_requires_new_capacity_evidence_for_a_later_higher_ceiling` (unit). [Given_CdcRecordSizeIncrease](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcRecordSizeIncreaseTests.cs) — `It_keeps_fully_aligned_interrupted_rollout_blocking_ordinary_commands`; `It_rejects_out_of_order_or_unknown_limits_without_effects` (unit). [Marked-command fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.Runbook.cs) — `It_executes_marked_record_size_increase_with_explicit_inventory` (false/true); `It_executes_marked_record_size_retry_after_partial_broker_change` | `cdc-size-no-consumers`, `cdc-size-consumers`, `cdc-size-increase`, `cdc-size-retry` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in [run details](evidence/t24-sqlserver-record-size/run-details.json) | [T24 qualification](#sqlserver-record-size-qualification-t24), [marked commands](evidence/t24-sqlserver-record-size/marked-commands.json), [rollout evidence](evidence/t24-sqlserver-record-size/rollout-observations.json) | Passed T24: 20 live cases, 9 marked invocations; no independent consumer/ACL certification |
| [Guarded generation retirement](operations-runbook.md#generation-retirement) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding) | PostgreSQL | [Given_CdcArtifactCleanupProviderDatabase](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcArtifactCleanupProviderTests.cs) — `It_removes_provider_artifacts_and_only_owned_unused_database_jobs`; `It_keeps_shared_provider_artifacts_and_rejects_unsafe_deletion`; `It_rejects_broad_or_orphaned_provider_artifacts`; `It_rejects_a_different_live_physical_source`; `It_reconciles_actual_committed_deletion_after_lost_response`. [Managed lifecycle fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_resumes_interrupted_retirement_and_preserves_shared_artifacts_and_source_history` (offset-removal interruption, partial cleanup, incident/state last, history/peer retention). Exact snippet qualified T25 | `cdc-retire` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T25 | [T25 results](#postgresql-history-and-retirement-qualification-t25) | Documented T10; PostgreSQL T25 qualified |
| [Guarded generation retirement](operations-runbook.md#generation-retirement) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding) | SQL Server | [Given_CdcArtifactCleanupProviderDatabase](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcArtifactCleanupProviderTests.cs) — `It_removes_provider_artifacts_and_only_owned_unused_database_jobs`; `It_keeps_shared_provider_artifacts_and_rejects_unsafe_deletion`; `It_rejects_broad_or_orphaned_provider_artifacts`; `It_rejects_a_different_live_physical_source`; `It_reconciles_actual_committed_deletion_after_lost_response`. [Managed lifecycle fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_resumes_interrupted_retirement_and_preserves_shared_artifacts_and_source_history` (offset-removal interruption, partial cleanup, incident/state last, history/peer retention). Exact snippet qualified T26 | `cdc-retire` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T26 | [T26 results](#sql-server-history-and-retirement-qualification-t26) | Documented T10; SQL Server T26 qualified |
| [Destructive stack teardown](operations-runbook.md#stack-teardown) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding) | PostgreSQL | [CdcLifecycleOrdering.Tests.ps1](../../eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1) — `performs governed generation cleanup for every peer before removing volumes`; `retains partial cleanup and retries controller retirement before volume deletion`; `restores stopped infrastructure without resuming before destructive cleanup`; `resumes retired <project>/<provider> teardown after removal of <removed>`; `resumes cleanup after one inventoried file was removed and preserves cleanup authority`; `retains nested state at <location> and gives a sanitized protection diagnostic`. [E2ETeardownSafety.Tests.ps1](../../eng/docker-compose/tests/E2ETeardownSafety.Tests.ps1) — `covers both compose projects: the local primitive then the published primitive`. Wrapper ordering qualified separately; exact stack-teardown snippet already executed by T16/T18 | `cdc-stack-teardown`, `cdc-e2e-teardown` | Mocked wrappers; live stack cleanup in T16/T18 | [T25 results](#postgresql-history-and-retirement-qualification-t25) | 287 wrapper cases passed T25; prior T16/T18 live stack teardown; E2E alternative has no new live T25 claim |
| [Destructive stack teardown](operations-runbook.md#stack-teardown) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding) | SQL Server | [CdcLifecycleOrdering.Tests.ps1](../../eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1) — `performs governed generation cleanup for every peer before removing volumes`; `retains partial cleanup and retries controller retirement before volume deletion`; `restores stopped infrastructure without resuming before destructive cleanup`; `resumes retired <project>/<provider> teardown after removal of <removed>`; `resumes cleanup after one inventoried file was removed and preserves cleanup authority`; `retains nested state at <location> and gives a sanitized protection diagnostic`. [E2ETeardownSafety.Tests.ps1](../../eng/docker-compose/tests/E2ETeardownSafety.Tests.ps1) — `covers both compose projects: the local primitive then the published primitive`. Wrapper ordering qualified separately; exact stack-teardown snippet already executed by T17/T19 | `cdc-stack-teardown`, `cdc-e2e-teardown` | Mocked wrappers; live stack cleanup in T17/T19 | [T26 results](#sql-server-history-and-retirement-qualification-t26) | 287 wrapper cases passed T26; prior T17/T19 live stack teardown; E2E alternative has no new live T26 claim |
| [Compatible representation-restamp handoff](operations-runbook.md#representation-restamp) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction) | PostgreSQL | [Given_PostgresqlRepresentationRestampCdcStateTests](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/RepresentationRestampCdcStateTests.cs) — `It_publishes_a_real_tracking_restamp_after_projector_drain` (real restamp and separate publication outcome). [Given_RepresentationRestampCommand](../../src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Given_RepresentationRestampCommand.cs) — `It_rejects_lifecycle_and_recovery_latch_before_selecting_a_page`; `It_classifies_cancellation_after_a_committed_page_through_the_real_workflow`; `It_rejects_a_completed_operation_through_the_real_runner_before_selecting_a_page` (unit). [Managed retirement runbook fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.Runbook.cs) executes exact status snippet T25; restamp execution remains sibling evidence | `cdc-restamp-handoff-status` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T25 | [T25 results](#postgresql-history-and-retirement-qualification-t25) | Documented T11; PostgreSQL T25 qualified. Restamp/publication does not prove purge or an exact baseline |
| [Compatible representation-restamp handoff](operations-runbook.md#representation-restamp) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction) | SQL Server | [Given_SqlServerRepresentationRestampCdcStateTests](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/RepresentationRestampCdcStateTests.cs) — `It_publishes_a_real_tracking_restamp_after_projector_drain` (real restamp and separate publication outcome). [Given_RepresentationRestampCommand](../../src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Given_RepresentationRestampCommand.cs) — `It_rejects_lifecycle_and_recovery_latch_before_selecting_a_page`; `It_classifies_cancellation_after_a_committed_page_through_the_real_workflow`; `It_rejects_a_completed_operation_through_the_real_runner_before_selecting_a_page` (unit). [Managed retirement runbook fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.Runbook.cs) executes exact status snippet T26; restamp execution remains sibling evidence | `cdc-restamp-handoff-status` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T26 | [T26 results](#sql-server-history-and-retirement-qualification-t26) | Documented T11; SQL Server T26 qualified. Restamp/publication does not prove purge or an exact baseline |
| [Sensitive-data disclosure response](operations-runbook.md#sensitive-data-response) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction) | PostgreSQL | [Given_CdcManagedLifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcManagedLifecycleTests.cs) — `It_verifies_and_journals_stop_while_retaining_all_other_state`; `It_does_not_treat_acknowledgement_or_lost_stop_reply_as_shutdown`; `It_rejects_task_state_inconsistent_with_the_stopped_summary` (unit). [Managed lifecycle fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_resumes_interrupted_retirement_and_preserves_shared_artifacts_and_source_history`; [provider cleanup](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcArtifactCleanupProviderTests.cs) — `It_removes_provider_artifacts_and_only_owned_unused_database_jobs`. Exact stop/retire qualified T25 | `cdc-disclosure-containment-result`, `cdc-retire` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T25 | [T25 results](#postgresql-history-and-retirement-qualification-t25) | Documented T11; PostgreSQL T25 qualified. Consumer fencing, platform purge and independent-store closure remain deployment-owned; fixture success cannot certify them |
| [Sensitive-data disclosure response](operations-runbook.md#sensitive-data-response) | [CDC-INV-14; CDC-INV-15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction) | SQL Server | [Given_CdcManagedLifecycle](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcManagedLifecycleTests.cs) — `It_verifies_and_journals_stop_while_retaining_all_other_state`; `It_does_not_treat_acknowledgement_or_lost_stop_reply_as_shutdown`; `It_rejects_task_state_inconsistent_with_the_stopped_summary` (unit). [Managed lifecycle fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcManagedLifecycleTests.cs) — `It_resumes_interrupted_retirement_and_preserves_shared_artifacts_and_source_history`; [provider cleanup](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcArtifactCleanupProviderTests.cs) — `It_removes_provider_artifacts_and_only_owned_unused_database_jobs`. Exact stop/retire qualified T26 | `cdc-disclosure-containment-result`, `cdc-retire` | LocalSingleBroker / AuthorizationDisabledLocal; immutable images in T26 | [T26 results](#sql-server-history-and-retirement-qualification-t26) | Documented T11; SQL Server T26 qualified. Consumer fencing, platform purge and independent-store closure remain deployment-owned; fixture success cannot certify them |

## Documentation Verification

| Check | Owner task | Actual result |
| --- | --- | --- |
| Structure against every DMS-1326 subsection; deployment/ownership claims against shipped source | T01 | Passed source/structure review; 172 relative links/anchors, 18 procedure records and 38 unique reserved IDs checked. No live qualification implied. |
| PostgreSQL setup/E2E source review; local snippet syntax/settings smoke check and existing wrapper suites | T02 | Passed: 208 relative links/anchors; 10 PowerShell snippets parsed; 5 wrapper invocations checked against declared parameters; exact settings snippet accepted by production bootstrap reader with declared fixture inputs, normal settings/secret reference and 600/700 permissions preserved; CdcBootstrapWorkflow + CdcE2EWorkflow Pester: 130 passed, zero failed/skipped. No live provider exercise or T13–T15 test wiring claimed. |
| Deployment-state/retry/validation/source-mismatch source review and existing unit fixtures | T04 | Passed: 241 relative links/anchors; 14 marked PowerShell snippets parsed; four complete procedure records and named evidence methods checked. SchemaTools command contract/retry: 119 passed; Backend.Cdc established validation: 182 passed; zero failed/skipped. Includes source review of both-provider managed lifecycle rejection fixtures. No live provider exercise or T13–T15 snippet wiring claimed. |
| Managed lifecycle/recovery source review, snippet syntax/help and existing wrapper/controller/containment fixtures | T05 | Passed: 271 relative links/anchors; 20 total marked PowerShell snippets parsed (6 added); four new CLI option sets match built help and both wrapper alternatives accept `-d`; named evidence methods verified. SchemaTools configuration/containment: 460 passed; backend lifecycle/recovery: 392 passed; failed incident-persistence/stop cases: 4 passed; lifecycle Pester: 253 passed under PowerShell 7.6.6; zero failed/skipped in final runs. Initial PowerShell 7.4.10 Pester run failed 15 empty-environment-value cases; rerun with existing 7.6.6 resolved all. Live snippets remain pending T18/T19/T21/T22; no T13–T15 wiring claimed. |
| Monitoring/retention source review, bounded inspection syntax and existing status/telemetry contracts | T07 | Passed: 368 Backend.Cdc telemetry/controller-status tests, 17 Core projection/CDC status serialization/contract tests and 5 exporter metric-contract cases (no live provider startup); zero failed/skipped. Checked 435 relative links/anchors, 36 paired unique PowerShell snippets (6 added), 12 existing wrapper parameter sets and inspection bounds/units against production sources and provider documentation. PostgreSQL/SQL Server live snippets remain pending T27/T28; durable snippet wiring T13–T15. |
| Security/topic/consumer checklist source review, snippet syntax and existing policy/consumer/qualification tests | T08 | Passed: 321 Backend.Cdc policy/consumer unit tests and 33 qualification-runner Pester tests on PowerShell 7.6.6; zero failed/skipped. Checked 466 relative links/anchors, 39 paired unique PowerShell snippets (3 added), 14 wrapper parameter sets and named policy methods/all six consumer broker scenario IDs. Secured/local Kafka and MessageContract snippets remain live-pending T20; no independent consumer certification or T13–T15 wiring claimed. |
| Guarded retirement/stack teardown source review, snippet syntax/help and existing cleanup/CLI/wrapper fixtures | T10 | Passed: 487 Backend.Cdc retirement/artifact-cleanup tests, 500 SchemaTools command/configuration/packaged-host tests, 253 lifecycle Pester and 34 E2E teardown safety Pester tests on PowerShell 7.6.6; zero failed/skipped. Checked 526 relative links/anchors, 45 paired unique snippets (44 PowerShell + 1 JSON), 16 wrapper commands, new CLI options against built help, all four wrapper destructive flag sets, production retirement-result serialization and named evidence methods. Both-provider live retirement/teardown snippets remain pending T25/T26; no platform purge or T13–T15 wiring claimed. |
| Restamp/disclosure handoff source review, snippet syntax/help and existing restamp/lifecycle/CLI contracts | T11 | Passed: 31 Backend restamp, 596 Backend.Cdc lifecycle/retirement, 493 SchemaTools command/configuration and 36 DocumentCacheAdmin parser/result/exit-code tests; 1156 total, zero failed/skipped. Checked 564 relative links/anchors, 47 paired snippets (46 PowerShell + 1 JSON), 16 existing wrapper parameter sets, both new CLI option sets against built help, production stop-result serialization and named evidence methods. No live provider snippet, consumer-access revocation or platform purge claimed; exact exercises remain T25/T26 and durable snippet wiring T13–T15. |
| Exact marked command/configuration examples through production host/configuration paths | T13 | Passed: 56 focused cases; full SchemaTools `FullyQualifiedName~Cdc` run 771 passed, zero failed/skipped (.NET SDK 10.0.102, PowerShell 7.4.10, Linux; no live images). [Command checks](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookCommandTests.cs) dispatch 19 marked CLI examples and check help/options; [configuration checks](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookConfigurationTests.cs) execute both marked settings blocks with isolated inputs/overrides, load production settings, render templates and parse the marked acknowledgement; [rejection cases](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookRejectionTests.cs) reuse original controller fixtures for both providers. Deliberate temporary option/provider/settings-field/acknowledgement mutations each failed meaningfully and were restored. Live procedures remain pending; no wrapper/output qualification claimed. |
| Serialized output, packaged stdout/stderr and exit codes, touched links/anchors | T14 | Full SchemaTools `FullyQualifiedName~Cdc`: 810 passed; final focused documentation/packaged rerun: 102 passed, zero failed/skipped; Admin serialization/exit selection: 56 passed, zero failed/skipped (.NET SDK 10.0.102, Linux; no live images). [Output cases](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookOutputTests.cs) compare marked ready/backlog/unavailable/terminal excerpts with both-provider controller results, omitted lag/percentiles and operation-scoped stop/retire models; the existing [source-mismatch case](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookRejectionTests.cs) now checks its serialized excerpt. [Packaged cases](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookPackagedTests.cs) reuse the separate-process harness for marked commands, one stdout JSON value, watch/diagnostics stderr and 0/1/2/130 exits; controlled controller results are not live qualification. [Link checks](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookLinkTests.cs) cover 12 operator documents, owning-design and E18 anchors; copied field/value/removal and broken-anchor mutations must fail. [Admin cases](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit/CdcRunbookHistoryOutputTests.cs) serialize admitted/rejected excerpts for all three operations and map exits 0/10. Those same excerpts are asserted in the existing packaged `CdcPublicationHistoryTests.ReadResult` for both providers; build/discovery passed, live gate/exact-command exercises remain T25/T26. Local reports: `/tmp/dms1326-t14-results/{t14-cdc,t14-final,t14-admin}.trx`. No wrapper/CI or live provider qualification claimed. |
| Wrapper Pester examples and Contract/PR qualification wiring | T15 | Passed: [exported Contract report](evidence/t15-contract/qualification.json) and [exported snippet outcomes](evidence/t15-contract/cdc-runbook-wrappers.json), copied unchanged from `TestResults/cdc-docs-contract-t15-20260922-01`. Linux, .NET SDK 10.0.102, PowerShell 7.6.6, Pester 5.7.1; Contract/offline profile, no live images. Controller unit 4614, CLI 810, Admin output 6, controller offline 418, Pester 502: **6350 passed, zero failed/skipped**. Required-case guards confirm all 14 `CDC-DOC` wrapper IDs (13 distinct snippets) and 65 CLI cases across ten methods. [Bootstrap](../../eng/docker-compose/tests/CdcBootstrapWorkflow.Tests.ps1), [E2E](../../eng/docker-compose/tests/CdcE2EWorkflow.Tests.ps1), [lifecycle](../../eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1) and [worker](../../eng/docker-compose/tests/CdcWorkerStartup.Tests.ps1) seams check both providers, forwarding, initial failure/retry, retained custom roots, verified stop/start and interrupted cleanup. [Drift checks](../../eng/docker-compose/tests/CdcRunbookWrappers.Tests.ps1) reject missing/duplicate snippets, arguments, environment/provider changes and undeclared expressions; [runner/export checks](../../eng/ci/tests/CdcQualification.Tests.ps1) reject excluded/skipped/unexecuted required cases and strip private attachment fields. Standalone required Pester command: 502 passed; CI budget/classification: 261 passed. PowerShell analysis and whitespace checks clean. Initial 7.4 run failed existing empty-environment retention cases; final 7.6.6 runs resolved them. Initial E2E mock-argument assertions were corrected before final passing runs. No live provider qualification claimed by T15; see T16 results below. |

E18 owns projection performance and lifecycle evidence; link its workload limits
when adding tuning guidance. [Production-scale qualification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
is not assigned to this runbook. Consumer conformance examples likewise remain
DMS-1324 evidence, not certification of third-party consumer stores.

## Shared Helper and Live Qualification Handoff

The [PowerShell helper](../../eng/docker-compose/tests/cdc-runbook-snippets.ps1) supplies test-only
parameter binding and bounded live wrapper invocation; the [.NET excerpt helper](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcRunbookExamples.cs)
is linked into the existing Admin unit/integration projects without NUnit fixture/category
attributes. Reusing a helper must not move tests out of their provider/suite categories.
T16 supplies the small live snippet-to-command helper and PostgreSQL wiring; later tasks reuse
it and assert their own discovery. No live execution is supplied by T15's wrapper doubles.

The current [suite filter owner](../../eng/ci/cdc-qualification.psm1) and
[runner project dispatch](../../eng/ci/Invoke-CdcQualification.ps1) select:

| Suite | Project | Filter (plus `Category=PostgresqlIntegration` or `Category=MssqlIntegration`) |
| --- | --- | --- |
| Admission | Backend.Cdc.Tests.Integration | `Category=CdcControllerAdmission` |
| PostgreSQL Admission setup | RunbookSetup.Live.Tests.ps1 | Explicit selection, both `CDC-DOC cdc-pg-bootstrap-local` and `CDC-DOC cdc-pg-e2e-setup` required; excluded from Contract glob |
| SQL Server Admission setup | RunbookSetup.Live.Tests.ps1 | Explicit `Mssql` selection; `CDC-DOC cdc-sqlserver-bootstrap-local`, `CDC-DOC cdc-sqlserver-bootstrap-published` and `CDC-DOC cdc-sqlserver-e2e-setup` required; excluded from Contract glob |
| Lifecycle | Backend.Cdc.Tests.Integration | `Category=CdcControllerManagedLifecycle` |
| Recovery | Backend.Cdc.Tests.Integration | `Category=CdcControllerNativeRecovery` |
| RecordSize | Backend.Cdc.Tests.Integration | `Category=CdcControllerRecordSize` |
| Telemetry | Backend.Cdc.Tests.Integration | `Category=CdcConnectorTelemetryQualification` |
| History | DocumentCacheAdmin.Tests.Integration | `Category=CdcPublicationHistory` |
| History cleanup sidecar | Backend.Cdc.Tests.Integration | `Category=CdcArtifactCleanup` |
| MessageContract | Backend.Cdc.Tests.Integration | `(Category=CdcMessageContractSerialized` OR `Category=CdcMessageContractKafka)` |

Keep `DatabaseIntegration` on live fixtures; Contract's backend selection is
`Category!=DatabaseIntegration`. The separate Kafka lane selects `CdcControllerKafkaPolicy`
with `CdcAuthorizationEnabled` / `CdcAuthorizationDisabledLocal`. Provider/nightly and
secured lanes remain the live evidence owners. The shared helper does not confer provider,
ACL, API-scenario or consumer qualification. Later tasks own any small filter/discovery
additions their live cases require.

Contract requires the named `CDC-DOC <snippet-id>` Pester cases (including the separate
`cdc-managed-start-rejected` case for `cdc-managed-start`) and minimum parameterized counts
for command/configuration/output/packaged/link checks. Missing, duplicated, excluded,
skipped or unexecuted required wrapper cases fail. The existing PR Pester selection uses
the same required-case guard. Admin's six history-output cases also run in Contract.
`qualification.json` retains those checks and actual outcomes; the exporter publishes
`wrappers/cdc-runbook-wrappers.json` with only `TestId`, `SnippetId`, `Outcome`.
The narrowly allowlisted `cdc-runbook-*.json` attachment family excludes settings, raw
command output, credentials and payloads, even when their values look innocuous.

## SQL Server Initial User Mapping (T29)

T29 resolves the former root `PROBLEMS.md` blocker: shared managed setup can now proceed
from a deployment-owned restricted SQL login and a new database with no connector user.
The provider maps the same-name user, verifies its SID/type, and reuses narrow grants and
effective-access validation. The controller's original durable completion boundary selects
`ValidateOnly` on subsequent setup, including before connector registration. See the
[owning SQL Server contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server)
and [story handoff](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md#sql-server-initial-connector-user-mapping).
The resolution is retained here because a root `PROBLEMS.md` is the implementation-loop
stop flag. No manual user preparation or public callback is the successful setup path.

[Sanitized T29 evidence](evidence/t29-sqlserver-initial-user-mapping.json) records immutable
images, source hashes, exact executed cases, corrections, and final outcomes. It retains
only case metadata, provider modes/outcomes/diagnostic codes, and writer-authorization
results from the existing qualification exporter; no settings, credentials, SIDs, offsets,
or document data are included. Faults target fixture-owned disposable SQL Server instances,
databases, principals, and controller state. The provider access fixture prepares only the
login; its elevated-user rejection case explicitly injects an existing invalid user.

| Procedure / layer | Design / invariant | Provider | Stable test identifiers | Snippet ID | Profile / image | Sanitized artifact | Actual result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Initial user mapping / controller admission | [SQL Server setup; CDC-INV-14/15](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server) | SQL Server | [Given_SqlServer_Controller_Admission](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcMssqlAdmissionTests.cs): `It_admits_a_fresh_owned_source_only_after_live_barrier_and_lag`; `It_rejects_initial_connector_mapping_before_registration_or_publication` (4 cases); `It_never_repairs_connector_mapping_after_durable_provider_completion` (2 cases); `It_reconciles_exact_capture_after_lost_provider_evidence_without_repair` | None: runtime prerequisite; [public commands qualified in T17](#sql-server-setup-qualification-t17) | Mssql Admission category selection; qualified Connect digest, pinned SQL Server 2025 and resolved Kafka digest in artifact; `aclIsolationProven: false` | [Admission cases](evidence/t29-sqlserver-initial-user-mapping.json) | 8 final cases passed; no skips. Happy path verifies absent user before setup, same login SID afterward, and eventual writer publication. Initial rejection and post-completion removal/conflict keep publication unauthorized. |
| Mapping, narrow grants and effective access / provider integration | [SQL Server provider](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server) | SQL Server | [Given_MssqlCdcProviderAccessRetry](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcProviderAccessRetryTests.cs); every executed method/case appears in artifact | None: provider regression | Fixture-owned SQL Server 2025; restricted connector live probe | [Provider cases](evidence/t29-sqlserver-initial-user-mapping.json) | 29 final cases passed; no skips. Includes a connector-credential boundary probe and exact retry after a missing-login rejection. |
| Mapping and interruption / unit | [SQL Server provider](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server) | SQL Server | [Given_MssqlCdcPrincipalAccess_Initial_Setup / ValidateOnly](../../src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/CdcSqlServerHeartbeatDatabaseProviderTests.cs); filter `FullyQualifiedName~MssqlCdc\|FullyQualifiedName~CdcProviderRetry` | None | In-memory executor | [Check totals](evidence/t29-sqlserver-initial-user-mapping.json) | 82 passed, plus 6 work-table exclusion checks; includes absent/unsupported/elevated login, conflicting SID/type, safe quoting, insufficient setup authority, interrupted mapping/grants, exact retry and validation-only rejection. |
| Durable completion and initial retry / unit | [Managed setup boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence) | Both provider variants | [Given_CdcProviderSetupOrchestration](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcProviderSetupOrchestrationTests.cs); [Given_Cdc_command_enable_retry](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/CdcCommandEnableRetryTests.cs) | None | Controller/CLI fakes | [Check totals](evidence/t29-sqlserver-initial-user-mapping.json) | 68 controller + 86 CLI cases passed; no skips. |
| Shared public-wrapper handoff / Pester | [Local bootstrap](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci) | Both provider variants | [CdcBootstrapWorkflow.Tests.ps1](../../eng/docker-compose/tests/CdcBootstrapWorkflow.Tests.ps1), [CdcE2EWorkflow.Tests.ps1](../../eng/docker-compose/tests/CdcE2EWorkflow.Tests.ps1): retained-state retry before writer/seed; SQL Server local/published E2E provider-admission rejection | None: mocked wiring | PowerShell 7.6.6 / Pester | [Check totals](evidence/t29-sqlserver-initial-user-mapping.json) | 131 passed; no skips. Public command/snippet live evidence is recorded separately in [T17](#sql-server-setup-qualification-t17). |

T03 documents the [SQL Server setup examples](operations-runbook.md#sql-server-setup)
and [E2E variant](operations-runbook.md#sql-server-e2e-variant). [T17](#sql-server-setup-qualification-t17)
now supplies the exact marked examples through public local, published and direct DMS E2E surfaces. These results
are provider/controller qualification, not DMS-1325 API-driven message evidence, ACL
isolation proof, or production deployment qualification. No relational mapping/hash or
`RelationalMappingVersion` change was made.


## PostgreSQL Setup Qualification (T16)

**Passed:** both required live setup cases, 30 controller Admission cases and nine
reused E18 cases; zero failures/skips. The E2E case additionally required two passing
setup smoke tests. See the [qualification report](evidence/t16-postgresql-setup/qualification.json),
[named live outcomes](evidence/t16-postgresql-setup/cdc-runbook-live-setup.json) and
[images, substitutions and observations](evidence/t16-postgresql-setup/run-details.json).
Both wrappers passed on their first invocation in the final run. A separate read-only
inspection confirmed both E2E databases and one snapshot `EffectiveSchema` row.

Qualification ran on Linux/amd64 with PowerShell 7.6.6, Pester 5.7.1 and
.NET SDK 10.0.102. The selected supported deployment is `LocalSingleBroker` /
`AuthorizationDisabledLocal`; no ACL isolation is claimed. The existing
[Postgresql Admission runner](../../eng/ci/Invoke-CdcQualification.ps1) now requires
both named live setup cases in addition to the provider suite. Missing, skipped,
unexecuted or duplicate named cases fail its report.

The [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1) invokes
the shipped wrappers using the shared snippet/process helper. The role is prepared
as a restricted fixture prerequisite using stdin, rather than replaying the
interactive `cdc-pg-connector-role` prompt. Settings use the exact marked block
with a private `Read-Host` fixture input. Declared substitutions are owner-only
temporary settings/state/environment paths, fresh credentials, loopback host port
5435, fresh CMS target 1, 120-second calls and 600-second waits, and two watch passes.
Local bootstrap uses Ed-Fi/TPDM and `edfi_cdc`; E2E uses Ed-Fi/Homograph/Sample/TPDM,
`edfi_datamanagementservice_e2e` and its distinct snapshot. The staged settings and
inventory are asserted against these selections. No retained input or controller
artifact is repaired between attempts. All raw output and settings remain private.

The fixture asserts writer-publication authority, DMS health, the qualified worker
image, publication tables `CdcHeartbeat,Document,DocumentCache` and exclusion of
`DocumentProjectionWork`. Exact `cdc-pg-status` and `cdc-pg-watch` invocations
return JSON and exit 1: provider/connector observations are `Satisfied`, standalone
projection is `Unknown` and aggregate readiness is `NotReady`. This is an observed
CLI limitation, not a successful aggregate-readiness claim. The two selected
`Given_CdcE2ESetup` tests only check HTTP/database health. They do not qualify
DMS-1325 API-driven message scenarios.

Published-bootstrap and build-based E2E alternatives were not selected for this
live run; their Contract binding checks remain separate. Governed teardown is
fixture cleanup; the complete retirement/shutdown procedure matrix remains owned
by its later tasks.

The existing [Given_Postgresql_Controller_Admission](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcPostgresqlAdmissionTests.cs)
ran through `Invoke-CdcQualification.ps1 -Lane Postgresql -Suite Admission`:
**30 passed, zero failed/skipped**. The compact
[admission results](evidence/t16-postgresql-setup/admission-results.json) project
exact IDs/outcomes from the sanitized exported TRX; raw diagnostics and volatile
controller observations are not checked in. The [qualified image manifest](evidence/t16-postgresql-setup/qualified-image.json)
identifies the pinned Connect worker.

Behavioral coverage includes `It_admits_a_fresh_owned_source_only_after_live_barrier_and_lag`,
`It_retries_exact_empty_binding_after_interrupted_binding_or_activation`,
`It_repeats_fresh_readiness_after_an_interrupted_offline_wait`,
`It_forbids_initial_retry_after_atomic_publication_intent_loses_its_reply`, and
`It_rejects_ineligible_live_databases_without_mutating_binding_or_capture`
(unbound tracking, canonical rows, cache rows, work rows, latch, rebuilding,
source mismatch). These are real provider/controller cases, not execution of the
operator's initial-retry snippet; that procedure remains T18.

The existing E18 provider cases below ran on the same pinned PostgreSQL 16.8
image used for local setup. All nine passed without skips; the
[sanitized TRX](evidence/t16-postgresql-setup/e18/e18.trx) contains their exact
fixture/method identifiers. This is reused provider behavior evidence, separate
from the new runbook invocation cases.

| E18 fixture | Executed methods | Result |
| --- | --- | --- |
| [Given_A_Postgresql_DocumentCacheProjector](../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlDocumentCacheProjectorTests.cs) | `It_drains_long_outage_backlog_in_bounded_pages_and_restarts_from_durable_work` — asserts bounded pages 3/3/1 and no source/cache scan | Passed, 1 |
| [Given_A_Postgresql_DocumentCachePrerequisite_Validator](../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlDocumentCacheProviderPrerequisiteValidatorTests.cs) | `It_reports_sqlserver_prerequisites_as_not_applicable_for_initialization`, `It_reports_sqlserver_prerequisites_as_not_applicable_for_activation_preflight`, `It_reports_the_postgresql_provider_token` | Passed, 3 |
| [Given_A_Postgresql_DocumentCacheOfflineDeactivation_Command](../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlDocumentCacheOfflineDeactivationTests.cs) | `It_resumes_resetting_with_clear_latch_by_clearing_cache_and_work` | Passed, 1 |
| [Given_A_Postgresql_DocumentCacheOfflineActivation_Command](../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlDocumentCacheOfflineActivationTests.cs) | `It_resumes_rebuilding_without_repeating_destructive_clearing` | Passed, 1 |
| [Given_A_Postgresql_DocumentCacheOnlineCacheRebuild_Command](../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlDocumentCacheOnlineCacheRebuildTests.cs) | `It_resumes_rebuilding_without_repeating_cache_clearing` | Passed, 1 |
| [Given_A_Postgresql_DocumentCacheInternalOnlyCacheAheadRecovery_Command](../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlDocumentCacheInternalOnlyCacheAheadRecoveryTests.cs) | `It_resumes_resetting_with_the_latch_set_without_reentering_resetting`, `It_resumes_rebuilding_with_clear_latch_without_repeating_destructive_clearing` | Passed, 2 |

## SQL Server Setup Qualification (T17)

The existing [Mssql Admission selection](../../eng/ci/Invoke-CdcQualification.ps1)
passed all [42 controller cases](evidence/t17-sqlserver-setup/admission-results.json),
zero failed/skipped. These behavioral cases cover initial admission, intact retries,
rejected prerequisites and stale/unavailable observations. This baseline started
before the clock correction below. The final marked wrapper runs and full controller
unit run qualify the corrected build.

The live production path exposed a clock-domain integration gap: three of six fresh
SQL Server reads returned database timestamps earlier than their host request-start
times. [Sanitized reproduction and regression identifiers](evidence/t17-sqlserver-setup/projection-clock.json)
record the result. The [controller adapter](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcControllerObservations.cs)
now correlates the fresh durable read on the host clock, under the
[owning readiness contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).
The [clock regression fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcControllerProjectionObservationTests.cs)
and existing readiness fixtures passed 158 cases for both providers; the full CDC
controller unit project passed 4,628 cases, zero failed/skipped.
No relational mapping, schema hash, status payload timestamp or provider-position
comparison changed.

**Passed:** all three required marked setup cases and both E2E smoke tests, zero
failed/skipped. The [qualification report](evidence/t17-sqlserver-setup/qualification.json)
combines the 42-case Admission baseline with the final shared-fixture setup run;
[named outcomes](evidence/t17-sqlserver-setup/cdc-runbook-live-setup.json) and
[assertions, images and substitutions](evidence/t17-sqlserver-setup/run-details.json)
record the final run. The [initial complete Admission runner report](evidence/t17-sqlserver-setup/initial-qualification.json)
also passed 42 controller and three setup cases. Its published image lookup used
the local container name; after making image inspection fail closed and resolving
by Compose project/service, all three setup cases were rerun successfully. No
incomplete image evidence is used in the final assertions.

The shared [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1)
and Mssql Admission selection require local, published and direct E2E setup separately.
Missing, skipped, duplicate or unexecuted cases fail the required-case report.
The final local and published wrappers each required one intact initial retry;
per-case attempt counts are recorded in the assertions. Production setup created
the user from the fixture's restricted login and absent target database; assertions verified
matching SID, narrow effective access, writer-publication authority, CMS target 1,
original state and matching schema projects. No fixture-side user creation or SQL
repair occurred between provisioning and admission. Capture includes only
`CdcHeartbeat`, `Document` and `DocumentCache`; `DocumentProjectionWork` is excluded.

The settings blocks use declared private paths/credentials, base datastore tokens
`mssql`, host port 1435, 180-second calls, 600-second waits, the documented 60-second
observation-age bound and two watch passes. Local/published setup uses Ed-Fi/TPDM;
direct E2E uses Ed-Fi/Homograph/Sample/TPDM and separate primary/snapshot databases.
Snapshot assertions require one `EffectiveSchema` row and no connector user.
Published fixtures package matching branch application/CMS images under the
published Compose names and record immutable image IDs; they do not certify a
registry release.

Exact SQL Server status/watch commands return exit 1 with provider/connector
`Satisfied`, standalone projection `Unknown`, aggregate `NotReady` and
`aclIsolationProven: false`. Writer-publication success does not change this
standalone observation limitation. Smoke tests check HTTP/database health only;
DMS-1325 API-driven messages, secured Kafka and the build-based E2E alternative
remain unqualified here. Governed teardown completed for every selected case;
the complete retirement/fault matrix remains with T25/T26. Raw settings, output,
credentials, SIDs, offsets and document data remain outside the committed evidence.

The following behavioral evidence reuses E18/provider fixtures on an exclusively
owned SQL Server 2025 instance. It does not substitute for the marked setup commands.
Server-setting faults restore the original settings in teardown. No source scans,
new projector implementation or production recovery workflow were added.

| Setting / procedure | Stable test identifiers | Actual result / artifact |
| --- | --- | --- |
| RCSI and nested triggers: initialization rejection; Disabled correction/restart | [Given_CdcProjectionPrerequisite_On_An_Isolated_SqlServer](../../src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/CdcProjectionPrerequisiteTests.cs).`It_rejects_drift_on_retry_and_E18_initialization_without_repair` (`rcsi` / `nested triggers`, `Disabled`); same method with `Tracking`, `Resetting`, `Rebuilding` classifies unsupported incidents without correction or renewed-readiness assertions | All 8 parameter cases passed in the 15-case [prerequisite run](evidence/t17-sqlserver-setup/e18/prerequisites.json), zero failed/skipped |
| RCSI and nested triggers: activation rejection then correction/retry | [Given_DocumentCacheAdminMssqlActivationPrerequisite](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/Given_DocumentCacheAdminMssqlStatusAndPrerequisites.cs).`It_rejects_activation_when_read_committed_snapshot_is_disabled_without_mutation_then_succeeds_after_correction`; `It_rejects_activation_when_nested_triggers_are_disabled_without_mutation_then_succeeds_after_correction` | Both packaged command cases [passed](evidence/t17-sqlserver-setup/e18/activation.json), zero failed/skipped |
| Restart from durable queue; indexed no-scan paging; reset/rebuild crash recovery; prerequisite validator | Existing [Mssql projector](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlDocumentCacheProjectorTests.cs), [query-plan](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlDocumentCacheQueryPlanTests.cs), [rebuild](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlDocumentCacheOnlineCacheRebuildTests.cs), [deactivation](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlDocumentCacheOfflineDeactivationTests.cs), [cache-ahead](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlDocumentCacheInternalOnlyCacheAheadRecoveryTests.cs) and [validator](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlDocumentCacheProviderPrerequisiteValidatorTests.cs) fixtures; every exact method/case in artifact | [14 passed](evidence/t17-sqlserver-setup/e18/e18.json), zero failed/skipped |

Changes after successful active validation remain outside v1 support; the above
rejection evidence supplies no active recovery or renewed-readiness guarantee. Follow
the [SQL Server prerequisite boundary](operations-runbook.md#sql-server-prerequisite-and-failure-handoffs)
and [owning contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).

## PostgreSQL Lifecycle Qualification (T18)

The existing [Postgresql Lifecycle runner](../../eng/ci/Invoke-CdcQualification.ps1)
passed 16 `CdcControllerManagedLifecycle` / `PostgresqlIntegration` cases. After
correcting the live fixture's stale connector-name lookup, the complete shared
fixture was rerun from a fresh deployment: required `CDC-DOC cdc-managed-start`
passed, zero failed/skipped. No controller code changed after that 16-case run. The
[qualification report](evidence/t18-postgresql-lifecycle/qualification.json) requires
nine outcomes across six named controller methods and the marked live case;
missing, skipped, unexecuted or duplicate required cases fail qualification. This
report combines the unchanged passing controller selection with the final passing
live-case report. The [initial runner report](evidence/t18-postgresql-lifecycle/initial-qualification.json)
preserves the earlier live-fixture failure; it is not a passing whole-run claim.
[Controller outcomes](evidence/t18-postgresql-lifecycle/controller-results.json),
[condensed observations](evidence/t18-postgresql-lifecycle/controller-observations.json),
[live outcome](evidence/t18-postgresql-lifecycle/cdc-runbook-live-lifecycle.json) and
[live assertions](evidence/t18-postgresql-lifecycle/managed-lifecycle-runbook.json)
retain the distinct scopes. SQL Server results are recorded separately in [T19](#sql-server-lifecycle-qualification-t19).

The shared [live fixture](../../eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1)
executes the exact marked inventory, managed stop/start, intact restart/resume,
validation and provenance-rejection commands through the existing process harness.
Managed stop records inventory `Stopped` and a stopped shared worker; managed start
retains the custom state root, settings and binding, resumes explicitly and restores
HTTP health. Restart and resume each return exit 0 with `ready: true`. Standalone
`cdc-validate` returns exit 1 with `Projection` / `Unavailable`,
`preStartEligible: true` and `publicationReady: false` because its read-only
invocation has not run a projection executor. This remains distinct from lifecycle readiness and hosted DMS health.
The single-binding `cdc-disclosure-containment-result` command returns exit 0 and
`targetShutdownVerified: true` while the shared worker remains running and inventory
remains `Active`; it is not complete shared-worker shutdown or disclosure closure.
The original writer-publication intent count remains one: lifecycle success does
not authorize initial writer admission.

The controller method
`It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start`
proves persisted offsets, no tasks or consumption after a worker restart and before
guarded start. The live rejection matrix additionally retains `STOPPED`, zero tasks
and identical offsets after missing, corrupt, unsafe-permission and contradictory
binding/workflow/source-history inputs. Exact validate and resume commands reject
all twelve faults. Fixture restoration isolates tests; it is not an operator
provenance-repair procedure. Two independent empty/populated sources each produce
`ProviderSetup` / `ValidationFailed`, with validation `preStartEligible: false`;
both source identities and committed offsets remain unchanged. This live case
substitutes only the setup connection in a private settings copy while retaining
the original CMS selection. The controller fixture separately tests complete
physical-source substitution. Required controller cases also reject unavailable
provider/offset evidence and preserve a retained terminal incident despite healthy
current observations. These sampled checks do not certify unsampled history.

[Wrapper ordering results](evidence/t18-postgresql-lifecycle/wrapper-checks.json)
reuse the existing lifecycle/worker harness: all peer bindings must stop and verify
before worker shutdown; interrupted shutdown must recheck live stop evidence;
failed resume blocks DMS and unmanaged retry. These peer and interruption cases use
mocked infrastructure; live persistence and no-consumption evidence comes from the
provider fixtures above. The full wrapper/qualification selection passed 349 cases;
the final required-case guard selection passed 68. They are complementary evidence,
not claims of a live multi-binding deployment.

The first rehearsal exposed a production integration gap: standalone restart/resume
mutated Connect successfully but never started their owned projection executor,
so fresh readiness failed with projection unavailable. Under the amended
[owning lifecycle contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary),
all three lifecycle operations now start that executor after eligible preflight
(and verified stop for managed start); repeated start preserves an existing executor.
Read-only initialization and standalone observation remain unchanged. Fresh live
and controller qualification above uses the corrected build. Full CDC runtime
unit tests passed 4,641 and SchemaTools CDC tests passed 810, zero failed/skipped.
No schema, mapping-version or authoritative input changed.

[Run details](evidence/t18-postgresql-lifecycle/run-details.json) record immutable
images, private-path substitutions, supported timing budgets and assertions.
The final lifecycle fixture configures matching 1 GiB worker heap values from
initial creation after a rehearsal exhausted the 512 MiB default during plugin
scanning. The shipped default and retained failed-deployment settings were unchanged.
Qualification is `LocalSingleBroker` / `AuthorizationDisabledLocal`,
`aclIsolationProven: false`. It proves no consumer ACL isolation, production
multi-broker durability, API-message behavior, adoption or replacement workflow.
Governed teardown removed the owned stack, volumes and inventory. Raw logs,
settings, credentials and offset values stay private. The exact `cdc-enable-retry`
snippet remains unexercised; T16/T17 separately record intact setup-wrapper retries.

## SQL Server Lifecycle Qualification (T19)

The existing [Mssql Lifecycle runner](../../eng/ci/Invoke-CdcQualification.ps1)
now requires the same shared marked-command case as PostgreSQL, plus nine outcomes
across six named controller methods. The [qualification report](evidence/t19-sqlserver-lifecycle/qualification.json)
combines 19 passing `CdcControllerManagedLifecycle` / `MssqlIntegration` cases with
the final passing `CDC-DOC cdc-managed-start` live case, zero failed/skipped in
those final selections. The [initial runner report](evidence/t19-sqlserver-lifecycle/initial-qualification.json)
retains the earlier live-fixture failure; it is not a passing whole-run claim.
That rehearsal reached the source-mismatch SQL after completing lifecycle and
provenance checks. The `sqlcmd` default `QUOTED_IDENTIFIER OFF` rejected its count
query and an isolated population probe. The fixture now enables the required SET
option and uses SQL Server bracket quoting. After governed teardown, the entire
marked live case was rerun on a fresh deployment. No controller code changed after
the passing 19-case selection. Missing, skipped, unexecuted or duplicate required
cases fail qualification. Nightly installs
Pester for both providers' Lifecycle lanes; each requires an exclusively owned
disposable stack. No production runtime or design contract changed in T19.

[Controller results](evidence/t19-sqlserver-lifecycle/controller-results.json),
[condensed observations](evidence/t19-sqlserver-lifecycle/controller-observations.json)
and [live assertions](evidence/t19-sqlserver-lifecycle/managed-lifecycle-runbook.json)
record distinct scopes under the
[managed lifecycle owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
and [continuity/replacement boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral):

- Exact `cdc-state-inventory`, `cdc-managed-stop` and `cdc-managed-start` use the
  original custom root and retained settings. Complete stop leaves inventory
  `Stopped` and the worker stopped; managed start restores `Active` and HTTP health.
  The binding and settings remain unchanged. The required controller case
  `It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start`
  verifies persisted committed offsets and no consumption before explicit resume.
- `cdc-intact-restart` and `cdc-intact-resume` return exit 0 and `ready: true`.
  Standalone `cdc-validate` returns exit 1, `Projection` / `Unavailable`,
  `preStartEligible: true`, `publicationReady: false`; its read-only invocation
  has not run a projection executor. Lifecycle success does not create a second
  initial writer-publication intent or certify an unsampled interval.
- `cdc-disclosure-containment-result` returns exit 0 and
  `targetShutdownVerified: true` for one binding while the shared worker stays
  running and inventory stays `Active`. This is not complete shared-worker
  shutdown, consumer fencing or disclosure closure.
- Exact `cdc-provenance-rejection` and `cdc-intact-resume` reject all twelve
  missing/corrupt/unsafe-permission/contradictory binding, workflow and source-history
  faults. Every rejection leaves the connector `STOPPED`, zero tasks and identical
  offsets. Test restoration is fixture isolation, never an operator repair recipe.
- SQL Server fixture-only empty/populated database clones have different physical
  source identities. Both marked commands reject them with `ProviderSetup` /
  `ValidationFailed`; validation reports `preStartEligible: false`. Source
  identities, original settings/binding and committed offsets remain unchanged.
  The private settings copy substitutes the setup connection only; the required
  controller cases separately substitute the complete physical source. Cloning and
  identity fault injection are not supported adoption/replacement operations.
  Required controller methods also reject unavailable provider/offset evidence and
  preserve terminal incidents despite healthy current observations.

[Wrapper checks](evidence/t19-sqlserver-lifecycle/wrapper-checks.json) passed 364
cases, zero failed/skipped: required-case guards, shared-worker peers, interrupted
stop/start and snippet binding. These use mocked infrastructure for ordering;
live persistence/no-consumption evidence comes from the provider tests. They do
not claim a live multi-binding deployment.

[Run details](evidence/t19-sqlserver-lifecycle/run-details.json) record immutable
SQL Server 2025, Connect and broker images, application/CMS image IDs and declared
fixture substitutions. The selected profile is `LocalSingleBroker` /
`AuthorizationDisabledLocal`, `aclIsolationProven: false`; no secured ACL,
production durability, API-message or consumer-store certification is claimed.
Governed retirement/teardown removed all owned stack resources and inventory.
Raw settings, logs, credentials and offsets remain private. Exact
`cdc-enable-retry` remains unexercised; [T17](#sql-server-setup-qualification-t17)
separately records intact setup-wrapper retries. Remaining recovery, sizing,
retirement/history, telemetry and consumer procedures retain their pending tasks.

## PostgreSQL native recovery qualification (T21)

The PostgreSQL `Recovery` selection uses the existing
[provider-parameterized native-recovery fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcNativeRecoveryTests.cs).
Its [required-case report](evidence/t21-postgresql-recovery/qualification.json)
requires nine outcomes across seven methods; missing, skipped or duplicate cases
fail qualification. [Stable test results](evidence/t21-postgresql-recovery/controller-results.json)
and [condensed controller observations](evidence/t21-postgresql-recovery/controller-observations.json)
retain the test layer separately from
[packaged command results and watch passes](evidence/t21-postgresql-recovery/marked-commands.json).
The owning [native-recovery contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
limits every result below.

- `It_observes_publication_before_crash_revalidation_and_rejects_prior_process_metrics`
  observes committed-offset advancement and progress publication after SIGKILL,
  before any controller revalidation. It invokes exact `cdc-native-recovery-watch`,
  rejects retained pre-crash telemetry, then obtains fresh in-process readiness.
  The interval remains uncertified. The retained-terminal variant of
  `It_contains_recovered_connectors_and_retains_terminal_history_loss` also observes
  publication despite an incident latched before the crash.
- `It_detects_failed_task_recovery_on_the_same_worker_without_certifying_the_gap`
  uses the existing fixture-only fatal transform fault, restores the exact
  configuration, rejects stale metrics and invokes the same three-pass watch.
  Same-worker recovery and later health do not prove pre-consumption fencing.
- Both `It_routes_incomplete_or_acknowledged_but_unverified_shutdown_to_native_recovery`
  cases inject transport failures around a real stop/readback. Neither records a
  verified shutdown completion. After a real worker crash, exact
  `cdc-incomplete-shutdown-status` and `cdc-native-recovery-watch` report bounded
  observations. The controller rejects managed start without resuming. These
  transport fault hooks are not a network-partition or whole-stack shutdown test.
- `It_rejects_missing_provenance_after_native_recovery_without_reconstructing_state`
  invokes marked status against fixture-owned missing binding, journal and source
  history. Binding absence yields `Request/InvalidInput`, exit 2 and no `data`;
  missing journal/source history yields `WorkflowState/Unavailable`, exit 1. No missing file is
  reconstructed. `It_rejects_unknown_recovery_evidence_and_unauthorized_controller_mutations`
  separately injects unavailable provider/offset/worker/status/metrics evidence at
  the controller layer; unknown evidence never authorizes a mutation.
- Both terminal-history cases invoke marked watch through the packaged CLI.
  They require `incidentPersistence: "Persisted"`, `containment: "Stopped"` and a
  separate live STOPPED readback. Real committed-offset deletion is fixture-only
  fault injection; the other case retains its pre-existing terminal incident.
  The incident remains latched and restart stays rejected. A stop attempt alone
  cannot satisfy these assertions.
- `It_repeats_the_offline_barrier_sequence_after_worker_crash_interrupts_initial_readiness`
  retains the existing controller-only evidence for fresh initial authorization
  after an interrupted barrier; it does not claim an initial-retry snippet run.

The command harness reuses the marked-example extractor and literal-argument binder.
Only the two declared private settings/state paths are substituted; watch retains
exactly three passes. It checks actual process exit, final stdout JSON, each JSON
watch pass and final safe diagnostic lines on stderr. Final `data` must equal the
last pass. No mocked command result or raw Connect lifecycle call substitutes for
the marked invocation.

The fixture supplies live PostgreSQL, native Kafka and the pinned Connect worker,
plus the existing packaged-administration CMS test endpoint and runtime-compatible
schema workspace. Packaged observation loads the complete settings through production
configuration and reaches live provider, source-history, broker, offset-store,
connector and telemetry inspection. Healthy recovered cases assert satisfied
provider/configuration/runtime and healthy source history. Status/watch do not
start their invocation-owned projector: projection health remains unavailable,
so ordinary command results are exit 1 / not ready. Separate in-process projector
assertions establish fresh current readiness and stale-observation rejection.
A fresh CLI process cannot recover its predecessor's worker/task sample and reports
`Unobserved` for that gap; durable incomplete shutdown reports `NativeRecovery`.
No aggregate-ready, multi-binding, ACL, consumer-baseline, API-message or state-loss
recovery claim is made.

[Command/recovery checks](evidence/t21-postgresql-recovery/checks.json) record the
separate unit layers: `CdcCommandContainmentTests` covers preparation/journal
failures, failed or uncertain stop/readback, command deadlines and caller
cancellation. `Given_CdcControllerStatus.It_reports_failed_latch_and_still_attempts_containment`
(false/true) injects incident-write failure and combined write/stop failure. These
are unit fault seams, not live storage-failure evidence. The runbook maps
`WorkflowState/Unavailable` plus failed persistence to durable-state escalation,
and Connect failure plus failed containment to infrastructure fencing; successful
persistence never substitutes for verified stop.

[Run details](evidence/t21-postgresql-recovery/run-details.json) and the
[worker qualification record](evidence/t21-postgresql-recovery/qualified-image.json)
record immutable images, local authorization-disabled scope and fixture substitutions.
Only owned containers, volumes and temporary state are removed by fixture cleanup.
Raw logs/settings remain private. SQL Server's corresponding exercise passed
[T22](#sql-server-native-recovery-qualification-t22); remaining procedure tasks retain
their pending status.

## SQL Server native recovery qualification (T22)

The SQL Server `Recovery` selection reuses T21's
[provider-parameterized fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcNativeRecoveryTests.cs)
and packaged marked-command harness. The
[required-case report](evidence/t22-sqlserver-recovery/qualification.json) records
all nine outcomes across seven methods passing with no failures or skips.
[Stable test results](evidence/t22-sqlserver-recovery/controller-results.json),
[controller observations](evidence/t22-sqlserver-recovery/controller-observations.json)
and [11 marked command records](evidence/t22-sqlserver-recovery/marked-commands.json)
keep live command evidence separate from injected controller faults.
The [native-recovery owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#controller-managed-lifecycle-and-native-recovery-boundary)
and [T21 scenario descriptions](#postgresql-native-recovery-qualification-t21)
apply, with these SQL Server results and distinctions:

- Worker crash: committed offsets advance and progress records publish after
  SIGKILL, before controller revalidation. Exact `cdc-native-recovery-watch`
  observes the recovered service; in-process assertions reject pre-crash metrics
  and require fresh readiness. A retained terminal incident also permits observed
  publication before later CLI containment. Neither case certifies the interval,
  pre-consumption fencing or an exact consumer baseline.
- Task recovery: the existing SQL Server fixture temporarily changes its own
  connector login password and restarts its task to observe `Failed`. It restores
  the original password before controller inspection, then restarts the task.
  The worker identity and connector configuration remain unchanged; retained
  telemetry rejects and fresh observations succeed. The exact three-pass watch
  runs against the recovered services. This credential fault is confined to the
  disposable fixture; it is not an operator recovery step.
- Incomplete shutdown: both failed-stop and acknowledged-but-unverified-stop hooks
  leave shutdown unverified and its completion absent. After real worker recovery,
  exact `cdc-incomplete-shutdown-status` and `cdc-native-recovery-watch` report
  `NativeRecovery`; managed start rejects without resume. These are transport
  hooks around live services, not a whole-stack partition qualification.
- Missing binding returns `Request/InvalidInput`, exit 2, without `data`.
  Missing journal or source history returns `WorkflowState/Unavailable`, exit 1.
  Marked status never reconstructs those files. Unknown provider, offsets, worker,
  status and metrics use separately labeled controller fault hooks; unavailable
  continuity evidence cannot authorize restart. Preserve evidence and escalate
  through [unsupported provenance](operations-runbook.md#unsupported-provenance).
- Both terminal-history variants run exact watch, require durable
  `incidentPersistence: "Persisted"` and `containment: "Stopped"`, and independently
  read back STOPPED from the live worker. One deletes fixture-owned committed
  offsets; the other retains a pre-crash incident. Later observations retain the
  terminal incident and reject restart. A stop attempt does not qualify containment.
- Interrupted initial readiness repeats the offline barrier and obtains fresh
  authorization at the controller layer. This does not exercise the separate
  initial-enable-retry snippet.

All marked invocations use the complete matching private settings, original state,
existing test CMS/schema workspace, live SQL Server 2025, Kafka and qualified
Connect worker. Only the declared settings/state paths are substituted; watch
keeps three passes. Assertions verify process exit, final stdout JSON, stderr
watch passes and diagnostics. Standalone status/watch leave their projector
unstarted and report projection unavailable, exit 1 / not ready; fresh in-process
readiness is a separate assertion. A new CLI process reports `Unobserved` without
its predecessor's worker/task sample; durable incomplete-stop intent establishes
`NativeRecovery`. No aggregate-ready, ACL, multi-binding, API-message or state-loss
recovery claim follows.

[Unit and qualification checks](evidence/t22-sqlserver-recovery/checks.json) label
`CdcCommandContainmentTests` and
`Given_CdcControllerStatus.It_reports_failed_latch_and_still_attempts_containment`
(false/true) as unit fault seams. Incident-write failure requires durable-state
escalation; connector-stop failure requires infrastructure fencing under the
[runbook's diagnostic-to-action table](operations-runbook.md#native-recovery).
These checks do not claim live storage-failure or network-partition evidence.

[Run details](evidence/t22-sqlserver-recovery/run-details.json) and the
[qualified worker record](evidence/t22-sqlserver-recovery/qualified-image.json)
retain immutable image IDs, exact selection, source hashes and the local
`AuthorizationDisabledLocal` scope (`aclIsolationProven: false`). Fixture cleanup
removes only its own containers, volumes and state; raw settings and logs remain
private. Other procedure tasks remain pending.

<a id="postgresql-record-size-qualification-t23"></a>

## PostgreSQL record-size qualification (T23)

The complete fresh `Postgresql / RecordSize` lane passed **20/20** cases, with no
failures or skips. Its separate required-case guard passed all seven methods,
including both explicit inventory variants and the interrupted retry. See
[qualification](evidence/t23-postgresql-record-size/qualification.json),
[case outcomes](evidence/t23-postgresql-record-size/controller-results.json),
[run inputs and immutable images](evidence/t23-postgresql-record-size/run-details.json),
[worker qualification](evidence/t23-postgresql-record-size/qualified-image.json) and
[focused checks](evidence/t23-postgresql-record-size/checks.json).

The [shared marked-command fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.Runbook.cs)
uses the original managed state, complete private settings, packaged-test CMS/schema
workspace and fixture-owned PostgreSQL, Kafka and Connect services. It uses the
shipped Compose broker-size adapter rather than test-only broker configuration
mutation for these three cases. The exact commands are read from marked snippets;
only the runbook-declared identities, paths, evidence tokens and qualified limits
are substituted. Calls use finite 180-second/300-second call/wait budgets and a
60-second observation-age policy. Local authorization remains disabled and the
results report `aclIsolationProven: false`.

[Marked command results](evidence/t23-postgresql-record-size/marked-commands.json)
contain nine invocations:

- `cdc-size-no-consumers` + `cdc-size-increase`: an explicitly empty inventory,
  then missing-flag rejection (exit `2`) and confirmed completion (exit `0`).
- `cdc-size-consumers` + `cdc-size-increase`: a populated fixture attestation,
  with the same rejection/completion checks. The CLI creates the invocation ID
  and timestamp under the controller lock.
- `cdc-size-consumers` + `cdc-size-retry`: a fixture-only controller cancellation
  after a real broker change leaves pending intent, the old topic/producer limits,
  original settings and the broker override. Ordinary validation/watch/restart
  remain not ready. Missing renewed confirmation rejects without advancing limits.
  The packaged retry uses the same operation/binding/ceilings with revision-2
  capacity evidence; it durably retains two distinct invocation acknowledgements
  and completes without rollback, a new generation, or offset reset.
- Three `cdc-validate` invocations after the documented settings update: exit `1`,
  `preStartEligible: true`, `publicationReady: false`, `Projection/Unavailable`.
  These standalone observations do not start their own projector and cannot
  replace the successful increase result.

[Rollout observations](evidence/t23-postgresql-record-size/rollout-observations.json)
record the actual transition from **1,000,000** to **2,000,000** record bytes and
**33,554,432** to **67,108,864** producer-buffer bytes, plus broker read-back,
retained topic identities/partitions, source-partition identity and non-size
connector configuration. The command itself preserves the full settings file.
Only after completion does the fixture update `Cdc:MaxRecordBytes` and
`Cdc:ProducerBufferBytes`; the controller-written broker override remains intact.
The existing Compose-persistence case separately verifies replacement and retained
Kafka state using the shipped services.

The production integration fix starts the invocation-owned projection executor
after acknowledged alignment and fresh eligibility, before resume. Both-provider
unit regressions cover ordering, missing confirmation/provenance and startup failure;
a failure leaves pending intent without resume. Existing executors and caller-owned
disposal are preserved. The
[owning contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase)
records this boundary. There is no projection reactivation/rebuild or API-writer
admission, and status/watch do not complete pending increases.

Existing live fixtures also passed all seven interruption boundaries, the six
incomplete/mismatched-confirmation cases, and real over-budget producer recovery.
Both-provider acknowledgement unit results cover changed consumer deployments,
reused evidence and later higher ceilings. The producer case observes an actual
`RecordTooLargeException`, then recovery of the retained uncommitted materialized
record after alignment. It is bounded operational evidence, not an exact serialized
threshold calculation, an API request-size limit, or an API-driven message scenario.
The [message-size contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size)
continues to own those distinctions.

Initial test-helper/compile rehearsals were corrected before qualification; the
successful three-case rehearsal is separate from this complete fresh lane. Exported
artifacts use the existing sanitized evidence path and omit raw settings, credentials,
provider logs and document bodies. No consumer product, ACL isolation, production
capacity or multi-binding qualification is claimed here. SQL Server live procedure
qualification is recorded separately in [T24](#sqlserver-record-size-qualification-t24).

<a id="sqlserver-record-size-qualification-t24"></a>

## SQL Server record-size qualification (T24)

The fresh `Mssql / RecordSize` selection passed **20/20** cases, with no failures,
skips or SQL Server startup failures. The separate required-case guard passed all
seven methods, including the three marked-command cases. See
[qualification](evidence/t24-sqlserver-record-size/qualification.json),
[case outcomes](evidence/t24-sqlserver-record-size/controller-results.json),
[run inputs and immutable images](evidence/t24-sqlserver-record-size/run-details.json),
[qualified worker](evidence/t24-sqlserver-record-size/qualified-image.json) and
[focused checks](evidence/t24-sqlserver-record-size/checks.json).

The shared [provider-parameterized fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcRecordSizeIncreaseTests.Runbook.cs)
uses the T23 packaged command/settings harness with fixture-owned SQL Server 2025,
Kafka and Connect services, original managed state, and the packaged-test CMS/schema
workspace. The marked cases use the shipped Compose broker-size adapter. Their
SQL Server identities come from the admitted binding; the acknowledgement provider
is `SqlServer`. Only declared fixture substitutions are applied. Immutable image
identities and source/snippet hashes are retained in the run details. The local
profile reports `aclIsolationProven: false`.

[Marked command results](evidence/t24-sqlserver-record-size/marked-commands.json)
record nine invocations:

- Both `cdc-size-no-consumers` and `cdc-size-consumers` load the exact marked JSON
  and drive `cdc-size-increase` against live services. Each rejects missing
  `--confirm-consumer-capacity` with exit `2` and unchanged limits, then completes
  with exit `0`, `succeeded: true`, `data.succeeded: true`, `data.ready: true` and
  the original operation ID.
- `cdc-size-retry` follows a fixture-only cancellation after a real broker change.
  Pending intent, old topic/producer limits, original settings and the broker
  override remain; ordinary validate/watch/restart stay not ready. Missing renewed
  confirmation rejects without advancing limits. The packaged retry retains the
  operation, binding and ceilings, renews revision-2 consumer capacity evidence,
  retains two distinct invocation acknowledgements and completes without offset reset.
- Three post-update `cdc-validate` calls return exit `1`, `preStartEligible: true`,
  `publicationReady: false` and `Projection/Unavailable`. The standalone validator
  does not start its projector; these observations do not negate or replace the
  completed increase result.

[Rollout observations](evidence/t24-sqlserver-record-size/rollout-observations.json)
verify **1,000,000 → 2,000,000** record bytes and
**33,554,432 → 67,108,864** producer-buffer bytes, broker read-back, retained
source-partition/topic identity and non-size connector configuration. The CLI leaves
settings unchanged. After success, the fixture updates only `Cdc:MaxRecordBytes`
and `Cdc:ProducerBufferBytes` and verifies the broker override remains intact.
The existing Compose-persistence case also passes broker/worker replacement and
retained-history checks.

All seven interruption boundaries, six incomplete/mismatched-confirmation cases,
and real over-budget producer recovery passed. Both-provider unit evidence covers
changed consumer deployments, reused capacity evidence and changed requested
ceilings. The T23 invocation-owned projector fix requires no SQL Server-specific
change; its ordering, rejection and startup-failure regressions passed under the
[owning rollout contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#in-place-record-size-increase).
The producer case observes `RecordTooLargeException` and replay of the uncommitted
materialized record after alignment. This bounds operational capacity evidence;
the [message-size contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size)
continues to own serialized-size semantics.

The first selection was cancelled after its marked no-consumers command timed out:
observed lag was 1,012 ms against the shared helper's 1,000 ms threshold. The
record-size cases now use the 5,000 ms threshold in both public setup snippets;
SQL Server capture/poll cadence can exceed one second while caught up. The complete
fresh selection above supplies acceptance. This is a fixture configuration correction;
production readiness and other observation fixtures are unchanged. A second full
selection passed 19/20: the marked retry failed during Compose worker startup,
before its command ran, with SQL Server ready. An isolated retry passed 1/1 with
no further implementation change. The third complete selection supplies the
passing evidence above; neither earlier attempt is counted as qualification.

Artifacts use the existing sanitized exporter and omit raw settings,
credentials, provider logs, document bodies and raw offset positions. Consumer
attestations remain operator evidence; this run does not certify independent
consumer products, ACL isolation, production capacity, multi-binding deployments,
exact serialized thresholds or API-driven message scenarios.

## PostgreSQL history and retirement qualification (T25)

T25 executes the exact history, retirement, restamp-status and disclosure-stop
snippets on fixture-owned PostgreSQL services. The owning contracts are
[projection administration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration),
[guarded retirement](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding),
and [disclosure response](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction).
This supplies CDC-INV-14/15 procedure evidence; SQL Server is qualified separately in
[T26](#sql-server-history-and-retirement-qualification-t26).

The complete fresh History selection passed 25 packaged-history cases and six
provider-cleanup/retirement cases, with no failures or skips. Separate report
guards require all six history methods and all six cleanup methods; missing,
skipped or duplicate cases fail. Discovery matches the existing
`CdcPublicationHistory&PostgresqlIntegration` selection plus the narrow
`(CdcArtifactCleanup|CdcRunbookRetirement)&PostgresqlIntegration` follow-on.

- `cdc-history-internal-only`: published SchemaTools creates an ordinary managed
  source; published DocumentCacheAdmin performs each of `activate-offline`,
  `deactivate-offline`, and `recover-cache-ahead` against trusted internal-only
  history. Each exits 0, mutates the expected cache/lifecycle, and clears the latch.
- `cdc-history-rejected`: the same normalized tenant/data-store and provider-read
  source fingerprint are used for all three operations in each of 15 scenarios:
  possible, active, historical, unknown, absent/empty root, unreadable evidence,
  wrong target/deployment/source, missing receipt/journal/history and corrupt
  journal/history. All exit 10 with `downstreamHistoryPresentOrUnknown`,
  `mutated: false`, and unchanged database and provenance snapshots. Additional
  cases retain rejection after retired possible exposure and prove both winners
  of concurrent reservation/administration. These are fixture-owned evidence
  mutations, not operator repair steps.
- `cdc-retire`: missing destructive confirmation and a wrong generation exit 2,
  leaving the original journal unchanged. Existing controller fault seams
  interrupt after verified offset removal and during topic cleanup. The marked
  CLI retry completes and repeats with exit 0, the same retirement operation ID,
  empty diagnostics and unchanged settings. Live inspections verify governed
  artifacts absent, binding/incident removed last, and peer topic/shared offset
  storage preserved. Original creation receipt and historical source exposure
  survive; initial activation remains rejected. Provider cases separately cover
  shared/broadened artifacts, wrong source and committed deletion with lost reply.
- `cdc-restamp-handoff-status`: one real status observation exits 1 with unavailable
  invocation-owned projection health. This executes the handoff inspection, not
  an E18 restamp, Kafka replacement proof or a certified consumer baseline.
- `cdc-disclosure-containment-result`: verified stop exits 0 with
  `targetShutdownVerified: true` and `ready: false`; offsets and binding survive.
  Consumer fencing and platform purge are not performed or certified by this case.

Per-binding retirement does not remove shared volumes or prove byte purge.
The separately executed 287 lifecycle/E2E teardown Pester cases qualify wrapper
ordering, peer protection, interrupted cleanup and protected nested roots with
mocked native boundaries. Earlier [T16](#postgresql-setup-qualification-t16) and
[T18](#postgresql-lifecycle-qualification-t18) live fixtures already execute the
marked stack-teardown command. T25 starts no additional local/E2E stack, and does
not claim a new live execution of the E2E teardown alternative. Disclosure closure
still requires deployment-owned broker/platform purge and independent-store
attestations; retirement never authorizes old-generation restart or deferred
new-generation recovery.

The final run is `TestResults/cdc-docs-postgresql-history-t25-20260923-03`
(.NET 10.0.102, PowerShell 7.6.6, Pester 5.7.1). It records 63 marked Admin
invocations and six marked SchemaTools commands. The separate checks passed 819
SchemaTools CDC unit cases, 94 qualification-runner Pester cases, and the 287
wrapper cases above, plus 17 final relative-link cases, with no failures/skips. Formatting, ScriptAnalyzer errors,
diff whitespace and actual fixture-credential scans passed.

- [Qualification report](evidence/t25-postgresql-history/qualification.json) and
  [executed test IDs](evidence/t25-postgresql-history/test-results.json).
- [History observations](evidence/t25-postgresql-history/history-observations.json):
  snippet, scenario, operation, exit and production result; unchanged-state hashes
  and concurrency boundary observations retain their original test identity.
- [Marked retirement/handoff commands](evidence/t25-postgresql-history/marked-retirement-commands.json)
  and [cleanup observations](evidence/t25-postgresql-history/retirement-observations.json).
- [Checks](evidence/t25-postgresql-history/checks.json),
  [run details and source/snippet hashes](evidence/t25-postgresql-history/run-details.json),
  and [qualified worker](evidence/t25-postgresql-history/qualified-image.json).

The installed VSTest `--list-tests` emits the assembly catalog despite the supplied
filter; all required IDs appear there. The execution TRX and guards establish the
exact selected provider/counts. History admission uses a separately isolated
PostgreSQL admin server and the existing CMS stand-in; retirement uses the owned
provider/Connect/Redpanda services. Immutable image IDs/references are recorded,
and local ACL isolation remains false.

Two earlier complete runs passed all history and five provider cases but failed
the retirement assertion: first, wrong-generation input returns exit 2 rather than
1; second, offsets can advance before stop is verified. Assertions now follow the
production boundary: retained streaming offsets are sampled after verified stop,
and later source work is not consumed. The final complete selection supplies
acceptance. These were fixture corrections, with no production/design/schema or
mapping-version change. Initial compiler/analyzer issues were corrected before
live acceptance; prior failures remain recorded in run details.

Artifacts pass through the existing safe exporter and omit raw settings,
credentials, logs, document bodies and raw offsets. Original diagnostics remain
private. Successful cleanup is neither platform purge proof nor a supported
new-generation recovery procedure.

## SQL Server history and retirement qualification (T26)

T26 executes the exact history, retirement, restamp-status and disclosure-stop
snippets on fixture-owned SQL Server services. The owning contracts are
[projection administration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration),
[guarded retirement](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding),
and [disclosure response](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction).
This supplies CDC-INV-14/15 procedure evidence using the unchanged shared T25 wiring.
No production, test, design, schema or mapping-version changes were needed.

The complete fresh History selection passed 25 packaged-history cases and six
provider-cleanup/retirement cases, with no failures or skips. Separate report
guards require all six history methods and all six cleanup methods; missing,
skipped or duplicate cases fail. Discovery matches the existing
`CdcPublicationHistory&MssqlIntegration` selection plus the narrow
`(CdcArtifactCleanup|CdcRunbookRetirement)&MssqlIntegration` follow-on.

The shared [T25 scenario descriptions](#postgresql-history-and-retirement-qualification-t25)
apply with Admin provider token `sqlserver`, the same normalized empty tenant/CMS
ID, and the SQL Server provider-read physical-source fingerprint. SQL Server results:

| Exact snippet | Observed outcome |
| --- | --- |
| `cdc-history-internal-only` | All three offline operations exit 0, reach the expected lifecycle and clear the latch. |
| `cdc-history-rejected` | All three operations in each of 15 history-fault scenarios exit 10, `downstreamHistoryPresentOrUnknown`, `mutated: false`; database/provenance snapshots stay unchanged. Retired exposure remains rejected; both concurrency winners preserve the lock boundary. |
| `cdc-retire` | Missing cleanup intent and wrong generation exit 2 without journal mutation. Following fixture interruptions after offset removal and during topic cleanup, the packaged retry and repeat exit 0 with the original operation ID and unchanged settings. |
| `cdc-restamp-handoff-status` | Status exits 1 with invocation-owned projection health unavailable; this is the inspection handoff, not restamp execution or consumer-baseline proof. |
| `cdc-disclosure-containment-result` | Stop exits 0 with `targetShutdownVerified: true`, `ready: false`; binding and streaming offsets survive, and later source work is not consumed. |

Live retirement inspections verify owned artifacts absent, binding/incident removed
last, peer topic/shared offset storage preserved, and original creation receipt and
historical source exposure retained. Initial activation remains rejected. SQL Server
provider cases cover CDC capture instances, gating-role membership and owned unused
capture/cleanup jobs, including shared/broadened artifacts, a wrong source and a
committed deletion with a lost reply. The SQL Server schema-history topic is removed
only within the retired generation's scope. Faults affect fixture-owned artifacts.

Per-binding retirement does not remove shared volumes or prove byte purge.
The separately executed 287 lifecycle/E2E teardown Pester cases qualify wrapper
ordering, peer protection, interrupted cleanup and protected nested roots with
mocked native boundaries. Earlier [T17](#sql-server-setup-qualification-t17) and
[T19](#sql-server-lifecycle-qualification-t19) live fixtures already execute the
marked stack-teardown command. T26 starts no additional local/E2E stack, and does
not claim a new live execution of the E2E teardown alternative. Disclosure closure
still requires deployment-owned broker/platform purge and independent-store
attestations; retirement never authorizes old-generation restart or deferred
new-generation recovery.

The final run is `TestResults/cdc-docs-mssql-history-t26-20260923-01`
(.NET 10.0.102, runner PowerShell 7.4.10, wrapper PowerShell 7.6.6, Pester 5.7.1). It records 63 marked Admin
invocations and six marked SchemaTools commands. The separate checks passed 819
SchemaTools CDC unit cases, 94 qualification-runner Pester cases, and the 287
wrapper cases above, plus 17 final relative-link cases, with no failures/skips. Diff whitespace, source/snippet hashes and actual fixture-credential scans passed.

- [Qualification report](evidence/t26-sqlserver-history/qualification.json) and
  [executed test IDs](evidence/t26-sqlserver-history/test-results.json).
- [History observations](evidence/t26-sqlserver-history/history-observations.json):
  snippet, scenario, operation, exit and production result; unchanged-state hashes
  and concurrency boundary observations retain their original test identity.
- [Marked retirement/handoff commands](evidence/t26-sqlserver-history/marked-retirement-commands.json)
  and [cleanup observations](evidence/t26-sqlserver-history/retirement-observations.json).
- [Checks](evidence/t26-sqlserver-history/checks.json),
  [run details and source/snippet hashes](evidence/t26-sqlserver-history/run-details.json),
  and [qualified worker](evidence/t26-sqlserver-history/qualified-image.json).

The installed VSTest `--list-tests` emits the assembly catalog despite the supplied
filter; all required IDs appear there. The execution TRX and guards establish the
exact selected provider/counts. History admission uses a separately isolated
SQL Server 2025 admin server (major version 17, Agent running, nested triggers enabled)
and the existing CMS stand-in; retirement uses the owned
provider/Connect/Redpanda services. Immutable image IDs/references are recorded,
and local ACL isolation remains false.

The first complete live selection passed. An initial wrapper-check invocation used
the shell's default PowerShell 7.4.10 and failed 15 empty-environment-variable
expectations (272 passed). Repeating all 287 cases under the established 7.6.6
qualification runtime passed without source changes. The 7.4.10 live runner
performs no wrapper invocation in this selection; its complete History and cleanup
results passed independently.

Artifacts pass through the existing safe exporter and omit raw settings,
credentials, logs, document bodies and raw offsets. Original diagnostics remain
private. Successful cleanup is neither platform purge proof nor a supported
new-generation recovery procedure.

<a id="postgresql-telemetry-and-retention-qualification-t27"></a>

## PostgreSQL telemetry and retention qualification (T27)

The fresh complete PostgreSQL Telemetry selection passed **2/2 live cases**, with
zero failures/skips and both required-method guards passing. It executed **11 exact
marked inspections** on fixture-owned PostgreSQL 16.8, Redpanda and the shipped
immutable Connect/exporter image. See [qualification](evidence/t27-postgresql-telemetry/qualification.json),
[stable test outcomes](evidence/t27-postgresql-telemetry/test-results.json),
[observed fields/actions](evidence/t27-postgresql-telemetry/inspection-observations.json),
[image contract](evidence/t27-postgresql-telemetry/qualified-image.json) and
[revision, source/snippet hashes, substitutions and image digests](evidence/t27-postgresql-telemetry/run-details.json).
The profile is `LocalSingleBroker` / `AuthorizationDisabledLocal`, with
`aclIsolationProven: false`.

These procedures map to **CDC-INV-15** under the
[operations owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations),
[qualified telemetry contract](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry)
and [PostgreSQL source owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql).

| Exact snippet | Stable fixture/method | Executed outcome |
| --- | --- | --- |
| `cdc-telemetry-inspect` (five invocations) | `Given_CdcConnectorTelemetryQualification(Postgresql).It_qualifies_the_pinned_exporter_with_real_streaming_and_replaces_task_and_worker_metrics` | Initial, restarted-task and restarted-worker scrapes have finite current lag and typed optional statistics; stopped-task scrape has no selected connector/current lag (exported null, never zero). Scrape error is zero; worker start/heap are observed. The fixture's nonexistent Connect REST route makes curl fail and the exact script throws its unavailable-file action. |
| `cdc-pg-retention-inspect` (four invocations) | `Given_PostgresqlCdcRunbookRetention.It_executes_marked_slot_disk_and_progress_inspections_with_unavailable_actions` | Two bounded samples report active slot, retained/unconfirmed WAL distances, settings and timestamps after real heartbeat/committed-source advancement. Unlimited configured slot retention gives absent numeric budget; missing selected slot returns zero rows/exit 0 and routes to history diagnosis. Missing libpq service throws the unavailable-observation action. |
| `cdc-provider-disk-inspect` (two invocations) | Same retention method | Two explicit data/WAL paths report 1024-byte blocks, used/available capacity and percent use. Missing fixture WAL path makes the exact script reject the partial observation. Capacity is diagnostic; no disk-full fault, alert threshold or host backing-volume health claim. |

The existing exporter method separately calls the production telemetry adapter with
live worker/task correlation before and after worker restart, checks changed process
identity/start time and verifies isolation of a peer connector's measurements. The
manual curl scrape itself has no controller readiness authority. Optional-statistic
absence, malformed fields, stale collection, previous-pass reuse and changed
worker/task evidence are covered by **264 unit telemetry/native-recovery cases**;
**five metric-contract cases** also pass. These are explicitly unit layers in
[supporting test outcomes](evidence/t27-postgresql-telemetry/supporting-tests.json),
not extra live source faults. Runner Pester passed **100 cases**; source/analyzer
and documentation validation are recorded in the run details.

[Setup T16](#postgresql-setup-qualification-t16) supplies exact `cdc-status` and
`cdc-watch` evidence. [Recovery T21](#postgresql-native-recovery-qualification-t21)
supplies stale recovery evidence, missing provenance and terminal containment.
The two advancing source samples here reuse the existing heartbeat/committed-offset
assertions without exporting positions. They measure neither projection queue drain
nor consumer progress. Missing-slot diagnosis selects a nonexistent fixture slot;
it does not delete the real managed slot, prove terminal recovery, or authorize SQL
repair. Progress-topic policy/access inspection remains T20. E18's linked tuning
and workload evidence retains its original limits; production performance remains
deferred.

The [earlier failed attempt](evidence/t27-postgresql-telemetry/earlier-attempt.json)
remains separate: an invented exporter path still served metrics, so it did not
supply the intended unavailable-endpoint fault. The fixture now uses a nonexistent
route on its own Connect REST service. The full fresh second run supplies acceptance;
no production behavior changed. Fixture cleanup removed owned services and private
libpq/metrics files, preserving pre-existing services. Exported attachments contain
selected fields and actions, with no credentials, raw metrics, offsets or payloads.
SQL Server exact inspections remain pending T28; this is not API-message, ACL,
consumer-baseline or deployment-capacity certification.
