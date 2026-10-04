// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Mssql.Tests.Integration.MssqlProjectionReaders;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;
using Stage = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionReadStage;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Base for the projection reader fixtures: a leased DS 5.2 database, reset and reseeded for each test.
/// </summary>
public abstract class MssqlEducationOrganizationProjectionFixtureBase
{
    private IMssqlGeneratedDdlBaselineLease? _lease;

    internal MssqlGeneratedDdlFixture Fixture { get; private set; } = null!;
    internal MssqlGeneratedDdlTestDatabase Database { get; private set; } = null!;
    internal MssqlProjectionSeed Seed { get; private set; } = null!;
    internal RecordingLogger<MssqlEducationOrganizationProjectionSetReader> Logger { get; private set; } =
        null!;

    [OneTimeSetUp]
    public async Task LeaseDatabase()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        Fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _lease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            Fixture.GeneratedDdl
        );
        Database = _lease.Database;
    }

    [SetUp]
    public async Task ResetAndSeed()
    {
        await Database.ResetAsync();
        Seed = await MssqlProjectionSeed.CreateAsync(Database);
        Logger = new();
    }

    [TearDown]
    public void ClearPools()
    {
        SqlConnection.ClearAllPools();
    }

    [OneTimeTearDown]
    public async Task ReleaseDatabase()
    {
        if (_lease is not null)
        {
            await _lease.DisposeAsync();
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
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Education_Organization_Projection_Set_Reader
    : MssqlEducationOrganizationProjectionFixtureBase
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
    public async Task It_round_trips_names_of_75_utf16_units_with_supplementary_characters()
    {
        // nvarchar(75) counts UTF-16 units: 37 supplementary characters and one BMP character.
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 37)) + "é";
        name.Length.Should().Be(75);
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

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().BeOneOf(2627, 2601);
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
        observer.Commands[0].CommandText.Should().Be("SET LOCK_TIMEOUT 5000");
        observer.ReaderClosedBeforeCommit.Should().BeTrue();
    }

    [Test]
    public async Task It_returns_no_rows_when_the_commit_fails()
    {
        await Seed.StandardHierarchyAsync();
        string applicationName = "projection-commit-" + Guid.NewGuid().ToString("N");
        var observer = new RecordingProjectionReadObserver
        {
            OnBeforeCommit = (connection, _) =>
                KillSessionAsync(
                    Database.ConnectionString,
                    ((SqlConnection)connection).ServerProcessId,
                    applicationName
                ),
        };

        var result = await ReadAsync(Tagged(Database.ConnectionString, applicationName), observer: observer);

        observer.ReaderClosedBeforeCommit.Should().BeTrue();
        result
            .Should()
            .BeOfType<Result.TargetUnavailable>()
            .Which.Should()
            .Match<Result.TargetUnavailable>(static unavailable =>
                unavailable.Stage == Stage.Commit && unavailable.Describe.StartsWith("SqlException(")
            );
        AssertRedacted(Logger, Database.DatabaseName);
    }

    [Test]
    public async Task It_reports_a_failure_beginning_the_transaction_as_unavailable_in_the_prepare_stage()
    {
        string applicationName = "projection-prepare-" + Guid.NewGuid().ToString("N");
        var observer = new RecordingProjectionReadObserver
        {
            OnAfterAcquire = (connection, _) =>
                KillSessionAsync(
                    Database.ConnectionString,
                    ((SqlConnection)connection).ServerProcessId,
                    applicationName
                ),
        };

        // Without ConnectRetryCount=0, SqlClient would transparently reconnect the killed idle session.
        var connectionString = new SqlConnectionStringBuilder(
            Tagged(Database.ConnectionString, applicationName)
        )
        {
            ConnectRetryCount = 0,
        }.ConnectionString;

        var result = await ReadAsync(connectionString, observer: observer);

        result
            .Should()
            .BeOfType<Result.TargetUnavailable>()
            .Which.Should()
            .Match<Result.TargetUnavailable>(static unavailable =>
                unavailable.Stage == Stage.Prepare && unavailable.Describe.StartsWith("SqlException(")
            );
        AssertRedacted(Logger, Database.DatabaseName);
    }

    [Test]
    public async Task It_leaves_no_session_setting_on_a_pooled_connection()
    {
        var pooled = new SqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = "projection-pooled-" + Guid.NewGuid().ToString("N"),
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
        }.ConnectionString;

        (await ReadAsync(pooled, Request(Fixture.MappingSet, lockTimeoutSeconds: 7)))
            .Should()
            .BeOfType<Result.Set>();

        await using var connection = new SqlConnection(pooled);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT CAST(@@LOCK_TIMEOUT AS nvarchar(20)) + N'|' + CAST([transaction_isolation_level] AS nvarchar(5))
            FROM sys.dm_exec_sessions WHERE [session_id] = @@SPID;
            """,
            connection
        );
        // -1 waits indefinitely (the default); 2 is READ COMMITTED.
        ((string?)await command.ExecuteScalarAsync())
            .Should()
            .Be("-1|2");
    }

    [Test]
    public async Task It_reports_an_unresolvable_host_as_unavailable_in_the_acquire_stage()
    {
        var result = await ReadAsync(
            $"Server=hostile-host.invalid;Database=hostile_database;User Id=hostile;Password={HostileValue};Connect Timeout=5;TrustServerCertificate=true"
        );

        result
            .Should()
            .BeOfType<Result.TargetUnavailable>()
            .Which.Should()
            .Match<Result.TargetUnavailable>(static unavailable =>
                unavailable.Stage == Stage.Acquire && unavailable.Describe.StartsWith("SqlException(")
            );
        AssertRedacted(Logger, "hostile-host", "hostile_database");
    }

    [Test]
    public async Task It_reports_a_malformed_connection_option_as_unavailable_in_the_acquire_stage()
    {
        var result = await ReadAsync($"Server=127.0.0.1;{HostileValue}=1");

        result.Should().Be(new Result.TargetUnavailable(Stage.Acquire, "ArgumentException"));
        AssertRedacted(Logger);
    }
}

/// <summary>
/// Each test alters the physical schema of its own lease. Both lease strategies undo schema changes:
/// a snapshot slot is restored from its database snapshot before it is leased again, and a
/// backup-restore lease is dropped on release.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Education_Organization_Projection_Set_Reader_Over_An_Altered_Schema
{
    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineLease? _lease;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlProjectionSeed _seed = null!;
    private RecordingLogger<MssqlEducationOrganizationProjectionSetReader> _logger = null!;

    [OneTimeSetUp]
    public void LoadFixture()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
    }

    [SetUp]
    public async Task ProvisionAndSeed()
    {
        _lease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _lease.Database;
        await _database.ResetAsync();
        _seed = await MssqlProjectionSeed.CreateAsync(_database);
        await _seed.StandardHierarchyAsync();
        _logger = new();
    }

    [TearDown]
    public async Task ReleaseLease()
    {
        SqlConnection.ClearAllPools();
        if (_lease is not null)
        {
            await _lease.DisposeAsync();
            _lease = null;
        }
    }

    [Test]
    public async Task It_reports_a_dropped_column_as_a_missing_schema_object()
    {
        await _database.ExecuteNonQueryAsync(
            "ALTER TABLE [edfi].[School] DROP COLUMN [ShortNameOfInstitution];"
        );

        var result = await ReadAsync();

        result
            .Should()
            .Be(
                new Result.SchemaIncompatible(
                    EducationOrganizationProjectionSchemaIncompatibilityReason.SchemaObjectMissing,
                    "207"
                )
            );
        AssertRedacted(_logger, _database.DatabaseName, "School", "ShortNameOfInstitution");
    }

    [Test]
    public async Task It_reports_one_arm_of_another_column_type_as_a_data_type_incompatibility()
    {
        // The integer arm makes the union integer, so another arm's text short name fails to convert.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[StateEducationAgency] SET [ShortNameOfInstitution] = NULL;
            ALTER TABLE [edfi].[StateEducationAgency] ALTER COLUMN [ShortNameOfInstitution] int NULL;
            """
        );

        var result = await ReadAsync();

        result
            .Should()
            .Be(
                new Result.SchemaIncompatible(
                    EducationOrganizationProjectionSchemaIncompatibilityReason.DataTypeIncompatible,
                    "245"
                )
            );
        AssertRedacted(_logger, _database.DatabaseName, "ShortNameOfInstitution");
    }

    [Test]
    public async Task It_reports_a_value_it_cannot_read_as_its_required_type_as_a_materialization_mismatch()
    {
        // Every arm changes together, so the statement succeeds and returns an integer column where
        // the row requires a string.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[StateEducationAgency] SET [ShortNameOfInstitution] = NULL;
            UPDATE [edfi].[EducationServiceCenter] SET [ShortNameOfInstitution] = NULL;
            UPDATE [edfi].[LocalEducationAgency] SET [ShortNameOfInstitution] = NULL;
            UPDATE [edfi].[School] SET [ShortNameOfInstitution] = NULL;
            ALTER TABLE [edfi].[StateEducationAgency] ALTER COLUMN [ShortNameOfInstitution] int NULL;
            ALTER TABLE [edfi].[EducationServiceCenter] ALTER COLUMN [ShortNameOfInstitution] int NULL;
            ALTER TABLE [edfi].[LocalEducationAgency] ALTER COLUMN [ShortNameOfInstitution] int NULL;
            ALTER TABLE [edfi].[School] ALTER COLUMN [ShortNameOfInstitution] int NULL;
            """
        );
        await _database.ExecuteNonQueryAsync(
            "UPDATE [edfi].[School] SET [ShortNameOfInstitution] = 1 WHERE [SchoolId] = 100001;"
        );

        var result = await ReadAsync();

        result
            .Should()
            .Be(
                new Result.SchemaIncompatible(
                    EducationOrganizationProjectionSchemaIncompatibilityReason.MaterializationTypeMismatch,
                    null
                )
            );
        AssertRedacted(_logger, _database.DatabaseName);
    }

    [Test]
    public async Task It_reads_dangling_and_wrong_type_references_as_stored()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE [edfi].[LocalEducationAgency] DROP CONSTRAINT [FK_LocalEducationAgency_EducationServiceCenter_RefKey];
            ALTER TABLE [edfi].[LocalEducationAgency] DROP CONSTRAINT [FK_LocalEducationAgency_StateEducationAgency_RefKey];
            """
        );
        var parent = new ProjectionReference(
            await _database.ExecuteScalarAsync<long>(
                "SELECT [DocumentId] FROM [edfi].[LocalEducationAgency] WHERE [LocalEducationAgencyId] = 100;"
            ),
            100
        );

        // A valid parent, which precedence selects; a service center slot holding a local education
        // agency's identifier; and a state agency that does not exist. Neither of the last two is
        // selected, and both must reach validation unchanged.
        await _seed.LocalEducationAgencyAsync(
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

    private Task<Result> ReadAsync() =>
        Create(_database.ConnectionString, _logger)
            .ReadSetAsync(Request(_fixture.MappingSet), CancellationToken.None);
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Education_Organization_Projection_Set_Reader_Under_Concurrent_Transactions
    : MssqlEducationOrganizationProjectionFixtureBase
{
    private const string BlockedSql = """
        SELECT COUNT(*) FROM sys.dm_exec_requests r
        JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
        WHERE s.program_name = @app AND r.blocking_session_id <> 0;
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
    public async Task It_waits_for_an_uncommitted_change_and_reads_both_of_its_rows_after_it_commits()
    {
        // The round-1 counterexample: T1 changes A and B and stays open; T2 changes C and commits.
        // T2 commits first: under locking read committed its update would wait for T1.
        await Database.ExecuteNonQueryAsync(
            "UPDATE [edfi].[EducationServiceCenter] SET [NameOfInstitution] = N'C-new' WHERE [EducationServiceCenterId] = 10;"
        );
        await using var t1 = await OpenWriterAsync();
        await ExecuteAsync(
            t1,
            "UPDATE [edfi].[StateEducationAgency] SET [NameOfInstitution] = N'A-new' WHERE [StateEducationAgencyId] = 1;"
        );
        await ExecuteAsync(
            t1,
            "UPDATE [edfi].[School] SET [NameOfInstitution] = N'B-new' WHERE [SchoolId] = 900001;"
        );

        var read = ReadAsync(_readerConnectionString);
        await WaitUntilAsync(
            async () => await CountAsync(Database.ConnectionString, BlockedSql, _applicationName) == 1,
            "the read is blocked by T1"
        );
        await t1.Transaction.CommitAsync();

        var result = (Result.Set)await read;

        Names(result).Should().Equal("A-new", "B-new", "C-new");
    }

    [Test]
    public async Task It_propagates_cancellation_while_blocked_and_releases_its_transaction_and_connection()
    {
        await using var writer = await OpenWriterAsync();
        await ExecuteAsync(
            writer,
            "UPDATE [edfi].[School] SET [NameOfInstitution] = N'Held' WHERE [SchoolId] = 900001;"
        );
        using var cancellation = new CancellationTokenSource();

        var read = ReadAsync(
            _readerConnectionString,
            Request(Fixture.MappingSet, lockTimeoutSeconds: 60, commandTimeoutSeconds: 120),
            cancellationToken: cancellation.Token
        );
        await WaitUntilAsync(
            async () => await CountAsync(Database.ConnectionString, BlockedSql, _applicationName) == 1,
            "the read is blocked by the writer"
        );
        await cancellation.CancelAsync();

        await read.Awaiting(static task => task).Should().ThrowAsync<OperationCanceledException>();
        await WaitUntilAsync(
            async () =>
                await CountAsync(
                    Database.ConnectionString,
                    "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = @app;",
                    _applicationName
                ) == 0,
            "the read's session has ended"
        );

        // The read's shared locks are gone: the writer can change a row the read had already read.
        await ExecuteAsync(
            writer,
            "SET LOCK_TIMEOUT 0; UPDATE [edfi].[StateEducationAgency] SET [NameOfInstitution] = N'Free' WHERE [StateEducationAgencyId] = 1;"
        );
        Logger
            .Records.Should()
            .NotContain(static record => record.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Test]
    public async Task It_reports_a_lock_timeout_as_unavailable_in_the_execute_stage()
    {
        await using var writer = await OpenWriterAsync();
        await ExecuteAsync(
            writer,
            "UPDATE [edfi].[School] SET [NameOfInstitution] = N'Held' WHERE [SchoolId] = 900001;"
        );

        var result = await ReadAsync(
            _readerConnectionString,
            Request(Fixture.MappingSet, lockTimeoutSeconds: 1)
        );

        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, "SqlException(1222)"));
        AssertRedacted(Logger, Database.DatabaseName, "School");
    }

    [Test]
    public async Task It_reports_a_command_timeout_as_unavailable_in_the_execute_stage()
    {
        await using var writer = await OpenWriterAsync();
        await ExecuteAsync(
            writer,
            "UPDATE [edfi].[School] SET [NameOfInstitution] = N'Held' WHERE [SchoolId] = 900001;"
        );

        var result = await ReadAsync(
            _readerConnectionString,
            Request(Fixture.MappingSet, lockTimeoutSeconds: 60, commandTimeoutSeconds: 1)
        );

        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, "SqlException(-2)"));
        AssertRedacted(Logger, Database.DatabaseName, "School");
    }

    [Test]
    public async Task It_reports_being_chosen_as_a_deadlock_victim_as_unavailable_in_the_execute_stage()
    {
        // The writer holds a School row and outranks the read, which waits for it holding shared locks
        // on the rows it has read; the writer then updates one of those.
        await using var writer = await OpenWriterAsync("SET DEADLOCK_PRIORITY HIGH;");
        await ExecuteAsync(
            writer,
            "UPDATE [edfi].[School] SET [NameOfInstitution] = N'Held' WHERE [SchoolId] = 900001;"
        );

        var read = ReadAsync(_readerConnectionString, Request(Fixture.MappingSet, lockTimeoutSeconds: 60));
        await WaitUntilAsync(
            async () => await CountAsync(Database.ConnectionString, BlockedSql, _applicationName) == 1,
            "the read is blocked by the writer"
        );
        var writerSecondUpdate = ExecuteAsync(
            writer,
            "UPDATE [edfi].[StateEducationAgency] SET [NameOfInstitution] = N'Deadlock' WHERE [StateEducationAgencyId] = 1;"
        );

        var result = await read;
        await writerSecondUpdate;

        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, "SqlException(1205)"));
        AssertRedacted(Logger, Database.DatabaseName, "School");
    }

    private async Task<Writer> OpenWriterAsync(string? sessionSetup = null)
    {
        var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        if (sessionSetup is not null)
        {
            await using var setup = new SqlCommand(sessionSetup, connection);
            await setup.ExecuteNonQueryAsync();
        }

        return new Writer(connection, (SqlTransaction)await connection.BeginTransactionAsync());
    }

    private static async Task ExecuteAsync(Writer writer, string sql)
    {
        await using var command = new SqlCommand(sql, writer.Connection, writer.Transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The names of A, B and C, in that order.</summary>
    private static IEnumerable<string> Names(Result.Set set) =>
        new long[] { 1, 900001, 10 }.Select(id =>
            set.Rows.Single(row => row.EducationOrganizationId == id).NameOfInstitution
        );

    private sealed class Writer(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        public SqlConnection Connection { get; } = connection;

        public SqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
