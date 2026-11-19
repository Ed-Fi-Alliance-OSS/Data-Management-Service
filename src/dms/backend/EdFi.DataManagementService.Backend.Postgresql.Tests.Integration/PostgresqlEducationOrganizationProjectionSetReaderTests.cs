// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.PostgresqlProjectionReaders;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;
using Stage = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionReadStage;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Base for the projection reader fixtures: a provisioned DS 5.2 baseline, and an isolated copy of it
/// with the seed helpers for each test.
/// </summary>
public abstract class PostgresqlEducationOrganizationProjectionFixtureBase
{
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;

    internal PostgresqlGeneratedDdlFixture Fixture { get; private set; } = null!;
    internal PostgresqlGeneratedDdlTestDatabase Database { get; private set; } = null!;
    internal PostgresqlProjectionSeed Seed { get; private set; } = null!;
    internal RecordingLogger<PostgresqlEducationOrganizationProjectionSetReader> Logger
    {
        get;
        private set;
    } = null!;

    [OneTimeSetUp]
    public async Task ProvisionBaseline()
    {
        Fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"EducationOrganizationProjection:{Fixture.MappingSet.Key.EffectiveSchemaHash}",
            Fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task CreateIsolatedDatabase()
    {
        Database = await _baseline.CreateIsolatedDatabaseAsync();
        Seed = await PostgresqlProjectionSeed.CreateAsync(Database);
        Logger = new();
    }

    [TearDown]
    public async Task DropIsolatedDatabase()
    {
        NpgsqlConnection.ClearAllPools();
        if (Database is not null)
        {
            await Database.DisposeAsync();
            Database = null!;
        }
    }

    [OneTimeTearDown]
    public async Task ReleaseBaseline()
    {
        if (_baseline is not null)
        {
            await _baseline.DisposeAsync();
        }
    }

    internal Task<Result> ReadAsync(
        string? connectionString = null,
        EducationOrganizationProjectionSetReadRequest? request = null,
        IEducationOrganizationProjectionReadObserver? observer = null,
        CancellationToken cancellationToken = default
    ) =>
        Create(connectionString ?? Database.ConnectionString, Logger, observer)
            .ReadSetAsync(request ?? Request(Fixture.MappingSet), cancellationToken);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Education_Organization_Projection_Set_Reader
    : PostgresqlEducationOrganizationProjectionFixtureBase
{
    [Test]
    public async Task It_reads_every_core_row_with_its_raw_reference_slots_in_identifier_order()
    {
        var expected = await Seed.StandardHierarchyAsync();

        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>().Which.Rows.Should().Equal(expected);
        AssertRedacted(Logger, Database.DatabaseName);
    }

    [Test]
    public async Task It_reads_an_empty_store_as_an_empty_set()
    {
        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>().Which.Rows.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_a_set_that_fills_the_cap_exactly()
    {
        var expected = await Seed.StandardHierarchyAsync();

        var result = await ReadAsync(request: Request(Fixture.MappingSet, maxProjectionRows: expected.Count));

        result.Should().BeOfType<Result.Set>().Which.Rows.Should().HaveCount(expected.Count);
    }

    [Test]
    public async Task It_reports_a_set_one_row_over_the_cap_as_too_large()
    {
        var expected = await Seed.StandardHierarchyAsync();

        var result = await ReadAsync(
            request: Request(Fixture.MappingSet, maxProjectionRows: expected.Count - 1)
        );

        result.Should().Be(new Result.TooLarge(expected.Count - 1));
    }

    [Test]
    public async Task It_round_trips_names_of_75_supplementary_characters()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 75));
        await Seed.StateEducationAgencyAsync(7, name, name);

        var result = await ReadAsync();

        result
            .Should()
            .BeOfType<Result.Set>()
            .Which.Rows.Should()
            .Equal(
                new EducationOrganizationProjectionRow(
                    7,
                    "Ed-Fi:StateEducationAgency",
                    name,
                    name,
                    null,
                    null,
                    null,
                    null
                )
            );
    }

    [Test]
    public async Task It_relies_on_the_database_to_reject_one_identifier_shared_by_two_types()
    {
        // Assumption A-1: EducationOrganizationIdentity keeps identifiers unique across member types.
        await Seed.LocalEducationAgencyAsync(100, "District 100");

        var act = () => Seed.SchoolAsync(100, "School 100");

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    [Test]
    public async Task It_attaches_every_command_to_the_transaction_and_closes_the_reader_before_commit()
    {
        await Seed.StandardHierarchyAsync();
        var observer = new RecordingProjectionReadObserver();

        var result = await ReadAsync(observer: observer);

        result.Should().BeOfType<Result.Set>();
        observer.Commands.Should().HaveCount(2);
        observer.Commands.Should().OnlyContain(static command => command.Attached);
        observer.Commands[0].CommandText.Should().Be("SET LOCAL lock_timeout = '5s'");
        observer.ReaderClosedBeforeCommit.Should().BeTrue();
    }

    [Test]
    public async Task It_returns_no_rows_when_the_commit_fails()
    {
        await Seed.StandardHierarchyAsync();
        var observer = new RecordingProjectionReadObserver
        {
            OnBeforeCommit = async (connection, _) =>
            {
                int processId = ((NpgsqlConnection)connection).ProcessID;
                await Database.ExecuteNonQueryAsync(
                    "SELECT pg_terminate_backend(@pid);",
                    new NpgsqlParameter("pid", processId)
                );
                await WaitUntilAsync(
                    async () =>
                        await Database.ExecuteScalarAsync<long>(
                            "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid;",
                            new NpgsqlParameter("pid", processId)
                        ) == 0,
                    "the reader's backend has exited"
                );
            },
        };

        var result = await ReadAsync(observer: observer);

        observer.ReaderClosedBeforeCommit.Should().BeTrue();
        result.Should().Be(new Result.TargetUnavailable(Stage.Commit, "PostgresException(57P01)"));
        AssertRedacted(Logger, Database.DatabaseName);
    }

    [Test]
    public async Task It_reports_a_failure_beginning_the_transaction_as_unavailable_in_the_prepare_stage()
    {
        // lock_timeout is capped at 2147483647 ms, so this many seconds is rejected by the server.
        var result = await ReadAsync(request: Request(Fixture.MappingSet, lockTimeoutSeconds: int.MaxValue));

        result.Should().Be(new Result.TargetUnavailable(Stage.Prepare, "PostgresException(22023)"));
        AssertRedacted(Logger, Database.DatabaseName);
    }

    [Test]
    public async Task It_leaves_no_session_setting_on_a_pooled_connection()
    {
        var pooled = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = "projection-pooled-" + Guid.NewGuid().ToString("N"),
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
            NoResetOnClose = true,
        }.ConnectionString;

        (await ReadAsync(pooled, Request(Fixture.MappingSet, lockTimeoutSeconds: 7)))
            .Should()
            .BeOfType<Result.Set>();

        await using var connection = new NpgsqlConnection(pooled);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('lock_timeout') || '|' || current_setting('transaction_isolation');",
            connection
        );
        ((string?)await command.ExecuteScalarAsync()).Should().Be("0|read committed");
    }

    [Test]
    public async Task It_reports_an_unresolvable_host_as_unavailable_in_the_acquire_stage()
    {
        var result = await ReadAsync(
            $"Host=hostile-host.invalid;Database=hostile_database;Username=hostile;Password={HostileValue};Timeout=5"
        );

        result.Should().Be(new Result.TargetUnavailable(Stage.Acquire, "SocketException"));
        AssertRedacted(Logger, "hostile-host", "hostile_database");
        Logger
            .Records.Should()
            .ContainSingle()
            .Which.Message.Should()
            .Contain("Acquire")
            .And.Contain("SocketException");
    }

    [Test]
    public async Task It_reports_a_malformed_connection_option_as_unavailable_in_the_acquire_stage()
    {
        var result = await ReadAsync($"Host=127.0.0.1;{HostileValue}=1");

        result.Should().Be(new Result.TargetUnavailable(Stage.Acquire, "ArgumentException"));
        AssertRedacted(Logger);
    }

    [Test]
    public async Task It_reports_an_incompatible_mapping_set_before_acquiring_a_connection()
    {
        var mappingSet = Fixture.MappingSet with
        {
            Model = Fixture.MappingSet.Model with { AbstractUnionViewsInNameOrder = [] },
        };

        var result = await ReadAsync(
            $"Host=hostile-host.invalid;Password={HostileValue}",
            Request(mappingSet)
        );

        result
            .Should()
            .Be(
                new Result.MappingIncompatible(
                    new EducationOrganizationProjectionMappingIncompatibility(
                        EducationOrganizationProjectionMappingIncompatibilityReason.UnionViewMissing
                    )
                )
            );
        AssertRedacted(Logger, "hostile-host");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Education_Organization_Projection_Set_Reader_Over_An_Altered_Schema
    : PostgresqlEducationOrganizationProjectionFixtureBase
{
    [SetUp]
    public async Task SeedHierarchy()
    {
        await Seed.StandardHierarchyAsync();
    }

    [Test]
    public async Task It_reports_a_dropped_column_as_a_missing_schema_object()
    {
        await Database.ExecuteNonQueryAsync(
            """ALTER TABLE "edfi"."School" DROP COLUMN "ShortNameOfInstitution" CASCADE;"""
        );

        var result = await ReadAsync();

        result
            .Should()
            .Be(
                new Result.SchemaIncompatible(
                    EducationOrganizationProjectionSchemaIncompatibilityReason.SchemaObjectMissing,
                    "42703"
                )
            );
        AssertRedacted(Logger, Database.DatabaseName, "School", "ShortNameOfInstitution");
    }

    [Test]
    public async Task It_reports_one_arm_of_another_column_type_as_a_data_type_incompatibility()
    {
        await Database.ExecuteNonQueryAsync(
            """
            ALTER TABLE "edfi"."StateEducationAgency"
            ALTER COLUMN "ShortNameOfInstitution" TYPE integer USING NULL;
            """
        );

        var result = await ReadAsync();

        result
            .Should()
            .Be(
                new Result.SchemaIncompatible(
                    EducationOrganizationProjectionSchemaIncompatibilityReason.DataTypeIncompatible,
                    "42804"
                )
            );
        AssertRedacted(Logger, Database.DatabaseName, "ShortNameOfInstitution");
    }

    [Test]
    public async Task It_reports_a_value_it_cannot_read_as_its_required_type_as_a_materialization_mismatch()
    {
        // Every arm changes together, so the statement succeeds and returns an integer column where
        // the row requires a string.
        foreach (
            var table in new[]
            {
                "StateEducationAgency",
                "EducationServiceCenter",
                "LocalEducationAgency",
                "School",
            }
        )
        {
            await Database.ExecuteNonQueryAsync(
                $"""
                ALTER TABLE "edfi"."{table}" ALTER COLUMN "ShortNameOfInstitution" TYPE integer USING 1;
                """
            );
        }

        var result = await ReadAsync();

        result
            .Should()
            .Be(
                new Result.SchemaIncompatible(
                    EducationOrganizationProjectionSchemaIncompatibilityReason.MaterializationTypeMismatch,
                    null
                )
            );
        AssertRedacted(Logger, Database.DatabaseName);
    }

    [Test]
    public async Task It_reads_dangling_and_wrong_type_references_as_stored()
    {
        await Database.ExecuteNonQueryAsync(
            """
            ALTER TABLE "edfi"."LocalEducationAgency" DROP CONSTRAINT "FK_LocalEducationAgency_EducationServiceCenter_RefKey";
            ALTER TABLE "edfi"."LocalEducationAgency" DROP CONSTRAINT "FK_LocalEducationAgency_StateEducationAgency_RefKey";
            """
        );
        var parent = new ProjectionReference(
            await Database.ExecuteScalarAsync<long>(
                """SELECT "DocumentId" FROM "edfi"."LocalEducationAgency" WHERE "LocalEducationAgencyId" = 100;"""
            ),
            100
        );

        // A valid parent, which precedence selects; a service center slot holding a local education
        // agency's identifier; and a state agency that does not exist. Neither of the last two is
        // selected, and both must reach validation unchanged.
        await Seed.LocalEducationAgencyAsync(
            102,
            "District 102",
            parentLocalEducationAgency: parent,
            educationServiceCenter: new ProjectionReference(999_001, 101),
            stateEducationAgency: new ProjectionReference(999_002, 424242)
        );

        var result = await ReadAsync();

        result
            .Should()
            .BeOfType<Result.Set>()
            .Which.Rows.Should()
            .ContainSingle(static row => row.EducationOrganizationId == 102)
            .Which.Should()
            .Be(
                new EducationOrganizationProjectionRow(
                    102,
                    "Ed-Fi:LocalEducationAgency",
                    "District 102",
                    null,
                    null,
                    100,
                    101,
                    424242
                )
            );
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Education_Organization_Projection_Set_Reader_Under_Concurrent_Transactions
    : PostgresqlEducationOrganizationProjectionFixtureBase
{
    private const string WaitingOnLockSql = """
        SELECT count(*) FROM pg_stat_activity
        WHERE application_name = @app AND wait_event_type = 'Lock';
        """;

    private string _applicationName = null!;
    private string _readerConnectionString = null!;

    [SetUp]
    public async Task SeedHierarchy()
    {
        await Seed.StandardHierarchyAsync();
        _applicationName = "projection-reader-" + Guid.NewGuid().ToString("N");
        _readerConnectionString = Tagged(Database.ConnectionString, _applicationName);
    }

    [Test]
    public async Task It_never_reads_one_uncommitted_change_without_the_other()
    {
        // The round-1 counterexample: T1 changes A and B and stays open; T2 changes C and commits.
        await using var t1 = new NpgsqlConnection(Database.ConnectionString);
        await t1.OpenAsync();
        await using var t1Transaction = await t1.BeginTransactionAsync();
        await ExecuteAsync(
            t1,
            """UPDATE "edfi"."StateEducationAgency" SET "NameOfInstitution" = 'A-new' WHERE "StateEducationAgencyId" = 1;"""
        );
        await ExecuteAsync(
            t1,
            """UPDATE "edfi"."School" SET "NameOfInstitution" = 'B-new' WHERE "SchoolId" = 900001;"""
        );
        await Database.ExecuteNonQueryAsync(
            """UPDATE "edfi"."EducationServiceCenter" SET "NameOfInstitution" = 'C-new' WHERE "EducationServiceCenterId" = 10;"""
        );

        var before = (Result.Set)await ReadAsync(_readerConnectionString);
        await t1Transaction.CommitAsync();
        var after = (Result.Set)await ReadAsync(_readerConnectionString);

        Names(before).Should().Equal("State Agency", "Lone School", "C-new");
        Names(after).Should().Equal("A-new", "B-new", "C-new");
    }

    [Test]
    public async Task It_propagates_cancellation_while_blocked_and_releases_its_transaction_and_connection()
    {
        await using var holder = await HoldAccessExclusiveLockAsync("School");
        using var cancellation = new CancellationTokenSource();

        var read = ReadAsync(
            _readerConnectionString,
            Request(Fixture.MappingSet, lockTimeoutSeconds: 60, commandTimeoutSeconds: 120),
            cancellationToken: cancellation.Token
        );
        await WaitUntilAsync(
            async () => await CountAsync(Database.ConnectionString, WaitingOnLockSql, _applicationName) == 1,
            "the read is waiting on the lock"
        );
        await cancellation.CancelAsync();

        await read.Awaiting(static task => task).Should().ThrowAsync<OperationCanceledException>();
        await WaitUntilAsync(
            async () =>
                await CountAsync(
                    Database.ConnectionString,
                    "SELECT count(*) FROM pg_stat_activity WHERE application_name = @app;",
                    _applicationName
                ) == 0,
            "the read's session has ended"
        );
        Logger
            .Records.Should()
            .NotContain(static record => record.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Test]
    public async Task It_reports_a_lock_timeout_as_unavailable_in_the_execute_stage()
    {
        await using var holder = await HoldAccessExclusiveLockAsync("School");

        var result = await ReadAsync(
            _readerConnectionString,
            Request(Fixture.MappingSet, lockTimeoutSeconds: 1)
        );

        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, "PostgresException(55P03)"));
        AssertRedacted(Logger, Database.DatabaseName, "School");
    }

    [Test]
    public async Task It_reports_a_command_timeout_as_unavailable_in_the_execute_stage()
    {
        await using var holder = await HoldAccessExclusiveLockAsync("School");

        var result = await ReadAsync(
            _readerConnectionString,
            Request(Fixture.MappingSet, lockTimeoutSeconds: 60, commandTimeoutSeconds: 1)
        );

        // Npgsql raises its client-side command timeout without a SQLSTATE, so it is classified by the
        // documented transient fallback.
        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, "NpgsqlException"));
        AssertRedacted(Logger, Database.DatabaseName, "School");
    }

    [Test]
    public async Task It_reports_being_chosen_as_a_deadlock_victim_as_unavailable_in_the_execute_stage()
    {
        // The read locks the four tables in statement order while parsing. A writer holding School
        // makes it wait holding StateEducationAgency; the writer then asks for StateEducationAgency.
        // New sessions check for deadlock after 5 s and the writer after 60 s, so the read detects the
        // cycle and is the one aborted.
        await Database.ExecuteNonQueryAsync(
            $"""ALTER DATABASE "{Database.DatabaseName}" SET deadlock_timeout = '5s';"""
        );
        await using var holder = await HoldAccessExclusiveLockAsync(
            "School",
            "SET deadlock_timeout = '60s';"
        );

        var read = ReadAsync(_readerConnectionString, Request(Fixture.MappingSet, lockTimeoutSeconds: 60));
        await WaitUntilAsync(
            async () => await CountAsync(Database.ConnectionString, WaitingOnLockSql, _applicationName) == 1,
            "the read is waiting on the lock"
        );
        var writerSecondLock = ExecuteAsync(
            holder.Connection,
            """LOCK TABLE "edfi"."StateEducationAgency" IN ACCESS EXCLUSIVE MODE;"""
        );

        var result = await read;
        await writerSecondLock;

        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, "PostgresException(40P01)"));
        AssertRedacted(Logger, Database.DatabaseName, "School");
    }

    private async Task<LockHolder> HoldAccessExclusiveLockAsync(string table, string? sessionSetup = null)
    {
        var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        if (sessionSetup is not null)
        {
            await ExecuteAsync(connection, sessionSetup);
        }

        var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"""LOCK TABLE "edfi"."{table}" IN ACCESS EXCLUSIVE MODE;""");
        return new LockHolder(connection, transaction);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The names of A, B and C, in that order.</summary>
    private static IEnumerable<string> Names(Result.Set set) =>
        new long[] { 1, 900001, 10 }.Select(id =>
            set.Rows.Single(row => row.EducationOrganizationId == id).NameOfInstitution
        );

    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}

/// <summary>
/// Cleanup exclusion through the production acquisition path: the shared data-source cache, a leased
/// data source and the request's data-source provider, with a pool of exactly one connection so a
/// second rental shows whether the first physical session was reused.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Education_Organization_Projection_Read_Through_The_Production_Data_Source
    : PostgresqlEducationOrganizationProjectionFixtureBase
{
    private NpgsqlDataSourceCache _cache = null!;
    private NpgsqlDataSourceProvider _dataSourceProvider = null!;

    [SetUp]
    public async Task CreateDataSource()
    {
        await Seed.StandardHierarchyAsync();
        string connectionString = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = "projection-datasource-" + Guid.NewGuid().ToString("N"),
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
        }.ConnectionString;
        var selection = new DataStoreSelection();
        selection.SetEffectiveTarget(
            new EffectiveDataStoreTarget(EffectiveTargetKind.Primary, connectionString)
        );
        _cache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _dataSourceProvider = new NpgsqlDataSourceProvider(
            selection,
            _cache,
            NullLogger<NpgsqlDataSourceProvider>.Instance
        );
    }

    [TearDown]
    public async Task DisposeDataSource()
    {
        // Before the base drops the database: the cache's data sources are not in the pool registry
        // that NpgsqlConnection.ClearAllPools clears.
        await _dataSourceProvider.DisposeAsync();
        _cache.Dispose();
    }

    [Test]
    public async Task It_never_rents_the_session_again_after_its_cleanup_failed_on_an_open_connection()
    {
        var (result, readSession) = await ReadThroughProductionPathAsync(restoreStatement: "SELECT 1 / 0;");

        result.Should().BeOfType<Result.Set>();
        Logger
            .Records.Should()
            .ContainSingle(static record => record.Level == LogLevel.Warning)
            .Which.Message.Should()
            .Contain("restoring the session")
            .And.Contain("PostgresException(22012)");

        await using var next = await _dataSourceProvider.DataSource.OpenConnectionAsync();
        next.ProcessID.Should().NotBe(readSession);
        (
            await Database.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid;",
                new NpgsqlParameter("pid", readSession)
            )
        )
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_rents_the_same_session_again_after_a_read_whose_cleanup_succeeded()
    {
        // The control: with pooling as configured, a clean read's session is reused, so the test
        // above cannot pass merely because sessions are never reused.
        var (result, readSession) = await ReadThroughProductionPathAsync(restoreStatement: "SELECT 1;");

        result.Should().BeOfType<Result.Set>();
        Logger.Records.Should().NotContain(static record => record.Level >= LogLevel.Warning);

        await using var next = await _dataSourceProvider.DataSource.OpenConnectionAsync();
        next.ProcessID.Should().Be(readSession);
    }

    private async Task<(Result Result, int ReadSession)> ReadThroughProductionPathAsync(
        string restoreStatement
    )
    {
        int readSession = 0;
        var observer = new RecordingProjectionReadObserver
        {
            OnAfterAcquire = (connection, _) =>
            {
                readSession = ((NpgsqlConnection)connection).ProcessID;
                return Task.CompletedTask;
            },
        };
        var reader = new PostgresqlEducationOrganizationProjectionSetReader(
            _dataSourceProvider,
            Logger,
            observer,
            provider => provider with { SessionRestoreStatement = restoreStatement }
        );

        var result = await reader.ReadSetAsync(Request(Fixture.MappingSet), CancellationToken.None);
        readSession.Should().NotBe(0);
        return (result, readSession);
    }
}
