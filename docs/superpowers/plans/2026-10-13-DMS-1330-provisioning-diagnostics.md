# DMS-1330 Provisioning Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make generated-DDL provisioning timeouts in unfiltered MSSQL integration runs actionable while preserving provisioning policy, provider failures, cleanup, and CI behavior/timing.

**Architecture:** Add observations to the existing single process-wide admission gate. Report only admitted generated-DDL command timeouts through best-effort `Console.Error`, and make optional timing output unable to mask errors or interrupt cleanup. Preserve public helper APIs and reuse existing machinery/provider regressions.

**Tech Stack:** .NET 10, C#, Microsoft.Data.SqlClient 6.1.4, NUnit 4.2.2, NUnit3TestAdapter 4.6.0, FluentAssertions, PowerShell, existing SQL Server integration infrastructure.

**Spec:** [Approved specification](../specs/2026-10-13-DMS-1330-provisioning-diagnostics-design.md), approved and recorded in `9e19f32cf`. [Original story](https://edfi.atlassian.net/browse/DMS-1330); [supporting reconciliation](../../../.plans/ref/DMS-1505-pre-spec-review.md).

**Status:** Written for review on branch `DMS-1330` at `9e19f32cf`. No implementation, build, or tests executed while writing this plan. Implementation requires plan review and an execution-method selection.

**Local context:** An unrelated `.config/dotnet-tools.json` edit (CSharpier 1.2.5 -> 1.3.0) appeared during planning. Preserve that local change separately; it is not a DMS-1330 deliverable or part of the plan commit.

## Global Constraints

- Follow `AGENTS.md`: .NET 10 style; non-nullable variables; `is null`/`is not null`; System.Text.Json for any .NET JSON; NUnit `Given_` fixtures, `Setup`, and `It_` tests; normalize diagnostic strings.
- Keep unset/blank admission unlimited and positive Int32 digit parsing, lazy first-use capture including failure capture, and invalid-setting failure before database creation.
- Keep the 300-second fallback, explicit timeout argument meanings, public signatures, existing snapshot/backup timeout distinctions, and separate creation/lifecycle budgets.
- Emit only for `SqlException.Number == -2` from admitted generated-DDL batch command execution. Use **suspected provisioning contention**; retain the original exception type/stack and primary-first cleanup aggregation.
- Count admitted operations, including the failing operation and excluding queued entrants, even with unlimited admission; sample at batch start and timeout. These are neither executing-SQL counts nor per-server/peak/server-wide measurements.
- Preserve transactionality, ownership/isolation, cache/hash keys, gate boundaries, lifecycle coordination, CSV header/columns/phases/outcomes, and fail/skip semantics.
- Preserve four CI shards, four fixture workers, explicit default CI limit 2 plus existing dispatch override, backup-restore, image/resources/readiness, artifacts and reader. Timing acceptance: no repeatable slowdown beyond observed baseline variation; no invented percentage tolerance.
- No extra gate, timeout environment setting, automatic host detection, cross-process throttle, enlarged lifecycle lock, retries, product/CMS/PostgreSQL/schema changes, or new dependencies.
- New ordinary backend fixtures/methods have exactly one effective MSSQL shard; explicit probes have none. No process-global environment/console mutation in parallel tests, and no timer races.
- Work in the current repository and branch as requested. No worktree/branch changes while preparing this plan. Do not start implementation or local tests from this planning turn.

## Review Focus

1. Other admissions finish before the timeout: report separate start/timeout samples, without claiming a peak or sustained overlap (Task 2).
2. Both CSV output and warning/error output fail: retain primary/cleanup evidence and continue disposal (Tasks 2-3).
3. The effective limit is already 1: explain the limitation and suggest external/server investigation, rather than recommending a meaningless reduction (Task 2).
4. Blank fixture labels, control characters, and provider-message secrets: use DDL identity fallback, one-line normalized context, and omit raw SQL/credentials/provider text from the added message (Task 2).
5. A queued entrant and repeated lease disposal: exclude the waiter from counts and release/decrement exactly once (Task 1).

---

## File Map and Execution Preparation

All paths are repository-relative. Task file lists below give exact paths. For commands only, define these PowerShell variables once:

```powershell
$backendTests = 'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj'
$apiTests = 'src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj'
$cliTests = 'src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration.csproj'
```

The gate file owns admission/counting; the diagnostics file owns failure classification, observation data, formatting and best-effort writer access; the timing-recorder file owns the existing CSV capability. The database helper retains orchestration/transaction ownership. Extract only the touched recorder from the large database file; keep its namespace, name, hash helper and callers unchanged. Do not consolidate other active hash implementations or defaults.

Before execution, read the spec and repository instructions, confirm the approved code baseline has not drifted, and verify Docker/SQL Server readiness without exposing the admin connection string. Use the AGENTS.md SQL Server 2025 setup with `ConnectionStrings__MssqlAdmin`; these are real-database backend/API tests, not Docker-stack E2E tests. The backend assembly's `DatabaseSetupFixture` needs the real configured server even when filtering to tests of pure internal helpers. Environment/setup failures and unexpected skips do not establish green verification.

Recover comparable pre-change CI timing artifacts before altering code, or arrange a controlled baseline later against `9e19f32cf`; retain engine image/build and runner context. Do not provision a new benchmark system or change CI image pins. Missing emulated-host/CI evidence remains an acceptance dependency, not permission to replace AC1/AC2 with helper tests.

### Task 1: Observable Admission with Existing Policy

**Files:**

- Create: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlGeneratedDdlProvisioningGate.cs`
- Modify: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlGeneratedDdlTestDatabase.cs` — lazy gate field, acquisition in `CreateProvisionedAsync`, old private gate/parser/lease removal.
- Test: `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlGeneratedDdlProvisioningGateTests.cs`

**Interfaces:** Internal types in `EdFi.DataManagementService.Backend.Tests.Integration.Common`:

```csharp
internal sealed record MssqlGeneratedDdlProvisioningSnapshot(
    bool IsAdmitted, int MaximumConcurrency, int AdmittedCount, TimeSpan AdmissionWaitElapsed);
// static property NotAdmitted returns (false, 0, 0, TimeSpan.Zero).

// internal sealed class MssqlGeneratedDdlProvisioningGate
// Constructor: MssqlGeneratedDdlProvisioningGate(string configuredValue)
// Properties: int MaximumConcurrency; int AdmittedCount
// Methods:
// static Lazy<MssqlGeneratedDdlProvisioningGate> CreateLazy(Func<string> readSetting)
// ValueTask<MssqlGeneratedDdlProvisioningLease> AcquireAsync()

// internal sealed class MssqlGeneratedDdlProvisioningLease : IAsyncDisposable
// Methods: MssqlGeneratedDdlProvisioningSnapshot CaptureSnapshot(); ValueTask DisposeAsync()
```

`MaximumConcurrency == 0` is an internal representation of unlimited admission, not an accepted setting value. A lease samples its owning gate and retains its own measured admission wait. The runtime uses one `CreateLazy` reader for `MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY`, coalescing absent environment input to `""`. No environment reader is called per lease. Consumers in Task 3 use `CaptureSnapshot`.

- [ ] **Step 1: Write failing `Given_MssqlGeneratedDdlProvisioningGate` tests, tagged only with `MssqlCiShards.Shard4`.** Arrange/act in `Setup` or scenario-specific fixtures; use instance-local gates. Write the following named assertions:

| Test | Assertions/code intent |
| --- | --- |
| `It_preserves_the_existing_configuration_parser` | `new Gate("").MaximumConcurrency.Should().Be(0)`; blank whitespace -> 0; `"1"` -> 1; `"0002"` -> 2; `"2147483647"` accepted. Parameterized `"0"`, `"-1"`, `"+2"`, `" 2"`, `"2 "`, `"x"`, `"2147483648"` throw `InvalidOperationException` with the existing setting-name/positive-integer message. `Gate` abbreviates the exact type above in this table. |
| `It_captures_configuration_once_including_initialization_failure` | Supplied reader call count is 1 after two `.Value` accesses, despite changing its local return value from `"2"` to `"1"`; same gate instance and limit 2. Repeat with invalid `"0"`: both accesses throw and reader call count remains 1. |
| `It_excludes_queued_entrants_and_admits_them_after_release` | Acquire two leases at limit 2; start third `AcquireAsync().AsTask()` while holding both: `gate.AdmittedCount.Should().Be(2); waiting.IsCompleted.Should().BeFalse();`. Release one, await third, and assert count still 2 and `CaptureSnapshot().IsAdmitted` true. No delay is needed: permits remain held until the assertion. |
| `It_counts_unlimited_operations_and_disposes_each_lease_once` | Hold four leases on an unlimited gate: count 4. Dispose one twice: count 3. Dispose the rest: count 0. Unlimited admission wait is `TimeSpan.Zero`. |
| `It_releases_admission_when_the_owned_operation_throws` | Throw a sentinel inside `await using` at limit 1; assert the same exception, count 0, and that a subsequent acquire completes. Drain waiters/release held leases in test cleanup. |

- [ ] **Step 2: Run the RED check.** `dotnet test $backendTests --filter 'FullyQualifiedName~Given_MssqlGeneratedDdlProvisioningGate'`. Expect missing new-type/member compile errors initially or failing new assertions after minimal declarations; preserve that evidence before filling in behavior.
- [ ] **Step 3: Implement the interfaces and replace the old private semaphore machinery.** Preserve parser/capture semantics. Measure only semaphore admission wait; increment after admission, decrement before releasing a permit, and guard disposal with `Interlocked.Exchange`. Retain one optional semaphore, no additional admission boundary, and no queue deadline. Keep `CreateProvisionedAsync`'s `await using` inside its existing `try`, through create/apply/reset discovery and before outer cleanup. Remove the superseded private parser/acquire/lease only after their callers have been replaced.
- [ ] **Step 4: Format the Task 1 files.** Run `dotnet csharpier format <file>` for each of the three listed files; inspect that formatting changed no unrelated files.
- [ ] **Step 5: Run the GREEN check from Step 2.** Expect all selected tests passing with a configured server and no unexpected skips.
- [ ] **Step 6: Review the Task 1 diff.** Confirm unchanged public signatures, default/explicit budgets, and admission boundaries; the old private machinery has no remaining callers before its removal.
- [ ] **Step 7: Commit only Task 1 files.** `git commit -m "test: observe MSSQL generated DDL provisioning admission"` after staging the three exact files. Record the test result and commit.

### Task 2: Actionable, Nonfatal Diagnostic Output

**Files:**

- Create: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlGeneratedDdlProvisioningDiagnostics.cs`
- Create: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlProvisioningTimingRecorder.cs` — relocate and narrowly extend the existing recorder.
- Modify: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlGeneratedDdlTestDatabase.cs` — remove the relocated recorder and its now-unused imports.
- Test: `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlGeneratedDdlProvisioningDiagnosticsTests.cs`
- Test: `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlProvisioningTimingRecorderTests.cs`

**Interfaces:** Consume Task 1 snapshots and the existing `MssqlProvisioningTimingContext` (do not change its constructor).

```csharp
internal sealed record MssqlGeneratedDdlTimeoutDiagnostic(
    MssqlProvisioningTimingContext Context, string DatabaseName,
    int BatchOrdinal, int BatchCount, int CommandTimeoutSeconds,
    TimeSpan BatchElapsed, TimeSpan AdmissionWaitElapsed,
    int MaximumConcurrency, int AdmittedAtBatchStart, int AdmittedAtTimeout);

// On internal static MssqlGeneratedDdlProvisioningDiagnostics:
// bool IsProvisioningCommandTimeout(Exception exception, bool isAdmitted)
// string Format(MssqlGeneratedDdlTimeoutDiagnostic diagnostic)
// void TryWrite(TextWriter writer, Func<string> format)

// Existing MssqlProvisioningTimingRecorder.Record signature stays unchanged.
// Add an internal entry point with explicit path/writer for deterministic tests:
// void RecordToPath(string timingsPath, TextWriter warningWriter,
//     string outcome, TimeSpan duration, string databaseName, int commandTimeoutSeconds,
//     string fixtureSignature, string generatedDdlHash, string phase, string leaseStrategy,
//     string callerMemberName, string callerFilePath, int callerLineNumber,
//     bool isDiagnostic = false, string detail = "", int? batchOrdinal = null,
//     int? batchCount = null, string batchHash = "")
```

`Record` supplies the current optional environment path and `Console.Error`, then delegates to `RecordToPath`. Preserve its existing SHA-256 helper, worker/shard resolution, synchronization and CSV escaping. Capture the writer only for output; do not own/dispose caller-supplied writers.

- [ ] **Step 1: Write failing diagnostics tests in `Given_MssqlGeneratedDdlProvisioningDiagnostics`, shard 4.** Copy the small private synthetic-SqlException pattern from `MssqlDatabaseLifecycleCoordinatorTests.cs`; do not refactor every existing synthetic factory.

| Test | Assertions/code intent |
| --- | --- |
| `It_classifies_only_an_admitted_SqlClient_command_timeout` | Number -2 with `isAdmitted: true` -> true; -2 with false, SQL 1205/547/50000, `TimeoutException`, `OperationCanceledException`, and non-SQL exceptions -> false. Change provider message text without changing number and assert identical classification. |
| `It_reports_separate_counts_and_an_actionable_unlimited_remedy` | Format fixture `"family"`, database `"dmsfp_probe"`, batch 2/5, budget 300, elapsed 301.25 seconds, admission wait 17 seconds, limit 0, start count 3, timeout count 1. Assert the exact field strings below and the sampling/evidence qualification. |
| `It_formats_numbers_invariantly` | With NUnit `[SetCulture("fr-FR")]`, assert `elapsedSeconds=301.250` and `admissionWaitSeconds=17.000`, not decimal-comma values. NUnit restores the test culture; do not change process-global settings. |
| `It_explains_an_already_effective_limit_of_one` | Limit 1: output contains `"Already limited to 1 in this process"` and investigation of other processes/server waits; does not instruct a further reduction. Limit 2 does give the process-restart setting remedy. |
| `It_normalizes_context_and_falls_back_to_ddl_identity` | Blank signature uses supplied DDL hash. CR/LF/tab/NUL in contextual labels become spaces; one output line. Caller-path/provider-message sentinels containing `Password=secret` and raw-SQL text are not copied into the added message. |
| `It_keeps_the_primary_failure_when_formatting_or_writing_fails` | A supplied formatter throws, or supplied writer throws: `TryWrite` does not throw. From a real sentinel-throwing test method, rethrow via the existing lifecycle aggregator and assert the same primary object/throw site; with a cleanup sentinel, `InnerExceptions.Should().Equal(primary, cleanup)`. |

Pin these observable fields without reproducing the whole implementation:

```csharp
message.Should().StartWith("MSSQL generated-DDL provisioning timeout: suspected provisioning contention.");
message.Should().Contain("batch=2/5").And.Contain("elapsedSeconds=301.250")
    .And.Contain("commandTimeoutSeconds=300").And.Contain("admissionWaitSeconds=17.000")
    .And.Contain("admittedAtBatchStart=3").And.Contain("admittedAtTimeout=1")
    .And.Contain("limit=unlimited").And.Contain("MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY=1");
```

The wording must label process-wide admitted operations (including the failing operation; excluding queued entrants), distinguish them from executing/server-wide SQL, and say samples do not prove an engine wait. Include normalized `fixture`, `database`, `strategy` fields and restart instructions. Use `0.000` invariant seconds. Do not recommend a new timeout setting.

- [ ] **Step 2: Write failing recorder tests in `Given_MssqlProvisioningTimingRecorder`, shard 4.** `It_preserves_the_csv_contract` compares the exact existing 19-column header, writes two records and asserts one header, exact supplied numeric/phase/batch values, and CSV escaping for a comma-bearing fixture. `It_skips_a_blank_output_path` produces no output. `It_makes_path_write_and_warning_failures_nonfatal` places a regular file where a parent directory is required, calls `RecordToPath` with a child path and a supplied writer, and asserts safe warning/no throw; repeat with a throwing warning writer. `It_continues_disposal_after_timing_output_fails` executes the actual recorder in a `finally` before a signaled disposal callback and asserts that callback executes while primary/cleanup sentinels remain intact. Use private temporary paths and clean only those files/directories; no global environment changes.
- [ ] **Step 3: Run the RED check.** `dotnet test $backendTests --filter 'FullyQualifiedName~Given_MssqlGeneratedDdlProvisioningDiagnostics|FullyQualifiedName~Given_MssqlProvisioningTimingRecorder'`. Expect missing new members or failed new behavior assertions, not a server-setup failure.
- [ ] **Step 4: Implement classification, formatting, best-effort writing and recorder delegation.** Catch failures only inside diagnostic formatting/output operations; do not catch SQL/cleanup work in the writer helper. Wrap path normalization, directory/file access, CSV formatting and writer flush/disposal within the recorder's nonfatal output boundary. Use a warning such as `MSSQL fixture timing output unavailable (IOException).` without the failing path or raw exception message. A failed warning is nonfatal too. Do not swallow failures from the functional hash API used before provisioning. Keep the existing CSV schema and reader unchanged.
- [ ] **Step 5: Format the five Task 2 files.** Run `dotnet csharpier format <file>` for each listed file.
- [ ] **Step 6: Run the GREEN check from Step 3.** Expect all selected tests passing.
- [ ] **Step 7: Review the Task 2 diff.** Inspect the extracted recorder against its old body: changes are relocation, the explicit test seam, and bounded output resilience. Confirm no provider exception wrapping, dependency additions, or hash/cache cleanup.
- [ ] **Step 8: Commit only Task 2 files.** `git commit -m "test: add resilient MSSQL provisioning timeout diagnostics"` after staging the five exact files. Record the tests and commit.

### Task 3: Wire the Pipeline, Prove Failure Behavior, and Document Usage

**Files:**

- Modify: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlGeneratedDdlTestDatabase.cs` — internal provisioning overload, private apply/batch methods and narrow execution catch.
- Create/Test: `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlGeneratedDdlProvisioningPipelineTests.cs`
- Modify: `docs/RUNNING-LOCALLY.md`
- Evidence: `docs/superpowers/validation/2026-10-13-DMS-1330.md` — actual executed results or clearly identified missing evidence; no claims filled from this plan.

**Interfaces:** Consume Tasks 1-2 without renaming their types/members. Preserve all existing public and internal caller-facing overloads. The existing internal `CreateProvisionedAsync` delegates through a new internal overload:

```csharp
// Task 3 overload on MssqlGeneratedDdlTestDatabase:
internal static Task<MssqlGeneratedDdlTestDatabase> CreateProvisionedAsync(
    string generatedDdl, int commandTimeoutSeconds, MssqlProvisioningTimingContext context,
    Func<MssqlGeneratedDdlProvisioningGate> resolveProvisioningGate,
    TextWriter diagnosticWriter, string? databaseCollation = null);

// Change only private apply/batch signatures to append:
// Func<MssqlGeneratedDdlProvisioningSnapshot> captureProvisioning, TextWriter diagnosticWriter

// Add a standalone-apply writer seam on the same class:
internal Task ApplyGeneratedDdlAsync(string generatedDdl, int commandTimeoutSeconds,
    MssqlProvisioningTimingContext context, TextWriter diagnosticWriter);
// Both this overload and the existing public standalone overload use the private
// apply path with NotAdmitted; the public overload supplies Console.Error.
```

Resolve the runtime lazy gate inside the existing provisioning `try`, preserving invalid-config timing/error behavior. After acquiring the lease, pass `lease.CaptureSnapshot` and the writer into private apply/batch execution. Public standalone apply supplies `() => MssqlGeneratedDdlProvisioningSnapshot.NotAdmitted` and never acquires admission. The normal wrapper supplies `Console.Error`; tests inject local dependencies. Take the start sample just before execution; catch/report only around `await command.ExecuteNonQueryAsync()`. Stop batch elapsed before formatting, capture the timeout sample while the lease is still active, call `TryWrite`, then `throw;`. Connection opening/creation, commit, reset discovery, restore/reset retain their existing catch/cleanup boundaries.

- [ ] **Step 1: Write failing `Given_MssqlGeneratedDdlProvisioningPipeline` tests with `DatabaseIntegration`, `MssqlIntegration`, and `[Explicit]`, without any shard category.** Select this fixture by fully qualified name for the planned provider proofs. Repeated real-timeout tests belong outside normal shards to preserve AC2; they are required execution verification, not ignored acceptance work. Use supplied gate/writers; no global state replacement. Implement a local blocker arrangement using a unique, test-owned application-lock resource in `master`, held by a nonpooled separate session before invoking provisioning. Generated batch SQL explicitly executes `USE [master];` and requests the same resource/principal with `@LockOwner=N'Session'` and `@LockTimeout=-1`. Hold the blocker until the command outcome; use a finite 5-second command timeout, with an independent generous test watchdog only to detect a stuck test. Release the blocker and drain/dispose any pending operation in cleanup even if the watchdog fails. SQL lock identity is database-scoped ([Microsoft contract](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver17)); do not confuse an applock return code with a SqlException number.

| Test | Assertions/code intent |
| --- | --- |
| `It_reports_a_real_provisioning_command_timeout_and_drops_the_failed_database` | Through the new overload: SQL failure has `Number == -2`, original batch method in stack; added output has batch/strategy/budget/count fields and qualified cause; isolated gate count returns to 0. Read the safe `database` identity from the captured line and assert that exact database is absent from the server catalog. |
| `It_preserves_a_real_timeout_when_the_output_writer_throws` | A capture-then-throw writer saves the line before throwing. Assert the provider -2 failure remains primary, admission releases, and the exact failed database is dropped. |
| `It_rolls_back_standalone_ddl_without_a_provisioning_diagnostic` | Create an owned empty database, create a probe schema/table in early GO batches, then block a later batch in `master`. Call the shared internal standalone-apply path with a supplied writer. Assert SQL number -2, absent probe schema/table after rollback, and an empty diagnostic writer. Review that the public wrapper uses this same NotAdmitted path and never acquires the gate. Dispose the known owned database. |
| `It_preserves_non_timeout_sql_failure_classification` | Admitted provisioning with `THROW 50000, 'injected failure', 1;` returns SQL number 50000, no contention output, and releases the isolated gate. Use normal owned-resource cleanup rather than asserting a globally empty catalog while other fixtures run. |

No timer racing or heavyweight authoritative-DDL contention fixture is needed. The blocker is a test technique, not a claimed reproduction of CPU/compile-memory contention. Provider assertions inspect the original primary failure, including the first inner exception when real cleanup also fails; do not reject legitimate primary-first aggregation. Do not add a general phase/provider injection framework solely to test untouched connection/restore paths; review that reporting is confined to the command invocation and preserve existing phase regressions.

- [ ] **Step 2: Run the RED check.** `dotnet test $backendTests --filter 'FullyQualifiedName~Given_MssqlGeneratedDdlProvisioningPipeline'`. Expect the missing internal overload/new output assertions to fail. A connection/readiness failure is an environment failure, not RED evidence.
- [ ] **Step 3: Implement the narrow wiring described in Interfaces.** Retain gate release before outer failed-database drop, transactional GO-batch application, reset discovery, primary-first aggregation and existing timing phases. Do not change snapshot/backup/cache/lifecycle APIs. Make construction of the added failure message and timeout observation part of the protected diagnostic operation.
- [ ] **Step 4: Format the changed/new C# files.** Run `dotnet csharpier format <file>` on the helper and pipeline tests.
- [ ] **Step 5: Run the GREEN check from Step 2.** Expect all four provider cases passing, no unexpected skips, original errors retained and no test-owned resource leak.
- [ ] **Step 6: Add local guidance in `docs/RUNNING-LOCALLY.md`.** Show the backend project's unfiltered `dotnet test` command and `$env:MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY = '1'` before a new test process. Explain unset unlimited, lazy capture/restart, process-only scope, unchanged 300-second fallback, sampled admitted counts, suspected versus proven contention, already-limit-1 advice, optional CSV output, and shard filters as a fallback. Reuse existing server setup/admin connection guidance; distinguish backend/API tests from Docker-stack E2E. Do not present limit 1 as measured universal efficacy.
- [ ] **Step 7: Run focused regressions and shared-consumer checks.** Run:

```powershell
dotnet test $backendTests --filter 'FullyQualifiedName~Given_MssqlGeneratedDdlProvisioning|FullyQualifiedName~Given_MssqlProvisioningTimingRecorder|FullyQualifiedName~Given_MssqlGeneratedDdlBaselineDatabase|FullyQualifiedName~Given_MssqlGeneratedDdlBackupBaselineDatabase|FullyQualifiedName~Given_MssqlBackendBaselineCache|FullyQualifiedName~Given_MssqlGeneratedDdlTestDatabase|FullyQualifiedName~Given_MssqlGeneratedDdlLeaseStrategy|FullyQualifiedName~Given_MssqlLifecycleExceptionAggregation|FullyQualifiedName~Given_Mssql_Ci_Shard_Guardrails'
dotnet test $apiTests --filter 'FullyQualifiedName~Given_Mssql_CrudRoundTrip'
dotnet test $cliTests --filter 'FullyQualifiedName~Given_DocumentCacheAdminCliIntegrationHarness.It_invokes_status_through_the_real_process_against_an_isolated_sql_server_target_when_configured'
```

Expect all selected tests passing with the configured server; log actual selected/passed/skipped counts. Do not change CI scripts or run unrelated suites solely because they reference Common.

- [ ] **Step 8: Verify strategy and timing-sink regressions in fresh processes.** Run relevant snapshot/backup regressions with local default configuration and CI-like `backup-restore`/limit 2, restoring shell variables afterward. For the recorder's real backup-disposal regression, run the existing backup fixture in a fresh process with `MSSQL_FIXTURE_TIMINGS_PATH` pointing below a regular file used as a parent; expect safe warnings and passing isolation/disposal tests. Construct/clean only the test-owned temporary file/directory and restore the calling shell environment. This is process-start configuration, not in-test global mutation.

- [ ] **Step 9: Prove fixture-setup output visibility with the pinned adapter.** Use a temporary, uncommitted `MssqlGeneratedDdlProvisioningTimeoutVisibilityProbe.cs` in the backend test project: `Given_MssqlGeneratedDdlProvisioningTimeoutVisibilityProbe`, an explicit fixture without shard tags whose `OneTimeSetUp` invokes the blocked provisioning arrangement through the normal runtime overload, retains blocker cleanup, and lets the actual timeout fail setup. Run `dotnet test $backendTests --filter 'FullyQualifiedName~Given_MssqlGeneratedDdlProvisioningTimeoutVisibilityProbe' --logger 'trx;LogFileName=DMS-1330-output-visibility.trx'` using the existing adapter and default console settings, without a CSV path or extra verbosity flag; capture terminal output and TRX. Expected: nonzero exit for the intentional setup failure and a visible actionable explanation plus original SQL error. Remove the temporary probe after draining its resources; it must not be committed or counted as a passing test. A filtered witness is not AC1 acceptance. If `Console.Error` is suppressed, stop at this acceptance blocker and seek a targeted design correction; do not silently add exception wrapping, a NUnit dependency, or verbosity as a new prerequisite.
- [ ] **Step 10: Collect AC1 acceptance evidence.** On an actual emulated target host, run `dotnet test $backendTests --logger 'trx;LogFileName=DMS-1330-unfiltered.trx'` without a category/fixture filter. Record host/emulation, image/build, captured settings, actual counts/outcome, failure output and cleanup; no credential dumps. An unfiltered pass or actionable qualified provisioning-timeout failure establishes AC1; other failures need their own analysis. The historical 667-test/CU7 report is not a present result. If host access is unavailable, record the outstanding evidence and keep AC1 open.
- [ ] **Step 11: Collect AC2 timing evidence.** Collect repeated comparable before/after existing CI lane test-step wall times by shard and affected actual consumer lane, with setup phases separately. Retain all samples/context and justify comparability; do not sum overlapping CSV phases as suite wall time or introduce a percentage threshold. If no repeatable slowdown beyond baseline variation is established, record acceptance; otherwise investigate and leave AC2 open. If CI evidence is unavailable, record the exact gap. Do not mark the story complete from helper tests alone.
- [ ] **Step 12: Write the validation record.** Record commands/results, provider and runner proofs, shared-consumer checks, AC1/AC2 artifacts or gaps, and final scope review in the evidence file. Confirm no reporting catch around connection, database creation, commit, reset discovery, restore or reset; no workflow/schema/product changes; and no intentionally failing probe or secrets in the final diff.
- [ ] **Step 13: Commit the integrated change.** Stage only the helper, pipeline tests, local documentation and evidence (the evidence directory is ignored; explicitly stage only that artifact if it is committed). Use `git commit -m "test: wire actionable MSSQL provisioning timeout failures"`. Preserve unrelated local changes separately.

## Completion and Execution Handoff

Task 1 supplies admission observations; Task 2 supplies safe output; Task 3 wires and verifies them. AC1 maps to Task 3 Steps 1/9/10, AC2 to Steps 7/8/11, and AC3 to all three task test cycles. All five Review Focus cases have named test ownership. The plan uses the approved unlimited default, qualified cause, timing interpretation and bounded scope; no story/design amendment is proposed.

Implementation-ready does not mean acceptance-proven: an emulated host, comparable CI evidence, configured SQL Server and pinned-runner output proof remain validation dependencies. Keep the story open until the actual AC outcomes are supported. No code/tests/plan execution has occurred while creating this artifact.

Recommended execution method: **Native**, because the three tasks share a small set of sequential internal interfaces and form one bounded test-infrastructure change. The user must review this plan and choose Native (`superpowers:executing-plans`) or Subagent-driven (`superpowers:subagent-driven-development`) before implementation. Preserve the original current-repository/current-branch instruction and use the selected execution skill's review workflow.
