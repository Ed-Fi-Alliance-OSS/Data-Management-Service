# DMS-1303 Schema Fingerprint Re-stamp Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace manual schema-fingerprint re-key SQL with a supported, atomic SchemaTools command after an independently validated, data-preserving migration.

**Architecture:** A standalone command computes target metadata with the existing loader/builder. A focused service validates and re-keys the parent/component rows on one connection and transaction, with narrowly contained provider SQL. Ordinary provisioning and runtime guards remain unchanged; provisioning and restart are separate operator steps.

**Tech Stack:** .NET 10, System.CommandLine, System.Text.Json, existing Npgsql/Microsoft.Data.SqlClient, NUnit/FluentAssertions/FakeItEasy, existing real-database and WebApplicationFactory integration infrastructure.

**Spec:** [Approved DMS-1303 specification](../specs/2026-10-08-DMS-1303-schema-restamp-design.md), approved 2026-10-08 and recorded in `eaa0df87`. Code baseline: `368b6cd7bca5e43ef601db5e2a488b4170236c19`, branch `DMS-1303`. [Human analysis](../../../.plans/DMS-1303.pre-spec.md), [nano analysis](../../../.plans/DMS-1303.pre-spec.nano.md), and [resolution report](../../../.plans/DMS-1303-implementation-pre-spec-review.md) supply evidence; the approved spec controls decisions.

**Status:** Implementation work completed at `b06b8d77` after final review. Focused DMS-1303 verification passes on both providers. The full SchemaTools unit and broad `Provision` filters were executed; unrelated Windows CDC setup failures, four provider-role skips, and SQL Server golden-schema `sqlcmd` timeouts are recorded in the progress and handoff artifacts. `RelationalMappingVersion` remains `v3`.

## Global Constraints

- One invocation targets one existing PostgreSQL or SQL Server database. Work in the requested current repository/branch; verify intervening code changes against the baseline before execution.
- Support physical migrations with unchanged ApiSchema format, ResourceKey mappings, seed fingerprints, and stored component metadata. Make no promise of a named release upgrade.
- Validate matching stamps without writes: valid matches succeed without confirmation; corrupt matches fail without writes.
- Keep `ddl provision` as a separate documented follow-up. Preserve `AppliedAt` as the original provisioning timestamp.
- Keep `RelationalMappingVersion = "v3"`; no bump or rollback belongs to this story.
- Require `--migration-completed` before re-key DML. Timeout must be positive, defaults to 300 seconds, and applies per database command, including lock acquisition; connection timeout remains a provider connection-string setting.
- Preserve documents, IDs, versions, timestamps, ResourceKey rows, SourceIdentity, cache rows, lifecycle/latches, and work queues. Do not claim CDC readiness or continuity.
- Exclude database creation, physical migration execution, metadata/seed repair, changed resource/project inventories or versions, format conversions, automatic provisioning, runtime cache invalidation, and a migration framework or catalog-diff engine.
- Follow [AGENTS.md](../../../AGENTS.md): modern .NET 10 style, non-nullable variables, `is null`/`is not null`, System.Text.Json, NUnit `Given_` fixtures/`Setup`/`It_` methods, and sanitized diagnostics. Add no packages or platform upgrades.
- A skipped database suite is missing execution evidence, not a passing verification. Configure both providers before claiming AC2.

## Review Focus

These conditions need explicit tests in addition to the AC happy paths:

1. Missing/empty database names or malformed connection options must never fall back to a provider's default database or echo credentials (Task 3).
2. Case/accent differences in stored metadata must fail ordinal comparison even on a case-insensitive SQL Server database (Tasks 1 and 4).
3. Parser errors, control characters in paths/components, and secret-shaped provider exception text must remain safe with `--verbose` (Task 3).
4. Cancellation or timeout while waiting for metadata locks must leave the original state intact and release resources (Tasks 2 and 4).
5. A transport failure during commit must report uncertainty; identical reruns must validate either the old or target state without automatic retries (Tasks 2 and 4).

## File Structure and Dependencies

Create a small `Restamping` area inside SchemaTools:

| File under `src/dms/clis/EdFi.DataManagementService.SchemaTools/` | Responsibility |
|---|---|
| `Restamping/SchemaRestampContracts.cs` | Local snapshot/result/failure contracts and one command-to-service interface. |
| `Restamping/SchemaRestampValidator.cs` | Pure transition compatibility validation using existing field/row contracts. |
| `Restamping/SchemaRestamper.cs` | Connection/transaction lifecycle, reads, decision, DML, and failure classification. |
| `Restamping/SchemaRestampSql.cs` | Fixed provider-specific metadata SQL and identifier rendering; no migration engine. |
| `Commands/DdlRestampCommand.cs` | Options, target computation, safe command results. |
| `Program.cs` (modify) | Register the command and contain its parser-error/exception surface. |

Tasks execute in order: **1 → 2 → 3 → 4 → 5**. Keep `SeedValidator`, the shared fingerprint contract, the loader/builder, ordinary provisioners, and runtime admission code unchanged. Existing representation-restamp/introspection/copy tooling remains in use and outside these tasks. The test-only connection factory below supplies deterministic failure evidence; do not add public CLI failpoints.

## Task 1: Define and Test Transition Compatibility

**Files:**
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools/Restamping/SchemaRestampContracts.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools/Restamping/SchemaRestampValidator.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/SchemaRestampValidatorTests.cs`

**Interfaces:** Define these public types in the SchemaTools `Restamping` namespace; do not add backend-wide contracts:

```csharp
record SchemaRestampFingerprint(short SingletonId, string ApiSchemaFormatVersion,
    string EffectiveSchemaHash, short ResourceKeyCount, byte[] ResourceKeySeedHash);
record SchemaRestampComponent(string EffectiveSchemaHash, SchemaComponentRow Payload);
record SchemaRestampSnapshot(IReadOnlyList<SchemaRestampFingerprint> Fingerprints,
    IReadOnlyList<ResourceKeyRow> ResourceKeys,
    IReadOnlyList<SchemaRestampComponent> SchemaComponents);
record SchemaRestampResult(bool Changed, string PreviousHash, string TargetHash, int ComponentCount);
enum SchemaRestampFailure { Validation, ConfirmationRequired, Connection,
    PermissionDenied, Timeout, Cancelled, TransactionFailed, CommitOutcomeUnknown }
sealed class SchemaRestampException(SchemaRestampFailure failure, string safeMessage)
    : Exception(safeMessage); // expose Failure; never include raw provider text
interface ISchemaRestamper {
    Task<SchemaRestampResult> RestampAsync(SqlDialect dialect, string connectionString,
        int commandTimeoutSeconds, EffectiveSchemaInfo target, bool migrationCompleted,
        CancellationToken cancellationToken);
}
// Consumes the snapshot and existing EffectiveSchemaInfo:
static void SchemaRestampValidator.ValidateOrThrow(
    SchemaRestampSnapshot stored, EffectiveSchemaInfo target, ILogger logger);
```

Use sealed records and the repository's normal declaration style in implementation. Contract snippets pin names/types, not full bodies.

- [x] **Step 1: Write `Given_SchemaRestamp_Compatibility` fixtures.** Build valid target metadata from the existing minimal schema; construct stored snapshots with a different valid 64-character lowercase hash and matching companions. Pin these assertions in named `It_` tests:

```csharp
// It_accepts_a_different_hash_with_compatible_metadata
_validate.Should().NotThrow();
// It_rejects_corrupt_metadata_even_when_the_hash_matches
_validate.Should().Throw<SchemaRestampException>()
    .Which.Failure.Should().Be(SchemaRestampFailure.Validation);
// It_rejects_format_case_changes_ordinally (also component/project/resource names)
_validate.Should().Throw<SchemaRestampException>();
```

Parameterize absence/two singleton rows, wrong ID, blank format, malformed hash, invalid count/seed length, unequal format/count/seed bytes, missing/extra/duplicate ResourceKey IDs or component endpoints, modified stored names/versions/extension flags, and a child associated with another hash. Include a non-blank but unequal format: shared shape validation alone must not pass it. Assert failures name the table/field category, not raw input payloads.

- [x] **Step 2: Run the new unit fixtures and confirm they fail because the transition validator is absent.**

Run: `dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/EdFi.DataManagementService.SchemaTools.Tests.Unit.csproj --filter "FullyQualifiedName~SchemaRestamp"`

- [x] **Step 3: Implement the contracts and validator.** Require exactly one singleton ID `1`; call `EffectiveSchemaFingerprintContract.GetStoredValidationIssues`/`GetExpectedValidationIssues`. Compare format with `StringComparison.Ordinal`, count exactly, and seed bytes with `SequenceEqual`. Check all child-hash associations before using `SeedValidator.ValidateResourceKeysOrThrow` and `ValidateSchemaComponentsOrThrow` with the target's `ResourceKeysInIdOrder` and `SchemaComponentsInEndpointOrder`. Translate their exceptions to controlled table/field diagnostics. Do not compare nonexistent stored ProjectHash or relax `ValidateEffectiveSchemaOrThrow`.

- [x] **Step 4: Format the two production files and unit test file; rerun the Step 2 command.** Expected: every new fixture passes; ordinary `SeedValidatorTests` still pass when running the whole unit project.
- [x] **Step 5: Commit only these three files.** Suggested message: `feat: validate compatible schema fingerprint transitions`.

## Task 2: Implement the Atomic Both-provider Service

**Files:**
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools/Restamping/SchemaRestamper.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools/Restamping/SchemaRestampSql.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/SchemaRestamperTests.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/SchemaRestampTestHelper.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/DdlRestampPgsqlTests.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/DdlRestampMssqlTests.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/Fixtures/restamp-extension-api-schema.json`

**Interfaces:**
- Consumes: Task 1's contracts/validator; existing Npgsql/SqlClient and `EffectiveSchemaTableDefinition`.
- Produces: `sealed class SchemaRestamper : ISchemaRestamper`, with constructors `SchemaRestamper(ILogger logger)` and `SchemaRestamper(ILogger logger, Func<SqlDialect, string, DbConnection> connectionFactory)`, implementing Task 1's exact `RestampAsync` signature. The default factory constructs an unopened provider connection; the overload enables controlled connection/transaction failures.
- Test helper: `BuildTarget(params string[] schemaPaths) -> EffectiveSchemaInfo`; `RunRestamp(string connectionString, string dialect, bool migrationCompleted, params string[] schemaPaths) -> (int ExitCode, string Output, string Error)` uses the shipped CLI once Task 3 exists. For this task, integration tests call the service directly. Use `Given_SchemaRestamp_Pgsql_*` / `Given_SchemaRestamp_Mssql_*` fixture names and existing provider categories.

- [x] **Step 1: Write service tests and direct-service database fixtures.** Provision the minimal core plus a small extension with stable project/version/endpoint metadata; alter only their stamped hash consistently to establish the transaction test's old state. This synthetic setup tests re-key mechanics; Task 5 supplies actual migration evidence.

```csharp
// It_commits_only_the_parent_and_child_hash_changes
_result.Should().Be(new SchemaRestampResult(true, _oldHash, _target.EffectiveSchemaHash, 2));
// It_does_no_dml_for_a_valid_match_without_confirmation
_result.Changed.Should().BeFalse();
_executedDml.Should().BeEmpty();
// It_reports_commit_transport_uncertainty_without_retry
_failure.Failure.Should().Be(SchemaRestampFailure.CommitOutcomeUnknown);
_commitAttempts.Should().Be(1);
```

Capture the complete parent including `AppliedAt`, all children, and ResourceKey rows before/after; compare exactly except the two approved hash locations. Unit connection/command doubles assert every command has the configured timeout, same connection/transaction, and the lock order. Signal a waiting command before cancelling its token; assert cancellation before commit rolls back/disposes. Simulate commit transport failure and cleanup failure: never replace the primary diagnostic or describe uncertain commit as a confirmed rollback.

- [x] **Step 2: Run Task 1's unit command and both new direct-service integration fixtures; confirm the missing service fails.** Integration commands are listed in Task 4; configure both providers first using repository instructions.
- [x] **Step 3: Implement `SchemaRestamper.RestampAsync` and its fixed SQL helper.** Validate supported dialect, positive timeout, and explicit target database before opening. Build one connection/transaction; lock `EffectiveSchema` → `ResourceKey` → `SchemaComponent`. PostgreSQL uses `LOCK TABLE ... IN SHARE ROW EXCLUSIVE MODE`; SQL Server uses actual table-reading commands with `TABLOCKX, HOLDLOCK` (including empty tables), not optimized-away queries. Reuse shared identifiers/quoting; all values are parameters.

Read the singleton projection (up to two rows suffices to detect ambiguity) and **all** ResourceKey/component rows under those locks. Reject null/malformed database values with controlled diagnostics. Validate via Task 1 before checking equality. A valid match commits/releases the read-only transaction and returns `Changed = false`, without DML or confirmation. Otherwise check confirmation, capture children, delete old-hash children, update **only** the parent hash with predicates ID `1` and observed old hash, require one updated row, and reinsert captured payloads under the target hash. Preserve all other parent fields. Commit before returning a changed result.

Classify failures by controlled operation context and provider codes, not exception-message parsing. Before commit begins, attempt rollback and preserve the primary failure if cleanup fails. Once commit is attempted, a transport/cancellation ambiguity returns `CommitOutcomeUnknown`; do not auto-retry. Do not call provisioning methods that own another connection.

- [x] **Step 4: Format new C# files with `dotnet csharpier format <each changed file>`; rerun unit and both integration commands.** Expected: re-key, no-op, missing-row, incompatible-metadata, timeout propagation, cancellation, and uncertainty assertions pass on their applicable seams.
- [x] **Step 5: Commit this service and its tests/extension fixture.** Suggested message: `feat: atomically re-stamp schema metadata on both providers`.

## Task 3: Ship the Command and Safe Operator Surface

**Files:**
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools/Commands/DdlRestampCommand.cs`
- Modify: `src/dms/clis/EdFi.DataManagementService.SchemaTools/Program.cs`
- Create: `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/DdlRestampCommandTests.cs`
- Modify: Task 2's integration helper and provider test files to exercise `CliTestHelper.RunCli`.

**Interfaces:**
- Consumes: `ISchemaRestamper.RestampAsync`, `IApiSchemaFileLoader.Load`, and `EffectiveSchemaSetBuilder.Build(...).EffectiveSchema`.
- Produces: `DdlRestampCommand.Create(ILogger logger, IApiSchemaFileLoader fileLoader, EffectiveSchemaSetBuilder schemaSetBuilder, ISchemaRestamper restamper, TextWriter output, TextWriter error) -> Command` and `DdlRestampCommand.InvokeAsync(ParseResult parseResult, TextWriter output, TextWriter error, CancellationToken cancellationToken = default) -> Task<int>`. The invocation wrapper contains parser errors, uses `InvocationConfiguration` with the default exception handler disabled, and is used by Program only for this subcommand. Do not change other commands' exit behavior.

- [x] **Step 1: Write `Given_SchemaRestamp_Command` tests with a fake `ISchemaRestamper`, real builder, and captured writers/log sink.** Assert the actual computed metadata changes when schema content changes and the expected options/token reach the fake once. Pin alias/default behavior and outputs:

```csharp
// It_reports_a_committed_transition_only_after_the_service_returns
_exitCode.Should().Be(0);
_output.Should().Contain("Effective schema re-stamp committed.");
// It_reports_a_valid_match_without_confirmation
_exitCode.Should().Be(0);
_output.Should().Contain("Already stamped; no changes.");
// It_keeps_parser_and_provider_errors_safe_in_verbose_mode
_exitCode.Should().Be(1);
(_output + _error + _logs).Should().NotContain(_credentialSentinel);
```

Cover missing required options, unsupported dialect, timeout `0`/negative, malformed connection options, absent/empty database name, absent/invalid JSON input, normalization failure, provider permission/timeout failures, cancellation, and uncertain commit. Bad arguments must never call the service. For hostile paths/component strings assert no injected diagnostic lines/control characters. Test unknown options containing the credential sentinel: parser diagnostics must not quote them. Test help with valid registration and reject raw-hash/create-database/force/managed-CDC flags.

- [x] **Step 2: Run the unit command and a process invocation `api-schema-tools ddl re-stamp --help` via `CliTestHelper`; confirm command absence/failing contracts.**
- [x] **Step 3: Implement the two command methods and registration.** Options: `--schema/-s` (first core, remaining extensions), `--connection-string/-c`, `--dialect/-d` restricted to `pgsql|mssql`, `--timeout/-t` positive/default `300`, and `--migration-completed`. Validate connection database with the appropriate provider connection-string builder; never accept implicit default databases. Load/build once without full DDL generation.

Program calls the invocation wrapper when the parsed command is `ddl re-stamp`; it must do so even if that command has parse errors. Report a fixed safe invalid-arguments diagnostic and exit `1`, rather than forwarding parser messages that quote values. The action catches typed safe failures and unexpected exceptions; logs only sanitized controlled messages/type names, never exception objects or raw provider messages. Existing safe loader formatting is reusable; `CommandErrorHandler`'s raw exception logging is not.

After service success print the appropriate fixed status line, hashes/count for a changed result, and the metadata-only/provision/restart reminder. Exit `1` for all failures, including cancellation; an uncertain commit directs identical offline rerun/inspection. Do not add automatic provision or broad logging refactors.

- [x] **Step 4: Format modified/new C# files; run the whole SchemaTools unit project and both process-based integration fixtures.** Expected: help/aliases/error contracts and direct-versus-process result parity pass; existing hash/emit/provision/CDC command tests remain unchanged and pass.
- [x] **Step 5: Commit command registration, handler, and tests.** Suggested message: `feat: expose the schema fingerprint re-stamp command`.

## Task 4: Prove Failure Atomicity, Preservation, and Concurrency

**Files:** Modify Task 2's `SchemaRestampTestHelper.cs`, `DdlRestampPgsqlTests.cs`, and `DdlRestampMssqlTests.cs`; modify `SchemaRestamperTests.cs` only if additional deterministic transaction assertions are needed. Change production service code only to fix a demonstrated failure of the approved behavior.

**Interfaces:** Reuse Task 2's helper and Task 3's shipped CLI. Reuse `ProvisionTestHelper.InsertRowsThatMustSurviveRerun(DbConnection, string)` and `ReadDocumentCacheMutableStateSnapshot(DbConnection, string)`. Extend the new helper with `CaptureState(DbConnection connection, string dialect) -> SchemaRestampState` (test-only record with complete parent/children/ResourceKeys, document metadata/data, cache/work row payloads, and existing mutable-cache snapshot). The existing snapshot's row counts alone do not prove payload preservation; capture complete ordered rows too.

- [x] **Step 1: Add failing provider cases, each on its own disposable database.**

```csharp
// It_rolls_back_after_child_delete_parent_update_or_partial_reinsertion
_exitCode.Should().Be(1);
_after.Should().BeEquivalentTo(_before, options => options.WithStrictOrdering());
// It_rejects_dml_on_matching_reruns
_exitCode.Should().Be(0); // DML-rejecting triggers remain installed
_after.Should().BeEquivalentTo(_before, options => options.WithStrictOrdering());
// It_serializes_competing_same_target_transitions
_results.Count(r => r.Changed).Should().Be(1);
_results.Count(r => !r.Changed).Should().Be(1);
```

Use targeted database triggers: reject the parent update after children were deleted; reject the first child insertion after the parent update; reject a later extension insertion after one child was reinserted. Assert restoration of the FK and exact old payloads after each failure. Seed non-default SourceIdentity/cache lifecycle/latch/work/document state, then verify that a successful re-key changes only the approved hashes. Add missing database/table/required column/singleton, malformed/multiple singleton, incompatible format/seeds/full rows, child orphan, and confirmation-omission cases. Use disposable relaxed tables only for corruption states production constraints ordinarily prevent.

Install DML-rejecting triggers on all three metadata tables to prove valid matching runs really perform no writes, with and without confirmation. SQL Server case-insensitive collation cases must reject case/accent metadata differences. Restricted administrative credentials must fail safely without a committed transition.

For concurrency, use direct-service `SchemaRestampResult` values for the two-result assertions above; process-based tests cover the shipped command separately. Hold the first invocation at a real transaction/lock signal before starting the second. Assert a same-target re-key then no-op; a separate conflict/timeout case must leave intact state. A competing metadata writer must remain blocked until validation/re-key completes; roll its transaction back before the preservation snapshot. Do not promise protection from an unrelated writer after locks are released. Do not use delays to guess ordering. Hold a blocker lock persistently for the timeout case; use a command/wait signal before cancellation. Test reruns from both possible states after the unit-simulated uncertain commit: changed from old, no-op from target, with full validation in either case.

- [x] **Step 2: Run the following targeted commands, with both providers configured.** Confirm injected failures occur at the intended database operations and assertions cover the complete state. If the implemented behavior already passes, record the targeted test's sensitivity to removing its protected check/transaction in an isolated execution test cycle; restore immediately, without committing that experiment.

```powershell
dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/EdFi.DataManagementService.SchemaTools.Tests.Integration.csproj --filter "FullyQualifiedName~SchemaRestamp&TestCategory=PostgresqlIntegration"
dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/EdFi.DataManagementService.SchemaTools.Tests.Integration.csproj --filter "FullyQualifiedName~SchemaRestamp&TestCategory=MssqlIntegration"
```

Configure `ConnectionStrings__PostgresAdmin` and `ConnectionStrings__MssqlAdmin` through the existing test configuration. Follow AGENTS.md for a suitable running SQL Server and diagnose setup failures separately; no Docker-stack E2E suite is required.

- [x] **Step 3: Implement only the focused helper/test instrumentation and any behavior correction demonstrated in Step 2.** No production failpoint option, schema-repair path, new lock framework, or unrelated cleanup.
- [x] **Step 4: Format changed C# files and rerun Step 2 plus the unit project.** Expected: all new cases execute and pass; no skips for either provider. Run existing provisioning regression fixtures once with filter `FullyQualifiedName~Provision` on the SchemaTools integration project, with both providers configured, to retain create-only/mismatch/preservation behavior.
- [x] **Step 5: Commit the failure/concurrency evidence and any required corrections.** Suggested message: `test: prove schema re-stamp atomicity and preservation`.

## Task 5: Validate a Real Migration and Publish the Runbook

**Files:**
- Create under `src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/schema-restamp/`: `source/api-schema.json`, `source/fixture.json`, `target/api-schema.json`, `target/fixture.json`, `migration.pgsql.sql`, `migration.mssql.sql`, and `README.md`.
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureContext.cs` and `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Fixtures/FixtureRepositoryPaths.cs`.
- Create: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/SchemaRestampMigrationScenario.cs`.
- Create: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Postgresql/Given_Postgresql_SchemaRestampMigration.cs` and `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_Mssql_SchemaRestampMigration.cs`.
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj` (reference the existing SchemaTools project; add no packages).
- Modify: `src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md`, `docs/RELATIONAL-BACKEND.md`, and `reference/design/backend-redesign/design-docs/ddl-generation.md`.

**Interfaces:**
- Add `FixtureKey.SchemaRestampSource` / `SchemaRestampTarget` and their exact new repository-directory mappings.
- New test-local scenario: `Task<SchemaRestampMigrationEvidence> RunAsync(SqlDialect dialect, bool applyPhysicalMigration)`. Define the local record with `int BeforeProvisionExitCode`, `HttpStatusCode BeforeStampStatus`, `int RestampExitCode`, `int TargetProvisionExitCode`, `HttpStatusCode CreateStatus`, and `JsonObject Returned`, `ExistingWidget`, `BeforeStoredDocument`, `AfterStoredDocument`. Provider fixtures arrange/act in `Setup` and assert this evidence in `It_` methods; categorize them `ApiIntegration` and their provider category. Negative cases set the unperformed target-provision result to `-1` and never assert it as success.
- Private `WebApplicationFactory<Program> CreateHost(FixtureContext fixture, string connectionString, SqlDialect dialect)` uses the existing API host configuration and `ExternalDoublesRegistration.RegisterAll`; clients use the existing smoke bearer token. Lease from `PostgresqlBaselineCache.CreateOrGetAsync(source)` followed by `CreateIsolatedDatabaseAsync()`, or `MssqlBaselineCache.CreateOrGetAsync(source)` followed by `AcquireRestoredDatabaseAsync()`, awaiting each call. Do not refactor `ApiIntegrationTestBase` into a general migration harness.
- Invoke Task 3's actual command/action in-process for the API scenario; Task 4 already proves the external CLI process. Use materialized schema paths from each context's fixture manifest in their declared core-first order, not glob order.

- [x] **Step 1: Create the minimal fixture pair and write failing migration scenarios.** Start from the existing minimal Widget/Gadget schema. Target adds only optional `widgetNote` (`string`, `maxLength: 120`, document-path column `WidgetNote`) to Widget; format, projects/versions, resources, identities, and ResourceKey inputs stay identical. Keep both fixtures' runtime-neutral defaults identical by using `FixtureContextLoader` on both. Assert actual source/target hashes differ and all compatibility metadata agrees before testing the transition.

```csharp
// It_admits_the_migrated_database_and_round_trips_the_added_field
_evidence.BeforeProvisionExitCode.Should().Be(1);
_evidence.BeforeStampStatus.Should().Be(HttpStatusCode.ServiceUnavailable);
_evidence.RestampExitCode.Should().Be(0);
_evidence.TargetProvisionExitCode.Should().Be(0);
_evidence.CreateStatus.Should().Be(HttpStatusCode.Created);
_evidence.Returned["widgetNote"]!.GetValue<string>().Should().Be("migration-proof");
// It_preserves_the_existing_document_and_resource_data
JsonNode.DeepEquals(_evidence.BeforeStoredDocument, _evidence.AfterStoredDocument).Should().BeTrue();
_evidence.ExistingWidget["widgetId"]!.GetValue<int>().Should().Be(1);
_evidence.ExistingWidget["widgetName"]!.GetValue<string>().Should().Be("before-migration");
// It_does_not_certify_an_unmigrated_physical_shape
_evidence.RestampExitCode.Should().Be(0); // compatible metadata alone is insufficient
_evidence.CreateStatus.Should().Be(HttpStatusCode.InternalServerError);
```

On the source host POST to `/data/testproject/widgets` with `widgetId: 1` and `widgetName: "before-migration"`, recording its API identity and complete `dms.Document` values/versions/timestamps. Stop the host. Apply the provider's nullable `WidgetNote` column ALTER independently when `applyPhysicalMigration` is true; obtain schema/table/type from generated target DDL, rather than guessing naming. The physical scripts contain no fingerprint re-key SQL.

Before re-stamping, demonstrate target ordinary provision rejects the source hash and a fresh target host returns HTTP 503; dispose that host before administration. Invoke re-stamp, run separate target provision through existing `DdlProvisionCommand.Create`, and create another fresh target host on the same leased database. Assert the old Widget's ID, `widgetId: 1`, `widgetName: "before-migration"`, and recorded document metadata survived; its new nullable field must be null/absent. POST/GET `widgetId: 2`, `widgetName: "after-migration"`, `widgetNote: "migration-proof"` with exact returned values. In the unmigrated negative case omit the ALTER and separate provision; matching metadata must not prevent the new-field API operation's provider failure. The existing unknown-failure response is HTTP 500 (`Handler/Utility.cs`); capture missing-column provenance inside tests without exposing it through the command. Compare the full existing `dms.Document` snapshots, rather than a helper's success boolean; do not compare different source/target resource-column sets as though the added nullable column were a regression.

- [x] **Step 2: Run both API provider filters and confirm the fixtures fail without the actual column migration/command path.**

```powershell
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj --filter "FullyQualifiedName~SchemaRestampMigration&TestCategory=PostgresqlIntegration"
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj --filter "FullyQualifiedName~SchemaRestampMigration&TestCategory=MssqlIntegration"
```

The API PostgreSQL suite uses `ConnectionStrings__DatabaseConnection`; configure it alongside the CLI suite's `ConnectionStrings__PostgresAdmin`. SQL Server uses `ConnectionStrings__MssqlAdmin`. Both contexts must load identical fully materialized inputs for baseline DDL, command hash, and runtime; raw-versus-materialized hashing would invalidate this proof.

- [x] **Step 3: Implement the focused scenario/fixture plumbing and both physical scripts.** Reuse the existing real HTTP pipeline/doubles, startup-status wait behavior, and baseline lease cleanup. Dispose each host and every lease in failure paths. Do not mutate a cached fixture or reuse a cached runtime verdict after administration.
- [x] **Step 4: Update the three owned documents and fixture README.** Document exact both-provider commands/options and the sequence: establish the independently validated migration recipe and backup/recovery boundary; stop/serialize DMS/workers/admin jobs; apply/validate physical DDL; run `ddl re-stamp` with target core/extension inputs and confirmation; run separate `ddl provision`; restart DMS/workers and verify fingerprint admission plus affected API behavior. Define preservation/no-op/corrupt-match/missing-row/uncertain-commit semantics and required privileges. A failed follow-up provision is separate from the committed stamp. Show the rerun without confirmation for a valid match.

Replace manual **fingerprint** re-key instructions with command references while retaining physical DDL. Relational docs sections 3/7 and DDL create-only design must state the bounded administrative exception and keep ordinary hash mismatch refusal, fresh provisioning for development/E2E, no hot reload, `v3`, and reprovision-only sibling requirements. Explain that metadata checks cannot certify physical shape or CDC continuity. The fixture example is not a named production-release upgrade or automatic rollback guarantee.

- [x] **Step 5: Format changed C# files, rerun both API commands, and compare runbook examples with shipped `ddl re-stamp --help`.** Expected: real migrated case succeeds and preserves data, unmigrated case fails the affected API operation, and source-hash provision/runtime refusal remains observable. Review the docs for stale absolute cross-hash prohibitions and manual re-key instructions; do not edit the historical PR.
- [x] **Step 6: Commit the fixture, scenarios, project reference, and documentation.** Suggested message: `test: validate and document a data-preserving schema re-stamp`.

## Completion and Review Handoff

| Acceptance criterion / safeguard | Delivery and evidence |
|---|---|
| AC1: supported command; runtime preflight passes after migration | Tasks 3/5: actual command, actual source/target schema hashes, physical ALTER, preserved data, fresh-host admission and new-field API operation. |
| AC2: both database engines | Tasks 2/4/5: provider transaction, process/error/concurrency matrix, and real migration on PostgreSQL and SQL Server. |
| AC3: valid matching no-op; missing singleton error | Tasks 1-4: full validation, no confirmation/DML on valid match, corrupt-match exception, missing-state refusal without initialization. |
| AC4: migration docs use command | Task 5: README/runbook/design exception and exercised fixture sequence. |
| Ordinary guards, preservation, safe errors, uncertain commit | Tasks 2-5: unchanged provision/runtime rejection, exact state snapshots, secret/control-character tests, deterministic failure/rerun evidence. |

Before claiming completion, confirm the task evidence covers the final versions of the whole SchemaTools unit project, focused SchemaTools both-provider suites, provisioning regressions, and both API migration filters; also run `git diff --check`. Re-run an earlier suite only if subsequent changes affect its behavior or its result leaves a concern unresolved. Record actual commands, executed counts, provider configuration class (no secrets), and results; separate setup failures/skips from assertion failures. Review the final diff for prohibited version changes, extra dependencies, unrelated cleanup, and relaxed ordinary guards.

No design blocker remains. Implementation evidence is still pending. A named release-upgrade promise requires a separate complete, validated recipe and approval. This plan requires user review and an execution-method choice before any product code, test implementation, builds, or database operations begin.
