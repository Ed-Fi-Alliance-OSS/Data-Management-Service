# Plan: repair nightly CDC qualification fixtures

## Objective and evidence

Complete the smallest fixture repairs needed to qualify the DMS-1323 CDC scenarios on the nightly environment, then update [PR #1255](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1255) with verified results.

Work in `fix-nightly-cdc-qualified-image`. Its existing commit `4cbd9313c` selects the Connect image from the checked-in qualification record. The [verification run](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/34981285451) accepted that image in all 13 jobs, but finished with 153 passing and 106 failing cases. Five fixture defects explain 104 failures; two Compose persistence setup failures still have unconfirmed CI causes.

Story: `reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md`.

## Recommended scope

Keep PostgreSQL 16 and make the test query use the same version-compatible JSON field lookup as production. `invalidation_reason` contributes to DMS source-history safety checks as well as test observations; preserve that check when the server exposes it.

Reuse only the identified DMS-1324-port fixture repairs. Also explicitly publish the fixture's broker port to address the reproduced Compose 2.38.2 inheritance failure. Keep production readiness requirements, image qualification, and bounded waits intact.

Treat the SQL Server restart failure separately: its unchanged focused replay passed, but its hosted root cause remains unconfirmed. Include bounded, sanitized telemetry validation locations in existing evidence and investigate further if the final matrix reproduces it. Do not call the failure resolved solely because a focused rerun passed.

## 1. Reuse the targeted DMS-1324-port repairs

- [x] Inspect and extract the relevant changes from the commits below. Apply individual file hunks so unrelated port implementation, documentation, and test changes stay out of this repair.

| Repair | Source commit | Target and intended behavior |
| --- | --- | --- |
| Kafka fixture lock acquisition | `84202c431` | In `CdcKafkaPolicyFixture.cs`, inspect the offset store through the existing adapter and observation helper while startup holds its session. Preserve the production startup lock and avoid acquiring that same session again. |
| Cleanup Compose inputs | `7a0050da8` | In `CdcControllerFixtureComposeKafka.cs`, copy the shipped `kafka-cdc.yml` and neighboring `kafka-broker.yml` into the fixture directory, preserving their relative references. |
| Cleanup failure reporting | `7a0050da8` | In `CdcRetirementOffsetStoreTests.cs`, assert the child process result with captured output before asserting that handoff occurred. |
| Cleanup PowerShell bridge | `84202c431` | In `eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1`, read the optional `d` hashtable entry by index so a missing entry is valid under strict mode. |
| Live telemetry request | `a6bd6bbaf` | In `CdcConnectorTelemetryQualificationTests.cs`, build the observation request from the actual rendered connector template using the existing fixture helper. |
| Backlog evidence freshness | `79fe4a756` | In `CdcManagedLifecycleTests.cs`, preserve the shared fixture's `MaximumObservationAge` when overriding wait timing. Keep the scenario's bounded wait. |
| Backlog polling assertion | `7a0050da8` | In the same lifecycle tests, require repeated polling for persistent backlog without assuming four polls fit into every run. Retain the four-poll requirement for the scenario that releases work on the fourth poll. |

These changes repair fixture inputs, locking, and timing assumptions while preserving the production admission, authorization, and continuity checks.

## 2. Make the PostgreSQL fixture query version-compatible

- [x] In `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcProviderAdmissionFixture.cs`, update the query in `RecordingPositions.ObserveSourceHistoryAsync`:

```sql
SELECT active
   AND wal_status IN ('reserved', 'extended')
   AND (to_jsonb(slot)->>'invalidation_reason') IS NULL
FROM pg_replication_slots AS slot
WHERE database = current_database()
```

- [x] Add a short source comment explaining that PostgreSQL 16 lacks `invalidation_reason` and that JSON field access follows the production provider's compatibility approach.

Production DMS also uses this field, but already reads it through `to_jsonb(slot)` in `CdcPostgresqlHeartbeatPublicationProvider.cs`. PostgreSQL 16 yields NULL for the absent JSON field; newer versions expose a reported invalidation reason. The fixture continues requiring an active slot and retained WAL on either version.

Keep the existing nightly PostgreSQL 16 image pin. A PostgreSQL upgrade or permanent additional version matrix is a separate support-policy decision. This repair requires no production SQL change or relational mapping version bump.

## 3. Validate locally before pushing

- [x] Format the changed C# files with `dotnet csharpier format`, build the affected project, and run `git diff --check`.
- [x] Run the existing Contract lane, including the PowerShell wrapper and image-selection regression checks:

```powershell
./eng/ci/Invoke-CdcQualification.ps1 -Lane Contract -Configuration Release -ResultsDirectory <new-results-directory>
```

- [x] Configure live tests with the nightly image digests and the checked-in qualified Connect image. Verify Docker is running. Use the CDC fixtures' isolated stacks and remove only containers created by these runs.
- [x] Run the Kafka lane to cover the shared secured setup and authorization behavior.
- [x] Run focused live cases covering cleanup with both reservation variants, telemetry, both backlog variants, and Compose persistence on both PostgreSQL and SQL Server.
- [x] Run a representative PostgreSQL admission case against the nightly PostgreSQL 16 digest and a recorded PostgreSQL 18 digest. Use existing tests to exercise the real catalog query; record exact image digests and results. Version 18 success alone does not establish version 16 compatibility.

Use PowerShell 7.6 for local Contract validation so empty environment variables remain distinct from absent variables, as required by the retained-input tests. The default local PowerShell 7.4 runtime does not preserve that distinction.

Use existing scenario tests as regression coverage. Add tests only if an uncovered behavior requires them. Retain bounded waits and fail-fast environment checks; skipped live tests do not count as qualification.

## 4. Commit, push, and verify the full hosted qualification

- [x] Commit and push the reused fixture repairs and PostgreSQL compatibility change with clear provenance (`3118016b1`).
- [ ] Review and commit the demonstrated Compose repair and minimal diagnostic follow-up after their local validation passes, then push the existing fix branch.
- [ ] Dispatch the nightly workflow with `lane=All` and `suite=All` on the final pushed commit. Confirm the run's head SHA matches that commit. The earlier run on `3118016b1` is diagnostic evidence, not qualification of the follow-up.
- [ ] Wait for all 13 live jobs: Kafka plus Admission, History, Lifecycle, RecordSize, Recovery, and Telemetry for each provider.
- [ ] Verify every selected test executes and passes, with zero failures, skips, or environment-unavailable outcomes. Check the published image records and scenario evidence as well as job conclusions.

### If Compose persistence still fails

The original two CI setup failures are unresolved. The complete SQL Server scenario passed locally; local PostgreSQL instead reached the confirmed catalog-query failure. Neither observation proves the original hosted failures were transient.

- [x] If either recurs, identify the failing setup stage from available evidence and reproduce with the same image pins.
- [x] If sanitized artifacts remain insufficient, add only the minimum safe failure stage, exception type, and source-location evidence needed to diagnose the failure. Keep raw logs, connection strings, and credentials out of published artifacts.
- [ ] Fix the demonstrated cause, run the affected scenario, then rerun the full hosted qualification on the final commit. Avoid speculative production changes or timeout increases.

## 5. Finish the PR

- [ ] Rewrite PR #1255's title and description around the final image-selection and fixture repairs. Include reused commit provenance, PostgreSQL version coverage, the final qualification run, head SHA, and actual test totals.
- [ ] Confirm the required PR checks pass. PR Contract checks alone do not establish live qualification.
- [ ] Report any remaining failure explicitly; call the repair fully qualified only after the complete hosted matrix passes on the final code commit.

## Completion criteria

The fix branch contains the targeted repairs, the existing Contract checks pass, focused PostgreSQL 16 and 18 validation passes, and all 13 hosted live jobs pass on the final commit. The PR contains accurate, reviewable validation evidence.

## Local validation completed

- Release build: zero warnings and errors; Csharpier and `git diff --check` passed.
- Contract on PowerShell 7.6.6: 4,829 passed, zero failures or skips. An initial PowerShell 7.4 run failed 15 empty-environment-variable tests; the full rerun on 7.6 passed.
- Kafka: 49 passed, zero failures or skips.
- Focused provider scenarios: 12 passed across PostgreSQL 16 and SQL Server 2025, covering Compose persistence, both backlog variants, both cleanup reservation variants, and fresh admission for each provider.
- Telemetry: both providers passed.
- Additional admission run: PostgreSQL 18.4 and SQL Server passed. These selections overlap and are not a full hosted qualification.
- Local evidence: `/tmp/dms-cdc-fix-validation/summary.json`; raw diagnostic logs remain local. Hosted results will be recorded in PR #1255.

## Hosted follow-up: Compose 2.38.2 port inheritance

Run `35002198135` on `3118016b1` completed with 251 passing and eight failing tests, zero skips or environment failures. Kafka, both Admission, History, Recovery, and Telemetry suites passed. Both providers retained Compose persistence and both cleanup wrapper failures (six cases total). SQL Server additionally failed restart and interrupted record-size resume with unavailable telemetry after the connector returned to RUNNING.

The runner uses Compose 2.38.2; the initial local qualification used Compose 5.5.1. Replaying the three PostgreSQL cases locally with the verified Compose 2.38.2 release binary reproduced all three failures. The generated broker container was healthy but had no published host port. Comparing the same fixture configuration under both versions showed that 2.38.2 loses the shipped `ports: !override` through the fixture's extra `extends` layer; 5.5.1 retains it. The shipped `kafka-cdc.yml` resolves correctly directly under 2.38.2.

The fixture now explicitly declares its isolated `127.0.0.1:<reserved-port>:19092` mapping. Configuration readback confirms exactly one correct mapping under both Compose versions. This preserves the shipped broker image, storage, startup, and production checks.

A bounded Compose startup attachment now preserves the failure stage, exception type, and repository code locations without exception messages or Docker output. A regression check verifies redaction and resource cleanup. Existing admission evidence also records bounded telemetry validation source locations to diagnose the two additional failures if they recur.

The follow-up Release build and all 93 offline fixture tests passed after the diagnostic changes. All six corrected persistence and cleanup cases passed with Compose 2.38.2 on both providers, with zero skips. Fixture resources were removed and the seven pre-existing containers remained. The unchanged SQL Server restart and interrupted record-size resume replays each passed, with zero skips. Passing focused replays do not establish the root cause of either hosted telemetry failure.

## Remaining execution order

1. Completed: corrected Compose 2.38.2 replay, six passing cases and resource cleanup verified.
2. Completed: diagnostic diff reviewed, formatted, rebuilt, and all 93 offline tests passed after the last diagnostic change.
3. Completed: both additional SQL Server cases passed unchanged focused replays. Their hosted root causes remain unconfirmed; preserve readiness guards and investigate any recurrence using the added evidence.
4. Commit and push the validated follow-up, then dispatch and verify all 13 nightly jobs on that SHA. If the restart failure recurs, use the new evidence to establish its cause and repeat qualification after any correction.
5. Verify current PR checks, update PR #1255 with the final results, and audit the completion criteria above. Keep incomplete or failed checks explicit.

## Follow-up: isolated hosted retirement failure

Full run `35011184375` on `d1366badb` passed Kafka, both History and Telemetry suites, and PostgreSQL Recovery at this checkpoint. PostgreSQL Lifecycle passed 14 cases, including both cleanup wrapper variants, but failed `It_rejects_actual_pinned_worker_offsets_until_their_exact_keys_are_tombstoned`. The remaining suites were still running.

The retirement failure did not reproduce in one focused PostgreSQL replay or five additional isolated replays. Its hosted cause remains unconfirmed. The existing artifact recorded the expected initial rejection of retained offsets, but omitted the later retirement result and individual journal steps. Add only coded retirement outcomes and step timestamps to the sanitized evidence; retain all cleanup checks and assertions. The diagnostic follow-up built with zero warnings/errors and passed all 95 selected cases (93 offline tests and both providers' live retirement cases), with zero skips. Attachment readback confirmed the intended safe fields.

Next, rerun the hosted PostgreSQL Lifecycle suite with this evidence while collecting the remaining full-run results. Investigate a recurrence using the result codes and journal steps. A targeted pass is insufficient for completion: the full 13-job qualification and current PR checks must still pass on the final code commit.

## Full-run review and next qualification

Run `35011184375` completed with 257 passes, two failures, and zero skips. One failure was the PostgreSQL offset-retirement case above; targeted hosted run `35014984546` on `79d47a2ef` subsequently passed all 15 PostgreSQL Lifecycle cases. The other was a SQL Server fixture-startup environment failure before `It_rejects_an_independent_empty_or_populated_source_without_mutating_either(False)` executed. Both providers' Compose persistence and cleanup wrapper cases passed, as did the earlier SQL Server restart and record-size resume cases.

The startup failure did not include the existing startup attachment because that attachment was restricted to Compose fixtures. Apply that same bounded, sanitized attachment to every pinned-image fixture startup failure, preserving cleanup and exception handling. Do not change startup deadlines or readiness checks. This extension built with zero warnings/errors and passed 94 selected cases (93 offline plus the SQL Server source-replacement replay), with zero skips. Nine startup attachments were checked for the intended safe metadata fields.

The two isolated failures remain unexplained; passing replays do not establish their causes. Commit the validated diagnostic extension and rerun the complete 13-job matrix. Completion still requires that full run and current PR checks to pass on the final code commit, with actual results recorded in the PR.
