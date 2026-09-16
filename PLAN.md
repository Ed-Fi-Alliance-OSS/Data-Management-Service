# Plan: finish the nightly CDC qualification fixes

## Objective

Repair the demonstrated nightly CDC failures with the smallest changes that preserve the DMS-1323 readiness, authorization, source-continuity, and cleanup contracts. Validate the complete live qualification and update [PR #1255](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1255).

Worktree: `/home/brad/work/dms-root/fix-nightly-cdc-qualified-image`.

Story: [DMS-1323 bootstrap and CDC enablement](reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md).

## Decisions

- Keep the nightly PostgreSQL 16 pin. Make the fixture query compatible with the supported catalog shape.
- Reuse only the relevant DMS-1324-port changes, with source-commit provenance.
- Use the checked-in qualified Connect image record.
- Preserve finite waits, fail-fast environment checks, readiness thresholds, and live policy validation.
- Make production changes only for demonstrated defects, with regression coverage.
- Treat passing replays as validation evidence; they do not establish the cause of an earlier failure.

## 1. Retain the completed image and fixture repairs

These changes are already committed on the fix branch. Review their final diff and retain their regression coverage.

| Repair | Source / implementation | Required behavior |
| --- | --- | --- |
| Qualified Connect image selection | `4cbd9313c` | Read the checked-in `CdcQualifiedWorkerImage.json` instead of the stale repository image variable. |
| Kafka fixture session reuse | DMS-1324-port `84202c431` | Observe offsets through the session already held during startup; preserve production locking. |
| Cleanup Compose inputs | DMS-1324-port `7a0050da8` | Copy both neighboring Compose files so relative references resolve. |
| Cleanup failure reporting | DMS-1324-port `7a0050da8` | Assert the child process result with captured output before asserting handoff. |
| Optional PowerShell argument | DMS-1324-port `84202c431` | Read the optional hashtable entry with `['d']` under strict mode. |
| Telemetry request | DMS-1324-port `a6bd6bbaf` | Use the actual rendered connector template. |
| Backlog observation timing | DMS-1324-port `79fe4a756`, `7a0050da8` | Preserve observation freshness and bounded polling; require four polls only when the scenario releases work on the fourth poll. |
| Compose 2.38.2 broker port | `d1366badb` | Explicitly publish the isolated fixture port as `127.0.0.1:<reserved-port>:19092`. |

The targeted port repairs and PostgreSQL query change were committed together in `3118016b1`. Avoid importing unrelated changes from the unmerged port branch.

The Compose failure was reproduced with the hosted runner's Compose 2.38.2: the fixture's additional `extends` layer lost the inherited `ports: !override`. All six affected persistence and cleanup cases passed locally on both providers after the explicit mapping.

## 2. Retain PostgreSQL version compatibility

The fixture's `RecordingPositions.ObserveSourceHistoryAsync` now follows the production provider's JSON field lookup:

```sql
SELECT active
   AND wal_status IN ('reserved', 'extended')
   AND (to_jsonb(slot)->>'invalidation_reason') IS NULL
FROM pg_replication_slots AS slot
WHERE database = current_database()
```

`invalidation_reason` is used by DMS source-history safety checks as well as testing. Production already accesses it through `to_jsonb(slot)`. PostgreSQL 16 lacks the column, so the absent JSON field yields NULL; versions exposing the field still reject a reported invalidation reason. The active-slot and retained-WAL checks remain mandatory.

- [x] Apply the compatible fixture query and explanatory comment.
- [x] Validate representative admission against the pinned PostgreSQL 16 image and a recorded PostgreSQL 18.4 image.
- [x] Confirm the final diff preserves production SQL and the PostgreSQL 16 pin.

This repair requires no PostgreSQL upgrade, permanent extra version matrix, or relational mapping version change.

## 3. Correct Kafka's successful-create readback classification

### Demonstrated defect

In `CdcKafkaAdminAdapter.CreateMissingTopicAsync`, a successful creation followed by an absent metadata readback previously became `ValidationFailed`. That classification disables the bounded metadata wait already implemented in `CdcKafkaProvisioning.PrepareAsync`.

A regression expectation changed to `Unavailable` reproduces the mismatch: one failure for successful creation, with the timeout and conflict cases passing. This establishes the adapter defect; it does not yet prove that this caused the earlier hosted Kafka failures.

### Smallest correction

- [x] Change only the default classification for successful creation followed by absent metadata from `ValidationFailed` to `Unavailable`.
- [x] Preserve explicit failures from the creation attempt, including authentication, invalid input, timeout, and conflict.
- [x] Require independent live readback with valid policy before recording completion. Creation acknowledgement alone remains insufficient.
- [x] Use the existing invocation deadline and metadata polling interval; issue no duplicate create.
- [x] Verify adapter and provisioning tests cover delayed metadata becoming valid, persistent absence reaching the deadline, and contradictory/unavailable inspection rejecting as before. All 348 selected tests passed after the correction.

Files: `CdcKafkaAdminAdapter.cs`, `CdcKafkaAdminAdapterTests.cs`, and existing `CdcKafkaProvisioningTests.cs` coverage in the CDC backend projects.

## 4. Resolve the restart telemetry failure without weakening readiness

### Confirmed evidence

On `ea4e0b2a2`, [SQL Server Lifecycle run 35032641677](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35032641677) passed 14 cases and failed the intact-running restart case. The first consumed current-lag sample after restart was exactly `-1`; observed Kafka topic settings were valid. The telemetry parser rejected the sample, and the invalidated observation prevented lifecycle completion.

The value establishes an unusable lag sample. It does not independently prove that the connector had not yet produced its first measurement. Keep that distinction in the diagnosis.

### Investigation and implementation sequence

- [x] Trace the post-resume observation, recovery classification, and lifecycle journal boundaries using the failed attachment and existing unit tests.
- [x] Reproduce the sequence deterministically: authorized restart, unusable first lag observation, then fresh valid evidence.
- [x] Determine whether initial observation can safely wait within the existing lifecycle deadline after the already-authorized resume.
- [x] If that correction is supported by the lifecycle contract, implement it narrowly: re-observe without repeating the mutation; keep publication readiness false until a complete valid pass.
- [x] Preserve immediate rejection for changed worker/source identity, contradictory policy, authorization failure, or lost continuity. Do not infer continuity from later healthy evidence.
- [x] Preserve rejection of unknown evidence after known catch-up evidence. A persistent unusable sample must fail within the original deadline.
- [x] Keep negative lag invalid. Do not turn `-1` into zero, accept task RUNNING as readiness, increase timeouts, or retry the whole scenario until it passes.

Regression cases must cover first unusable sample followed by valid evidence, persistent unusable evidence, unknown evidence after known catch-up, and safety-relevant changes during any permitted wait.

The implementation now classifies a correctly attributed, finite current-lag value of exactly `-1` separately after both worker/task identity brackets and the age check complete. It still returns unavailable telemetry and invalidates the pass. Any other invalidation supersedes this classification. Established validation permits another observation only when all other prerequisites pass and the caller supplies the managed resume operation ID. The lifecycle controller uses that allowance only before resume completion, within the original deadline, without repeating the mutation. Every iteration re-reads the complete evidence. The focused telemetry/lifecycle selection passed all 532 cases, and the complete controller unit suite passed all 3,588 cases. Live validation remains required.

## 5. Preserve the original admission-preparation failure

The full run [35040814242](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35040814242) on `f89639afdd86e7184b9bdd6a99d2cee8a708bd0c` passed 258 live tests and failed one SQL Server Admission case, with zero skips. `It_repeats_fresh_readiness_after_an_interrupted_offline_wait("queue")` failed during fixture preparation before its scenario ran. Its published evidence identifies the `Assert.Fail` wrapper at `CdcProviderAdmissionFixture.StartAsync`, which discarded the original exception; the underlying cause remains unknown.

- Preserve that exception as the inner exception of the assertion wrapper, without changing failure or cleanup behavior.
- Publish the original exception type and DMS code locations plus at most eight numeric SQL Server error codes. Never publish messages, commands, connection strings, full logs, or machine paths.
- Verify wrapped and unwrapped startup failures retain cleanup and redaction, then exercise the failed case with matching pinned images.
- Investigate any reproduced underlying failure before choosing a behavioral fix. A passing replay does not explain the hosted failure.
- Require a new complete hosted qualification on the final pushed commit; the previous 258/259 result is not qualification.

Validation: the complete Contract lane passed **4,868 tests**, zero failures/skips (3,588 controller, 715 CLI, 101 offline, 464 wrappers). The single failed SQL Server `queue` case passed locally with the pinned images; all seven pre-existing containers were preserved and fixture resources were removed. The hosted preparation failure remains unexplained.

## 6. Validate the final changes locally

Use the CDC qualification fixtures' isolated stacks and nightly image digests. Preserve unrelated Docker containers and remove only resources created by these runs. Run local builds and tests sequentially.

- [x] Format changed C# files with Csharpier, build Release, and run `git diff --check`.
- [x] Run the affected Kafka adapter/provisioning and lifecycle unit tests, including the new failure-sequence coverage.
- [x] Run the complete Contract lane with PowerShell 7.6: **4,867 passed**, zero failures or skips (3,588 controller unit, 715 CLI unit, 100 offline fixture, 464 wrapper checks).

```powershell
./eng/ci/Invoke-CdcQualification.ps1 -Lane Contract -Configuration Release -ResultsDirectory <new-results-directory>
```

- [x] Run focused live restart, guarded-start, and interrupted-resume cases on PostgreSQL and SQL Server for any changed lifecycle behavior.
- [x] Exercise Kafka provisioning and preserve coverage for policy rejection, delayed metadata, and authorization.
- [x] Retain the existing PostgreSQL 16/18 compatibility and Compose 2.38.2 evidence; repeat affected cases if subsequent edits change those paths.
- [x] Verify diagnostic attachments remain bounded and sanitized. Keep raw logs, credentials, connection strings, and full metric bodies out of published evidence.

PowerShell 7.4 failed empty-environment-variable tests locally; 7.6 preserves the distinction required by those tests. A skipped live test or environment failure does not count as qualification.

## 7. Commit, push, and qualify the final commit

- [x] Review the final diff against the story contracts. Record the port-fix provenance and explain each production correction.
- [ ] Commit and push to `fix-nightly-cdc-qualified-image`.
- [ ] Dispatch `nightly-cdc-qualification.yml` with `lane=All` and `suite=All` on the final pushed commit.
- [ ] Verify the workflow head SHA and all published image records.
- [ ] Require all 13 live jobs: Kafka, plus Admission, History, Lifecycle, RecordSize, Recovery, and Telemetry for each provider.
- [ ] Verify all 259 currently selected live tests pass with zero failures, skips, or environment-unavailable outcomes. Explain any legitimate selection-count change.
- [ ] Investigate any remaining failure from its artifacts. After any code correction, qualify the complete matrix again on the new final commit.
- [ ] Confirm current required PR checks: `license/cla`, `DMS CI Gate`, and `Config CI Gate`.
- [ ] Update and read back PR #1255's title/body with the final scope, provenance, exact commit, run links, image/version coverage, and actual test totals.

## Validation checkpoint

Recorded evidence as of this plan update. Results from the final hosted run and required PR checks will be recorded in PR #1255 after the commit is pushed:

| Evidence | Result | Limit |
| --- | --- | --- |
| [Full run 35040814242](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35040814242) on `f89639afd` | 258 passed, one failed, zero skips; 12/13 jobs passed | SQL Server Admission fixture preparation failure; original exception missing from published evidence. Hosted Contract passed all 4,867 tests. |
| Local Contract on PowerShell 7.6.6, production follow-up | 4,867 passed, zero failures/skips | Reviewed source hashes match the tested code. |
| Focused live production follow-up | Eight passed, zero failures/skips | Four per provider: guarded start, intact restart, and record-size interruptions at boundaries 1 and 5. Eight evidence attachments and resource cleanup verified; no negative lag sample occurred in this live selection. |
| Latest hosted Contract on `ea4e0b2a2` | 4,837 passed, zero failures/skips | Does not cover live qualification. |
| Latest local diagnostic selection | 108 passed, including eight live cases across both providers | Focused selection; final 100 offline cases also passed after the last diagnostic edit. |
| [Full run 35018130919](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35018130919) on `a57ab747f` | 256 passed, three failed, zero skips/environment failures | Previous complete matrix remains unsuccessful. |
| [PostgreSQL Lifecycle 35032631052](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35032631052) on `ea4e0b2a2` | 15 passed, zero failures/skips/environment failures | Does not explain the earlier PostgreSQL telemetry failure. |
| SQL Server Lifecycle 35032641677 on `ea4e0b2a2` | 14 passed, one failed, zero skips/environment failures | Confirms the unusable `-1` post-restart lag sample. |
| Kafka classification regression before correction | One failed, two passed | Expected failure demonstrates the classification mismatch. |

Earlier isolated retirement and fixture-startup failures also lack confirmed causes despite passing replays. Keep them explicit in the investigation record and inspect any recurrence. Local detailed evidence is under `/tmp/dms-cdc-fix-validation/`.

## Done when

The final branch contains the targeted repairs, the Contract lane and required PR checks pass, and all 13 live jobs pass on the final pushed commit with complete image and scenario evidence. PR #1255 accurately reports the results and any residual uncertainty. Full qualification is currently incomplete.
