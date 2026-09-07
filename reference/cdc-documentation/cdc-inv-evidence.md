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
| [Retirement](operations-runbook.md#retire-binding-generation); T17 | [Binding lifecycle](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15, supporting CDC-INV-11 | `Given_CdcControlRetirementOperator`, `Given_DocumentCacheAdminRetirementSourceSelection`, selected `Given_DocumentCacheAdminCdcJsonContracts`; separate live broker, packaged two-provider CLI, and mocked-dispatch layers | 16 new integration cases passed; 187 reused unit cases passed; [exact results, captures and manual review](evidence/t17/README.md) | T14/T15 runbook replay; T16 final reconciliation; platform purge remains deployment-owned |
| [Prerequisites](operations-runbook.md#prerequisites), [state](operations-runbook.md#deployment-state), [format](operations-runbook.md#procedure-format); T01 foundation | [Configuration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection), [binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 authoring support only | Manual review `T01-foundation`; no provider access | Reviewed; [record below](#t01-foundation-review), [root help](evidence/t01/help.txt), [CDC help](evidence/t01/cdc-help.txt) | Operational replay pending T14/T15; final reconciliation T16 |
| [Prerequisites/configuration](operations-runbook.md#prerequisites); T01 validation support | [Configuration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection); CDC-INV-15 support, not exercised-runbook closure | `EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit.Given_CdcControlOptionsTests`; all 77 exact selected case identities in artifact; provider-independent unit behavior | 77 passed, 0 failed, 0 skipped; [case results](evidence/t01/options-test-results.txt) | Provider claims still pending T14/T15 |
| [Monitoring](operations-runbook.md#monitoring), [routing](operations-runbook.md#incident-routing), [lag](operations-runbook.md#inspect-lag), [telemetry](operations-runbook.md#telemetry); T03 | [Status](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness), [telemetry](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations); CDC-INV-15 authoring support | Manual review `T03-monitoring`; provider-independent source/help/serialized fixture comparison | Reviewed; [record](#t03-monitoring-review), [projection help](evidence/t03/status-help.txt), [CDC status help](evidence/t03/cdc-status-help.txt) | [T12 assertions delivered](#t12-operator-path-review); T14/T15 deployed observations; T16 closure |
| [Observe CDC](operations-runbook.md#observe-cdc); T03 CLI support | [Status](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness); CDC-INV-15 | `Given_DocumentCacheAdminCdcJsonContracts`; mocked-dispatch CLI integration, all 7 exact cases in artifact | 7 passed / 0 failed / 0 skipped; [results](evidence/t03/cdc-json-results.txt), [capture provenance](#t03-monitoring-review) | [T12 assertions delivered](#t12-operator-path-review); no live provider evidence |
| [CDC containment](operations-runbook.md#observe-cdc), [continuity triage](operations-runbook.md#route-continuity-incident), [lag](operations-runbook.md#inspect-lag); T03 owning behavior reuse | [Continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity), [barrier](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#provider-source-position-barrier); CDC-INV-10/11/15 supporting evidence | `Given_CdcSetupControllerStatus`, `Given_CdcSetupControllerRestart`, `Given_CdcConnectorLagObservationMapping`, `Given_CdcSetupControllerStatusEndpointPreflight`; fake provider/Connect and HTTP unit fixtures | 104 passed / 0 failed / 0 skipped; [exact cases](evidence/t03/cdc-status-lag-results.txt), [JSON artifact mapping](#t03-monitoring-review) | T14/T15 live read-back/replay; T16 closure |
| [Projection handoff](operations-runbook.md#projection-repair-handoff), [cache-ahead](operations-runbook.md#cache-ahead-containment), [representation correction](operations-runbook.md#representation-correction-handoff); T04 | [Repair](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations), [projection administration](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration); CDC-INV-14/15 authoring support | Manual review `T04-projection-handoff`; source/help, existing E18 evidence and CLI fixture assertions | Reviewed; [record](#t04-projection-handoff-review), [E18 matrix](../document-cache-documentation/cdc-inv-evidence.md) | [T12 assertions delivered](#t12-operator-path-review); T14/T15 downstream replay; T16 closure; E18-S08 restamp handoff unmet |
| [Packaged rejection](operations-runbook.md#cache-ahead-containment); T04 existing provider evidence | [History gate](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration); CDC-INV-14/15 | `Given_DocumentCacheAdminPostgresqlRunbookWorkflows`, `Given_DocumentCacheAdminMssqlRunbookWorkflows`, `Given_DocumentCacheAdminCdcShippedComposition`; real PostgreSQL/SQL Server plus subprocess CLI and fake CMS; no broker/Connect | Configured run 8 passed / 0 failed / 0 skipped; [exact cases](evidence/t04/runbook-shipped-configured-results.txt). Initial missing-setting run 7 passed / 1 failed / 0 skipped: [results](evidence/t04/runbook-shipped-results.txt) | [T12 matrix/configuration delivered](#t12-operator-path-review); live replay T14/T15 |
| [History interpretation](operations-runbook.md#cache-ahead-containment); T04 provider behavior reuse | [Binding history](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-11/14 support | `Given_CdcDownstreamPublicationHistoryProvider`, 27 exact cases in artifact; real provider/evaluator with fake lifecycle store, no database/broker | 27 passed / 0 failed / 0 skipped; [case results](evidence/t04/downstream-history-results.txt) | [T12 command matrix delivered](#t12-operator-path-review); T14/T15 replay |
| [Packaged rejection](operations-runbook.md#cache-ahead-containment); T12 | [History gate](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration); CDC-INV-14/15 | `Given_DocumentCacheAdminPackagedHistoryRejections(provider,command,evidence)`; 36 real-provider CLI tuples, synthetic durable state, four assertions each | 144 passed; [matrix](evidence/t12/operator-matrix.txt), [exact identities](evidence/t12/operator-path-results.txt), [review/captures](#t12-operator-path-review) | Live replay T14/T15; final reconciliation T16 |
| [Observe CDC](operations-runbook.md#observe-cdc); T12 | [Status owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness); CDC-INV-15 | `Given_DocumentCacheAdminCdcJsonContracts` (11 mocked-dispatch cases), `Given_DocumentCacheAdminCdcShippedComposition` (3 PostgreSQL composition/diagnostic cases); boundaries below | 14 passed; [results](evidence/t12/operator-path-results.txt), [status captures](evidence/t12/cdc-contract-captures.json) | No SQL Server CDC-status or live capture claim; T14/T15 |
| [History interpretation](operations-runbook.md#projection-repair-handoff); T12 reuse | [Binding/history owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 support | Existing CLI dispatcher/service-registration and CDC history/registration unit cases, including both provider registrations; five existing real-provider Runbook cases | 70 unit + 5 integration passed; [review and exact selections](#t12-operator-path-review) | E18 internal-only successes remain test-only; restamp E18-S08; live replay T14/T15 |
| [Provider setup/state](operations-runbook.md#local-setup), [API handoff](operations-runbook.md#local-api-smoke), [planned stop/restart](operations-runbook.md#local-stop-restart), [cleanup](operations-runbook.md#local-cleanup); T02 | [Bootstrap](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci), [binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 authoring support | Manual review `T02-setup`; source/generated help and existing T03/T12 JSON, no provider access | Reviewed; [findings, help, replay commands and dependency](#t02-setup-review) | E19-06 API harness unmet; T14/T15 live replay; T16 reconciliation |
| [Setup/state/stop](operations-runbook.md#local-setup-verification); T02 wrapper behavior | [Initial admission](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence), [continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity); CDC-INV-14/15 supporting evidence | Explicit Pester FullName selection from `BootstrapEnableKafkaCdc.Tests.ps1`; exact 131 case identities in artifact, temporary filesystem/mocked Docker and source-surface assertions | 131 passed / 0 failed / 0 skipped; [results](evidence/t02/bootstrap-results.txt), [invocation](evidence/t02/bootstrap-invocation.txt) | Not provider/image/broker qualification; T14/T15 live admission/fence/cleanup evidence |
| [Continuity](operations-runbook.md#continuity-incident), [adoption](operations-runbook.md#adopt-missing-binding), [replacement](operations-runbook.md#replace-physical-source); T05 | [Continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity), [binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 authoring support | Manual review `T05-continuity`; source, generated help, existing fixture capture | Reviewed; [record and capture mapping](#t05-continuity-review) | T13 assertions; T14/T15 live replay; T16 closure |
| [Adoption/replacement](operations-runbook.md#adopt-missing-binding); T05 behavior reuse | [Binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-11/14/15 supporting evidence | `Given_CdcSetupControllerAdoption`, `Given_CdcSetupControllerReplaceSource`, `Given_CdcSetupControllerInitialEnable`; fake provider/Connect/Kafka/lifecycle collaborators | 68 passed / 0 failed / 0 skipped; [44 cases](evidence/t05/controller-results.txt), [24 retry cases](evidence/t05/retry-results.txt) | Not live provider or broker evidence; T13/T14/T15 |
| [Command output](operations-runbook.md#replace-physical-source); T05 serializer support | [Readiness](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness), [binding](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 | `Given_DocumentCacheAdminCdcJsonContracts`; existing mocked-dispatch CLI cases | 11 passed / 0 failed / 0 skipped; [identities](evidence/t05/cli-json-results.txt), [six capture outcomes](evidence/t05/capture-results.txt) | Production dispatch/default-tenant additions T13; deployed replay T14/T15 |
| [Retirement](operations-runbook.md#retire-binding-generation); T06 | [Binding lifecycle](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 authoring support | Manual review `T06-retirement`; source/help and existing fixture output | Reviewed; [record and capture mapping](#t06-retirement-review), [retire help](evidence/t06/retire-help.txt) | T17 assertions; T14/T15 live replay; T16 closure |
| [Retirement cleanup/refusal/retry](operations-runbook.md#retire-binding-generation); T06 behavior reuse | [Binding lifecycle](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-11/14/15 supporting evidence | `Given_CdcProviderArtifactTeardown`, `Given_CdcKafkaTeardown`, `Given_CdcSetupControllerRetirement`; mocked provider/Connect/Kafka/lifecycle | 32 passed / 0 failed / 0 skipped; [20 requested-filter cases](evidence/t06/teardown-requested-results.txt), [12 controller cases](evidence/t06/retirement-controller-results.txt) | Provider timeout capture is manual; T17 new assertions; T14/T15 real cleanup |
| [Retirement CLI boundary](operations-runbook.md#retire-binding-generation); T06 | [Binding lifecycle](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-14/15 supporting evidence | `Given_DocumentCacheAdminCdcCommandDispatcher` and `Given_DocumentCacheAdminCdcJsonContracts`; mocked controller/dispatcher, actual CLI executor and serialization | 42 passed / 0 failed / 0 skipped; [31 dispatcher cases](evidence/t06/dispatcher-results.txt), [11 CLI cases](evidence/t06/cli-json-results.txt), [eight captures](evidence/t06/capture-results.txt) | Default-tenant subprocess/retirement matrix T17; live provider/broker replay T14/T15 |
| [Retained retirement evidence](operations-runbook.md#retire-binding-generation); T06 | [Binding lifecycle](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding); CDC-INV-11/14 supporting evidence | Four exact `Given_LocalCdcStateStore` cases; real temporary filesystem with injected delete failures | 4 passed / 0 failed / 0 skipped; [private-umask case identities](evidence/t06/retirement-store-private-results.txt); initial default-umask run 2 passed / 2 failed, [results](evidence/t06/retirement-store-results.txt) | Not live controller-to-provider retirement; T17/T14/T15 |
| [Security](operations-runbook.md#cdc-security), [inspection](operations-runbook.md#inspect-cdc-security), [containment](operations-runbook.md#sensitive-data-containment); T07 | [Security](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations), [disclosure](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction); CDC-INV-14/15 authoring, CDC-INV-06/12 support | Manual review `T07-security`; existing provider/broker assertions reviewed, not executed; fixture-based absent-purge decision | Reviewed; [record and exact reusable identities](#t07-security-review); incident remains open without platform evidence | T14/T15 live replay; deployment platform/consumer-store evidence; T16 closure |
| [Credentials and ACL inspection](operations-runbook.md#inspect-cdc-security); T07 | [Security](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations); CDC-INV-12/15 support | `Given_CdcControlOptionsTests`, `Given_CdcKafkaAclPolicy`, `Given_CdcConnectRestSecretRedaction`; options/fake broker/HTTP unit tests | 115 passed / 0 failed / 0 skipped; [exact cases](evidence/t07/security-results.txt) | Live isolation T14/T15; no production qualification |
| [Generated security artifacts](operations-runbook.md#cdc-security); T07 | [Provider setup](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup); CDC-INV-06/08/15 support | `Given_CdcConnectorTemplateArtifactTests`, `Given_CdcConnectorTemplatePostgresqlRendering`, `Given_CdcConnectorTemplateSqlServerRendering`; renderer unit fixtures | 35 passed / 0 failed / 0 skipped; [exact cases](evidence/t07/templates-results.txt) | Live manifests/grants T14/T15; T16 reconciliation |
| [Retention/capacity](operations-runbook.md#retention-and-capacity), [consumer continuity](operations-runbook.md#consumer-continuity), [record increase](operations-runbook.md#increase-record-size), [overhead](operations-runbook.md#pipeline-overhead); T08 | [Operations](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations), [topic/record contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md); CDC-INV-14/15 authoring, CDC-INV-07/11 support | Manual review `T08-capacity`; source and sibling assertions, native metadata/tool reference review | Reviewed; [findings and pending observations](#t08-capacity-review) | T14/T15 live inspection; independent consumer/platform qualification; T16 closure |
| [Policy and size inspection](operations-runbook.md#increase-record-size); T08 behavior reuse | [Record size](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md#record-size), [offset store](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#kafka-connect-offset-store); CDC-INV-07/11/15 support | `Given_CdcKafkaRecordSize`, `Given_CdcKafkaTopicPolicy`, `Given_CdcKafkaOffsetStore`, `Given_CdcKafkaSchemaHistory`; fake Kafka admin/record readers | 82 passed / 0 failed / 0 skipped; [exact identities](evidence/t08/policy-results.txt) | No live provider, broker or consumer capacity claim; T14/T15/T16 |
| [CDC operator entry point](README.md), setup/discovery links; T09 | [Documentation disposition](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#documentation-audit-and-disposition); CDC-INV-14/15 authoring support | Manual review `T09-discovery`; shipped wrappers, CI lane configuration, source, existing help and evidence | Reviewed; [audit and corrections](#t09-discovery-review); no new test execution or live result | T13/T17 assertions; T14/T15 replay; T16 final reconciliation |

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

<a id="t12-operator-path-review"></a>
## T12 operator-path assertions and review

Reviewed 2026-09-06 at `03510aa5e5042930665ebbbfd72cf3f05b15456f` plus T12 test/evidence
changes. .NET SDK `10.0.102`, VSTest `18.0.1`, Linux/Bash; the PostgreSQL 16.8 and SQL
Server 2025 instances, image IDs, and host ports are unchanged from the
[T04 environment record](#t04-projection-handoff-review). Existing containers were retained;
fixtures disposed their isolated databases, CMS endpoints, and temporary state roots.
No production commands, authoritative inputs, or documentation-reading tests changed.

**Selection and boundaries.** From the repository root, supply
`ConnectionStrings__DatabaseConnection` and `ConnectionStrings__MssqlAdmin` through the
test-process environment. Credentials were resolved from the existing local test
containers without printing or committing them. No CDC configuration workaround from
T04 is required: the harness now supplies `ConnectorDatabasePrincipal`.

```bash
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration --list-tests --filter 'Category=Runbook|Category=CdcJsonContract|Category=CdcShippedComposition' -- NUnit.DisplayName=FullName
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration --no-build --filter 'Category=Runbook|Category=CdcJsonContract|Category=CdcShippedComposition' --logger 'trx;LogFileName=operator-paths.trx' --results-directory /tmp/dms-1326-t12
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit --filter 'FullyQualifiedName~Given_DocumentCacheAdminCdcCommandDispatcher|FullyQualifiedName~Given_DocumentCacheAdminServiceRegistration' --logger 'trx;LogFileName=cli-mapping-registration.trx' --results-directory /tmp/dms-1326-t12
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --no-build --filter 'FullyQualifiedName~Given_CdcDownstreamPublicationHistoryProvider|FullyQualifiedName~It_supplies_the_downstream_publication_history_provider_ahead_of_the_document_cache_default' --logger 'trx;LogFileName=history-registration.trx' --results-directory /tmp/dms-1326-t12
```

The discovery command builds the changed integration project. The adapter prints tests
outside the filter during discovery; `NUnit.DisplayName=FullName` distinguishes parameterized
fixtures. Before execution, the requested category union was mapped to the 163 identifiers
in [selection mapping](evidence/t12/selection-mapping.txt). The final TRX was checked against
those identities, including all 36 provider/command/evidence tuples, rather than relying
on category totals. Final results: **163 integration + 41 CLI unit + 29 history/registration
unit tests passed; no failures or skips**. See [integration results](evidence/t12/operator-path-results.txt),
[CLI mapping/registration](evidence/t12/cli-mapping-registration-results.txt), and
[history/registration](evidence/t12/history-registration-results.txt).

`Given_DocumentCacheAdminPackagedHistoryRejections(provider,command,evidence)` runs four
assertions per tuple: JSON rejection/target/source, lifecycle/latch/database preservation,
durable-record preservation, and output-stream/secret separation. Providers are
`postgresql` and `mssql`; commands are `activate-offline`, `deactivate-offline`, and
`recover-cache-ahead`. The [36-cell result matrix](evidence/t12/operator-matrix.txt) includes
the observed history status and actual CLI exit/classification for every combination.

| Fixture input | Shipped history observation | Boundary proved |
| --- | --- | --- |
| `active` | `active` | Matching target/current-source binding |
| `historical` | `historical` | Retained retirement, live binding removed by the existing lifecycle service using a synthetic cleanup proof |
| `unknown` | `unknown` | Unreadable binding record; no internal-only inference |
| `missing` | `unknown` | Empty isolated state root with deployment key configured |
| `mismatchedTarget` | `unknown` | Binding names another tenant with the same data-store ID |
| `mismatchedSource` | `historical` | Binding names the requested target under a different physical source |

These are **shipped CLI/history composition with real database execution and synthetic
state inputs**. The fixture calls the existing artifact generator and lifecycle service;
it does not implement a second store, run live capture, or establish a real cleanup proof.
A separate observation through the shipped history provider asserts the input's history
classification; that classification is not an additional CLI JSON field. All commands use
the current resolved fingerprint so they reach the history gate. Earlier expected-source
mismatch guards remain separately owned. Every tuple returns exit `10`,
`rejectedNoMutation`, `downstreamHistoryPresentOrUnknown`, `mutated=false`, and a
`phaseDiagnostics` entry with `diagnosticCategory=downstreamPublicationHistoryPresentOrUnknown`.
Before/after checks include lifecycle, cache-ahead latch, cache JSON/content versions,
work versions/oldest timestamp, counts, physical source, and durable binding/retirement bytes.
No `internalOnly` success is implied.

The existing `Given_DocumentCacheAdminCdcShippedComposition` remains **PostgreSQL-only**.
Its missing-binding path resolves the target but returns before opening the instance
connection, Kafka describes, or Connect requests; it cannot prove SQL Server CDC composition
or live capture. Client construction can still start a background Kafka bootstrap connection
and write diagnostics to stderr. Assertions now require exit `0`, `notReady`, the default-tenant
binding identity, `binding.state=notSatisfied`, `binding.category=bindingMissing`, one stdout
contract, and secret-free stream separation. Its private temporary root is created with
owner-only Unix permissions so an inherited permissive umask cannot change the tested path
to `statusObservationUnavailable`. See [captured status](evidence/t12/cdc-contract-captures.json).

`Given_DocumentCacheAdminCdcJsonContracts` adds `status` unknown/exit-zero and successful
`stop` notReady / `restart` notReady-or-unknown round trips through the real parser,
executor, and serializer with a **substituted dispatcher**. Its minimal status fixtures
have no target observations and prove transport/exit preservation only. Existing dispatcher
unit tests independently prove applied/refused operation mapping; existing T03 controller
[status/lag results](evidence/t03/cdc-status-lag-results.txt) retain containment/continuity
coverage. Two-provider history registration and behavior were reused, not reimplemented.

**Manual comparison and captures.** Reviewed the serialized results against
[E18 packaged rejection](../document-cache-documentation/operations-runbook.md#packaged-downstream-history),
[CDC repair routing](operations-runbook.md#projection-repair-handoff), and
[CDC status interpretation](operations-runbook.md#observe-cdc); followed their owner/evidence
links. T04 rejection fields/exit/latch claims match. Diagnostic details are under
`phaseDiagnostics`, not the ignored compatibility `Diagnostics` property. SQL Server
prerequisite correction, interrupted reset/rebuild, scrub, and scan-free restart retain
[E18's existing identities and exclusions](../document-cache-documentation/cdc-inv-evidence.md#matrix);
this selection reruns the existing five Runbook cases, not all E18 suites.

Raw TRX/logs and all 36 JSON attachments were captured before review under
`/tmp/dms-1326-t12` (attachments also originate in the test output directory). Committed
results omit machine paths, test run IDs, and timings. Six representative captures retain
raw stdout/stderr, exit code, synthetic durable records, and before/after observations:
[PG active activation](evidence/t12/postgresql-activate-offline-active.json),
[PG historical deactivation](evidence/t12/postgresql-deactivate-offline-historical.json),
[PG unknown recovery](evidence/t12/postgresql-recover-cache-ahead-unknown.json),
[SQL missing activation](evidence/t12/mssql-activate-offline-missing.json),
[SQL target-mismatch deactivation](evidence/t12/mssql-deactivate-offline-mismatchedTarget.json),
and [SQL source-mismatch recovery](evidence/t12/mssql-recover-cache-ahead-mismatchedSource.json).
Synthetic source hashes, document IDs, timestamps, and contract fields are retained;
formatting is normalized only. Status captures identify asserted exit/stderr separately
from emitted contract JSON. VSTest rendered Unicode quote escapes inside the shipped-status
wrapper; that rendered output is retained alongside its decoded inner contract and stderr.

Initial test iterations caught invalid fixture artifact names, inherited state-root
permissions, and overstrict status stderr/state assertions. These were corrected in tests;
no production capability gap was identified. Final CSharpier and whitespace checks passed.
T12's assertion gaps are closed. Live provider/broker/API replay remains T14/T15, adoption
and replacement assertions T13, retirement operator assertions T17, final reconciliation
T16, and the absent restamp handoff E18-S08. Repeat these tests only against disposable
provider targets; the synthetic retirement proof is not an operator command or purge evidence.

<a id="t02-setup-review"></a>
## T02 setup, state, and planned-stop review

Reviewed 2026-09-06 against source revision
`bee7c3f74f857c2ab1bcb39f33c6412cb3f11188` plus T02 documentation changes. The pre-existing
story edit remains outside this task's commit. Linux/Bash; PowerShell `7.4.10`, Pester
`5.7.1`, .NET SDK `10.0.102`. No provider, broker, Connect image, or DMS deployment was
started or accessed. Existing resources were untouched; the selected fixtures used
Pester's disposable filesystem and mocked Docker commands. No deployment cleanup applied.

**Evidence layer/result:** manual source/help/fixture comparison plus existing bootstrap
behavior tests. Final selection: **131 passed, 0 failed, 0 skipped, 38 not selected**;
exit `0`. [Exact executed names/results](evidence/t02/bootstrap-results.txt) and
[full invocation with explicit `Filter.FullName` selection](evidence/t02/bootstrap-invocation.txt)
are retained. The invocation is a transcript of behavior-test selection, not an executable
documentation catalog or a test of this runbook. The full mixed suite was not run.

The first selection passed 132 cases, excluding the operator/story documentation Describe
and the E2E teardown documentation context. Further manual inspection found that one
otherwise behavioral E2E provisioning case also asserted a provisioner's help comment
(`drops if present, then recreates`). Removed that case from the final selection and
reran; no new tests or production changes were needed. Only the final 131-case selection
supports this authoring result. Raw initial/final logs and XML remain under
`/tmp/dms-1326-t02`; that temporary directory is not durable evidence.

**Starting directory/commands executed:** repository root, no target/generation and no
operational side effects. Save the exact invocation transcript to the named temporary
script before replaying the Pester command:

```bash
pwsh -NoProfile -File /tmp/dms-1326-t02/run-bootstrap-tests.ps1
dotnet run --no-restore --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc enable --help
dotnet run --no-restore --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc status --help
dotnet run --no-restore --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc stop --help
dotnet run --no-restore --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc restart --help
```

All four help commands built successfully and exited `0`, with empty stderr and no JSON
contract. Captured [enable](evidence/t02/cdc-enable-help.txt),
[status](evidence/t02/cdc-status-help.txt), [stop](evidence/t02/cdc-stop-help.txt), and
[restart](evidence/t02/cdc-restart-help.txt) output before normalizing only trailing blank
lines. Test-result extraction retains exact names/outcomes and omits transient user paths,
run IDs, and durations. No credentials or deployment payloads appear in committed artifacts.

**Manual comparison completed:** setup entry scripts, E2E configure/provision/start order,
`cdc-enable.psm1`, `enable-kafka-cdc.ps1`, `cdc-teardown.psm1`, the binding-root and local
policy resolvers in `env-utility.psm1`, `cdc-setup.yml`, `kafka.yml`, CLI command surface
and exit-code mapping, connector renderer inputs, `CdcConnectRestAdapter` stop read-back,
and controller stop/restart paths. Followed new local procedure, source, design, and evidence
links/anchors and checked command paths from the E2E directory. No automated documentation,
link, or Markdown execution tests were added. Findings incorporated into the runbook:

- E2E setup resolves `./.env.e2e` to the Compose file, applies the engine overlay, configures
  a single explicit target before DMS starts, and restores temporary runtime environment
  settings on exit. Later one-shot observations need the same target exports. Keep the
  printed effective file path and the ambient absolute state root for all later commands.
- E2E provisioner drops/recreates both named databases. A setup rerun cannot be a restart
  or an interrupted-enable retry. Bootstrap's resume assertion and the enable phase's
  differently named switch require a live record and never-opened admission history.
- Published `start-published-dms.ps1` accepts no CDC switch. The local infrastructure
  switch starts services; the bootstrap/E2E orchestrator supplies actual enablement.
  `build-dms.ps1 E2ETest` is not a CDC opt-in surface.
- Generated connector source properties, env-provider password reference, and local policy
  come from shipped builders. `kafka-postgresql-source` is the worker on both providers;
  container-advertised broker names do not become host-resolvable merely by publishing a port.
- Planned stop must land while Connect is reachable. The adapter waits for `STOPPED`;
  stop success still needs the registered connector's read-back for this exercise because
  absent-connector success cannot prove a persisted fence. Local full stop may warn and
  proceed after fencing fails; a later worker startup can resume publication before status.
- Teardown retains host retirement history and normally refuses volume removal after a
  failed retirement. No binding edits, abandonment shortcut, or platform-purge promise.

**Existing serialized fixtures reused, not rerun:** [T03 controller/status review](#t03-monitoring-review)
and [T12 CLI contracts](#t12-operator-path-review), especially
[stop/restart success without readiness](evidence/t12/cdc-contract-captures.json).
They establish JSON field/exit-code interpretation with fake collaborators, not live
admission, worker persistence, provider capture, or API smoke. Fresh admission JSON remains
pending T14/T15; no synthetic operational output was written to fill that gap.

**Provider replay commands — recorded, not executed here:** from
`src/dms/tests/EdFi.DataManagementService.Tests.E2E`, in the prepared PowerShell sessions
from [PostgreSQL](operations-runbook.md#local-postgresql) or
[SQL Server](operations-runbook.md#local-sqlserver), respectively:

```powershell
pwsh ./setup-local-dms.ps1 -EnvironmentFile $cdcEnv -DatabaseEngine postgresql -EnableKafkaCdc
pwsh ./setup-local-dms.ps1 -EnvironmentFile $cdcEnv -DatabaseEngine mssql -EnableKafkaCdc
```

These are separate, sequential exercises, each with its own synthetic database/snapshot
names and persistent root. T14/T15 own actual qualified image/digest/provider versions,
resolved environment and mount capture, admission/status JSON and exits, API observations,
planned-stop read-back before/after worker restart, guarded restart, and cleanup results.
The [shared commands](operations-runbook.md#local-setup-verification),
[stop/restart](operations-runbook.md#local-stop-restart), and
[matching teardown](operations-runbook.md#local-cleanup) retain the same target/generation.
No evidence row here claims those commands have been run against a provider.

**Unmet E19-06 handoff:** the E2E setup wrapper exists, but inspection of
`src/dms/tests/EdFi.DataManagementService.Tests.E2E` found no relational Kafka feature,
API-to-Kafka fixture, or topic-consumer helper supplying the required upsert/delete smoke.
The [E19-06 story](../design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md)
owns those missing capabilities. There is therefore no exact API test filter to record
and no API pass count. T14/T15 must obtain that upstream harness and record its concrete
filter with nonzero results; ordinary resource tests and lower-layer message fixtures
cannot substitute. The authoring contract explicitly permits this pending dependency.
T16 reconciles all pending results before story completion; T06/T17 own detailed retirement.

<a id="t05-continuity-review"></a>
## T05 continuity, adoption, and replacement review

Reviewed 2026-09-06 against `37232f15b091de9318020ca7fed173ba0f1aec20` plus T05
changes. Source story's pre-existing edit remains outside this commit. Tooling: .NET SDK
`10.0.102`, VSTest `18.0.1`, Linux/Bash. Starting directory: repository root. No live
provider, broker, Connect worker, qualified image, DMS endpoint, or API consumer was
accessed for this task. No production code, test code, or authoritative input changed.

**Manual review completed:** the new [incident context](operations-runbook.md#incident-command-context),
[continuity](operations-runbook.md#continuity-incident),
[adoption](operations-runbook.md#adopt-missing-binding), and
[replacement](operations-runbook.md#replace-physical-source) procedures were compared with
`DocumentCacheAdminCommandSurface`, CDC request builder/dispatcher, command executor and
exit mapper, `CdcSetupController` and adoption/retry/replacement fixtures, the local binding
store's serialized record/atomic import, and the binding, continuity, initial-readiness,
consumer, and deferred-repair design owners. Manually followed new local file/anchor links
in runbook, entry point, CLI reference, and this index; read command paths from the stated
repository root. Reviewed secret-reference handling, record/default-tenant translation,
new versus retained generations, no-proof refusal output, and interruption disposition.
No automated documentation tests, link checker, executable catalog, or Markdown execution.

**Generated help:** each of these commands built successfully, exited `0`, and had empty
stderr. Captures: [status](evidence/t05/cdc-status-help.txt),
[stop](evidence/t05/cdc-stop-help.txt), [restart](evidence/t05/cdc-restart-help.txt),
[adopt](evidence/t05/cdc-adopt-help.txt), [replace-source](evidence/t05/cdc-replace-source-help.txt).

```bash
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc status --help
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc stop --help
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc restart --help
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc adopt --help
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc replace-source --help
```

**Behavior verification — all completed, no failures/skips:** exact selections follow;
TRX/logs retained under `/tmp/dms-1326-t05`. The initial task-prescribed FQN filter selected
44 adoption/replacement cases but no retry cases: the retry file's class is actually
`Given_CdcSetupControllerInitialEnable`. Corrected the coverage gap by separately selecting
its `CdcSetupControllerRetry` category, adding 24 cases. The 11 existing CLI JSON-contract
cases verify the serializer/executor surface separately from the controller. Total: 79
passed, 0 failed, 0 skipped. Builds succeeded. No mixed documentation suite was selected.

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --filter 'FullyQualifiedName~CdcSetupControllerAdoption|FullyQualifiedName~CdcSetupControllerRetry|FullyQualifiedName~CdcSetupControllerReplaceSource' --logger 'trx;LogFileName=controller.trx' --results-directory /tmp/dms-1326-t05
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --no-build --filter Category=CdcSetupControllerRetry --logger 'trx;LogFileName=retry.trx' --results-directory /tmp/dms-1326-t05
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration --filter Category=CdcJsonContract --logger 'trx;LogFileName=cli-json.trx' --results-directory /tmp/dms-1326-t05
```

Exact selected identities and outcomes: [44 controller cases](evidence/t05/controller-results.txt),
[24 retry cases](evidence/t05/retry-results.txt), [11 CLI cases](evidence/t05/cli-json-results.txt).
Controller fixtures use the shipped controller with fake provider/Connect/Kafka/lifecycle
collaborators. The successful adoption/replacement captures use PostgreSQL fixture inputs;
the selection includes provider-mismatch and other rejection cases, not real PostgreSQL or
SQL Server integration. Reused [T03 status/restart/lag results](#t03-monitoring-review) and
[T12 stop/restart contracts](evidence/t12/cdc-contract-captures.json) without claiming a new
live continuity replay.

**Capture provenance:** after the tests, a temporary reflection aid at
`/tmp/dms-1326-t05/capture/Program.cs` reused the existing `CdcSetupControllerHarness` and
`Given_CdcSetupControllerReplaceSource.ReplaceableTarget`. It called controller operations
on the fixture inputs below, then passed returned contracts/refusals through
`Given_DocumentCacheAdminCdcJsonContracts.ContractResult` / `ExecuteAsync` and the actual
CLI executor/JSON serializer. Admission exits use the shipped `ForAdmission` mapper;
adoption result construction mirrors the inspected dispatcher (proof `0`, no proof `10`).
The fixture dispatcher is substituted; this is not proof of production dispatch, live
validation, or actual execution of the runbook's deployment commands. T13 owns additional
operator assertions. The capture aid only invokes existing behavior and serializes results;
it is not a second controller or a documentation test.

Invocation from the repository root:

```bash
dotnet run --project /tmp/dms-1326-t05/capture
```

Exit `0`; [per-capture exits/stderr lengths](evidence/t05/capture-results.txt). Raw executor
stdout/stderr was captured before publication under `/tmp/dms-1326-t05/raw`; the executor
fixture trims stream edges and the aid adds one stdout newline. Published JSON is only
pretty-printed, with all fields, diagnostics, fingerprints, names, generations, and fixture
timestamps retained. Help captures normalize terminal blank lines; the refusal stderr
artifact adds a terminal newline. All captured identifiers/messages are synthetic; no
credentials/payloads required replacement. Test-result extraction preserves complete case
identities/outcomes and omits transient TRX IDs, user paths, and durations.

| Artifact | Existing fixture input reused | Observed boundary |
| --- | --- | --- |
| [Adoption proof](evidence/t05/adopt-completed.json) | `Given_CdcSetupControllerAdoption.It_imports_the_supplied_binding_record_when_every_verification_is_an_exact_match` and `.It_verifies_every_adoption_verification_kind`; default harness | Proof with full generation-7 binding and ten `exactMatch` verifications; exit `0`, empty stderr. |
| [Adoption refusal stderr](evidence/t05/adopt-stopped-refused.stderr.txt) | `.It_refuses_a_connector_that_is_not_running_its_single_task("STOPPED","STOPPED")`; `RunningConnector("STOPPED","STOPPED")` | Exit `10`; executor stdout empty, no proof JSON. Plain `adoptVerificationNotExactMatch` diagnostic on stderr even with `--json`. |
| [Replacement admitted](evidence/t05/replace-admitted.json) | `Given_CdcSetupControllerReplaceSource.It_fences_the_outgoing_connector_and_enables_the_replacing_generation`; `ReplaceableTarget()` | `admissionState=admitted`, exit `0`, new generation `7`, previous `6`. |
| [Replacement resumed](evidence/t05/replace-resumed.json) | `.It_resumes_a_replacement_whose_binding_and_tracking_activation_already_committed`; exact generation-7 binding plus `Reading(lifecycleStateToken: "Tracking")` | Same new generation admitted; existing test asserts activation is not rerun. |
| [Fence refusal](evidence/t05/replace-fence-refused.json) | `.It_refuses_retryably_when_the_outgoing_connector_could_not_be_fenced`; `Stop=Unavailable` with synthetic retryable 503 detail | `unknown`, exit `12`, `replaceSourceRefused` / `connectorNotRunning` / `retryable=true`. All missing-observation diagnostics preserved. |
| [Identity refusal](evidence/t05/replace-identity-refused.json) | `.It_refuses_a_replacing_source_whose_identity_was_never_rotated`; outgoing binding fingerprint equals current source | `unknown`, exit `12`, `replaceSourceRefused` / `sourceMismatch` / `retryable=false`. Outgoing fence not called in owning test. |

**Findings incorporated:**

- Binding JSON spells default tenant `default`; shell examples translate to the CLI's empty
  tenant without changing the record. Adoption imports the supplied record's identity;
  invocation flags do not substitute for reviewing the record and resolved database.
- Adoption requires a running connector and running sole task, complete matching retained
  artifacts, healthy exact resume position, full Kafka policy including record budget, and
  shared-offset policy. It cannot serve as a start authorization for a stopped connector.
  A lost-history refusal imports/latches nothing; missing-state containment remains an
  explicit operator handoff rather than an invented recovery recipe.
- Actual no-proof adoption emits no stdout contract, contradicting the CLI README's broad
  one-document claim. Corrected the adoption entry and JSON wording. No fake rejection
  proof was authored. Store-import refusal follows the adoption controller's `10` mapping.
- Added missing `replace-source` admission entry to the CLI reference. Controller-level
  replacement refusals serialize as `unknown` / `12` even before a fence and can carry a
  nonretryable prerequisite diagnostic. The runbook preserves these fields and does not
  translate a primary unavailable category into a transient-retry instruction.
- A replacement is a new-database provisioning handoff with already-distinct source
  identity; no identity rotation, CMS repointing, external writer gate, or consumer-store
  migration is performed by the command. Old records/configuration/offsets remain for
  original-source retirement. Retained unfenced generations can themselves refuse cutover.
- Same previous/new-generation replacement retry can resume an exact bound empty Tracking
  attempt before admission; direct enable loses the required outgoing fence context.
  Terminal loss, cache-ahead repair, and exact post-admission baseline replacement remain
  outside this procedure. Stop/restart preserves existing artifacts and requires separate
  fence/readiness interpretation.

**Pending handoff:** T13 adds missing operator-path assertions, including explicit CLI
record/default-tenant and interruption paths; T14/T15 run disposable PostgreSQL/SQL Server
continuity, adoption, replacement and retirement replays and capture real tool/image
versions, original-source retention, read-back, outcomes, and cleanup. The E19-06 API
consumer dependency recorded in [T02](#t02-setup-review) remains unmet. T06/T17 own detailed
retirement/proof/retry guidance and assertions. T16 closes pending rows against actual
results and reconciles corrections. No upstream authoring blocker or live pass is claimed.
Manual diff review and `git diff --check` passed. All temporary capture resources remain
outside Git; no deployment resources were created.

<a id="t06-retirement-review"></a>
## T06 destructive-retirement review

Reviewed 2026-09-06 against `97542586a904464eb6f31f57e322cb0b7c317599` plus T06
documentation changes. The pre-existing story edit remains input and outside this commit.
Starting directory: repository root; Linux/Bash, .NET SDK `10.0.102`. No provider, broker,
Connect image, or deployment was started or mutated for this task. Raw help, test logs/TRX,
and fixture streams were captured before normalization under `/tmp/dms-1326-t06`.
The committed artifacts retain synthetic identity, timestamps, contract fields, diagnostics,
and output/exit pairs; help's terminal blank line was trimmed, proof JSON was indented,
and test result extraction omits machine/user paths and run identifiers. Internal diagnostic
arrays retain CLR property casing and are explicitly **not** CLI stdout contracts.

**Selection:** T13 was assessed first as the next integration point. Its live prerequisites
were not established: GitHub repository variables provided the pinned connector/provider
images but no `CDC_CONTROL_BROKER_KAFKA_IMAGE`; the E19-06 API/consumer helper remains
absent as recorded in T02. No T13 tests or implementation were performed. Selected the
ready T06 authoring contract instead; T13 remains pending. This is not a failing provider
exercise or a substitute qualification claim.

**Manual review:** compared [the retirement procedure](operations-runbook.md#retire-binding-generation),
its [record selection](operations-runbook.md#incident-command-context),
[local cleanup handoff](operations-runbook.md#local-cleanup), and
[CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#commands)
with generated retire help, command surface/request builder/dispatcher/exit mapper,
`CdcSetupController.RetireAsync`, Connect stopped-state read-back, Kafka/provider teardown,
`CdcCleanupProofValidator`, and `LocalCdcBindingStateStore` retirement/deletion ordering.
Followed new local file/anchor links, including configuration, binding/disclosure owners,
proof artifacts, and the E18/history handoff. No documentation assertions, Markdown
execution, executable documentation catalogs, or automated link tests were used.

**Behavior invocations and results:**

```bash
dotnet run --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc retire --help
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --filter 'FullyQualifiedName~CdcSetupControllerTeardown|FullyQualifiedName~CdcProviderArtifactTeardown|FullyQualifiedName~CdcKafkaTeardown' --logger 'trx;LogFileName=teardown-requested.trx' --results-directory /tmp/dms-1326-t06
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --no-build --filter Category=CdcSetupControllerTeardown --logger 'trx;LogFileName=retirement-controller.trx' --results-directory /tmp/dms-1326-t06
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration --filter Category=CdcJsonContract --logger 'trx;LogFileName=cli-json.trx' --results-directory /tmp/dms-1326-t06
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit --filter FullyQualifiedName~Given_DocumentCacheAdminCdcCommandDispatcher --logger 'trx;LogFileName=dispatcher.trx' --results-directory /tmp/dms-1326-t06
umask 077
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-build --filter 'FullyQualifiedName=EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc.Given_LocalCdcStateStore.It_refuses_a_binding_for_a_generation_it_already_retired|FullyQualifiedName=EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc.Given_LocalCdcStateStore.It_refuses_a_retirement_whose_existing_record_names_a_different_binding|FullyQualifiedName=EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc.Given_LocalCdcStateStore.It_latches_incidents_idempotently_and_deletes_state_after_verified_cleanup|FullyQualifiedName=EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc.Given_LocalCdcStateStore.It_deletes_incident_before_binding_and_keeps_the_record_when_either_delete_fails' --logger 'trx;LogFileName=retirement-store-private.trx' --results-directory /tmp/dms-1326-t06
```

The final commands above exited `0`; builds succeeded. The requested FQN filter selects 20 adapter
cases but misses the controller class `Given_CdcSetupControllerRetirement`; the separate
category run supplies its 12 cases. Existing CLI/dispatcher suites supply 11/31 cases,
and the four explicit filesystem cases verify retained history and deletion failure
ordering. Total **78 passed / 0 failed / 0 skipped** after correcting the filesystem invocation environment.
No production or test code changed. The initial four-case filesystem command used the same
filter without `--no-build`, inherited umask `0002`, and wrote `retirement-store.trx`: two
cases passed and two failed before reaching their intended identity guard. The existing
`TempCdcStateRoot.WriteRetirement` helper creates directories/files with inherited modes;
the shipped store correctly classified those records as `LocalStateUnavailable`. Replayed
with `umask 077`; all four passed. T17 should preserve that environment or correct the
helper while adding its retirement assertions. No assertion was weakened.

**Captured output:** temporary reflection aid `/tmp/dms-1326-t06/capture/Program.cs` reused
`Given_CdcSetupControllerRetirement.EnabledBinding`, `CdcSetupControllerHarness`, and
`Given_DocumentCacheAdminCdcJsonContracts.ContractResult` / `ExecuteAsync`. It called
the real controller, mapped proof/refusal using the shipped dispatcher rule, and passed
that result through the actual CLI executor with a substituted dispatcher. This is
manual fixture capture, not subprocess/provider/broker integration evidence. Its retained
[outcome list](evidence/t06/capture-results.txt) records stdout/stderr lengths and exits.
Successful cases have empty stderr; refused/incomplete cases have empty stdout.

| Capture | Existing fixture/setup and exact outcome |
| --- | --- |
| [PostgreSQL proof](evidence/t06/retire-postgresql-completed.json) | `Given_CdcSetupControllerRetirement.It_names_every_governed_artifact_of_the_binding_in_the_proof`; `EnabledBinding(Postgresql)`, exit `0`, eight artifacts. |
| [SQL Server proof](evidence/t06/retire-sqlserver-completed.json) | `.It_retires_a_sql_server_generation_including_its_schema_history_topic`; `EnabledBinding(SqlServer)`, exit `0`, twelve artifacts including history topic/grants and capture/gating artifacts. |
| [Asserted absence proof](evidence/t06/retire-absent-asserted.json) | `.It_retires_an_acknowledged_absent_connector_on_the_operator_s_own_assertion`; `NotFound` collaborators plus operator assertion, exit `0`; offsets evidence explicitly credits the operator. |
| [Absent-connector stderr](evidence/t06/retire-absent-refused.stderr.txt), [diagnostics](evidence/t06/retire-absent-refused.diagnostics.json) | `.It_refuses_a_retirement_whose_connector_the_worker_no_longer_has`; same absent evidence in a fresh enabled harness without assertion, exit `10`, `retireRefused`, no proof. |
| [Wrong-source stderr](evidence/t06/retire-source-refused.stderr.txt), [diagnostics](evidence/t06/retire-source-refused.diagnostics.json) | `.It_refuses_to_retire_a_generation_bound_to_another_physical_source`; retained previous-generation fingerprint against current source, exit `10`, `sourceMismatch`, no proof. |
| [Connector-failure stderr](evidence/t06/retire-connector-incomplete.stderr.txt), [diagnostics](evidence/t06/retire-connector-incomplete.diagnostics.json) | `.It_leaves_the_binding_record_intact_when_the_connector_could_not_be_removed`; delete-config `Conflict` after offset removal, exit `12`, `retireIncomplete`, no proof. |
| [Provider-timeout stderr](evidence/t06/retire-provider-timeout.stderr.txt), [diagnostics](evidence/t06/retire-provider-timeout.diagnostics.json) | Existing enabled harness, provider teardown fake waits on the real linked budget cancellation token; `Timeouts.ProviderSetup=10ms` for capture only. Exit `12`, `retireIncomplete`, `providerSetup`, `timedOut`, retryable, no proof. New assertion remains T17. |
| [Timeout retry proof](evidence/t06/retire-timeout-retry-completed.json) | Same harness/retained generation after timeout; fixture models already-removed connector/Kafka artifacts, operator assertion, and successful provider removal. Exit `0`, mixed `notFound`/`deleted`; no invented partial-success proof. New sequential/live assertions remain T17/T14/T15. |

The initial capture reused the successfully retired harness for the absent-connector
refusal and correctly got **missing binding** instead. Preserved that raw attempt under
`raw-initial`, corrected capture setup to a fresh enabled harness, and reviewed the final
streams above. No source behavior or expected result was changed to fit the prose.

**Material findings/corrections:**

- `--source-connection-variable` selects the original database without publishing its
  credential; the provider validate-only fingerprint guard runs before fencing. No
  source-override status/dry-run surface is invented. Dispatcher tests specifically cover
  the named variable while CMS is unreachable, unset-variable refusal, and secret exclusion.
- A cleanup timeout is a controller diagnostic, not a partial proof. CLI stderr prints
  `retireIncomplete` and message; it does not expose the full `observed=timedOut` object.
  The procedure separates budget expiry during cleanup from failed initial source access.
- Connector absence is not worker proof that offsets are absent. A retry after removal
  requires the operator's recorded judgement; a successful capture credits that judgement.
  “Same operation” retains selection; the CLI generates a new operation ID each invocation.
- State deletion is ordered: retirement history first, incident next, binding last. The
  retained `CdcRetirement` is an identity/history record, not the full cleanup proof.
  Archive the proof separately, and do not claim a retirement record alone proves completion
  after a lost response. Proof comparison uses a protected pre-mutation record copy because
  successful retirement removes the live binding. Broader/consumer-group grants and shared
  worker topics survive.
- Corrected stale T06 routing and one inherited “adoption pending T05” sentence. Fixed
  new local anchor references during manual review. Procedure links use actual owning
  anchors; no command was executed from Markdown.

**Pending:** T17 adds missing real-dispatch/default-tenant and original-source operator
assertions, provider-timeout/same-selection retry, final state-removal failure and retained
record capture using the fixture access from T13. T14/T15 supply live PostgreSQL/SQL Server
retirement proof, source selection, component read-back, and partial-cleanup replays.
The E19-06 helper dependency remains unmet. T16 reconciles final examples and closes
pending rows; fixture cleanup never establishes remote/platform purge. The filesystem
feedback failure was resolved by protected creation permissions; no remaining task-blocking
failure occurred. Temporary capture resources remain outside Git; the filesystem tests
dispose their own roots. Manual diff review and `git diff --check` passed.

<a id="t07-security-review"></a>
## T07 security and disclosure containment review

**Revision/layer:** 2026-09-06, base `bb25f68533b31e3663545c17ad495a6d0976a62d`
plus this task's documentation changes. Manual review `T07-security` covers
[credentials](operations-runbook.md#cdc-security),
[provider/ACL inspection](operations-runbook.md#inspect-cdc-security), and
[disclosure containment](operations-runbook.md#sensitive-data-containment).
No production/test code, authoritative inputs, executable documentation catalog, or
documentation tests changed. Existing story edit excluded from this task's commit.

**Executed product behavior:** repository root, .NET SDK `10.0.102`, VSTest `18.0.1`,
`net10.0`, Debug. Both builds and selections exited `0`; **150 passed / 0 failed /
0 skipped**. Raw logs and TRX captured before publication under `/tmp/dms-1326-t07`.
Published files contain exact TRX test identities and outcomes, excluding machine-local
paths and timings; no behavior output was edited or inferred.

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit \
  --filter 'FullyQualifiedName~CdcControlOptions|FullyQualifiedName~CdcKafkaAclPolicy|FullyQualifiedName~CdcConnectRestSecretRedaction' \
  --logger 'trx;LogFileName=security.trx' --results-directory /tmp/dms-1326-t07

dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit \
  --filter 'FullyQualifiedName~CdcConnectorTemplateArtifact|FullyQualifiedName~CdcConnectorTemplateSqlServerRendering|FullyQualifiedName~CdcConnectorTemplatePostgresqlRendering' \
  --logger 'trx;LogFileName=templates.trx' --results-directory /tmp/dms-1326-t07

dotnet run --no-build --project src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin -- cdc stop --help
```

The first selection passed [115 cases](evidence/t07/security-results.txt): required
principals, separate Java/admin vocabularies, fake-broker ACL filtering/isolation and
not-applicable observations, plus HTTP error/config/task-trace/offset redaction. The
second passed [35 cases](evidence/t07/templates-results.txt): PostgreSQL/SQL Server
rendering and optional redacted artifact output, including work-table exclusion and
SQL Server history security propagation. These are product unit tests with fixture
inputs independent of prose. They establish no live authentication, provider grants,
broker deletion, or production isolation.
Generated [stop help](evidence/t07/stop-help.txt) exited `0`, empty stderr; only terminal
blank lines removed. Compared status/retire syntax with existing
[T03 status help](evidence/t03/cdc-status-help.txt) and
[T06 retire help](evidence/t06/retire-help.txt), command surface and dispatcher.
No live mutation commands were executed from the runbook.

**Source and existing evidence review:**

- Compared examples against `CdcControlOptions` and its validator,
  `CdcControlServiceCollectionExtensions.BuildAdminClient`, template input allow-list,
  renderer and redacted artifact tests. Java worker ConfigProvider references and .NET
  resolved secret values belong to different processes; the admin adapter forwards its
  dictionary without worker placeholder expansion. Principal settings do not supply
  credentials. SQL Server forwards connector security to both history clients.
- Reviewed `CdcProviderManifestEmitter` and `CdcSetupController.ProviderSetupRequest`
  construction: provider JSON uses `validation_diagnostics`, and the controller sets
  `IncludeManifestPayload: false`. Corrected the draft's generic diagnostics key and
  documented optional retained manifests without promising CLI export or a state-root
  artifact. Current status summarizes provider/Kafka validation, not full grant inventories.
- Reviewed provider capture/grant assertions in
  [PostgreSQL access/retry](../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCdcProviderAccessRetryTests.cs)
  and [SQL Server access/retry](../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcProviderAccessRetryTests.cs).
  Exact reusable identities are below. These tests were **source-reviewed, not executed
  in T07**; no provider access/grant result is claimed. Validation reports unsafe existing
  grants/capture without silently removing them. SQL Server effective access includes
  public/inherited and deny precedence, not just direct grants.
- Reviewed [broker-backed tests](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration/CdcControlBrokerBackedTests.cs)
  and [fixture configuration](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration/CdcControlBrokerFixture.cs).
  `StandardAuthorizer` answers real ACL queries; plaintext fixture clients use
  `User:ANONYMOUS` as a superuser. This supports real metadata/filter and artifact cleanup
  assertions when executed; it cannot certify authentication/denial for separate production
  principals. `CDC_CONTROL_BROKER_KAFKA_IMAGE` is unset in this iteration; no qualified
  broker/image replay was attempted and no integration pass/skip is claimed.
- Reviewed the earlier [T06 teardown assertions](evidence/t06/teardown-requested-results.txt),
  [T06 serialized proof/refusal captures](#t06-retirement-review), and
  [T12 stop/status contracts](evidence/t12/cdc-contract-captures.json). Local ACL-disabled
  teardown reports grants `notFound`; it does not prove revocation in production. Retirement
  preserves consumer-group grants and shared worker topics. The routine status describe
  path does not restore missing grants; initial provisioning can, so it is not an incident
  revocation/recovery procedure.

Reusable provider identities: PostgreSQL fixtures use namespace
`EdFi.DataManagementService.Backend.Postgresql.Tests.Integration`; SQL Server fixtures
use `EdFi.DataManagementService.Backend.Mssql.Tests.Integration`. Combine that namespace,
fixture, and method below for the full identity:

| Fixture | Exact method identity | Review finding; execution owner |
| --- | --- | --- |
| `Given_PostgresqlCdcProviderAccessRetry` | `It_should_fail_closed_on_mismatched_grants_without_removing_them` | Direct work-table grant diagnosed and retained; T14 live replay. |
| `Given_PostgresqlCdcProviderAccessRetry` | `It_should_fail_closed_on_work_table_publication_membership_without_removing_it` | Work-table publication membership diagnosed without removal; T14. |
| `Given_PostgresqlCdcProviderAccessRetry` | `It_should_fail_closed_on_elevated_connector_role_without_downgrading_it` | Elevated connector role refused; T14. |
| `Given_MssqlCdcProviderAccessRetry` | `It_should_fail_closed_on_public_forbidden_permissions_without_removing_them` | Effective public work-table/source-write/extra-table access diagnosed; T15. |
| `Given_MssqlCdcProviderAccessRetry` | `It_should_exact_match_when_public_work_table_grant_is_denied_to_connector` | Effective deny precedence inspected; T15. |
| `Given_MssqlCdcProviderAccessRetry` | `It_should_fail_closed_on_work_table_capture_without_disabling_it` | Forbidden capture diagnosed without cleanup; T15. |

The authorizer suite is ordered and shares one stack; individual later methods are not
standalone execution recipes. The following exact methods of
`EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration.Given_CdcControlBrokerBackedStack`
were reviewed with their setup and preceding operations:

- `It_reports_the_shared_connect_offset_store_as_compacted_durable_and_worker_only`
- `It_fails_closed_when_the_offset_store_carries_a_grant_beyond_the_connect_worker`
- `It_fails_closed_when_the_offset_store_is_covered_by_an_over_broad_topic_pattern`
- `It_provisions_and_verifies_the_binding_grants_against_a_real_authorizer`
- `It_fails_closed_when_an_instance_consumer_can_read_another_instances_topic`
- `It_fails_closed_when_an_instance_consumer_can_read_the_progress_topic`
- `It_requires_a_stopped_connector_before_its_committed_offsets_can_be_deleted`
- `It_deletes_exactly_the_binding_governed_artifacts_and_leaves_the_shared_offset_store`

The cleanup test observes absent public/progress topics and public-topic ACLs while the
shared offset topic/ACLs remain. It uses PostgreSQL, which has no schema-history topic.
It does not observe managed-platform remote/tiered storage or consumer stores. T14/T15
own live runbook replay with their prerequisites; SQL Server history remains provider-specific.

**Manual absent-purge walkthrough (not a new fixture result or incident JSON):**

Use synthetic incident `disclosure-lab-01`. Read the existing T06
[PostgreSQL completion proof](evidence/t06/retire-postgresql-completed.json) and
[capture result](evidence/t06/capture-results.txt): CLI exit `0`, operation `operation-1`,
generation `7`, topic `edfi.documents.instance.binding-g7.documents.v1`,
`verifiedAt=2026-08-28T09:00:01+00:00`, every governed artifact `deleted`.
Those are actual serialized controller/CLI fixture fields with fake external adapters.
No new stop/revocation/deletion time or platform confirmation is fabricated.

| Decision checkpoint | Available evidence | Manual disposition |
| --- | --- | --- |
| Connector/task fencing and consumer access revoked? | Proof contains cleanup assertions, but no deployed persisted-fence or effective-denial observation. | Deployment containment unverified; keep external admission closed and request owner evidence. |
| Governed retirement succeeded? | Existing fixture stdout proof and exit `0`; identity and every cleanup state reviewed. | Fixture component cleanup complete only. Same result for the SQL Server proof's additional history artifacts. |
| Public topic and covered remote/tiered copies purged? | Platform deletion request/time/guarantee/confirmation deliberately absent. `verifiedAt` is only the proof time. | **Incident remains open**, even if deployment containment and component deletion were separately confirmed. |
| Independent consumer copies gone? | Consumer-store/export deletion evidence absent. | Separate response obligation remains open with those owners. |
| May old topic restart or enablement resume? | No new-generation cutover/snapshot/consumer-namespace/barrier evidence; workflow deferred. | CDC unavailable; no restart/recreate/re-enable instruction. |

Repeating the review with [SQL Server completion](evidence/t06/retire-sqlserver-completed.json)
does not change the result: deleting schema history adds no remote purge evidence.
The [provider-timeout capture](evidence/t06/retire-provider-timeout.stderr.txt) gives exit
`12` and no proof; it additionally requires same-selection retirement reconciliation/retry.
The missing platform evidence remains open after the
[successful retry](evidence/t06/retire-timeout-retry-completed.json).

**Manual review outcome:** followed new local source/design/procedure/evidence links and
anchors; checked command target/default-tenant selection, source override, confirmation,
status/stop/proof shapes and exits against generated help/source/captured output. Reviewed
new examples and reused captures for credentials, bearer tokens, raw UUIDs/source positions,
payloads and student data. All identifiers are synthetic or opaque fixture fingerprints;
configuration examples name secrets without values. No new operational JSON was invented.
The containment procedure leaves the incident open without platform evidence and links the
deferred cutover owner. `git diff --check` passed. No database, broker, Connect, or DMS
service was accessed; no deployment resources needed cleanup.

**Remaining evidence:** T14/T15 live provider/authorizer/fence/retirement exercises;
T13/T17 additional operator assertions; T16 final reconciliation. Production identity and
platform purge guarantees remain deployment-owned. E18-S08 restamp and E19-06 API-consumer
handoffs stay explicitly unmet. T07 authoring is complete against existing behavior/help
and fixture outputs; it does not close these downstream results or the overall story.

<a id="t08-capacity-review"></a>
## T08 retention, continuity and capacity review

Reviewed 2026-09-06 against `92d80f03d1a445d13439f720cb5f3d23248d5f50` plus T08
changes; .NET SDK `10.0.102`, Linux/Bash. The pre-existing story edit remains outside
this commit. T13 was considered first; `CDC_CONTROL_BROKER_KAFKA_IMAGE` is unset and the
[T02 E19-06 helper handoff](#t02-setup-review) remains unmet. Selected ready T08 authoring;
no provider, Connect, broker, consumer or qualified image was started/accessed. No live
inspection or performance result is claimed. No deployment cleanup required.

### Executed behavior checks

From repository root:

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit \
  --filter 'FullyQualifiedName~CdcKafkaRecordSize|FullyQualifiedName~CdcKafkaTopicPolicy|FullyQualifiedName~CdcKafkaOffsetStore|FullyQualifiedName~CdcKafkaSchemaHistory' \
  --logger 'trx;LogFileName=policy.trx' --results-directory /tmp/dms-1326-t08
```

Build/test exit `0`: **82 passed, 0 failed, 0 skipped**. Exact TRX-derived identities
and outcomes: [policy-results.txt](evidence/t08/policy-results.txt). Raw stdout/stderr
were captured before review in `/tmp/dms-1326-t08/policy.log`, with `policy.trx`; temporary
captures are not durable evidence. The committed result list preserves all selected
identities/outcomes; no operational JSON was fabricated. Tests use fake Kafka admin
responses/record readers, not real broker or provider access. They verify product policy
independently of prose; no documentation assertions or documentation tooling was run.

### Manual comparison and findings

Reviewed the new anchors
[retention context](operations-runbook.md#retention-and-capacity),
[PostgreSQL](operations-runbook.md#inspect-postgresql-retention),
[SQL Server](operations-runbook.md#inspect-sqlserver-retention),
[broker](operations-runbook.md#inspect-broker-capacity),
[consumer](operations-runbook.md#consumer-continuity),
[size increase](operations-runbook.md#increase-record-size), and
[overhead](operations-runbook.md#pipeline-overhead).
Manually followed their local links/anchors to the configuration catalog, incident/status
and stop context, E18 prerequisite/evidence owners, renderer, source metadata adapters,
emitted enqueue routines, sibling fixtures, topic/consumer/record-size design and
unassigned production qualification. This is authoring review, not an executed SQL/Kafka
exercise. No new CDC CLI syntax was introduced: existing T02/T05 help and T12 JSON shapes
supply stop/restart/status references.

- Compared PostgreSQL selection to `CdcPostgresqlHeartbeatPublicationProvider.ReplicationSlotSql`:
  exact slot/database/plugin, retained/flush LSN and optional slot state fields. Added
  native WAL-span/safe-size inspection with the
  [PostgreSQL 16 metadata reference](https://www.postgresql.org/docs/16/view-pg-replication-slots.html).
  WAL-address distance is not allocated bytes, connector committed position or Kafka lag;
  null safe size is not unlimited disk evidence.
- Compared SQL inspection to `CdcSqlServerHeartbeatDatabaseProvider` help-jobs/runtime
  and capture-instance SQL and `CdcProviderSetupResultMapper`. Retained-LSN inspection
  is metadata-only. Cleanup retention minutes and capture-job polling seconds stay
  distinct from connector poll configuration. Scheduled cleanup is not required to run
  continuously. Row-version queries use aggregate
  [tempdb](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-tran-version-store-space-usage?view=sql-server-ver17)
  and [ADR/PVS](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-tran-persistent-version-store-stats?view=sql-server-ver17)
  metadata; missing access/rows remain unproved. PVS off-row size is not all version storage.
- Compared Kafka policy to `CdcKafkaAdminAdapter` and the four selected suites: topic
  explicit policy, replica/broker minimum limits, unknown unreadable settings, shared
  offsets and SQL Server schema history. Inspections do not apply public-topic retention
  to internal topics. Checked native config/offset tool syntax against Apache Kafka
  [ConfigCommand](https://github.com/apache/kafka/blob/4.0.0/core/src/main/scala/kafka/admin/ConfigCommand.scala)
  [GetOffsetShell](https://github.com/apache/kafka/blob/4.0.0/tools/src/main/java/org/apache/kafka/tools/GetOffsetShell.java),
  and [LogDirsCommand](https://github.com/apache/kafka/blob/4.0.0/tools/src/main/java/org/apache/kafka/tools/LogDirsCommand.java).
  Corrected literal-topic regex quoting and noted per-partition errors can accompany exit
  zero. Broker metrics/cleaner logs and retained log bytes are separate from offset spans;
  no cleaner health is inferred from a live-key count. Live tool help/output remains T14/T15.
- Consumer evidence follows the design-owned deadline, tombstone floor and renewal rules;
  no second policy table. Invalid/expired/uncertain proof invalidates the entire store
  and requires consumer-owned full bootstrap. Per-partition durable application includes
  idle/empty partitions; group commits alone are insufficient. Capacity includes dirty
  retained log, skew, maximum records, durable writes and concurrent traffic.
- `CdcControlOptions.ToDeploymentPolicy`, `CdcConnectorTemplateContracts`, renderer and
  validator require buffer >= `max(33554432, MaxRecordBytes)`. Coordinated increase raises
  consumers, then broker/replica/topic, then producer buffer/request size. `MaxRecordBytes`
  is mutable policy, not binding identity. Read-back must cover every layer; partial
  rollout stays unavailable. `CdcSetupController.UnsatisfiedRestartPrerequisite` checks
  current policy/config but restart never rewrites it. Packaged CLI has no size-rollout
  orchestrator or render-export verb. The Connect update boundary may start tasks; the
  deployment must preserve containment/healthy continuity or leave that step pending.
- Compared E19-05 `UpdateRetainedConnectorSizeConfigAsync` and boundary fixture: it uses
  renderer plus retained-config REST PUT and explicit test restart. Its observer/custom
  test orchestration is not an operator helper and was not copied into the runbook.
- E18 writer timings and query-plan assertions are component evidence, not canonical
  pipeline or independent consumer capacity certification. Provider enqueue/ack overhead,
  projector outage/drain and Kafka lag are distinct. Linked still-unassigned representative
  qualification; no new performance harness, benchmarks or arbitrary thresholds added.

Manual link/anchor and secret/payload review completed; `git diff --check` passed.

### Reused sibling evidence and live handoff

Source-reviewed, **not run in T08**:

- Namespace `EdFi.DataManagementService.Backend.Cdc.Tests.Integration`,
  `Given_MessageContractRecordSize` (both provider fixture instances):
  `It_publishes_the_complete_synthetic_record_one_byte_below_the_pinned_producer_boundary`,
  `It_fails_the_producer_without_partial_publication_and_blocks_readiness_despite_healthy_progress_and_lag`,
  `It_replays_the_rejected_record_and_recovers_readiness_after_aligned_limits_change_in_place`.
  [Fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRecordSizeTests.cs)
  captures bounded size/config/offset evidence when its qualified runtime prerequisites exist.
- Same namespace, `Given_MessageContractConsumerBroker`:
  `It_requires_durable_application_and_every_checkpoint_including_the_empty_partition`,
  `It_renews_an_idle_proof_from_real_unchanged_ends_only_after_all_checkpoints_complete`,
  `It_continues_from_durable_next_offsets_without_replaying_bootstrap`,
  `It_reconstructs_from_earliest_after_checkpoint_loss`,
  `It_reconstructs_from_earliest_after_checkpoint_corruption`.
  [Fixture](../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractConsumerBrokerTests.cs)
  is test-consumer conformance, not independent consumer certification.
- PostgreSQL and MSSQL `DocumentCacheWriterTests`:
  `DocumentCacheWriterPerformanceEvidence_it_compares_projector_and_direct_fill_workload_modes`;
  existing [E18 index](../document-cache-documentation/cdc-inv-evidence.md) identifies
  both provider projects and the concurrency/query-plan supporting evidence. No numeric
  production result is inferred or copied.

T14/T15 must capture selected identity, source revision, provider/Kafka tool/qualified-image
versions, exact inspection commands from these anchors, raw then sanitized stdout/stderr,
native exits, timestamps/units, missing permissions and cleanup. PostgreSQL owns slot/WAL
observations in T14; SQL Server owns jobs/retained ranges/version storage in T15; both own
broker observations and the size-policy read-back handoff. External consumer reports and
platform capacity evidence remain deployment-owned. Use existing E19-05 fixtures where
applicable and retain nonzero selected test counts; do not call a missing prerequisite a
pass. T16 reconciles these pending rows with actual outcomes. E19-06 API and E18-S08
restamp handoffs remain as previously recorded.

<a id="t09-discovery-review"></a>
## T09 setup and discovery review

**Revision/scope:** reviewed 2026-09-06 at
`a6ab4b20355e87ba5e776961adf4ece3b2815c7c` plus T09 documentation changes. Preserved the
pre-existing story edit outside this task's commit. T13 was considered first; its qualified
broker image input remains unset and the E19-06 helper handoff remains unmet. Selected
ready T09 authoring; no T13 implementation or test execution is claimed.

**Manual search:** from the repository root, ran the task's search before and after edits:

```bash
rg -n -i "registration has not landed|SQL Server.*Kafka|Kafka.*SQL Server|07-ops-docs-runbooks|EdfiDoc|deleted=true" docs eng/docker-compose reference src/dms/tests src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md
```

Both searches returned matches (exit `0`). Classified the results by the design's
[documentation disposition](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#documentation-audit-and-disposition):

| Material reviewed | Disposition and correction |
| --- | --- |
| [Compose guide](../../eng/docker-compose/README.md#deployment-owned-cdc-kafka-connect) | Current. Consolidated duplicated setup/state/retry/retirement/status guidance into runbook links. Removed advice to delete the state store to reset generations and the unconditional fence claim. Kept infrastructure-only startup distinct from post-provisioning registration, and the published script distinct from local opt-in. |
| [Relational guide](../../docs/RELATIONAL-BACKEND.md#optional-cdc-publication) | Current. Added both-provider setup/evidence and E18/CLI/configuration handoffs; replaced future-tense CDC security ownership. Table provisioning alone does not enable publication. |
| [DMS E2E README](../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/README.md#cdc-support) | Current. API lanes are distinct from the setup wrapper's CDC opt-in. DS 6.1 PostgreSQL CI retains a Kafka host entry; MSSQL CI omits it. Neither setting nor an API test pass qualifies CDC. E19-06 API-consumer helper and live replay remain unmet/pending. |
| [RestClient debugger setup](../../src/dms/tests/RestClient/local-development-setup.http) | Current setup, no connector registration in this Keycloak/debugger flow. Replaced the global pending-support claim with a fresh-database/self-contained CDC runbook handoff; no retrofit or container-only host address assumption. |
| [Instance Management E2E README](../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md#cdc-support) | Current route-context lane; its setup has no CDC switch, and local CDC requires one unqualified target. Replaced the global pending-support claim with that scoped restriction and runbook/evidence links. |
| [CLI README](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#runbook-links), [E18 README](../document-cache-documentation/README.md), and [E19-S04 operator handoffs](../design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md) | Replaced operator links to the runbook story with delivered procedures/evidence. Story ownership links in epic/Jira indexes and the invariant-to-story table remain valid planning/traceability references. |
| [CDC design status](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#implementation-status-and-operator-entry-point) | Reconciled disposition and implemented-surface status, without changing normative contracts, design front matter, or deferred workflows. |
| [Bootstrap boundaries](../design/backend-redesign/design-docs/bootstrap/command-boundaries.md), [generic transform design](../design/backend-redesign/design-docs/expandjsonsmt-replacement.md) | Replaced future local-opt-in wording and legacy default-connector assumption; linked admission/state/retry owners instead of duplicating their algorithm. Generic expander contract unchanged; renderer is shipped, cross-repository image qualification remains a prerequisite, and API-consumer handoff remains unmet. |
| Legacy JSON/shared-topic/OpenSearch and `deleted=true` references | Historical/obsolete CDC shapes remain classified in the design disposition and ADR 0002; no legacy examples promoted. Other search hits in deadlock/auth/token-info/batch drafts and middleware/request-model names are outside this CDC setup scope and were not blindly renamed. PRD provider capability and SQL Server schema-history/record-budget references remain valid. |
| Existing mixed Pester suite | Search hits describe provider capability or old documentation assertions. No documentation assertions selected or modified; prior T02 selected behavior evidence remains separate. |

**Source and link review:** inspected `start-local-dms.ps1`, `start-published-dms.ps1`,
`bootstrap-local-dms.ps1`, `cdc-teardown.psm1`, both E2E setup wrappers, the connector renderer,
and the DS 6.1 jobs in `.github/workflows/on-dms-pullrequest.yml`. Compared the handoffs with
[T02 wrapper/help evidence](#t02-setup-review), [T05 continuity evidence](#t05-continuity-review),
and [T06 retirement evidence](#t06-retirement-review); no new commands or JSON examples
were added. Manually followed changed relative paths and read target headings/explicit
anchors for setup, prerequisites, state, setup verification, API smoke, stop/restart,
continuity, adoption, retirement, cleanup, projection repair, security, status, lag,
configuration, CLI, evidence, and design ownership. Changed links resolve; remaining
story links name work packages, not executable procedures. `git diff --check` passed.

**Evidence boundary:** documentation/comment changes only; no C# or PowerShell behavior
changed, so no new compile/test run was required. No automated documentation, link, or
catalog tests ran. No provider, broker, qualified image, or DMS deployment was accessed;
no operational JSON captured and no deployment cleanup needed. T13/T17 additional
assertions, T14/T15 live provider exercises, the E19-06 API-consumer and E18-S08 restamp
handoffs, and T16 final consistency review remain pending. This review is not live CDC or
production-capacity qualification.

<a id="pending-delivery"></a>
## Pending delivery and verification

These are explicit pending results, not runnable anchors or claims of coverage. Each
authoring task adds its actual procedure anchors and evidence rows when delivered.
Authoring can finish against existing help/source/available fixture output; new assertions
and live replays stay assigned to the downstream owners. T16 closes these entries using
actual results before the story is complete.

| Pending scope | Authoring / evidence owner | Result and required handoff |
| --- | --- | --- |
| Fresh PostgreSQL/SQL Server setup, API upsert/delete, planned stop/guarded restart | T02 delivered; T14/T15 live replay | [T02 review](#t02-setup-review): 131 bootstrap behavior cases passed; help/source/fixture authoring complete. E19-06 API consumer harness absent, dependency unmet. Actual provider/image/API/fence/cleanup results pending T14/T15. |
| Monitoring/incident routing and status/lag JSON | T03/T12 delivered; T14/T15 live observations | Authoring and existing fixture capture reviewed in [T03](#t03-monitoring-review); additional CLI contracts verified in [T12](#t12-operator-path-review). Deployed endpoint/provider/metrics/fence read-back remains pending; fixture success does not close live evidence. |
| E18 packaged downstream-history handoff and repair scope | T04/T12 delivered; E18-S08 restamp; T16 reconciliation | [T04 review](#t04-projection-handoff-review): 8 configured integration and 27 history unit cases passed; [T12](#t12-operator-path-review) adds all 36 provider/command/evidence tuples with 144 passing assertions. Dedicated restamp utility/procedure/evidence is absent; E18-S08 handoff unmet. |
| Continuity, complete-record adoption, physical-source replacement | T05 delivered; T13 assertions; T14/T15 replay | [T05 review](#t05-continuity-review): 68 controller and 11 CLI cases passed; six captured outcomes, help/source/default-tenant/retry review complete. Additional operator assertions and live provider replay pending; T16 closes results. |
| Destructive retirement, partial failure, timeout and same-operation retry | T06/T17 delivered; T14/T15 replay | [T17 results](evidence/t17/README.md): registered/never-registered PostgreSQL broker cleanup, injected provider timeout and retry, shared worker state/retirement history; packaged source selection and refusal for both providers; CLI proof/output contracts. Provider runbook replay remains T14/T15. |
| Security, consumer isolation, sensitive-data containment | T07 delivered; T14/T15 replay | [T07 review](#t07-security-review): 150 unit cases passed; source/help/fixture review and absent-purge walkthrough complete. Live provider/authorizer/fence/deletion replay pending; platform purge and independent consumer stores remain deployment-owned. |
| Provider retention, consumer continuity, record budget, capacity observations | T08 delivered; T14/T15 replay | [T08 review](#t08-capacity-review): 82 policy tests passed; bounded inspection/source review complete. Live queries/tool output, consumer reports and coordinated rollout observations pending. Production qualification remains separately owned/unassigned. |
| Discovery references and final consistency | T09 delivered; T16 closure | [T09 review](#t09-discovery-review): active setup links, provider/lane distinctions, and historical disposition manually reconciled. Final review of all anchors, exact test selections, provider results, artifacts, and limitations remains T16. |

Setup, provider artifacts, routing, durability, ACLs, and consumer conformance remain in
their sibling stories/suites. Start with the
[CDC contract-to-evidence ownership table](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability)
and the [E18 evidence matrix](../document-cache-documentation/cdc-inv-evidence.md);
later rows link exact reusable test identities rather than copying those implementations.
