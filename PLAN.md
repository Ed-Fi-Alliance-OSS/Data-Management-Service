# DMS-1577: Stabilize nightly CDC qualification

## Objective

Deliver one focused stabilization PR using the existing qualification harness:
make failures diagnosable, correct demonstrated defects, and prove that the complete
pipeline passes repeatedly from clean environments.

Story: [DMS-1577](https://edfi.atlassian.net/browse/DMS-1577).

## Starting evidence

The [September 30 failed job](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/36690880353/job/109807639450)
passed all 42 SQL Server controller admission tests and two of three runbook setup
cases. `cdc-sqlserver-e2e-setup` retained only `NotPassed`; its underlying cause is
unresolved. Do not assume it is another instance of an earlier failure.

Recent stabilization commits on `main` provide the initial incident inventory:

| Commit | PR | Change / evidence limitation |
| --- | --- | --- |
| `5a8c5ac82` | [#1255](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1255) | Image selection, fixture compatibility, CDC observation fixes and bounded SQL startup recovery; includes both fixture and production changes. |
| `cb750b868` | [#1264](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1264) | Recovery for an additional observed SQL startup-crash signature; underlying vendor cause remains unresolved. |
| `797b9e9d6` | [#1275](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1275) | Bounded HTTP 409 restart handling; original incidents did not retain the HTTP status. |
| `cf6537dcb` | [#1282](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1282) | Image-pull retries and attempt evidence. |
| `0abbaf2c2` | [#1296](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1296) | Connect port reservation and startup evidence; the original port race was suspected, not confirmed. |
| `c20552f83` | [#1305](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1305) | E2E claims initialization order. |

Related scheduled-E2E work: `35408aaf7`, [#1277](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1277),
corrected DocumentCache authentication settings and failure reporting.

## Scope and constraints

- Reuse the existing scripts, fixtures, workflow and artifact export path.
- Preserve all required scenarios, assertions and production readiness checks.
- Add retries only for an identified transient operation, with explicit bounds and
  retained attempt evidence. Do not retry entire tests or suites to obtain a pass.
- Keep raw logs, credentials, settings and document contents out of public artifacts.
- Defer harness rewrites, new orchestration and PR-gate changes unless investigation
  establishes that they are necessary. Record separately scoped work in linked tickets.
- Keep `RelationalMappingVersion` at `v3` for this release line.

## Implementation plan

### 1. Close the runbook diagnostic gap

- [x] Keep generated diagnostics outside the repository checkout. Use a unique
  system temporary directory locally and the runner temporary directory in CI;
  update the results default, workflow invocation, fallback report and artifact
  upload path together. Publish sanitized results only as GitHub Actions artifacts
  using the existing 14-day retention. Keep raw logs private. Regression tests use
  temporary directories and clean up their generated files. Do not commit generated
  logs, reports or diagnostic snapshots.
- [x] Extend the existing runbook results and exporter to retain a small, allowlisted
  failure record: case ID, phase/operation or assertion ID, repository source location,
  process exit/status or failure category, and elapsed time/deadline where applicable.
- [x] Capture failures during setup, test execution and teardown, including Pester
  setup/block failures. Record the failing phase before invoking work so a timeout
  can still be attributed.
- [x] Preserve the original failure if cleanup, diagnostic collection or export also
  fails; retain secondary failures separately.
- [x] Add focused regression tests that verify the final exported artifact contains
  useful failure information and excludes sensitive values. Cover an assertion
  failure, setup/process failure or timeout, and a secondary cleanup/export failure.

Start with `eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1`,
`eng/docker-compose/tests/cdc-runbook-snippets.ps1`,
`eng/ci/Invoke-CdcQualification.ps1`, and `eng/ci/cdc-qualification.psm1`.
Use the existing Pester test suites; do not introduce a reporting framework.

### 2. Review the matrix and fix demonstrated problems

- [x] Confirm the matrix from `Get-CdcQualificationMatrix.ps1` and
  `Get-CdcQualificationProviderSuite`. Update the stale 15-job documentation to
  match the current 17-job matrix below.
- [x] Briefly trace shared setup, execution and teardown across every lane, including
  passing lanes. Check image/configuration consistency, dependency readiness,
  initialization order, shared resources/concurrency, partial setup cleanup and
  cancellation. Note relevant differences between local, PR and nightly execution.
- [ ] Check nested timeout budgets. In particular, investigate the runbook's
  600-second internal wait inside a wrapper with a 600-second process timeout.
  This is a review target, not a confirmed cause of the September 30 failure.
- [ ] Run SQL Server Admission with the improved diagnostics, then run the full
  matrix on the branch through the existing workflow. Do not stop at the previously
  failing lane.
- [ ] For each failure, record the evidence and classify it as a product defect,
  test defect, infrastructure failure or unresolved cause. Distinguish confirmed
  causes from suspected mitigations, including in the historical inventory.
- [ ] Fix demonstrated defects at their shared source where appropriate. Add targeted
  regression coverage that would fail without the correction, including ordering,
  partial setup and cleanup when those are involved. Run affected existing checks.

Current live qualification matrix:

| Lane | Required suites | Jobs |
| --- | --- | ---: |
| Kafka | All | 1 |
| PostgreSQL | Admission, Lifecycle, Recovery, RecordSize, Telemetry, History, MessageContract, ApiE2E | 8 |
| SQL Server | Admission, Lifecycle, Recovery, RecordSize, Telemetry, History, MessageContract, ApiE2E | 8 |

The Contract lane remains part of normal validation in addition to these 17 live
jobs. Use the existing runner for focused checks and the existing
`nightly-cdc-qualification.yml` manual dispatch with `lane=All`, `suite=All` for
complete hosted runs. Confirm the selected matrix against the candidate revision.

### 3. Demonstrate stability and prepare the PR evidence

- [ ] Freeze the candidate revision after fixes and relevant checks pass.
- [ ] Obtain three consecutive complete 17-job matrix passes on that exact revision,
  each starting from clean, disposable environments. Keep dependency inputs fixed
  and record the actual image identities and runner/tool versions.
- [ ] A failed complete run resets the consecutive-pass count. A code change starts
  validation of a new candidate. Manually rerunning failed jobs does not count.
- [ ] Inspect retained results for required case counts, missing/skipped scenarios,
  environment failures and cleanup outcomes; do not rely solely on a green workflow
  badge. Record bounded transient recoveries even when the final result passes.
- [ ] Attach a concise PR evidence summary: qualification matrix, incident
  classifications, corrections and regression checks, three run links with commit
  and attempt numbers, and remaining limitations or linked follow-up tickets.

## Completion criteria

The PR is ready when failures retain actionable sanitized diagnostics, the shared
paths have been reviewed, demonstrated in-scope defects have corrections and
regression coverage, documentation matches the actual matrix, and three consecutive
clean complete runs pass on the same candidate revision without manual job reruns.
Do not claim an unexplained historical failure is resolved merely because a replay
passes; document that limitation explicitly.

## Implementation status

- Diagnostic metadata now includes case operations, phase, assertion source/line,
  process outcome, timeout budget and elapsed time. Pester setup/block failures and
  independent export failures are retained. Raw messages remain private.
- Local results default outside the checkout; checkout-local destinations are
  rejected, including a checkout-local TMPDIR for private logs. Both CI workflows
  use the runner temporary directory.
- The shared SQL Server lifecycle adapter called the removed `Invoke-FixtureSql`
  helper. It now calls `Invoke-CdcFixtureSql`; targeted execution tests verify the
  actual adapter, database forwarding and session-option ordering. This is a test
  harness defect introduced by the fixture extraction, not evidence of the cause
  of the September 30 Admission failure.
- Broad Pester validation passed 835 tests. Additional export-failure and interrupted
  case tests passed with the focused diagnostic suite (7 tests). Contract .NET
  validation and hosted qualification remain in progress.
- Shared-path review covered the pinned-image provider fixture, separate Kafka
  policy fixture, History servers/retirement, runbook ownership and governed
  teardown, and API E2E setup/test/cleanup/export deadlines. Historical incidents
  remain classified with their original evidence limitations; the PR evidence
  summary is the running validation record.
- The 600-second wrapper/600-second internal wait remains a diagnostic lead. No
  timeout increase or additional retry has been introduced without live evidence.
- Three consecutive complete hosted passes have **not** yet been obtained. The
  candidate is not frozen and the PR is not ready for merge.
