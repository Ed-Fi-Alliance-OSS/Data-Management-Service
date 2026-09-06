# CDC Runbook Evidence Index

This index records evidence for the [CDC runbook](operations-runbook.md), not a second
behavioral specification. DMS-1326 owns exercised-runbook evidence for `CDC-INV-14` and
`CDC-INV-15`; see the [design traceability table](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability).
Other invariants retain their owning suites. Projection evidence remains in the
[E18 matrix](../document-cache-documentation/cdc-inv-evidence.md).

## Row format and evidence layers

Each row identifies a delivered runbook anchor, design owner, applicable invariant IDs,
exact test identity (including parameterized cases), provider and evidence layer, actual
result, sanitized artifact location, and pending verification owner if any. A manual
review uses its review ID instead of inventing a test identity. An artifact accompanying
a row records revision, tools/provider/image versions, starting directory, invocation,
stdout/stderr and exit code, sanitization, and cleanup disposition as applicable.

Use separate rows for manual review, mocked CLI contracts, provider integration,
broker/connector integration, and API-driven exercises. A passing mock is not provider
evidence; an unavailable prerequisite or skipped case is not a pass. Keep output captured
before sanitization in the controlled local evidence workspace; publish only reviewed
sanitized artifacts while preserving relevant JSON fields and outcome/exit-code pairs.
No executable documentation catalog or automated documentation/link tests are used.

| Runbook anchor / review | Design owner / invariants | Exact identity and layer | Result / artifact | Remaining owner |
| --- | --- | --- | --- | --- |
| [Prerequisites](operations-runbook.md#prerequisites), [state](operations-runbook.md#deployment-state), [format](operations-runbook.md#procedure-format); T01 foundation | [Configuration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection), [binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 authoring support only | Manual review `T01-foundation`; no provider access | Reviewed; [record below](#t01-foundation-review), [root help](evidence/t01/help.txt), [CDC help](evidence/t01/cdc-help.txt) | Operational replay pending T14/T15; final reconciliation T16 |
| [Prerequisites/configuration](operations-runbook.md#prerequisites); T01 validation support | [Configuration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection); CDC-INV-15 support, not exercised-runbook closure | `EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit.Given_CdcControlOptionsTests`; all 77 exact selected case identities in artifact; provider-independent unit behavior | 77 passed, 0 failed, 0 skipped; [case results](evidence/t01/options-test-results.txt) | Provider claims still pending T14/T15 |
| [Monitoring](operations-runbook.md#monitoring), [routing](operations-runbook.md#incident-routing), [lag](operations-runbook.md#inspect-lag), [telemetry](operations-runbook.md#telemetry); T03 | [Status](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness), [telemetry](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations); CDC-INV-15 authoring support | Manual review `T03-monitoring`; provider-independent source/help/serialized fixture comparison | Reviewed; [record](#t03-monitoring-review), [projection help](evidence/t03/status-help.txt), [CDC status help](evidence/t03/cdc-status-help.txt) | T12 new assertions; T14/T15 deployed observations; T16 closure |
| [Observe CDC](operations-runbook.md#observe-cdc); T03 CLI support | [Status](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness); CDC-INV-15 | `Given_DocumentCacheAdminCdcJsonContracts`; mocked-dispatch CLI integration, all 7 exact cases in artifact | 7 passed / 0 failed / 0 skipped; [results](evidence/t03/cdc-json-results.txt), [capture provenance](#t03-monitoring-review) | T12 additional assertions; no live provider evidence |
| [CDC containment](operations-runbook.md#observe-cdc), [continuity triage](operations-runbook.md#route-continuity-incident), [lag](operations-runbook.md#inspect-lag); T03 owning behavior reuse | [Continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity), [barrier](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#provider-source-position-barrier); CDC-INV-10/11/15 supporting evidence | `Given_CdcSetupControllerStatus`, `Given_CdcSetupControllerRestart`, `Given_CdcConnectorLagObservationMapping`, `Given_CdcSetupControllerStatusEndpointPreflight`; fake provider/Connect and HTTP unit fixtures | 104 passed / 0 failed / 0 skipped; [exact cases](evidence/t03/cdc-status-lag-results.txt), [JSON artifact mapping](#t03-monitoring-review) | T14/T15 live read-back/replay; T16 closure |
| [Projection handoff](operations-runbook.md#projection-repair-handoff), [cache-ahead](operations-runbook.md#cache-ahead-containment), [representation correction](operations-runbook.md#representation-correction-handoff); T04 | [Repair](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations), [projection administration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration); CDC-INV-14/15 authoring support | Manual review `T04-projection-handoff`; source/help, existing E18 evidence and CLI fixture assertions | Reviewed; [record](#t04-projection-handoff-review), [E18 matrix](../document-cache-documentation/cdc-inv-evidence.md) | T12 missing assertions; T14/T15 downstream replay; T16 closure; E18-S08 restamp handoff unmet |
| [Packaged rejection](operations-runbook.md#cache-ahead-containment); T04 existing provider evidence | [History gate](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration); CDC-INV-14/15 | `Given_DocumentCacheAdminPostgresqlRunbookWorkflows`, `Given_DocumentCacheAdminMssqlRunbookWorkflows`, `Given_DocumentCacheAdminCdcShippedComposition`; real PostgreSQL/SQL Server plus subprocess CLI and fake CMS; no broker/Connect | Configured run 8 passed / 0 failed / 0 skipped; [exact cases](evidence/t04/runbook-shipped-configured-results.txt). Initial missing-setting run 7 passed / 1 failed / 0 skipped: [results](evidence/t04/runbook-shipped-results.txt) | T12 per-command/per-shape shipped assertions and fixture configuration; success here is not complete downstream evidence |
| [History interpretation](operations-runbook.md#cache-ahead-containment); T04 provider behavior reuse | [Binding history](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-11/14 support | `Given_CdcDownstreamPublicationHistoryProvider`, 27 exact cases in artifact; real provider/evaluator with fake lifecycle store, no database/broker | 27 passed / 0 failed / 0 skipped; [case results](evidence/t04/downstream-history-results.txt) | T12 production command composition across evidence shapes; T14/T15 replay |

<a id="t01-foundation-review"></a>
## T01 foundation review

Reviewed 2026-09-06 against source revision
`b1f57aae6aece5e77084f687aa148100f1035c7b` plus T01 documentation changes. The pre-existing
story edit was used as input and left outside this task's commit. Tool: .NET SDK
`10.0.102`, Linux/Bash. No provider, broker, or qualified image was started or exercised.
No deployment resources required cleanup. Raw stdout/stderr and TRX were captured before
sanitization under `/tmp/dms-1326-t01`; that temporary workspace is not durable evidence.
Reviewed help retains all text with its extra terminal blank line removed for repository
whitespace checks; test-case extraction omits host/user paths and run IDs.
The committed artifacts above retain the evidence needed for this task.

**Starting directory:** repository root. **Prerequisites:** repository .NET SDK and
restore access. **Target/generation:** none; help and isolated option tests do not invoke
deployment operations. **Effect:** local build/help/test only.

Commands executed:

```bash
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- --help
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc --help
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --filter FullyQualifiedName~CdcControlOptions --logger 'trx;LogFileName=cdc-control-options.trx' --results-directory /tmp/dms-1326-t01
```

**Outcome:** both help commands exited `0`, emitted text, and had empty stderr; no JSON
contract applies. The test command exited `0`, with 77 passed and no failures/skips.
**Verification:** manually compared command groups/options with the CLI reference and
`DocumentCacheAdminCommandSurface`; compared configuration with `CdcControlOptions`, its
validator, `DocumentCacheAdminConfiguration`, the command surface overrides, dispatcher,
provider-input factory, renderer/shared rules/input validator, lag reader, state store,
and Compose enable/resolver/mount code. Manually followed the new local links/anchors
and checked procedure conventions against the story's Runnable Examples and Setup Handoffs.
**Interruption/retry:** these commands mutate no deployment state and may be rerun from
the same directory. They do not validate operator credentials or live readiness.

Material findings and corrections:

- CLI configuration directly loads JSON and environment providers, not user secrets or
  remote secret providers. Corrected that claim in the CLI README and catalog; documented
  the actual precedence and the distinction between request-only and override flags.
- Separate Java connector and librdkafka admin credentials; database principals are
  required on every verb, while typed Kafka principals are ACL-conditional. Consumer
  principal/group uniqueness applies even with ACLs disabled.
- SQL Server poll interval has no renderer default and must be no greater than the
  heartbeat interval; generic option validation alone is insufficient. Recorded the
  default heartbeat, producer buffer minimum, timeout units, and metrics-URI derivation.
- Wrapper mount precedence differs from direct CLI configuration. Documented the host
  versus `/state` handoff and the default root's dependence on the starting directory.
- Marked status containment effects, external write-admission authority, single-controller
  state storage, and the boundary between initial admission and eventual observation.
- Operational JSON examples are not introduced by T01. T03 captures existing status
  fixtures before sanitizing; missing assertions and live replays remain pending below.
  The E18 runbook's packaged-history explanation and old discovery links are reconciled
  by T04/T09, not treated as verified CDC recovery instructions here.

<a id="t03-monitoring-review"></a>
## T03 monitoring review

Reviewed 2026-09-06 against source revision
`e3bef5730c0296ced3e1aaf2a4f1905685847e4d` plus T03 documentation changes. Toolchain:
.NET SDK `10.0.102`, Linux/Bash. Provider tokens in fixtures are `postgresql` and
`sqlServer`; all dependencies are fake/in-memory. No database, broker, Connect image,
DMS endpoint, or operator deployment was accessed. No deployed resources require cleanup.
Provider/image versions are therefore not applicable; live observations remain T14/T15.

**Starting directory/prerequisites:** repository root; restored SDK/test dependencies.
**Target/generation:** none for help/tests; capture uses existing fixture identifiers,
default tenant, data store `1`, deployment `dms`, instance `binding`, generation `7`.
**Effects:** build/help/tests and local fixture serialization only.

Executed commands (each exited `0`):

```bash
dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- status --help
dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc status --help
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration --filter Category=CdcJsonContract --logger 'trx;LogFileName=cdc-json.trx' --results-directory /tmp/dms-1326-t03
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --filter 'FullyQualifiedName~CdcConnectorLagObservation|FullyQualifiedName~CdcSetupControllerStatus|FullyQualifiedName~CdcSetupControllerRestart' --logger 'trx;LogFileName=cdc-status-lag.trx' --results-directory /tmp/dms-1326-t03
dotnet run --project /tmp/dms-1326-t03/capture/capture.csproj --no-restore
```

**Results:** help produced text with empty stderr; no JSON applies. CLI tests: 7 passed,
0 failed, 0 skipped. Controller/lag tests: 104 passed, 0 failed, 0 skipped. These commands
built their dependencies successfully. The filter also selects status-endpoint preflight
cases; Jolokia reader behavior is covered within `Given_CdcConnectorLagObservationMapping`. Exact selected class and
parameterized test identities are retained in [CLI results](evidence/t03/cdc-json-results.txt)
and [controller/lag results](evidence/t03/cdc-status-lag-results.txt). No tests inspect
documentation, extract snippets, or validate links; no documentation assertions were run.

**Capture provenance:** a temporary console project referenced the existing CDC control
unit-test and CLI integration-test assemblies. It invoked
`Given_CdcSetupControllerStatus.EnabledBinding` / `CdcSetupControllerHarness.StatusAsync`
with the same scenario inputs as the selected tests below, then used the shipped
`DocumentCacheAdminExitCodeMapper.ForStatus` and the existing
`Given_DocumentCacheAdminCdcJsonContracts.ContractResult<CdcStatus>` / `ExecuteAsync`
fixture helpers to capture one real CLI serialization and exit/stderr tuple per scenario.
It did not invoke the production dispatcher against live services. This supplements the
seven existing CLI tests, whose status fixtures have empty target arrays, with full
controller-produced target components; it is not a new integration assertion or provider
exercise. T12 owns additional product assertions.

Lag observation captures invoke the existing
`Given_CdcConnectorLagObservationMapping.Map` helper and `CdcJsonContract.Serialize`.
They are internal serialized observations with no CLI exit code. The successful input is
`(1000, 400, 900, 1500)` milliseconds; absent inputs use the fixture's `summary` with no
reading. [Capture outcomes](evidence/t03/capture-results.txt) distinguish these layers.
The temporary console project is a capture aid, not a documentation catalog or committed
verification tool. It initially needed its assembly lookup corrected to the CLI's actual
`dms-document-cache` assembly name; the completed capture command above exited `0`.

| Artifact | Existing controller/mapping scenario reused | Observed outcome |
| --- | --- | --- |
| [ready.json](evidence/t03/ready.json) | `Given_CdcSetupControllerStatus.It_reports_the_target_ready_when_every_observation_is_satisfied` | `ready` / CLI exit `0` |
| [binding-missing.json](evidence/t03/binding-missing.json) | `Given_CdcSetupControllerStatus.It_reports_the_binding_missing_without_observing_any_governed_artifact` | `notReady`, `bindingMissing` / `0` |
| [lag-unavailable.json](evidence/t03/lag-unavailable.json) | `Given_CdcSetupControllerStatus.It_reports_the_target_not_ready_when_the_connector_lag_is_unknown` | `unknown`, `statusObservationUnavailable` / `0` |
| [offset-store-invalid.json](evidence/t03/offset-store-invalid.json) | `Given_CdcSetupControllerStatus.It_reports_the_target_not_ready_when_the_shared_connect_offset_store_does_not_conform` | `notReady`, `connectOffsetStoreInvalid` / `0` |
| [lost-fence-refused.json](evidence/t03/lost-fence-refused.json) | `Given_CdcSetupControllerStatus.It_reports_a_latched_loss_whose_connector_fence_the_worker_refused` | SQL Server missing schema history; durable loss, fence conflict diagnostic / `0` |
| [lost-fence-retried.json](evidence/t03/lost-fence-retried.json) | Same harness, next poll with stop accepted; follows `Given_CdcSetupControllerStatus.It_fences_again_on_a_later_poll_when_the_first_stop_was_refused` (capture first refusal is `Conflict`; test uses `Unavailable`) | Loss retained, no fence-failure diagnostic / `0`; fake runtime remains pre-stop `RUNNING`, not stopped-state read-back evidence |
| [lost-latch-unavailable.json](evidence/t03/lost-latch-unavailable.json) | `Given_CdcSetupControllerStatus.It_reports_a_source_history_latch_that_did_not_become_durable` | Loss, latch not durable, binding unavailable / `0`; stop attempted |
| [lag-observation-Succeeded.json](evidence/t03/lag-observation-Succeeded.json) | `Given_CdcConnectorLagObservationMapping.It_reports_lag_within_the_threshold_with_every_quantile_populated` | `withinThreshold`, all five numbers; serializer only |
| [lag-observation-Unavailable.json](evidence/t03/lag-observation-Unavailable.json), [MetricsAbsent](evidence/t03/lag-observation-MetricsAbsent.json), [MalformedResponse](evidence/t03/lag-observation-MalformedResponse.json) | `Given_CdcConnectorLagObservationMapping.It_reports_absent_evidence_as_unknown_with_null_values(Unavailable,"connectorLagUnavailable")`, `(MetricsAbsent,"connectorLagMetricsAbsent")`, `(MalformedResponse,"connectorLagMalformedResponse")`; exact runner display names in results | `unknown`, five null numeric fields, distinct diagnostics; serializer only |

Raw stdout/stderr, help, TRX, and the capture aid were written first under
`/tmp/dms-1326-t03`. Manual sanitization review found the emitted JSON contains only
synthetic fixture identities, opaque fingerprints, timestamps, and bounded diagnostics;
all JSON fields/values are retained verbatim, including nulls and enum casing. Each CLI
capture's stderr was empty. Help's extra terminal blank line was removed. TRX summaries
retain counters/class/case names while omitting local host paths and run IDs. The temporary
workspace is not durable; only reviewed artifacts under `evidence/t03` are committed.

**Manual review:** followed the new local links/anchors from monitoring, projection/CDC
observation, incident routing, configuration/binding/continuity triage, lag, telemetry, and
this index, including E18 procedures and design ownership. Compared command options with
both generated help captures and the command surface; checked the executor, serializer,
exit mapper, CDC contracts/evaluators, controller status/fence implementation, Jolokia
reader/mapper, options/catalog, projection status contract, telemetry instruments/units/
labels, and diagnostic limits. Rechecked each captured JSON outcome and component field
against the prose. `git diff --check` is the whitespace feedback loop, not a documentation
content test.

Material findings/corrections:

- CDC stdout is the shared contract itself: `readiness` is the outcome, with no outer
  `outcome` property. Successfully produced `notReady` and `unknown` both exit zero.
- The CLI's projection process view differs from the deployed projector's endpoint view;
  CDC correlation explicitly reads the latter. Projection process `targetGeneration`
  must not be mistaken for the binding generation; CDC `dataStoreId` is a JSON string.
- Status latches/fences loss despite an old comment calling a status read non-mutating.
  Latching, fence acceptance, and stopped-state read-back are separate evidence. Status
  does not refresh its runtime component after stop acceptance. Captured retry output
  retains the earlier runtime verdict; no end-to-end containment success is inferred.
- A failed latch still attempts a fence; `statusSourceHistoryLatchNotDurable` and
  `statusIncidentFenceNotApplied` require separate responses. Tests also cover later-poll
  retry, provider-unavailable fencing, and permanent loss despite recreated artifacts.
- Lag numbers are internal observation fields, not embedded in CDC status. Unavailable
  lag clears all five values. Ambiguous MBeans and negative bridge sentinels map to
  `MetricsAbsent`; malformed response shape maps to `MalformedResponse`. Negative values
  supplied directly to the mapper use `connectorLagUnusableReading`.
- Oldest-work age is a sampled histogram, not a continuous gauge or an exact backlog
  count. Status timing uses seconds; projector dispatch duration uses milliseconds.
  `CdcTelemetryLabels` alone is not an emitted CDC lag instrument.
- Pending procedure links route to delivered triage/design owners rather than nonexistent
  T02/T05/T06/T07/T08 anchors. E18's packaged-history wording still requires T04; its
  internal-only mutation paths are not authorized merely by stopping CDC.

**Interruption/retry and remaining work:** these local help/test/capture operations may
be repeated with no deployment changes. No new product assertions were added. T12 owns
missing shipped-composition/status assertions; T14/T15 capture real DMS endpoint, provider,
broker, lag, and fence read-back; T16 reconciles their results and closes pending rows.

<a id="t04-projection-handoff-review"></a>
## T04 projection handoff review

Reviewed 2026-09-06 against source revision
`14fcb921e8d41aa19956f6da05b3232e1b35dda2` plus T04 documentation changes. The pre-existing
story edit remains outside this task's commit. Tools: .NET SDK `10.0.102`, VSTest `18.0.1`,
Linux/Bash. Existing local servers: PostgreSQL `16.8-alpine`, image
`sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0`, host port `5435`;
SQL Server `2025-latest`, local image ID
`sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a`, host port `14333`.
No qualified Connect image, broker, consumer, or API-driven CDC exercise was used.

**Starting directory/prerequisites:** repository root, restored SDK dependencies, reachable
provider admin connections supplied as `ConnectionStrings__DatabaseConnection` and
`ConnectionStrings__MssqlAdmin` through the test-process environment. Local credentials
were resolved from the running test containers without printing or committing them.
PostgreSQL uses the admin database `postgres`; fixtures create generated-DDL targets.
**Target/generation:** disposable fixture-owned targets, not the deployed DMS databases.
The shipped CDC status fixture uses deployment `local`, instance `ds1`, generation `1`,
and its temporary empty state root. **Effects:** local builds/help and real-provider
fixture mutations, including isolated scrub/cache-ahead and SQL Server prerequisite
correction; CMS is a fixture server. Existing containers were not torn down or replaced.

Commands executed (environment connection values are named secret inputs, not literals):

```bash
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration --filter 'Category=Runbook|Category=CdcShippedComposition' --logger 'trx;LogFileName=runbook-shipped.trx' --results-directory /tmp/dms-1326-t04
DataManagement__DocumentCache__Cdc__ConnectorDatabasePrincipal=dms_connector dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration --filter 'Category=Runbook|Category=CdcShippedComposition' --logger 'trx;LogFileName=runbook-shipped-configured.trx' --results-directory /tmp/dms-1326-t04
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --filter FullyQualifiedName~CdcDownstreamPublicationHistory --logger 'trx;LogFileName=downstream-history.trx' --results-directory /tmp/dms-1326-t04
dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- activate-offline --help
dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- deactivate-offline --help
dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- recover-cache-ahead --help
```

**Outcomes and correction:** the initial integration command exited `1`: 7 passed,
1 failed, no skips. `It_reports_a_cdc_status_contract_for_a_target_with_no_binding`
received CLI exit `1`, empty stdout, and stderr
`Unexpected DocumentCache administration CLI failure: ConnectorDatabasePrincipal must be supplied.`
The shared harness writes the old `ConnectorPrincipal` key, but current options require
`ConnectorDatabasePrincipal`. Supplying that actual prerequisite through environment
configuration fixed the invocation: the same complete selection exited `0`, all 8 passed,
none failed/skipped. No production behavior or test assertion was changed. T12 owns
making this existing fixture supply the current setting itself when extending its
shipped-composition coverage; retain the explicit environment input for replay meanwhile.
History unit tests exited `0`, 27 passed with no failures/skips. Both test projects built
successfully. All three help invocations exited `0`; their text is captured in
[activation help](evidence/t04/activate-offline-help.txt),
[deactivation help](evidence/t04/deactivate-offline-help.txt), and
[recovery help](evidence/t04/recover-cache-ahead-help.txt). Help emits no operational JSON.

**Evidence limits:** the two real-provider `It_routes_cache_ahead_status_to_internal_recovery_and_preserves_evidence_when_history_is_unknown`
cases run the shipped CLI, assert `command=internalOnlyCacheAheadRecovery`,
`status=rejectedNoMutation`, `classification=downstreamHistoryPresentOrUnknown`,
`mutated=false`, lifecycle `tracking`, latch still true, exit `10`, and unchanged
lifecycle/cache/work state. No deployment key is supplied for those cases, so they prove
unknown-history rejection, not active/historical-record command composition. The two
`It_routes_suspected_direct_mutation_to_scrub_and_reports_processing_backlog` cases cover
admitted scrub and subsequent status. SQL Server also runs
`It_corrects_disabled_lifecycle_prerequisite_status_before_activation`: rejected activation
with `providerPrerequisiteFailed` / `10`, followed by corrected new-empty activation
`completed` / `succeeded` / `0` / `tracking`.

The shipped-composition fixture has three cases, including one diagnostic-only case.
Its no-binding status checks are intentionally limited: they do not yet assert every
readiness/category/exit field. Provider history tests cover matching source `active`,
earlier source and retirement `historical`, missing/unavailable/unreadable/unrelated
state `unknown`, default-tenant translation, unresolved source, and evaluator rejection.
Those use a fake lifecycle service, not live persisted binding/retirement command flows.
T12 still owns per-command/per-provider assertions for active, historical, unknown,
missing, and mismatched history. No test-only `internalOnly` success is claimed as a
production recovery option. No new operational JSON literal/example is introduced by
T04; field descriptions above come from the existing assertions and reviewed contracts.

**Manual review and findings:** compared the shipped `CdcControlServiceCollectionExtensions`
registration, `CdcDownstreamPublicationHistoryProvider`, Core history proof evaluator and
preflight classifier, mutating dispatcher, administrative work-clearance guard, exit mapper,
CLI help/reference, and runbook fixtures. Followed the new E18/CDC links and anchors, their
design owners, CLI `#examples`, evidence artifacts, and E18-S08 work package. No automated
documentation tests, snippet extraction, executable catalog, or link tests were used.

- Replaced the stale unknown-only default-provider explanation and conditional production
  recovery recipes. Positive history still rejects; stopping/removing/retiring cannot
  establish internal-only authority. Earlier guards can reject with other classifications.
- Corrected E18's activation explanation: new-empty activation transitions directly to
  `Tracking`; the offline reset/baseline path cannot pass the packaged history gate.
  Kept projection queue, poison, enqueue, lifecycle, bounded rebuild, and scrub procedures
  in E18, with CDC routing and no published-state or replacement-baseline promise.
- Reused E18 matrix rows `CDC-INV-04/14/15` by source review: both providers' projector
  `It_drains_long_outage_backlog_in_bounded_pages_and_restarts_from_durable_work`, query-plan
  `It_uses_the_oldest_work_index_for_ordinary_queue_paging_without_source_or_cache_scans`,
  rebuild `It_resumes_rebuilding_without_repeating_cache_clearing`, administrative mutex
  `It_serializes_retryable_incomplete_rebuild_status_and_reissue_resumes_the_workflow`, and
  scrub `It_rejects_before_scan_when_lifecycle_or_latch_is_not_admitted` (Disabled/Resetting/
  Rebuilding with clear latch, Tracking with set latch). These supporting suites were
  inspected, not rerun or copied into another matrix.
- Retained SQL Server target/activation validation and `Disabled`-only initialization
  correction/restart. Reviewed the E18 prerequisite validator's initialization retry,
  activation reread, and active-target unsupported classification. The executed CLI
  correction case does not establish renewed readiness for post-validation changes in
  `Tracking`, `Resetting`, or `Rebuilding`.
- The existing E18 restamp boundary links only the owning design/work package. No utility,
  dedicated operator procedure, or restamp evidence exists in this checkout. Recorded the
  E18-S08 handoff as unmet, with T16 reconciliation; do not invent commands or claim reused
  restamp results. The design still distinguishes clear-latch `Tracking` publication from
  `Disabled` canonical-only correction, requires external offline fencing and fresh
  versions for changed bytes, and excludes purge-required incidents from same-topic repair.
  T07 owns the detailed disclosure containment procedure; re-enablement remains deferred.
- Corrected the inherited E18 claim that the CLI directly loads user-secrets/remote providers
  to match T01's verified JSON/environment configuration boundary.

**Capture and cleanup:** raw process logs, help, and TRX were written under
`/tmp/dms-1326-t04` before sanitization. Committed summaries retain every selected class/case,
counter, outcome, and the initial failure message, omitting host paths/run IDs/stack traces.
Help retains text with only trailing blank lines removed. The fixtures own disposable
databases/leases, CMS hosts, and temporary binding roots and dispose them; shared generated
baselines and pre-existing database containers remain available. No operator binding,
connector, topic, or DMS E2E stack was mutated. These help/test commands can be repeated
with the same configured prerequisites. `git diff --check` is the whitespace check.
No T04 blocker remains; T12, T14/T15, E18-S08, and T16 pending evidence is explicit above.

<a id="pending-delivery"></a>
## Pending delivery and verification

These are explicit pending results, not runnable anchors or claims of coverage. Each
authoring task adds its actual procedure anchors and evidence rows when delivered.
Authoring can finish against existing help/source/available fixture output; new assertions
and live replays stay assigned to the downstream owners. T16 closes these entries using
actual results before the story is complete.

| Pending scope | Authoring / evidence owner | Result and required handoff |
| --- | --- | --- |
| Fresh PostgreSQL/SQL Server setup, API upsert/delete, planned stop/guarded restart | T02; T14/T15 live replay | Pending. Record qualified image, exact wrapper arguments, persistent mount, and E19-06 harness identities; an absent upstream harness is an unmet dependency. |
| Monitoring/incident routing and status/lag JSON | T03 delivered; T12 assertions; T14/T15 live observations | Authoring and existing fixture capture reviewed in [T03](#t03-monitoring-review). Deployed endpoint/provider/metrics/fence read-back remains pending; fixture success does not close live evidence. |
| E18 packaged downstream-history handoff and repair scope | T04 delivered; T12 additional assertions; E18-S08 restamp; T16 reconciliation | [T04 review](#t04-projection-handoff-review): 8 configured integration and 27 history unit cases passed; full per-command/per-provider history-shape composition remains T12. Dedicated restamp utility/procedure/evidence is absent; E18-S08 handoff unmet. |
| Continuity, complete-record adoption, physical-source replacement | T05; T13 assertions; T14/T15 replay | Pending. Record exact case identities, default-tenant translation, outcome/exit code, preserved generation, and retry evidence. |
| Destructive retirement, partial failure, timeout and same-operation retry | T06; T17 assertions; T14/T15 replay | Pending. Distinguish successful cleanup proof from incomplete/refused cleanup; preserve shared state and retirement records. |
| Security, consumer isolation, sensitive-data containment | T07; T14/T15 replay | Pending. Link authorizer-backed evidence separately from local ACL-disabled results; component cleanup cannot prove platform purge. |
| Provider retention, consumer continuity, record budget, capacity observations | T08; T14/T15 replay | Pending. Link existing owning evidence; small exercises do not establish production capacity. |
| Discovery references and final consistency | T09; T16 closure | Pending. Final manual review reconciles all anchors, exact test selections, provider results, artifacts, and limitations. |

Setup, provider artifacts, routing, durability, ACLs, and consumer conformance remain in
their sibling stories/suites. Start with the
[CDC contract-to-evidence ownership table](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability)
and the [E18 evidence matrix](../document-cache-documentation/cdc-inv-evidence.md);
later rows link exact reusable test identities rather than copying those implementations.
