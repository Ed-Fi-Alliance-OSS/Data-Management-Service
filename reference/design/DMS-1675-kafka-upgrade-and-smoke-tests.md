---
jira: DMS-1675
status: Kafka qualification and security assessment complete; image remediation remains open
---

# DMS-1675: Upgrade Kafka and qualify CDC against Apache Kafka

Jira: https://edfi.atlassian.net/browse/DMS-1675

## Problem

The Kafka update originally proposed by Dependabot #1426 (targeting 4.3.1) was included in dependency consolidation PR #1436 and subsequently raised to 4.3.2 to address broker vulnerabilities reported against 4.3.1. Kafka qualification and test-fixture changes exceed the scope of that dependency bundle and need a separate PR.

At the initial split, Compose selected the new broker while CdcComposeBrokerSizeDeployment.BrokerImage still pinned a different broker image and checked the deployed image for exact equality. Native Kafka fixtures consumed that stale constant. The Backend CDC Connector Template Smoke Tests used Redpanda startup and rpk commands, so they did not qualify Apache Kafka 4.3.2. Normal PR checks skip this manually dispatched smoke lane and do not establish that the live RecordSize qualification suites pass.

## Intended outcome

Use the same digest-pinned Apache Kafka 4.3.2 broker in the DMS deployment and primary CDC smoke/qualification fixtures, with a durable check preventing image-pin drift. Keep the non-Kafka Dependabot updates in PR #1436 independently reviewable.

## Scope and acceptance criteria

1. Move the Kafka version bump out of PR #1436 into a separate PR. Target apache/kafka:4.3.2@sha256:852a0d49fc4e656b022e7f23785411802c9a088c78a7c5e39f33969de9c227a7. The new PR begins as a draft; the bump alone does not complete this story.
2. Align CdcComposeBrokerSizeDeployment.BrokerImage and all applicable native Kafka fixture pins with the shipped Compose broker. Add a meaningful consistency check that fails when the Compose image and application/fixture authority disagree.
3. Migrate the primary connector-template smoke lane from Redpanda to Apache Kafka, reusing existing native Kafka fixture support where appropriate. Replace Redpanda-specific startup and rpk operations; preserve connector validation/registration, classloading, partitioner, heartbeat, offset progress, and restart assertions.
4. Audit shared fixture callers, workflow variables, configuration, and documentation. Remove obsolete Redpanda requirements from migrated paths. Retain a separate Redpanda compatibility lane only if an explicit supported-deployment requirement is established; do not break other callers by removing shared behavior blindly.
5. Run connector-template smoke tests and live PostgreSQL and MSSQL RecordSize qualification suites against a recorded revision containing the completed Kafka implementation and intended image pins. For subsequent changes, rerun affected selections when those changes can alter behavior exercised by the selections, image inputs, test selection, or qualification outcomes. Otherwise, document why the existing live evidence remains applicable and record focused validation. Verify Compose-backed broker-size changes and restart persistence use the intended Kafka image. Run relevant unit/contract tests and other affected live Kafka suites. Record actual results and failures; a skipped test or offline contract result is not live qualification.
6. Validate fresh startup and retained-volume persistence with the shipped broker, including the startup script, KRaft configuration, volume ownership, cluster/topic identity and offsets.
7. Refresh the pinned-image vulnerability assessment and record remaining findings and their disposition. The prior 2026-11-03 Linux/amd64 Trivy scan found 9 package findings: 1 critical, 3 high, 2 medium, and 3 unknown. Critical: CVE-2026-111497 (brotli-libs); high: CVE-2026-117610, CVE-2026-118370, CVE-2026-77214 (libexpat). These package findings do not establish exploitability. Do not describe 4.3.2 as security-cleared without resolving or assessing them.

## Implementation status

Pin alignment and its Compose drift test, the shared smoke-fixture migration, and
the shared caller/configuration/documentation audit are implemented. Record-size
helpers read Kafka broker settings, telemetry creates topics with Kafka tools, and
restamp tests consume through Kafka's console consumer. Startup/cleanup tests use
the shared pin. Both workflows and the runner no longer require Redpanda; the
runner and smoke workflow resolve and validate the branch's Kafka pin. No explicit
Redpanda support requirement was found, so no compatibility lane is retained.

Live smoke, both providers' RecordSize and Compose persistence, Kafka policy,
telemetry, message contracts, and relevant unit/contract qualification passed as
recorded below. Live qualification found and corrected the runbook's obsolete JMX
tool class name. The refreshed security scan and disposition are recorded below.
Qualification now targets only the shipped broker; historical-image fixtures and
prerequisites have been removed. The three required live selections were rerun on
the November 11 PR head after the pin-mismatch classification fix, as recorded
below. The broader November 8 results remain historical evidence for their
original run scope.

## Head qualification — 2026-11-11

Tested commit: `5b700f7c43b13426effbf79e01d98c1281e30459`, matching PR #1440's
head before and after the run. This includes the pin-mismatch classification fix,
fixture cleanup, rebased inputs, and Linux process-tree cleanup fix. This new run
supersedes the earlier qualification at `1b0b718`; those earlier results are not
used to qualify this head. Tracked files and HEAD were unchanged throughout the
run, from 22:11 to 23:36 UTC on November 11. Each selection ran once; no failures,
fixes, or reruns were needed. Commit `7974be064` subsequently updated only this
qualification record. Commit `715624dbe` then added support for quoted Kafka image
pins and corresponding tests; it was not part of this live run.

### Subsequent changes — 2026-11-12

The quote-parsing change preserves the resolved image for the shipped, unquoted
Compose pin. The five focused `CDC qualification Kafka image` Pester cases pass,
including unquoted/single-quoted/double-quoted pins, invalid-pin rejection, smoke
workflow configuration, and the RecordSize persistence evidence guard. Application
code, fixtures and image inputs were unchanged by `715624dbe`, so the recorded live
results remain applicable to those scenarios.

Commit `60eae892d` keeps root exit observation active when descendant
inventory fails, bounded by the existing cleanup deadline. Focused process tests
cover inventory failure before root exit and explicit cleanup cancellation while
preserving the original failure. These tests cover the exceptional inventory path;
no Kafka image inputs or live scenario assertions changed. No live selections were
rerun for these follow-ups, and the November 11 results remain attributed to
`5b700f7`, not to a later revision.

Validation of this follow-up, based on `715624dbe`: all 4,777 CDC unit tests passed
in Release with zero failures or skips, including both new inventory-failure cases.
Both new cases failed at the root-exit ordering assertion with the pre-fix behavior.

Commit `d3db9c8f4` then changed Linux descendant-exit polling to recognize zombie
and dead processes as terminated without waiting for another parent to reap them.
Process-test assertions and teardown use the same termination semantics. All 13
focused process tests passed in Release with zero failures or skips, including the
unreaped-zombie regression; the commit also records that regression failing before
the fix and a cancellation reproducer completing without a cleanup timeout.

The ESRCH follow-up, based on `d3db9c8f4`, also recognizes process exit when a read
from an already-open `/proc/<pid>/stat` descriptor fails with Linux ESRCH
(`IOException.HResult == 3`). Other I/O and permission failures still propagate.
The regression deterministically opens the descriptor, terminates and reaps the
process, then reads it. It failed with the actual ESRCH exception before the catch
was added. Afterward, all 16 focused process tests passed in Release with zero
failures or skips, including checks that other I/O and permission errors propagate.
Changed C# files were formatted with CSharpier. The focused command was:

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/EdFi.DataManagementService.Backend.Cdc.Tests.Unit.csproj -c Release --no-restore --nologo --filter 'FullyQualifiedName~Given_CdcWorkerDockerCommand|FullyQualifiedName~Given_CdcWorkerProcessTree' --logger 'console;verbosity=minimal'
```

These two polling changes affect observation of captured Linux descendants during
Docker command failure or cancellation cleanup. Successful Docker commands and
cleanup of already-exited roots do not enter that polling path. Broker settings,
image inputs, live test selection and live scenario assertions are unchanged, so
the successful smoke and RecordSize results at `5b700f7` remain applicable to those
Kafka scenarios. The focused tests validate the changed cleanup behavior. No live
selections were rerun for these polling changes; the November 11 live results
remain attributed to `5b700f7`.

### Recorded live results

| Selection | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Connector-template smoke, both providers | 8 | 0 | 0 |
| PostgreSQL RecordSize | 20 | 0 | 0 |
| MSSQL RecordSize | 20 | 0 | 0 |

Both RecordSize reports also passed all seven required-method groups, including
exactly one shipped-image Compose persistence case per provider. These guards
check the 20 executed scenarios and are not additional tests. Both persistence
cases recorded fresh startup, managed down/up, and acknowledged size recreation:
broker/worker replacement retained cluster/topic identity, sentinel data and
source offsets, with fresh readiness validation. `ReplicaFetchMaxBytes` reached
2,000,000 on the intended Kafka image. All three reports recorded zero environment,
Connect readiness, or SQL Server startup failures or recoveries. No other CDC
selections were rerun for this follow-up.

Release builds ran on Linux/amd64 with .NET SDK 10.0.102, PowerShell 7.6.6,
Docker 29.9.2 and Compose 5.7.0. Immutable image inputs were pulled successfully
and inspected locally:

| Role | Image |
| --- | --- |
| Kafka | `apache/kafka:4.3.2@sha256:852a0d49fc4e656b022e7f23785411802c9a088c78a7c5e39f33969de9c227a7` |
| Connect | `edfialliance/ed-fi-kafka-connect@sha256:bfabcabfd28868d32ab0271b42b7d147b9566c89bfe1747716368208e8291921` |
| PostgreSQL | `postgres:18.6-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873` |
| SQL Server | `mcr.microsoft.com/mssql/server:2025-latest@sha256:4bab24f36c1ecd48e85f7d37df26e6bf301641d84c3fe652f9a0dcc947d512e1` |

Kafka and PostgreSQL came from the repository's image-resolution scripts;
Connect came from this head's `CdcQualifiedWorkerImage.json`, which differs from
the November 8 input. SQL Server matched the current repository CI variable.
`CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true`, `CDC_CONNECTOR_TEMPLATE_KEEP_CONTAINERS=false`
and `MSBUILDDISABLENODEREUSE=1` were set; `NODE_OPTIONS` was cleared. Each invocation
used a private temporary directory and a new exported results directory.

The three selections used these commands from the repository root, with the
image environment variables set to the pins above:

```powershell
$archive = '/home/brad/work/dms-root/qualification-results/DMS-1675-20261111-5b700f7'
$raw = '/tmp/dms-1675-qualification-20261111-5b700f7/ConnectorTemplateSmoke'
$project = 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj'

dotnet test $project -c Release --nologo --filter 'Category=CdcConnectorTemplateSmoke' `
    --results-directory $raw --logger 'trx;LogFileName=ConnectorTemplateSmoke.trx' `
    --logger 'console;verbosity=quiet' -- "NUnit.WorkDirectory=$raw"

./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -Suite RecordSize `
    -Configuration Release -ResultsDirectory "$archive/Postgresql-RecordSize" -PullImages
./eng/ci/Invoke-CdcQualification.ps1 -Lane Mssql -Suite RecordSize `
    -Configuration Release -ResultsDirectory "$archive/Mssql-RecordSize" -PullImages
```

Smoke's exit code and TRX were checked with `Get-CdcQualificationReport`, requiring
all eight cases to pass, and exported with `Export-CdcQualificationEvidence`.
The provider runner applied the same result/export checks plus its RecordSize
required-case guard. Final TRX counts were independently checked against all
three reports; every test outcome was `Passed`.

Retained local evidence is under
`/home/brad/work/dms-root/qualification-results/DMS-1675-20261111-5b700f7/`:
`ConnectorTemplateSmoke/qualification.json`, `Postgresql-RecordSize/qualification.json`,
and `Mssql-RecordSize/qualification.json`, with sanitized TRX and structured
attachments in those directories. `summary.json` records the tested commit/tree,
times, counts and guards; `runtime-inputs.json` records exact image identities;
`commands.ps1`, `run-all.ps1`, and `invocations.txt` retain the exact invocation
wrappers and commands. `invocations.json` records unchanged-head checks after
each selection; `pr-head.json` verifies the PR head after qualification.
`finalize.py` independently checks TRX outcomes, required-case counts, both
Compose persistence attachments, image identity, and container cleanup before
writing the summary and checksums. There are 203 files plus `SHA256SUMS`, whose
fingerprint is
`c49d4223a2d813a8960893c6c470e257215ab05041f0f8abbc9cc7580b044860`.

Private logs/TRX remain under `/tmp/dms-1675-qualification-20261111-5b700f7/`,
`/tmp/cdc-qualification-83c72a3a815f443fa129dc588755b31f/` (PostgreSQL), and
`/tmp/cdc-qualification-0caf0c419436448f81cd36b0b7df43ae/` (MSSQL). These are local
runs, not GitHub Actions results or published CI artifacts. Generated diagnostics
are not committed. All fixture containers were removed; the final container
inventory exactly matched the two pre-existing containers.

## Live qualification — 2026-11-08

Tested implementation: `09a6cb66f7c0f4473c1f348bb0214cfad1cf78c2`, plus the
runbook JMX command correction described below. No application or fixture code
changed during qualification.

Runs use Release builds from this checkout on Linux/amd64, .NET SDK 10.0.102,
PowerShell 7.6.6, Docker 29.9.2, Compose 5.7.0 and Pester 5.7.1. Telemetry
inspection uses curl 8.5.0, psql 18.6 and the CI-pinned sqlcmd (Go) 1.10.0 archive,
verified against its workflow SHA-256.

Immutable image inputs (the locally inspected image IDs match these digests):

| Role | Image |
| --- | --- |
| Kafka | `apache/kafka:4.3.2@sha256:852a0d49fc4e656b022e7f23785411802c9a088c78a7c5e39f33969de9c227a7` |
| Connect | `edfialliance/ed-fi-kafka-connect@sha256:13d9afb3ee322bae4f9cc1939b32079e99543ca27d48cb908b1cebfcdb60546d` |
| PostgreSQL | `postgres:18.6-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873` |
| SQL Server | `mcr.microsoft.com/mssql/server:2025-latest@sha256:4bab24f36c1ecd48e85f7d37df26e6bf301641d84c3fe652f9a0dcc947d512e1` |

Kafka and PostgreSQL inputs are resolved by the repository's qualification-image
scripts; Connect comes from `CdcQualifiedWorkerImage.json`. SQL Server matches
the repository's `CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE` CI variable read
for this run. Fail-fast is enabled, container retention is disabled, and
`NODE_OPTIONS` is cleared.

| Selection | Passed | Failed | Skipped | Result |
| --- | ---: | ---: | ---: | --- |
| Contract: controller unit | 4,757 | 0 | 0 | Passed |
| Contract: SchemaTools CDC CLI | 819 | 0 | 0 | Passed |
| Contract: admin runbook | 6 | 0 | 0 | Passed |
| Contract: offline integration | 577 | 0 | 0 | Passed |
| Contract: PowerShell wrappers/qualification | 952 | 0 | 0 | Passed |
| Connector-template smoke, both providers | 8 | 0 | 0 | Passed |
| Live Connect readiness recovery/cleanup, both providers | 2 | 0 | 0 | Passed |
| PostgreSQL RecordSize | 20 | 0 | 0 | Passed |
| SQL Server RecordSize | 20 | 0 | 0 | Passed |
| Kafka secured policy, after correction | 49 | 0 | 0 | Passed |
| Kafka local policy, after correction | 2 | 0 | 0 | Passed |
| Runbook command/link regression after correction | 55 | 0 | 0 | Passed |
| PostgreSQL telemetry | 2 | 0 | 0 | Passed |
| SQL Server telemetry | 2 | 0 | 0 | Passed |
| PostgreSQL message contracts | 502 | 0 | 0 | Passed |
| SQL Server message contracts | 496 | 0 | 0 | Passed |

Contract also passed the required-method guards for all ten CLI groups and all
14 wrapper cases; those checks validate coverage of the tests above and are not
additional test executions. Smoke covers rendered connector validation and
registration, classloading, Murmur2 partitioner vectors, heartbeat and committed
offset progress, retained state after restart, and both restamp scopes for each
provider. Its qualification report records zero Connect readiness or SQL Server
startup failures/recoveries.

The targeted live readiness-recovery selection also passed for both providers,
recording two deliberately injected timeouts and two injected recoveries, with
zero observed startup/readiness failures. It verifies actual Connect replacement
and cleanup, not the complete Admission lane.

Both provider Compose persistence cases retained cluster/topic identity, a public
sentinel and committed source offsets through managed down/up and acknowledged
size recreation. Evidence records replacement broker/worker containers and fresh
readiness validation. The size change raised `ReplicaFetchMaxBytes` from
1,000,000 to 2,000,000; the deployed broker image matched the shipped 4.3.2 pin.
Both complete RecordSize selections passed all 20 scenarios and their seven
required-method groups, including producer recovery without an offset reset.

The first Kafka run failed one of 49 secured tests:
`It_executes_marked_broker_retention_inspections_and_rejects_denied_authority`.
The marked `cdc-kafka-retention-inspect` snippet invoked the removed
`kafka.tools.JmxTool` class. The pinned 4.3.2 image supplies
`org.apache.kafka.tools.JmxTool`; updating the actual operator snippet fixes the
live inspection. The complete Kafka lane was rerun: 49 secured and two local
tests passed, as did both required-case reports. The existing live test is the
regression check: it failed with the old command and passed with the corrected
command. All 55 focused runbook command/link tests also passed after this change.
The original failed evidence remains in `Kafka-All`; the successful rerun is in
`Kafka-All-fixed`.

Both telemetry selections passed their required-case reports. MessageContract
selections include serialized-contract tests and live broker fixtures, including
the migrated record-size helper, replay, progress acknowledgements and provider
routing. PostgreSQL also passed all six required reference-consumer cases.

Run selections are the complete `Contract` and `Kafka` lanes and each provider's
`RecordSize`, `Telemetry`, and `MessageContract` suites through
`eng/ci/Invoke-CdcQualification.ps1`. Smoke uses the same Release integration
project with `--filter 'Category=CdcConnectorTemplateSmoke'`, a TRX logger and
an external `NUnit.WorkDirectory`; its TRX is also checked with
`Get-CdcQualificationReport` and exported with `Export-CdcQualificationEvidence`.
The extra direct readiness selection uses
`FullyQualifiedName~Given_PinnedImageFixtureConnectReadinessLiveRecovery` with
the same report/export checks. The focused SchemaTools regression selects
`Given_Cdc_runbook_commands` and `Given_Cdc_runbook_links`.
See [the qualification guide](../../docs/CDC-QUALIFICATION.md) for prerequisites
and lane semantics. Every invocation uses a new results directory.

Sanitized reports and exported evidence are retained locally under
`/home/brad/work/dms-root/qualification-results/DMS-1675-20261108/` (601 files
plus `SHA256SUMS`). The archive's `summary.json` records the tested implementation
revision, corrected runbook Git blob and all final report outcomes. Its
`SHA256SUMS` fingerprint is
`ee7fa548343cfaa67d670ed03b1f48e7e7f53f9cc94bcc5513b5e24a115efee6`.
Private TRX/logs remain in `/tmp/dms-1675-qualification-20261108/` and the
invocation-specific `/tmp/cdc-qualification-*` directories. Generated diagnostics
and raw logs are not committed. These are local results, not GitHub Actions
results or a published CI archive. Fixture-owned containers were cleaned up;
the two pre-existing containers were left unchanged.

## Image security — 2026-11-08

The [security assessment](../../docs/KAFKA-IMAGE-SECURITY.md) records the refreshed
scan and remaining remediation. The [startup and persistence guide](../../docs/KAFKA-UPGRADE.md)
describes current-image checks and the retained Compose fingerprint restriction.

No broker image change or package update was made during the assessment.
Runtime/tool/provider inputs match the earlier qualification. Functional tests
do not resolve image vulnerabilities.

Trivy 0.76.2 rescanned the exact target digest on Linux/amd64 with newly downloaded
vulnerability and Java databases, no severity filtering and no ignore file.
The result is **10 package findings across nine CVEs: 1 critical, 7 high and
2 medium**. All are Alpine packages with fixed versions available; no Java archive
findings were reported. The critical Brotli and the previously named high Expat
findings remain. The security guide records every finding, fixed versions,
read-only dependency/runtime observations and their limits. Disposition is
**remediation required**; there is no security-clearance claim, suppression or
recorded risk acceptance. No exploit testing was performed.

Local evidence: `/home/brad/work/dms-root/qualification-results/DMS-1675-20261108-upgrade-security/`.
It contains the full image scan, DB timestamps, scanner checksum list, exact image
identities, startup/package inspection and the original test reports, including
exploratory failures. The archived test scope predates removal of the historical
image lane; its reports are preserved as recorded. `SHA256SUMS` has
fingerprint
`734b35a746a4787d6ae9c4c2b2bc638f414083c7d7b19c838606d41122bed0f7`.
This is a local evidence archive, not a published CI artifact. Private TRX, console output and container diagnostics remain under
`/tmp/dms-1675-upgrade-security/`. Generated artifacts are not committed. All
fixture-owned containers and volumes were cleaned up; pre-existing containers
were left unchanged.

## References

- Dependency bundle: https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1436
- Original Kafka Dependabot proposal (closed): https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1426
- Broker Compose: eng/docker-compose/kafka-broker.compose.yml
- Deployment image authority: src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcComposeBrokerSizeDeployment.cs
- Fixture: src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.cs
- Native Kafka fixtures: CdcControllerFixtureKafka.cs and CdcControllerFixtureComposeKafka.cs in the same integration-test directory
- Qualification guide: docs/CDC-QUALIFICATION.md
