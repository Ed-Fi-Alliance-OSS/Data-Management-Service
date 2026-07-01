# MSSQL Integration Setup Speedup Plan

## Goal

Bring GitHub Actions MSSQL integration setup time much closer to PostgreSQL integration setup time by avoiding repeated generated-DDL provisioning and replacing it with cheaper database lease operations.

The primary target is the setup portion of these jobs:

- Backend MSSQL integration shards in `.github/workflows/on-dms-pullrequest.yml`
- DMS API MSSQL integration tests
- SchemaTools MSSQL integration tests, if they use similar generated-DDL provisioning paths

The current evidence points to SQL Server database setup as the dominant cost, especially:

- Applying generated DDL batch-by-batch in `MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(...)`
- Creating snapshot-backed baseline slots in `MssqlGeneratedDdlBaselineDatabase`
- Reusing snapshot slots through `SINGLE_USER`, `RESTORE DATABASE ... FROM DATABASE_SNAPSHOT`, and `MULTI_USER`
- Clearing SQL client connection pools around database reset/drop/restore operations
- Running multiple generated-DDL provisions concurrently on hosted runners

PostgreSQL is faster because its test harness can provision one baseline and cheaply clone it with `CREATE DATABASE new_db TEMPLATE baseline_db`. SQL Server needs an equivalent "DDL once, lease many" mechanism.

## Non-Goals

- Do not weaken database isolation for tests that depend on clean per-test state.
- Do not remove MSSQL coverage just to reduce time.
- Do not make local test setup depend on GitHub Actions-specific behavior.
- Do not introduce a SQL Server feature unavailable in the existing Developer container image.

## Success Criteria

Use GitHub Actions timing artifacts to compare before and after.

Minimum acceptable improvement:

- Backend MSSQL shard setup time reduced by at least 35 percent.
- Slowest backend MSSQL shard `dotnet test` step reduced meaningfully, with shard 3 no longer dominated by fixture provisioning.
- No increase in MSSQL test flakiness over at least three PR runs.

Preferred outcome:

- Generated-DDL application happens once per unique fixture per shard process.
- Most test database leases are backup restores, snapshot restores, or simple data resets.
- Full generated-DDL provisioning becomes rare and visible in timing artifacts.

## Phase Advancement Rule

Each phase must be verified independently before work begins on the next phase.

Required gate for every phase:

1. Push the phase changes to the branch.
2. Let the real GitHub Actions workflow run for that pushed commit.
3. Review the workflow result and timing artifacts.
4. Move to the next phase only after the phase-specific validation passes in GitHub Actions.

## Current Code Paths

PostgreSQL baseline flow:

- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/PostgresqlGeneratedDdlBaselineDatabase.cs`
- Creates one provisioned database, detaches it, and leases isolated databases with `CREATE DATABASE ... TEMPLATE`.

MSSQL baseline flow:

- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlGeneratedDdlBaselineDatabase.cs`
- Creates a provisioned database, creates a database snapshot, and restores the same database from the snapshot when a slot is reused.
- If no idle slot exists, the harness creates another fully provisioned database and snapshot.

MSSQL direct provisioning flow:

- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/MssqlGeneratedDdlTestDatabase.cs`
- Creates a fresh database and applies every generated-DDL `GO` batch inside a transaction.
- Many backend MSSQL tests call `CreateProvisionedAsync(...)` directly.

API MSSQL cache:

- `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Mssql/MssqlBaselineCache.cs`
- Caches baseline databases per fixture for API-level tests.
- This pattern should be extended to backend MSSQL integration tests.

CI container setup:

- `.github/actions/start-mssql-test-container/action.yml`
- Already uses tmpfs for `/var/opt/mssql` and sets SQL Server memory limits, so the next major wins should be in the test harness rather than the container.

## Phase 0: Improve Measurement Before Changing Behavior

### Objective

Make the setup cost visible enough that each implementation step can be evaluated independently.

### Tasks

1. Extend `MssqlProvisioningTimingRecorder` to record named setup phases, not only total `CreateProvisionedAsync(...)` time.

   Suggested phases:

   - `create-empty-database`
   - `apply-generated-ddl`
   - `create-snapshot`
   - `restore-snapshot`
   - `backup-baseline`
   - `restore-backup`
   - `reset-database`
   - `drop-database`

2. Add these fields to timing records:

   - `FixtureSignature`
   - `GeneratedDdlHash`
   - `Phase`
   - `LeaseStrategy`
   - `Shard`
   - `TestWorkerId`, if available from NUnit context
   - Caller file/member/line, preserving the current caller fields

3. Keep `MSSQL_FIXTURE_TIMINGS_PATH` as the controlling environment variable.

4. Update `eng/ci/summarize-test-timings.ps1`, or add a companion summarizer, to group MSSQL setup time by:

   - Shard
   - Fixture signature
   - Caller file
   - Phase

### Validation

Run one PR build before behavior changes and upload the timing artifact. This is the baseline for later phases.

## Phase 1: Add Backend MSSQL Baseline Cache

### Objective

Stop backend MSSQL tests from repeatedly applying the same generated DDL when multiple fixtures use the same schema material.

### Design

Add an assembly-level cache in the backend MSSQL integration test project, similar to the DMS API `MssqlBaselineCache`.

Suggested file:

- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlBackendBaselineCache.cs`

Suggested public surface inside the test assembly:

```csharp
internal static class MssqlBackendBaselineCache
{
    public static Task<MssqlGeneratedDdlBaselineDatabase> CreateOrGetAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = 300
    );

    public static Task DisposeAllAsync();
}
```

Cache key:

- Fixture directory descriptor cache key when available
- Strict mode
- Generated DDL hash

The cache must reject a key reuse if the generated DDL hash changes.

### Implementation Steps

1. Add `MssqlBackendBaselineCache`.
2. Add a backend MSSQL `[SetUpFixture]` cleanup hook, or extend the existing `DatabaseSetupFixture`, to call `MssqlBackendBaselineCache.DisposeAllAsync()`.
3. Convert the highest-cost backend fixtures from direct `CreateProvisionedAsync(...)` to cached baseline leasing.
4. Start with fixtures that reuse common authoritative schemas:

   - `src/dms/backend/Fixtures/authoritative/sample`
   - `src/dms/backend/Fixtures/authoritative/ds-5.2`
   - `src/dms/backend/Fixtures/authoritative/ds-5.2-tpdm`
   - Profile fixtures used repeatedly by `MssqlProfile*` tests

### Test Conversion Rules

Use one of these patterns per test class.

Pattern A: one database per test class plus reset

- Use when the class only changes data.
- Acquire a database once in `OneTimeSetUp`.
- Continue calling `ResetAsync()` in `SetUp`.
- Release the lease in `OneTimeTearDown`.

Pattern B: one database per test

- Use when a test mutates schema, connection state, metadata, or hard-to-reset data.
- Acquire the lease in `SetUp`.
- Release in `TearDown`.

Pattern C: direct provisioning remains

- Use only for tests whose purpose is to validate direct generated-DDL provisioning behavior.
- Examples include harness tests for `MssqlGeneratedDdlTestDatabase` itself.

### Validation

Run each MSSQL shard and compare:

- Number of `apply-generated-ddl` phases
- Total setup seconds
- Number of fixture cache hits
- Test failures caused by shared state

## Phase 2: Add SQL Server Backup-Restore Baseline Leasing

### Objective

Provide SQL Server with a cheaper clone primitive closer to PostgreSQL `CREATE DATABASE ... TEMPLATE`.

Snapshot restore resets one existing database. It does not cheaply create independent databases for parallel leases. A full database backup can be restored repeatedly into unique databases, which should avoid reapplying generated DDL for every active slot.

### Proposed Common Test Harness Types

Add these to:

- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/`

Suggested files:

- `MssqlGeneratedDdlBackupBaselineDatabase.cs`
- `MssqlGeneratedDdlBackupBaselineLease.cs`
- `MssqlDatabaseFileMetadata.cs`

Suggested API:

```csharp
public sealed class MssqlGeneratedDdlBackupBaselineDatabase : IAsyncDisposable
{
    public static Task<MssqlGeneratedDdlBackupBaselineDatabase> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = 300
    );

    public Task<MssqlGeneratedDdlBackupBaselineLease> AcquireRestoredDatabaseAsync(
        int commandTimeoutSeconds = 300
    );
}

public sealed class MssqlGeneratedDdlBackupBaselineLease : IAsyncDisposable
{
    public MssqlGeneratedDdlTestDatabase Database { get; }
}
```

Required change to `MssqlGeneratedDdlTestDatabase`:

- Add an internal factory for an existing database name and connection string.

Example:

```csharp
internal static MssqlGeneratedDdlTestDatabase AttachExisting(string databaseName)
{
    return new(databaseName, MssqlTestDatabaseHelper.BuildConnectionString(databaseName));
}
```

Use `InternalsVisibleTo` only if this factory must remain hidden from other assemblies.

### Backup Creation Flow

1. Create one provisioned baseline database using the existing `CreateProvisionedAsync(...)`.
2. Set CI-friendly database options:

   - `ALTER DATABASE ... SET RECOVERY SIMPLE`
   - Consider `ALTER DATABASE ... SET AUTO_UPDATE_STATISTICS_ASYNC OFF`
   - Consider setting compatibility options only if existing tests require them

3. Run `CHECKPOINT`.
4. Build a backup file path in the same SQL Server data directory as the database files.
5. Execute:

```sql
BACKUP DATABASE [baseline_db]
TO DISK = N'<backup path>'
WITH INIT, COPY_ONLY, CHECKSUM, COMPRESSION;
```

6. Drop the provisioned baseline database after the backup is created if no longer needed.

Notes:

- SQL Server sees paths inside the SQL Server container, not the GitHub runner filesystem.
- In CI, `/var/opt/mssql` is tmpfs and is removed with the container.
- Local backup files may remain until the developer tears down the SQL Server container. Document this.

### Lease Flow

1. Generate a unique database name.
2. Read backup logical file names with `RESTORE FILELISTONLY`.
3. Build data/log target paths in the SQL Server data directory.
4. Execute:

```sql
RESTORE DATABASE [lease_db]
FROM DISK = N'<backup path>'
WITH
    MOVE N'<logical data file>' TO N'<lease data file path>',
    MOVE N'<logical log file>' TO N'<lease log file path>',
    RECOVERY,
    CHECKSUM,
    REPLACE;
```

5. Return `MssqlGeneratedDdlTestDatabase.AttachExisting(leaseDatabaseName)`.
6. Dispose by clearing relevant connection pools and dropping the leased database.

### Concurrency

Use the existing shared-baseline dictionary pattern:

- One backup per fixture signature and generated-DDL hash.
- Many concurrent leases can restore from the same backup.
- Guard backup creation with `Lazy<Task<...>>`.

Do not serialize all backup restores initially. Measure first. If hosted runners degrade under parallel restores, add a small semaphore controlled by an environment variable:

- `MSSQL_BACKUP_RESTORE_MAX_CONCURRENCY`

Default:

- `0` or unset means no additional throttling.
- CI can set `2` if restore storms are slower than limited concurrency.

### Cleanup

Leased databases should always be dropped on lease disposal.

Backup files are harder to delete portably from inside SQL Server without enabling unsafe features. Preferred cleanup:

- CI: rely on container removal.
- Local: document teardown or provide an optional developer cleanup script that runs `docker exec` when the local container name is known.

Do not enable `xp_cmdshell` for cleanup.

### Validation

Add integration tests for the new backup baseline class:

- Creates two independent leases from one generated-DDL backup.
- Data inserted in lease A does not appear in lease B.
- Disposing a lease drops its database.
- Reusing the same fixture signature with different generated DDL throws.
- Concurrent leases do not collide on file names.

## Phase 3: Add Strategy Selection and Roll Out Safely

### Objective

Allow A/B testing between the current snapshot-slot strategy and the new backup-restore strategy.

### Strategy Switch

Add an environment variable:

- `MSSQL_GENERATED_DDL_LEASE_STRATEGY`

Values:

- `snapshot-slot`
- `backup-restore`

Initial default:

- `snapshot-slot` for the first PR that introduces the new implementation.

CI rollout:

1. Introduce implementation with default `snapshot-slot`.
2. Add one experimental workflow run or branch run with `backup-restore`.
3. If stable and faster, set backend MSSQL shards to `backup-restore`.
4. If still stable, set DMS API MSSQL integration to `backup-restore`.
5. Remove or lower reliance on snapshot-slot only after multiple green runs.

### Where to Apply the Switch

Create a small facade in the common test project:

```csharp
public static class MssqlGeneratedDdlLeaseStrategy
{
    public static Task<IMssqlGeneratedDdlBaseline> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = 300
    );
}
```

If an interface causes too much churn, keep the switch in backend/API cache classes and return a common lease wrapper.

The goal is not architectural purity. The goal is to avoid spreading environment-variable checks across many tests.

## Phase 4: Convert Backend MSSQL Tests by Setup Cost

### Objective

Convert the most expensive test classes first and avoid broad risky churn.

### Conversion Order

Use timing artifacts from Phase 0. If those are unavailable, start with repeated authoritative/profile generated-DDL callers.

Likely high-impact groups:

1. Profile merge/routing tests

   - `MssqlProfileRootTableOnlyMergeTests.cs`
   - `MssqlProfileRootTableOnlyMergeFixtureTests.cs`
   - `MssqlProfileSeparateTableMergeFixtureTests.cs`
   - `MssqlProfileCollectionAlignedExtensionMergeTests.cs`
   - `MssqlProfileNestedCollectionMergeTests.cs`
   - `MssqlProfileTopLevelCollectionMergeTests.cs`
   - `MssqlProfileTopLevelCollectionReferenceBackedMergeTests.cs`
   - `MssqlProfileExecutorRoutingTests.cs`
   - `MssqlProfileIfMatchEtagTests.cs`

2. Authoritative sample and DS 5.2 smoke tests

   - `MssqlGeneratedDdlAuthoritativeSmokeTests.cs`
   - `MssqlGeneratedDdlAuthoritativeDs52TpdmSmokeTests.cs`
   - `MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs`
   - `MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociationSmokeTests.cs`
   - `MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs`

3. Link/reference/descriptor integration tests using authoritative fixtures

   - `MssqlAuthorizationLinkInjectionIntegrationTests.cs`
   - `MssqlLinkInjectionIntegrationTests.cs`
   - `MssqlNestedCollectionReferenceLinkInjectionIntegrationTests.cs`
   - `MssqlExtensionChildCollectionReferenceLinkInjectionIntegrationTests.cs`
   - `MssqlCollectionAlignedExtensionReferenceLinkInjectionIntegrationTests.cs`
   - `MssqlDescriptorReadGetTests.cs`
   - `MssqlDescriptorReadQueryFilterTests.cs`
   - `MssqlDescriptorReadTestSupportTests.cs`

4. Remaining direct `CreateProvisionedAsync(...)` callers, excluding harness tests.

### Per-Class Decision Checklist

For each class:

1. Does the class mutate schema?

   - Yes: use per-test backup-restore lease or leave direct provisioning if the test validates provisioning.
   - No: continue.

2. Does `ResetAsync()` already keep tests independent?

   - Yes: use one class-level lease and keep `ResetAsync()`.
   - No: use per-test lease.

3. Does the class run in parallel with other fixtures?

   - If class-level DB is used, ensure no static mutable state or shared database connection leaks.

4. Does the test rely on database name, file path, snapshot name, or drop behavior?

   - If yes, review manually before conversion.

### Validation After Each Batch

Run:

```powershell
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj --filter "Category=MssqlCiShard<N>"
```

On CI, verify:

- No new failures.
- Fewer full `apply-generated-ddl` phases.
- Setup time shifts from `apply-generated-ddl` to `restore-backup`.
- Total setup time decreases.

## Phase 5: Optimize Remaining Direct Provisioning

### Objective

Reduce cost for tests that still require full database creation and generated-DDL application.

### Database Creation Options

Update `MssqlTestDatabaseHelper.CreateDatabase(...)` or add a specialized creation method for generated-DDL tests.

Consider:

```sql
CREATE DATABASE [name]
ON PRIMARY
(
    NAME = N'<name>',
    FILENAME = N'<data path>',
    SIZE = 256MB,
    FILEGROWTH = 256MB
)
LOG ON
(
    NAME = N'<name>_log',
    FILENAME = N'<log path>',
    SIZE = 128MB,
    FILEGROWTH = 128MB
);

ALTER DATABASE [name] SET RECOVERY SIMPLE;
```

Measure before keeping these values. The goal is to avoid repeated autogrowth while applying generated DDL.

### DDL Application

Keep `GO` batch splitting for correctness. Do not attempt to concatenate SQL Server batches that require separate compilation units.

Possible low-risk improvements:

- Record per-batch durations to identify pathological generated SQL.
- Avoid `SqlConnection.ClearPool(connection)` unless a measured failure mode requires it after DDL application.
- Ensure command timeout is high enough to avoid false failures on slow hosted runners.

### Concurrency Guard

If timing shows hosted runners get slower when multiple full provisions happen at once, add an optional semaphore around the full generated-DDL provisioning path.

Environment variable:

- `MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY`

Suggested CI value:

- `1` or `2`

This can reduce resource thrashing even if it serializes a subset of setup work.

## Phase 6: Optimize Reset for Class-Level Leases

### Objective

Make `ResetAsync()` cheap enough that class-level leased databases remain attractive.

### Current Behavior

`MssqlDatabaseResetSql.Build(...)` discovers tables and sequences at reset time, disables triggers/constraints, deletes rows, reseeds identities, restarts sequences, and reenables constraints/triggers.

### Improvements

1. Precompute reset SQL after DDL provisioning.

   - Generated DDL fixes the table list for a database.
   - Avoid repeated metadata queries in every reset.

2. Add a truncate fast path.

   - Use `TRUNCATE TABLE` for tables that are not blocked by foreign keys.
   - Fall back to delete for the remaining tables.
   - Validate carefully because SQL Server truncation has stricter FK rules than PostgreSQL.

3. Measure whether `WITH CHECK CHECK CONSTRAINT ALL` dominates reset.

   - If so, consider whether tests need trusted constraints after every reset.
   - Be conservative. Query plan and FK behavior may depend on trusted constraints.

### Validation

Add focused tests for reset behavior:

- Rows are removed from all generated resource tables.
- Baseline tables are preserved.
- Identity values and sequences restart as expected.
- Foreign keys and triggers are enabled after reset.

## Phase 7: Update GitHub Actions Configuration

### Objective

Use the new harness behavior in CI and rebalance shards based on real setup cost.

### Workflow Changes

File:

- `.github/workflows/on-dms-pullrequest.yml`

Add environment variables to backend MSSQL shard jobs:

```yaml
env:
  MSSQL_GENERATED_DDL_LEASE_STRATEGY: backup-restore
  MSSQL_FIXTURE_TIMINGS_PATH: ${{ github.workspace }}/TestResults/test-timings/mssql-shard-${{ matrix.shard }}/mssql-fixture-setup-timings.csv
```

Optional after measurement:

```yaml
  MSSQL_BACKUP_RESTORE_MAX_CONCURRENCY: "2"
  MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY: "1"
```

Review tmpfs sizing:

- Backup-restore needs room for the baseline backup and multiple concurrently restored databases.
- Current `MSSQL_TMPFS_SIZE` is `4g`.
- If restore failures indicate insufficient space, increase to `6g` or reduce restore concurrency.

### Shard Rebalancing

After conversion, rebalance MSSQL shards using timing artifacts, not test counts.

Process:

1. Export per-test and per-fixture setup times.
2. Sum total runtime by category.
3. Move categories so each shard has similar total setup plus test-body time.
4. Re-run at least one PR build to confirm the slowest shard moved down.

## Risks and Mitigations

### Backup files may remain in local SQL Server containers

Mitigation:

- Rely on CI container teardown.
- Document local cleanup through existing MSSQL teardown guidance.
- Do not enable `xp_cmdshell`.

### Backup restore may not beat snapshot restore for every case

Mitigation:

- Keep `snapshot-slot` as a strategy.
- Use timing data to select strategy per suite if necessary.

### Parallel restores may overload hosted runners

Mitigation:

- Add `MSSQL_BACKUP_RESTORE_MAX_CONCURRENCY`.
- Keep NUnit fixture parallelism unchanged initially, then tune only if measurements justify it.

### Some tests may rely on class-level database state

Mitigation:

- Convert in batches.
- Prefer one class-level lease plus existing `ResetAsync()` for tests already written that way.
- Use per-test leases for tests with unclear isolation requirements.

### Schema-mutating tests may break shared baseline assumptions

Mitigation:

- Keep schema-mutating tests on per-test leases.
- Keep harness tests on direct provisioning when they validate provisioning behavior itself.

### SQL Server file path handling differs between Linux containers and local Windows SQL Server

Mitigation:

- Derive data/log/backup paths from `sys.master_files`.
- Preserve both `/` and `\` separator handling, following the existing snapshot path helper.
