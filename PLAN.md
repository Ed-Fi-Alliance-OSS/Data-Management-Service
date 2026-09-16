# Plan: finish the nightly CDC qualification fixes

## Objective

Repair the demonstrated nightly CDC failures with the smallest changes that preserve the DMS-1323 readiness, authorization, source-continuity, and cleanup contracts. Validate the complete live qualification and update [PR #1255](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1255).

Worktree: `/home/brad/work/dms-root/fix-nightly-cdc-qualified-image`.

Story: [DMS-1323 bootstrap and CDC enablement](reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md).

## Execution sequence

The simplest responsible path is to finish the targeted repairs already on this branch,
validate the demonstrated production corrections, and resolve the remaining startup
failure from evidence. The sections below retain implementation details and prior results;
historical passes do not qualify the current source.

1. **Retain the targeted DMS-1324-port repairs and qualified image selection.** Review
   the listed source commits and the final diff. Import only the relevant fixture and
   wrapper changes; keep their regression coverage.
2. **Keep PostgreSQL 16 and use the production-compatible catalog lookup in the fixture.**
   Read `invalidation_reason` through `to_jsonb(slot)` while retaining the active-slot
   and WAL-retention checks. This field also protects DMS source-history validation;
   production already uses the compatible lookup. Retain PostgreSQL 16/18 validation
   evidence. Keep production provider SQL, image pins, and mapping version `v3` unchanged.
3. **Finish validation of the demonstrated CDC corrections.** Cover delayed Kafka metadata,
   the narrowly scoped initial-lag wait, and the SQL Server idle commit boundary. Preserve
   authorization, identity, continuity, containment, freshness, finite deadlines, and
   single-attempt mutations. For the idle boundary, require a strictly greater commit to
   cross a barrier; the same commit is insufficient. Run the new native idle-offset live
   control and the existing intact-restart scenario with the corrected binaries.
4. **Mitigate the captured SQL Server LSA startup failure under the approved fixture policy.**
   The user authorized one bounded fixture-only recreation for the exact captured failure.
   Implement and validate section 7's amendment below. Preserve evidence for every failed
   attempt and distinguish this CI mitigation from a repair to SQL Server's internal defect.
5. **Validate the complete final source.** Format changed C#, review the diff, and run local
   tests sequentially: core CDC tests, the full Contract lane, and affected live cases.
   Verify image records, actual test selection, sanitized evidence, and cleanup of owned
   resources. Expected selections are 437 core tests and 4,966 Contract checks; investigate
   any discrepancy rather than treating missing or skipped tests as success.
6. **Commit, push, and qualify the exact final SHA.** Run hosted Contract and all 13 live
   jobs, expecting all 261 selected live tests to pass with zero failures, skips, or
   environment-unavailable outcomes. Confirm required PR gates, update PR #1255 with
   provenance and actual results, and read back the published description. Do not merge.

**Done means:** the reviewed changes are committed and pushed, the full qualification and
required checks pass on that commit, and the PR records the evidence and any remaining
limitations. Until then, report qualification as incomplete.

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

## 6. Wait for SQL Server Agent startup before fixture preparation

The full run [35046746577](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35046746577) on `4139dd3ac` passed 258 tests and failed one SQL Server Admission case during fixture preparation. The preserved original exception identifies SQL error 50000 from `MssqlDatabaseProvisioner.CheckCdcProjectionPrerequisites`: the production guard rejected unrelated pending server configuration.

The fixture previously accepted `SELECT 1` as readiness. In two of three fresh starts of the pinned SQL Server 2025 image, local sampling observed `show advanced options` temporarily differ between configured and active values before SQL Server Agent registered its session. This reproduces a startup race capable of triggering the exact guard. The hosted attachment does not identify which configuration row was pending.

- Wait for an Agent session from the current SQL Server start, using `msdb.dbo.syssessions` and `sys.dm_os_sys_info`.
- Require settled server configuration, with the same documented default-memory exceptions as the production guard.
- Use `sqlcmd -b` so a failed SQL readiness predicate produces a nonzero exit code.
- Retain the existing 90-second startup deadline, two-second poll interval, cancellation, cleanup, and fail-fast behavior. The readiness probe is read-only.
- Keep the production prerequisite guard unchanged. Do not apply unrelated settings, retry provisioning, or rerun whole scenarios until they pass.
- Validate Agent-disabled and pending-setting rejection, successful Agent startup, and no configuration mutation on isolated real SQL Server instances.
- Run the affected offline fixture checks and both previously failing Admission cases. Then qualify the full final commit again.

Validation: the three real SQL Server controls passed, including rejection without configuration mutation. All **49 affected offline fixture tests** and **both previously failing live Admission cases** passed with zero failures/skips. Fixture cleanup preserved all seven pre-existing containers. The complete final-source Contract lane and hosted matrix remain required.

Microsoft documents that each Agent start creates a [row in `msdb.dbo.syssessions`](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/dbo-syssessions-transact-sql?view=sql-server-ver17). The [configuration catalog documentation](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-configurations-transact-sql?view=sql-server-ver17) describes configured/active values and the default-memory exceptions.

## 7. Retain the last SQL readiness probe result

The full run [35052233394](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35052233394) on `8b63dfca9` finished with **258 passes, one failure, zero skips, and one environment failure**. All 36 SQL Server Admission tests passed. The SQL Server Lifecycle provider-unavailable case failed before its scenario: the fixture's SQL readiness probe did not succeed within the existing 90-second deadline. The attachment identifies that deadline but does not identify the failed readiness condition.

A bounded local probe completed 29 fresh starts of the same pinned SQL image successfully, with a maximum readiness time of 14.139 seconds. One additional attempt was interrupted and excluded from those results; its container was inspected and removed. All seven pre-existing containers were preserved. These successful starts do not explain the hosted timeout.

- Publish the number of completed SQL readiness probes, the last exit code, and one fixed state: `NotObserved`, `Ready`, `AgentStarting`, `ConfigurationPending`, or `SqlCommandFailed`.
- Classify the existing fixed SQL messages inside the fixture. Never publish raw SQL output, commands, credentials, or container logs.
- Keep the SQL predicate, 90-second deadline, two-second polling, cancellation, cleanup, and production guard unchanged.
- Verify failed-probe evidence survives cancellation and resource cleanup while raw output remains private.
- Exercise the failed live case with the diagnostic change. A passing replay alone does not establish the cause.
- Require complete Contract and hosted qualification on the final pushed commit again.

Validation: all **53 affected fixture tests** passed, including four new checks for cancellation, cleanup, and raw-output redaction. The single failed live Lifecycle case passed with the new diagnostics; the timeout did not reproduce. All seven pre-existing containers were preserved. Complete final-source Contract (expected **4,872 tests** before subsequent additions) and hosted qualification remain required.

Follow-up: run 35057484986 passed SQL Server Lifecycle but failed two SQL Server RecordSize cases and one SQL Server Admission case during startup. Both last probes had exit code 1 and state `SqlCommandFailed` (18, 41, and 39 completed probes); neither identified Agent startup or pending configuration as the last failure. The job log does not retain the underlying command error. The same RecordSize job also failed one scenario after an initial `-1` lag sample, covered by section 9.

- [ ] Investigate the three SQL command failures. Existing logs do not retain their causes. A fixture follow-up now captures an allowlisted container status, numeric exit code, and out-of-memory flag before cleanup, with a separate five-second diagnostic bound. SQL command categories distinguish stopped containers, login failure, connection timeout/failure, and missing sqlcmd. Validate cancellation, cleanup, and redaction; no raw inspect output, state error text, or configuration is published.
- [ ] Choose any behavioral correction from demonstrated evidence. Preserve the original startup deadline and production guards; successful replays alone do not resolve the cause.

Run [35062017924](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35062017924)
on `850cff957` finished with 257 passes, two failures, zero skips, and one environment
failure; all 16 TRX reports and 13 image records were verified. The SQL Admission
cancelled-lag case failed before its scenario. After 20 probes, the new diagnostics recorded
`ContainerNotRunning`, container status `exited`, exit code 1, and `OomKilled=false`.
The published job log does not retain the SQL process error. This establishes container
exit as the immediate failure, without identifying why SQL Server exited.

- [x] Before cleanup of an exited SQL container, collect bounded startup-log classifications
  and numeric error codes under the existing diagnostic time budget. Keep raw log text,
  paths, credentials, and configuration out of published evidence; preserve the original
  exception and cleanup even if log collection fails.
- [x] Verify this diagnostic path, then replay the failed case once. Do not automatically
  restart the container, extend startup time, or attribute the exit to memory without evidence.

The log follow-up passed all 141 offline fixture checks, including 11 new classification,
redaction, bounds, failed-read, cancellation, and cleanup cases. Inspection and log collection
share the existing five-second diagnostic token. The provider startup commands, readiness
predicate, retry loop, PostgreSQL TCP probe, and cleanup implementation remain unchanged.
The failed Admission case passed once. Exact selection, image pins, source hashes, and cleanup
were verified against the pre-run inventory: seven containers, six networks, and 290 existing
volumes. SQL startup succeeded in this replay, so it does not explain the hosted process exit.
The complete local Contract lane on the preceding `e164bdb19` commit passed all 4,915 checks;
the new log diagnostics require final-source Contract and full hosted qualification again.

Follow-up on `1cb8c1517`: the full run finished with **256 passes, three failures,
zero skips and two environment failures**; all 16 TRX reports and 13 image records were
verified. Both SQL RecordSize startup failures now retain a fatal log marker, fatal reason
code 6 and errno 2, with `OomKilled=false` and no memory/mapping marker. These codes alone
do not identify the underlying cause. Investigate the process failure before choosing an
environment correction; do not infer OOM, add automatic restarts, or extend readiness time.

The isolated hosted diagnostic [35077597913](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35077597913)
completed 40 fresh SQL starts successfully with the original image on x86_64. It did not
include the surrounding fixture stack and does not explain the process exits. The next
control runs the actual SQL RecordSize suite with bounded, encrypted process-log capture
on a temporary diagnostic branch: [35079333656](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35079333656).
The private decryption key stays local. Keep this instrumentation out of the fix PR.

### Approved amendment: bounded LSA startup recreation

The user approved this amendment after diagnostic run
[35092738931](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35092738931)
finished with 78 passes and one startup failure. The complete private log identifies
AppLoader LSA initialization status `0xc0070102` and termination status `0xC0000001`.
The underlying reason LSA stalled remains unknown. Microsoft's
[matching report](https://github.com/microsoft/mssql-rs/issues/387) and
[merged mitigation](https://github.com/microsoft/mssql-rs/pull/386) support bounded recreation
as a CI mitigation, with their different macOS VM environment explicitly acknowledged.

This amendment supersedes the earlier prohibition on automatic recreation and changes
only the fresh SQL fixture startup budget. All other deadlines and constraints remain.

- [x] Permit at most two starts (one recreation), before provisioning and before any test scenario.
- [x] Require exited status, exit code 1, no OOM kill, complete readable logs, the exact LSA
  and AppLoader termination markers, fatal reason 6, errno 2, and no contradictory SQL,
  memory, mapping, or signal evidence. Unknown or incomplete evidence must fail.
- [x] Keep a 90-second limit per startup attempt, a five-second bound for inspection and
  evidence retention, a ten-second removal bound, and one shared 200-second overall cap.
  Caller cancellation and an expired total deadline must prevent recreation.
- [x] Retain a sanitized failed-attempt attachment before removing the owned unprovisioned
  provider container and its anonymous volumes. A failed removal or evidence write aborts
  recovery. KeepContainers disables recreation. Final startup failure preserves cleanup.
- [x] Require the unchanged Agent-session and settled-configuration readiness predicate
  on the replacement. Preserve image pins, PostgreSQL behavior, production SQL, mapping
  version, single-attempt DMS mutations, and one execution of provisioning/scenario work.
- [x] Validate exact-match recovery, rejection cases, second-failure cap, cancellation,
  deadlines, cleanup, redaction, and the actual fixture wiring. Run a controlled real
  Docker recreation and the affected live Lifecycle scenario with the pinned images.
- [ ] Run complete local/hosted Contract and all live qualification jobs on the final
  pushed SHA. Update expected test totals for added regression coverage and record
  successful mitigated startups distinctly from an ordinary first-attempt startup.

Local validation passed **4,966 Contract checks** (3,611 controller, 715 CLI, 176 offline
fixture, 464 wrappers), including all **34 new policy cases**, and **437 core CDC tests**.
The controlled real Docker recreation and both affected independent-source Lifecycle
variants passed with the original image pins. The controlled recreation passed again
with the final compiled classifier; the compiled policy also accepts the actual complete
private failure capture. Sanitized attachments and exact resource cleanup were verified
(seven pre-existing containers, six networks, and 290 volumes preserved). Formatting and
diff checks passed. Hosted qualification of the final pushed SHA remains required; record
its run links, 261 live results, Contract totals, and PR checks in PR #1255.

Completion for this amended item means the supported failure class is captured and the
bounded mitigation is validated. It does not require or claim an upstream SQL Server fix.

## 8. Require TCP readiness during PostgreSQL fixture startup

The qualification run [35057484986](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35057484986) on `c4a82bf83` has a PostgreSQL Recovery preparation failure: `It_detects_failed_task_recovery_on_the_same_worker_without_certifying_the_gap` failed on its first host database connection, before the scenario ran. The attachment preserves an `NpgsqlException`, but not the underlying network message; the precise hosted cause remains unconfirmed.

The fixture's socket-based `pg_isready` can accept the image's temporary initialization server before TCP is available. Controlled starts of both pinned PostgreSQL 16 and recorded PostgreSQL 18.4 images reproduced this defect: the socket probe succeeded while the TCP probe and host PostgreSQL handshake failed. After initialization, TCP readiness, the host handshake, and an authenticated SQL query succeeded.

- [x] Change only the fixture readiness probe to `pg_isready -h 127.0.0.1`, with an explanatory comment.
- [x] Preserve the existing 90-second deadline, two-second interval, cancellation, cleanup, image pins, and production provider SQL.
- [x] Demonstrate rejection of the temporary server and acceptance of the final server on PostgreSQL 16 and 18.4; remove the isolated control resources.
- [x] Format the change and run the affected offline fixture selection: **53 passed**, zero failures/skips.
- [x] Run the failed Recovery case once on PostgreSQL 16 and once on PostgreSQL 18.4, sequentially. Both selected cases passed; exact test selection and preservation of all seven pre-existing containers were verified.
- [ ] Review and commit this change, then run the full qualification on the new final SHA. A passing replay does not prove the cause of the hosted failure.

## 9. Apply the initial-lag wait to the acknowledged RecordSize resume

Run 35057484986 also failed five PostgreSQL RecordSize scenarios after resume: both confirmed-increase variants and interrupted boundaries 1, 2, and 3 with uncertain responses. Each attachment records the first consumed post-resume current-lag value as exactly `-1`, followed by rejection. A sixth RecordSize failure occurred before its scenario on the first host database connection, matching the separate startup investigation in section 8.

The RecordSize operation has a separate resume loop. It does not use the narrow `AwaitingInitialCurrentLag` allowance already implemented for managed lifecycle, so it rejects that initial observation immediately.

- [x] Reproduce this path with deterministic tests before changing production behavior.
- [x] Allow complete fresh observations only during the acknowledged rollout's first-lag wait, within the original invocation deadline and while every other prerequisite remains valid.
- [x] Preserve the pending rollout gate, renewed confirmation requirements, full policy/identity/continuity checks, and one execution of each mutation.
- [x] Stop allowing initial-lag waits after usable evidence arrives. Unknown lag during later catch-up or after resume completion must still reject.
- [x] Verify success after initial unusable samples, persistent unavailability, cancellation, worker/provider/policy/continuity changes, and unknown evidence after usable evidence or completion on both providers.
- [ ] Run the affected live RecordSize cases on both providers, then repeat complete Contract and hosted qualification on the final commit.

Validation checkpoint: all 18 new RecordSize regression cases passed on both providers. The full 224-case selection passed 222 and exposed two existing cancellation tests racing their 200 ms test deadline. Cancellation cases now use a separate test budget; the 200 ms timeout cases and all production deadlines remain unchanged. The complete 224-case selection subsequently passed with zero failures/skips. Terminal-loss tests require an additional containment stop and incident persistence, while rollout mutations remain single-attempt.

### Follow-up: diagnose malformed offset evidence during intact restart

Run [35062017924](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35062017924)
on `850cff957` passed 14 SQL Server Lifecycle cases and failed intact restart. After an initial
post-resume `-1` lag sample, a complete fresh pass classified the committed offset as malformed,
persisted a source-history incident, and stopped the connector. The reported missing typed
commit LSN, change LSN, and event serial do not prove those three fields were absent from the
HTTP response: the parser clears all typed positions on several different rejection paths.

- [x] Capture bounded field kinds and validity classifications from the same offset response
  consumed by the production adapter, plus its parsed offset state. Do not make another HTTP
  request or publish offset values, partition names, raw responses, or exception messages.
- [x] Verify byte preservation, cancellation, read failures, response-size enforcement,
  diagnostic bounds, and redaction. Keep production parsing and continuity guards unchanged.
- [x] Replay the failed case once after the diagnostic change. Inspect any recurrence before
  selecting a behavioral correction; a passing replay does not explain the hosted failure.
- [ ] Complete the full final-source Contract lane and hosted matrix after the investigation.

The ten focused RecordSize cases passed on `850cff957` (four PostgreSQL and six SQL Server),
with exact selection, source hashes, pins, and cleanup verified. They did not consume an
initial `-1` sample. Local and hosted Contract passed all 4,901 cases on that source. The
diagnostic follow-up passed all 130 offline fixture tests, including 14 new response/state
checks. The single failed SQL restart case passed once with those diagnostics: all six
consumed responses were parsed as streaming offsets, no initial `-1` lag occurred, and
cleanup preserved all seven pre-existing containers. This validates the diagnostic path
without explaining the hosted failure. The hosted live matrix on `850cff957` has already
failed and is not final qualification.

### Follow-up: recognize the pinned SQL Server idle-stream offset

Run [35069035378](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35069035378)
on `1cb8c1517` again failed the intact SQL Server restart case. The consumed-response diagnostics
now show a valid commit LSN, literal `"NULL"` change LSN, and a nonnegative integer serial other
than 1. The parser returned `Malformed`, which latched source-history loss and stopped the
connector. The exact serial value was not retained. The completed matrix passed 256 tests and failed three; the other two failures were SQL process exits before RecordSize scenarios.

The [pinned Debezium 3.6.0 source](https://github.com/debezium/debezium/blob/v3.6.0.Final/debezium-connector-sqlserver/src/main/java/io/debezium/connector/sqlserver/SqlServerStreamingChangeEventSource.java)
emits a commit-only offset with change LSN `"NULL"` and serial 0 after a streaming iteration
finds no captured rows, before publishing a heartbeat. The current DMS parser rejects this
supported form. Debezium orders the null in-transaction LSN before real change positions and
restarts by reading the commit inclusively; do not invent an after-image position or treat
this as proof that a barrier in the same transaction was crossed.

- [x] Reproduce the exact idle offset with the pinned live image and a deterministic parser regression.
- [x] Support only the demonstrated commit-only form through the shared provider-position,
  continuity, and observation contracts. Preserve the actual marker in evidence, source identity,
  retained-range checks, snapshot/null-offset rejection, terminal incidents, and fresh lag requirements.
- [x] Verify conservative ordering: a greater commit can cross an earlier barrier, but a commit-only
  boundary must not certify a barrier in the same commit. Keep missing/invalid commit LSNs,
  unsupported serial values, malformed change LSNs, and absent offsets rejected.
- [x] Cover parser, provider comparison, continuity, observation/incident serialization, and
  the live intact-restart path. Preserve all operation deadlines and single-attempt mutations.
- [ ] Rerun complete local/hosted Contract and the full hosted matrix on the corrected final commit;
  include any new test selection counts explicitly. The `1cb8c1517` matrix is unsuccessful.

The native-offset control reproduced the exact form on the pinned worker: valid commit LSN,
change LSN `"NULL"`, serial 0, no snapshot marker, and a matching source partition. The old
parser rejected it as `Malformed`. The control pauses capture in its own fixture database,
restarts the connector with DMS processing offline, and restores capture before validating
continuity. Three earlier controls timed out without observing the marker; they are not
positive evidence. The active one-second heartbeat query explained why a no-capture interval
was needed. Original binary hashes, all four image pins, and cleanup of the owned resources
were verified; seven pre-existing containers, six networks and 290 volumes were preserved.
The shared correction is implemented and under validation; final-source Contract and live
qualification remain required.

The first corrected live run accepted the native idle marker as `Streaming`, and the
separate intact-restart case passed with fresh readiness. The native control then timed
out on an incorrect healthy-continuity expectation: its deliberate capture-job stop left
failed job history, which the unchanged provider mapper rejects after restart. The revised
control requires offset acceptance while keeping continuity unknown, pre-start/publication
ineligible, and source-loss incidents absent for that job-health failure. It preserves the
job history and production guard. Healthy idle-position semantics remain covered by the
core tests; ordinary intact restart is covered independently. Resource cleanup was verified.

The revised native control passed, consuming one real idle marker as `Streaming` and
preserving the capture-job rejection. The separate intact-restart pass reached fresh
readiness with unchanged production sources. Both runs retained all four image pins;
the revised test's source hashes and exact cleanup inventory were verified.

## 10. Validate the final changes locally

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
- [x] After the PostgreSQL TCP readiness, RecordSize, offset, and SQL exit diagnostic changes, rerun the complete Contract lane on the corrected source: **4,932 passed** (3,611 controller, 715 CLI, 142 offline, 464 wrappers), zero failures/skips. Four reports, all three TRX files, individual results, and nine unchanged source hashes were independently verified. Repeat affected validation if subsequent edits change this source; hosted Contract must still pass on the final pushed commit.

- [x] Run the complete core CDC unit selection for the shared position/continuity changes: **437 passed**, including 35 new idle-boundary cases, zero failures/skips. Verify the final DMS CI unit job also passes after the correction.

PowerShell 7.4 failed empty-environment-variable tests locally; 7.6 preserves the distinction required by those tests. A skipped live test or environment failure does not count as qualification.

## 11. Commit, push, and qualify the final commit

### Follow-up: isolate behavioral tests from short timeout budgets

Hosted Contract on `1bd936f44` passed 4,930 checks and failed two: stale stop readback
and retained-history loss while enable waits for its barrier. Local Contract passed all
4,932 on the same commit. The hosted artifact omits assertion messages, so the exact
failed assertions are not known.

The tests impose 50 ms and 300 ms call budgets on unrelated observation and persistence
work. A single-CPU stop-readback replay passed all 12 variants. Controlled provider delays
then demonstrated the timing dependency: 80 ms caused all 12 readback cases to fail before
their intended containment assertions, and 350 ms caused both enable cases to fail before
the expected incident was recorded. Matched corrections passed all 14 under those delays.

- [x] Make the readback test end polling through an explicit unavailable response after
  the invalid readback, with the normal fixture budget and all containment assertions intact.
- [x] Keep the enable-loss test's normal fixture budget; retain its barrier, incident,
  stop/readback ordering, and closed-publication assertions.
- [x] Remove all temporary diagnostic delays and analyzer suppressions. Change no
  production deadline or behavior; retain the separate timeout-specific tests.
- [x] Run both affected test classes normally: 166 controller and 86 CLI cases passed,
  zero failures/skips, after removing diagnostic delays.
- [x] Complete final-source local Contract: **4,932 passed**, zero failures/skips; all
  four reports, three TRX files, individual results, and eleven source hashes verified.
- [ ] Commit the test corrections and require hosted Contract and full live qualification
  on the new final commit.

### Bounded SQL diagnostic follow-up

The actual RecordSize diagnostic run [35079333656](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35079333656)
passed all 17 cases. All 17 privately captured SQL logs were complete and contained no
fatal marker. Together with the earlier 40 isolated starts, this still does not establish
the cause of the prior process exits.

A single broader diagnostic run [35085220051](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35085220051)
covers the five affected SQL fixture suites (79 expected tests), using production source
`1bd936f44`, unchanged image pins and deadlines, and bounded encrypted process-log capture.
It runs on a temporary branch outside this PR. Inspect any recurrence before making an
environment or startup change; keep raw logs private. This diagnostic is not final qualification.

### Final delivery

- [x] Review the final diff against the story contracts. Record the port-fix provenance and explain each production correction.
- [ ] Commit and push to `fix-nightly-cdc-qualified-image`.
- [ ] Dispatch `nightly-cdc-qualification.yml` with `lane=All` and `suite=All` on the final pushed commit.
- [ ] Verify the workflow head SHA and all published image records.
- [ ] Require all 13 live jobs: Kafka, plus Admission, History, Lifecycle, RecordSize, Recovery, and Telemetry for each provider.
- [ ] Verify all 261 selected live tests pass with zero failures, skips, or environment-unavailable outcomes. Two additional SQL Server Lifecycle cases cover a real idle commit boundary and controlled container recreation; all original 259 cases remain selected.
- [ ] Investigate any remaining failure from its artifacts. After any code correction, qualify the complete matrix again on the new final commit.
- [ ] Confirm current required PR checks: `license/cla`, `DMS CI Gate`, and `Config CI Gate`.
- [ ] Update and read back PR #1255's title/body with the final scope, provenance, exact commit, run links, image/version coverage, and actual test totals.

## Validation checkpoint

Recorded evidence as of this plan update. Results from the final hosted run and required PR checks will be recorded in PR #1255 after the commit is pushed:

| Evidence | Result | Limit |
| --- | --- | --- |
| Approved LSA recreation amendment, local validation | 4,966 Contract and 437 core CDC checks passed; controlled recreation and both affected Lifecycle variants passed | Final hosted Contract, all 261 live cases, and required PR gates must pass on the pushed SHA. |
| [Full run 35086104754](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35086104754) on `47e159f71` | All 260 live tests and all 13 jobs passed; local/hosted Contract each passed 4,932; required PR gates passed | Baseline before the approved recreation amendment; does not qualify the amended source. |
| Local Contract before the PostgreSQL TCP change, `c4a82bf83` | 4,872 passed, zero failures/skips | Must be rerun after the latest fixture edit. |
| PostgreSQL temporary-server controls, versions 16 and 18.4 | Both reproduced socket success before TCP readiness; both accepted final TCP startup | Demonstrates the fixture defect; the exact hosted network error was not preserved. |
| Offline fixture selection after the PostgreSQL TCP change | 53 passed, zero failures/skips | Focused live Recovery checks and final full qualification remain required. |
| Full run 35057484986 on `c4a82bf83` | 248 passed, 11 failed, zero skips, five environment failures; 9/13 jobs passed | All 16 TRX reports and 13 image records verified. Six initial-lag failures, two PostgreSQL startup failures, three SQL startup failures. |
| [Full run 35052233394](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35052233394) on `8b63dfca9` | 258 passed, one failed, zero skips; 12/13 jobs passed | SQL readiness timeout before a Lifecycle scenario; exact failed condition unknown. Local and hosted Contract passed all 4,868 tests. DMS and Config CI gates passed; CLA status is missing. |
| [Full run 35046746577](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35046746577) on `4139dd3ac` | 258 passed, one failed, zero skips; 12/13 jobs passed | Startup guard rejected pending server configuration. Hosted Contract passed 4,868 tests and all required PR checks passed. The intact-restart case exercised an initial `-1` sample and passed after fresh valid evidence. |
| [Full run 35040814242](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35040814242) on `f89639afd` | 258 passed, one failed, zero skips; 12/13 jobs passed | SQL Server Admission fixture preparation failure; original exception missing from published evidence. Hosted Contract passed all 4,867 tests. |
| Local Contract on PowerShell 7.6.6, production follow-up | 4,867 passed, zero failures/skips | Reviewed source hashes match the tested code. |
| Focused live production follow-up | Eight passed, zero failures/skips | Four per provider: guarded start, intact restart, and record-size interruptions at boundaries 1 and 5. Eight evidence attachments and resource cleanup verified; no negative lag sample occurred in this live selection. |
| Hosted Contract on `ea4e0b2a2` | 4,837 passed, zero failures/skips | Does not cover live qualification. |
| Earlier local diagnostic selection | 108 passed, including eight live cases across both providers | Focused selection; final 100 offline cases also passed after the last diagnostic edit. |
| [Full run 35018130919](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35018130919) on `a57ab747f` | 256 passed, three failed, zero skips/environment failures | Previous complete matrix remains unsuccessful. |
| [PostgreSQL Lifecycle 35032631052](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/35032631052) on `ea4e0b2a2` | 15 passed, zero failures/skips/environment failures | Does not explain the earlier PostgreSQL telemetry failure. |
| SQL Server Lifecycle 35032641677 on `ea4e0b2a2` | 14 passed, one failed, zero skips/environment failures | Confirms the unusable `-1` post-restart lag sample. |
| Kafka classification regression before correction | One failed, two passed | Expected failure demonstrates the classification mismatch. |

Earlier isolated retirement and fixture-startup failures also lack confirmed causes despite passing replays. Keep them explicit in the investigation record and inspect any recurrence. Local detailed evidence is under `/tmp/dms-cdc-fix-validation/`.

## Done when

The final branch contains the targeted repairs, the Contract lane and required PR checks pass, and all 13 live jobs pass on the final pushed commit with complete image and scenario evidence. PR #1255 accurately reports the results and any residual uncertainty. Record final hosted results and any remaining limitations against the pushed SHA in PR #1255.
