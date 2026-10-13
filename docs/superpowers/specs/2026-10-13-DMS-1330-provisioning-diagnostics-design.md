# DMS-1330: Actionable MSSQL provisioning-timeout diagnostics

**Status:** Design discussion approved on 2026-10-13; written-specification approval pending. This is a specification, not an implementation plan.

**Baseline:** `Ed-Fi-Alliance-OSS/Data-Management-Service`, current branch `DMS-1330`, inspected HEAD `f76c9ea207d5a79c920f78c16dba999eeec9cee5`. No branch substitution, implementation, builds, or local tests. Test-source inspection establishes coverage intent, not passing results.

## Intent, authority, and scope

Help maintainers running the unfiltered backend MSSQL integration suite on emulated hosts understand generated-DDL provisioning timeouts and take a concrete corrective action. Preserve CI behavior and timing. The three unnumbered [DMS-1330 acceptance criteria](https://edfi.atlassian.net/browse/DMS-1330) are called AC1-AC3 here; these labels introduce no requirements.

The story and applicable repository designs govern this specification. The [human pre-spec](../../../.plans/DMS-1330-implementation-pre-spec.md), [nano pre-spec](../../../.plans/DMS-1330-implementation-pre-spec.nano.md), and [reconciliation report](../../../.plans/ref/DMS-1505-pre-spec-review.md) are supporting analysis. The report's filename contains DMS-1505, but its reviewed story is DMS-1330. Its resolution ledger remains the detailed evidence index; this specification incorporates the consequential corrections below.

In scope: observations at the existing MSSQL provisioning gate, actionable failure output for provisioning DDL command timeouts, bounded protection against diagnostic I/O masking failures or interrupting cleanup, practical machinery coverage, and local execution guidance.

Excluded: a finite unset default, a timeout environment setting, automatic host detection, another semaphore, cross-process DDL admission, a broader lifecycle lock, changed fixture parallelism, new retries, ignored failures, engine repinning, product/CMS/PostgreSQL changes, generated DDL or schema-version changes, and unrelated hash/default/refactoring cleanup. Shard execution may be documented as a fallback; it cannot replace unfiltered AC1 validation. Do not absorb sibling provider, E2E, template, native-JSON, or benchmark work.

## Approved decisions and rationale

| Decision | Agreement |
| --- | --- |
| Diagnostic sufficiency | Explicit **suspected provisioning contention**, measured local context, an actionable remedy, and the retained original SQL failure are acceptable for AC1. Do not claim a confirmed engine wait or CPU/memory cause. |
| Local policy | Keep unset/blank admission unlimited. Document the existing concurrency setting as opt-in; do not add environment-configurable command timeouts. |
| CI timing | Judge unchanged timing as no repeatable slowdown beyond observed baseline run-to-run variation, using comparable lane wall times and separate phase timings. No arbitrary percentage tolerance is authorized. |
| Scope and behavior | Shared-helper diagnostics, bounded diagnostic-I/O protection, the data flow below, focused verification, and local documentation were approved before writing this artifact. |

The chosen approach adds diagnostics and guidance to established machinery. A finite local default could reduce contention automatically but would change shared-consumer behavior without evidence for a universally safe limit. Configurable timeouts are a legitimate story option, but introduce cache/lease precedence work and do not reduce contention. Both alternatives are excluded by the approved scope decision, not treated as impossible. Engine-backed attribution would require more machinery; the approved qualified wording avoids that requirement. Scope includes recorder resilience because a fallible timing write could otherwise hide the very failure this story explains. No additional story or design deviation is approved.

## Affected components, contracts, and flow

Path aliases: **Common/** = `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/`; **MssqlTests/** = `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/`.

| Component | Responsibility and change boundary |
| --- | --- |
| Common/MssqlGeneratedDdlTestDatabase.cs | Keep `CreateProvisionedAsync` as the admission owner and `ExecuteGeneratedDdlBatchAsync` as the timeout observation point. Carry internal admission observations into batch execution. Public method signatures and command-argument meanings remain unchanged. |
| Existing private gate/lease implementation | Retain one process-wide, lazily configured gate. A small internal, instance-testable gate state owns the captured configuration, existing optional semaphore, admitted count, and admission lease. No generic policy or telemetry framework. |
| Internal diagnostic formatting/output | Format safe contextual fields and write immediate failure output to `Console.Error`; accept a writer in the internal test seam. Do not mutate a process-global writer or introduce a NUnit dependency into Common. |
| Common/MssqlProvisioningTimingRecorder, currently in the database file | Preserve existing CSV header, columns, phases, outcomes, and optional-path behavior. Make optional output failures nonfatal to provisioning and cleanup, with best-effort safe warning output. |
| MssqlTests machinery fixtures | Cover the selected behavior using the existing friend-assembly access and NUnit/FluentAssertions conventions. Reuse existing lease/cache/cleanup regressions. |
| docs/RUNNING-LOCALLY.md | Add backend MSSQL commands, process-start gate configuration, interpretation of diagnostics, and the distinction from API integration and Docker-stack E2E tests. |

Actual shared consumers include backend MSSQL tests, API `MssqlBaselineCache.BuildBaselineAsync`, and DocumentCacheAdmin `BuildMssqlBaselineAsync`. Common-assembly references from CDC, E2E, or PostgreSQL projects alone do not prove use of this gate. Review actual callers of changed helpers; do not expand coverage solely from project references.

The flow remains: fixture/cache -> strategy-selected baseline -> new snapshot slot or initial backup baseline -> `CreateProvisionedAsync` -> admission -> database creation -> transactional DDL batches -> reset-plan discovery -> admission release. Snapshot/backup creation follows outside admission. A failed provision releases admission before outer failed-database disposal. Restore/reset and standalone `ApplyGeneratedDdlAsync` retain their existing boundaries.

### Admission observations

Capture the effective configuration once at the same lazy first-use boundary as today, including invalid-configuration failure retention. Preserve the current parser: unset/blank means unlimited; positive Int32 digits, including leading zeros, are accepted; zero, negatives, signs, surrounding numeric whitespace, malformed input, and overflow are rejected before database creation. Admission still has no new cancellation or queue deadline.

Measure admission wait separately from batch execution. Increment the admitted count after successful admission and before creation; decrement it when the admission lease ends on every success/failure path, before releasing its semaphore permit. Count operations when admission is unlimited too. Lease disposal must not double-release or make the count negative.

Sample the admitted count at batch start and at timeout before the failing operation releases admission. Both samples include that operation and exclude queued entrants. These are process-wide admitted create/provision operations, not executing DDL commands or per-server measurements. They include creation/reset discovery and may include operations targeting different servers. Two samples do not establish peak overlap throughout the batch. Do not infer server-wide concurrency or engine waits from them.

### Diagnostic contract

Emit the contention explanation only for `SqlException.Number == -2` raised from command execution in a generated-DDL batch belonging to an admitted `CreateProvisionedAsync` operation. Do not use message-text matching. Connection opening, database creation, commit, reset-plan discovery, restore/reset, non-timeout SQL errors, cancellation, and standalone DDL application do not receive this provisioning-contention classification. An existing production cache timeout classifier is a semantic precedent, not a dependency to introduce into Common.

The diagnostic contains:

- A stable generated-DDL provisioning-timeout label and **suspected provisioning contention** wording.
- Fixture signature, falling back to generated-DDL identity for a blank signature; lease strategy; batch ordinal/count; and generated test database identity.
- Measured batch execution elapsed time and the effective command timeout in seconds. Describe the timeout as the command budget, not an exact wall-clock ceiling or total setup deadline.
- Admission wait elapsed, admitted-count samples at batch start/timeout, and the captured limit or `unlimited`, with the count scope stated.
- A process-restart remedy: for unlimited or limits above 1, set `MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY=1` before starting the test process. For an already-effective limit of 1, explain that further in-process throttling cannot reduce it; check other test processes and SQL Server waits/logs. If the remedy does not resolve the timeout, inspect server-side evidence rather than declaring contention proven.

Normalize contextual strings and control characters into a single-line diagnostic; use invariant numeric formatting. Do not print raw SQL, connection strings, credentials, or copy provider message text into the added diagnostic. Preserve the original provider error through rethrowing and existing failure reporting. The runtime output requires no `MSSQL_FIXTURE_TIMINGS_PATH` or extra verbosity setting.

## Failure handling and regression safeguards

Formatting and diagnostic output are best effort and cannot replace the primary exception, change success/failure/skip classification, or block cleanup. Rethrow the original exception with its original type and stack. When rollback/drop also fails, retain existing primary-first aggregation. Optional CSV path/format/write failures likewise cannot mask the primary or prevent subsequent disposal; report a safe warning if an output channel remains usable. Suppress only diagnostic-operation failures, not SQL or cleanup failures.

Keep transaction scope, rollback, reset-plan discovery, database ownership, snapshot/backup lease isolation, hash checks, cache keys, and lifecycle coordination unchanged. The 300-second fallback and explicit timeout arguments remain: snapshot lease overrides can govern new-slot provisioning or restore/reset; backup initial construction uses the captured baseline timeout, while lease overrides govern restored-lease commands. No timeout-policy changes are introduced. Creation/lifecycle commands have separate budgets.

CI retains its current four shards, four fixture workers, dedicated containers, explicit default limit 2 (and existing dispatch override), backup-restore strategy, moving image policy, resources/readiness, prebuilt assemblies, path promotion, artifacts, and CSV reader. Missing local admin configuration may skip; missing exported CI configuration or an unreachable configured server must continue to fail. The new success-path work is small bookkeeping; unchanged performance remains an acceptance result to verify, not a source-derived guarantee.

## AC-to-design-to-verification mapping

All verification below is required later as applicable; none was executed during specification work.

| Acceptance criterion | Design behavior and affected components | Meaningful verification |
| --- | --- | --- |
| AC1: An unfiltered emulated-host backend MSSQL run passes or fails with an actionable diagnostic naming provisioning contention | Qualified cause, measured context, actionable gate remedy, and retained provider failure in Common; local guidance | Exact contextual/remedy assertions for admitted DDL timeout; different advice at limit 1; negative classification for other phases/errors and standalone apply. Later execute the actual unfiltered suite on an emulated host and retain terminal output/results proving success or visible actionable timeout output. Documentation, message-unit tests, and shard runs alone cannot establish AC1. |
| AC2: CI lane behavior and timing remain unchanged | Preserve lane/configuration, public contracts, CSV shape and reader; shared-helper success/failure behavior | Review configuration delta; run existing snapshot/backup/cache/reset/cleanup regressions and representative actual API/CLI consumers. Compare repeated comparable before/after CI test-step wall times per backend shard and affected shared-consumer lane; use phase timings separately. Record runner context, engine build/image identity, configuration, outcomes, and test inventory changes. Accept only no repeatable slowdown beyond observed baseline variation; inconsistent or confounded evidence requires further measurement. |
| AC3: Chosen mitigation covered by machinery tests where practical | Instance-testable admission observations, formatter/output seam, recorder resilience; MssqlTests | Controlled signals prove exact counts, queued-entrant exclusion, finite/unlimited behavior, fault release, and disposal correctness. Supplied writers prove exact output and throwing-sink preservation without replacing global console/env state. Test CSV compatibility and recorder I/O failure, primary-first cleanup behavior, and cleanup continuation, including backup-baseline disposal. Add narrowly scoped provider/wiring coverage; existing regression tests are not a substitute for tests of the new behavior. |

Deterministic tests belong in the existing MSSQL test assembly, which already has `InternalsVisibleTo` access. Supply captured configuration, controlled elapsed values, and local writers to internal seams; test the same paths production machinery uses. Reuse the assembly's existing synthetic SqlException precedent for controlled error cases. Use signals/hooks rather than `Task.Delay`, short `CancelAfter`, or a wall-clock race to manufacture timeouts. For a real-provider timeout proof, hold a server-side blocker until the command outcome, so a finite command timeout is the behavior under test rather than one timer racing another. SQL-shape/synthetic tests do not prove real-provider or runner behavior: later provider-backed failure-path coverage and acceptance evidence must verify command classification, rollback/drop, and output visibility.

Reuse `MssqlGeneratedDdlTestDatabaseTests`, snapshot/backup baseline tests, `MssqlBackendBaselineCacheTests`, and lifecycle aggregation tests as relevant regression anchors. New ordinary fixtures/methods must have exactly one effective MSSQL shard; explicit tests stay outside shard filters. Avoid a heavyweight deliberately contended fixture in ordinary CI solely to reproduce this bug. Do not expand the product parity catalog.

## Evidence reconciliation, dependencies, and handoff

The branch still matches the pre-specs' analyzed HEAD. Carry forward report R01-R18 rather than reopen verified questions. In particular: the full AC1 includes cause-naming; timeout/cache precedence is resolved by source; primary-error preservation has a current recorder-I/O hole; actual consumer usage is narrower than project references; nested CSV phase sums are not suite wall time; and July counts, engine build, DMV samples, and green-CI reports are historical story evidence. The human/nano documents remain analysis and are not rewritten as approved requirements. Their omissions and overstatements do not govern this design.

Applicable inspected designs: [MSSQL gap-closure epic](../../../reference/design/backend-redesign/epics/17-mssql-gap-closure/EPIC.md), its [inventory](../../../reference/design/backend-redesign/epics/17-mssql-gap-closure/00-gap-analysis-and-follow-up-inventory.md), the [DMS-1279 runtime/defer design](../../../reference/design/backend-redesign/epics/17-mssql-gap-closure/03-sql-server-2025-and-native-json.md), and [test-migration parity/shard contracts](../../../reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md). Live relevant epic siblings were inspected; no dedicated approved DMS-1330 implementation design or sibling completion blocker was found in that scope. DMS-1279/1328 retain runtime/storage ownership, 1285/1286 provider coverage, 1284 Docker E2E, 1289/1429 scheduled lanes, and 1271 template restore.

Provider/output references: SqlClient 6.1.4 defines timeout number -2 in [TdsEnums](https://github.com/dotnet/SqlClient/blob/v6.1.4/src/Microsoft.Data.SqlClient/src/Microsoft/Data/SqlClient/TdsEnums.cs); [CommandTimeout documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqlcommand.commandtimeout) explains why the command budget is not an overall setup deadline. [NUnit TestContext documentation](https://docs.nunit.org/articles/nunit/writing-tests/TestContext.html) distinguishes immediate error output from fixture-result output. Repository pins are NUnit 4.2.2 and adapter 4.6.0; documentation is not an executed proof of visibility through that combination.

**Remaining evidence dependencies:** a configured real SQL Server for provider tests; an emulated target host for unfiltered AC1 acceptance; comparable baseline and changed CI runs for AC2; and visibility of fixture-setup diagnostics through the pinned runner. The original reproduction configuration/raw runs remain unavailable. They are useful history, not required to begin implementation. A limit of 1 is an actionable recommendation, not a claim of measured efficacy or a universal performance guarantee. Diagnostic-sink failure preserves the provider error; it cannot guarantee output if every output channel fails.

**Approval and planning boundary:** Final written-specification approval is the remaining design gate. After it, hand this specification, the original story, and the reconciliation report to `superpowers:writing-plans`. The plan should sequence the bounded Common changes, focused tests, documentation, and later acceptance evidence, preserving the decisions above. Recheck revision-sensitive anchors if code changes. Do not claim the story complete until AC1-AC3 have evidence. No implementation plan or implementation is part of this specification task.

**Self-review:** All three ACs mapped; report discrepancies reconciled; facts separated from approved decisions and missing execution evidence; scope and chosen approach checked against story/design authority. No extra default, timeout, engine-attribution, schema, product, or infrastructure commitment is incorporated.
