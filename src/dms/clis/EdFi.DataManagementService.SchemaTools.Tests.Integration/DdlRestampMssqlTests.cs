// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Restamping;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_SchemaRestamp_Mssql_Compatible_Transition
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private EffectiveSchemaInfo _target = null!;
    private string? _restrictedLogin;
    private readonly string _oldHash = new('a', 64);

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require ConnectionStrings__MssqlAdmin.");
        }
        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        _target = SchemaRestampTestHelper.BuildTarget(
            CliTestHelper.GetMinimalSchemaPath(),
            SchemaRestampTestHelper.ExtensionPath
        );
        var provision = ProvisionTestHelper.RunProvision(
            "mssql",
            _connectionString,
            [CliTestHelper.GetMinimalSchemaPath(), SchemaRestampTestHelper.ExtensionPath],
            createDatabase: true
        );
        provision.ExitCode.Should().Be(0, provision.Error);
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.SetOldHash(connection, "mssql", _oldHash);
        SchemaRestampTestHelper.InsertComponents(connection, "mssql", _target, _oldHash);
    }

    [TearDown]
    public void TearDown()
    {
        if (_databaseName is not null)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
        if (_restrictedLogin is not null)
        {
            var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
            using var connection = new SqlConnection(master.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE [name] = N'{_restrictedLogin}') DROP LOGIN [{_restrictedLogin}];";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public async Task It_commits_only_the_parent_and_child_hash_changes()
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        ProvisionTestHelper.InsertRowsThatMustSurviveRerun(connection, "mssql");
        var before = SchemaRestampTestHelper.Capture(connection, "mssql");
        var mutableStateBefore = ProvisionTestHelper.ReadDocumentCacheMutableStateSnapshot(
            connection,
            "mssql"
        );
        var result = await new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Mssql,
            _connectionString,
            30,
            _target,
            true,
            CancellationToken.None
        );
        var after = SchemaRestampTestHelper.Capture(connection, "mssql");

        result.Should().Be(new SchemaRestampResult(true, _oldHash, _target.EffectiveSchemaHash, 2));
        after.Hash.Should().Be(_target.EffectiveSchemaHash);
        after.Format.Should().Be(before.Format);
        after.Count.Should().Be(before.Count);
        after.Seed.Should().Be(before.Seed);
        after.AppliedAt.Should().Be(before.AppliedAt);
        after.Keys.Should().Equal(before.Keys);
        after.Documents.Should().Equal(before.Documents);
        after.CacheRows.Should().Equal(before.CacheRows);
        after.ProjectionWork.Should().Equal(before.ProjectionWork);
        ProvisionTestHelper
            .ReadDocumentCacheMutableStateSnapshot(connection, "mssql")
            .Should()
            .BeEquivalentTo(mutableStateBefore);
        after
            .Components.Should()
            .Equal(
                before.Components.Select(row =>
                    row.Replace(_oldHash, _target.EffectiveSchemaHash, StringComparison.Ordinal)
                )
            );
    }

    [Test]
    public async Task It_re_stamps_through_the_shipped_cli_process()
    {
        var result = SchemaRestampTestHelper.RunRestamp(
            _connectionString,
            "mssql",
            true,
            CliTestHelper.GetMinimalSchemaPath(),
            SchemaRestampTestHelper.ExtensionPath
        );

        result.ExitCode.Should().Be(0, result.Error);
        result.Output.Should().Contain("Effective schema re-stamp committed.");
        result.Output.Should().Contain(_target.EffectiveSchemaHash);
        result.Output.Should().Contain("Run 'ddl provision' separately");
        result.Error.Should().NotContain(_connectionString);
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.Capture(connection, "mssql").Hash.Should().Be(_target.EffectiveSchemaHash);
    }

    [Test]
    public async Task It_validates_a_match_without_confirmation_or_dml()
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.SetOldHash(connection, "mssql", _target.EffectiveSchemaHash);
        SchemaRestampTestHelper.InsertComponents(connection, "mssql", _target, _target.EffectiveSchemaHash);
        var before = SchemaRestampTestHelper.Capture(connection, "mssql");
        var result = await new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Mssql,
            _connectionString,
            30,
            _target,
            false,
            CancellationToken.None
        );
        var after = SchemaRestampTestHelper.Capture(connection, "mssql");
        result.Changed.Should().BeFalse();
        after.Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_performs_no_metadata_dml_when_a_valid_match_has_dml_rejecting_triggers()
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.SetOldHash(connection, "mssql", _target.EffectiveSchemaHash);
        SchemaRestampTestHelper.InsertComponents(connection, "mssql", _target, _target.EffectiveSchemaHash);
        SchemaRestampTestHelper.InstallDmlRejectingTriggers(connection, "mssql");
        var before = SchemaRestampTestHelper.Capture(connection, "mssql");

        var result = await new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Mssql,
            _connectionString,
            30,
            _target,
            false,
            CancellationToken.None
        );

        result.Changed.Should().BeFalse();
        SchemaRestampTestHelper.Capture(connection, "mssql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_rolls_back_deleted_children_when_the_parent_update_fails()
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.InstallParentUpdateRejectingTrigger(connection, "mssql");
        var before = SchemaRestampTestHelper.Capture(connection, "mssql");

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Mssql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.TransactionFailed);
        SchemaRestampTestHelper.Capture(connection, "mssql").Should().BeEquivalentTo(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_restores_the_complete_metadata_snapshot_when_component_reinsertion_fails(
        bool rejectLaterComponent
    )
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var components = _target.SchemaComponentsInEndpointOrder;
        var rejected = rejectLaterComponent ? components[^1] : components[0];
        SchemaRestampTestHelper.InstallComponentInsertRejectingTrigger(
            connection,
            "mssql",
            rejected.ProjectEndpointName
        );
        var before = SchemaRestampTestHelper.Capture(connection, "mssql");

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Mssql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.TransactionFailed);
        SchemaRestampTestHelper.Capture(connection, "mssql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_serializes_competing_same_target_transitions()
    {
        var restamper = new SchemaRestamper(NullLogger.Instance);
        var results = await Task.WhenAll(
            restamper.RestampAsync(
                SqlDialect.Mssql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            ),
            restamper.RestampAsync(
                SqlDialect.Mssql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            )
        );

        results.Count(result => result.Changed).Should().Be(1);
        results.Count(result => !result.Changed).Should().Be(1);
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.Capture(connection, "mssql").Hash.Should().Be(_target.EffectiveSchemaHash);
    }

    [TestCase("format")]
    [TestCase("count")]
    [TestCase("seed")]
    [TestCase("component")]
    public async Task It_rejects_corrupt_compatibility_metadata_without_mutating_it(string field)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.CorruptMetadata(connection, "mssql", field);
        var before = SchemaRestampTestHelper.Capture(connection, "mssql");

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Mssql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Validation);
        SchemaRestampTestHelper.Capture(connection, "mssql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_times_out_while_another_transaction_holds_the_metadata_lock()
    {
        using var blocker = new SqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText =
                "SELECT COUNT_BIG(*) FROM [dms].[EffectiveSchema] WITH (TABLOCKX, HOLDLOCK);";
            await lockCommand.ExecuteNonQueryAsync();
        }

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Mssql,
                _connectionString,
                1,
                _target,
                true,
                CancellationToken.None
            );

        try
        {
            (await action.Should().ThrowAsync<SchemaRestampException>())
                .Which.Failure.Should()
                .Be(SchemaRestampFailure.Timeout);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.Capture(connection, "mssql").Hash.Should().Be(_oldHash);
    }

    [Test]
    public async Task It_reports_restricted_metadata_privileges_without_committing_a_transition()
    {
        _restrictedLogin = $"restamp_reader_{Guid.NewGuid():N}";
        var password = $"{Guid.NewGuid():N}Aa1!";
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        using (var admin = new SqlConnection(master.ConnectionString))
        {
            await admin.OpenAsync();
            using var command = admin.CreateCommand();
            command.CommandText = $"CREATE LOGIN [{_restrictedLogin}] WITH PASSWORD = '{password}';";
            await command.ExecuteNonQueryAsync();
        }
        using (var admin = new SqlConnection(_connectionString))
        {
            await admin.OpenAsync();
            using var command = admin.CreateCommand();
            command.CommandText =
                $"CREATE USER [{_restrictedLogin}] FOR LOGIN [{_restrictedLogin}]; GRANT SELECT ON SCHEMA::[dms] TO [{_restrictedLogin}];";
            await command.ExecuteNonQueryAsync();
        }
        var restricted = new SqlConnectionStringBuilder(_connectionString)
        {
            UserID = _restrictedLogin,
            Password = password,
        };

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Mssql,
                restricted.ConnectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.PermissionDenied);
        using var verify = new SqlConnection(_connectionString);
        await verify.OpenAsync();
        SchemaRestampTestHelper.Capture(verify, "mssql").Hash.Should().Be(_oldHash);
    }

    [Test]
    public async Task It_cancels_while_waiting_for_a_metadata_lock_and_preserves_the_old_stamp()
    {
        using var blocker = new SqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText =
                "SELECT COUNT_BIG(*) FROM [dms].[SchemaComponent] WITH (TABLOCKX, HOLDLOCK);";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var applicationName = $"dms1303-restamp-cancel-{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = applicationName,
        };
        using var cancellation = new CancellationTokenSource();
        using var monitor = new SqlConnection(_connectionString);
        await monitor.OpenAsync();
        var restamp = new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Mssql,
            connectionString.ConnectionString,
            30,
            _target,
            true,
            cancellation.Token
        );

        try
        {
            await SchemaRestampTestHelper.WaitForLockWaitAsync(monitor, "mssql", applicationName);
            await cancellation.CancelAsync();
            Func<Task> action = async () => await restamp;
            (await action.Should().ThrowAsync<SchemaRestampException>())
                .Which.Failure.Should()
                .Be(SchemaRestampFailure.Cancelled);
        }
        finally
        {
            await cancellation.CancelAsync();
            await transaction.RollbackAsync();
        }

        using var verify = new SqlConnection(_connectionString);
        await verify.OpenAsync();
        SchemaRestampTestHelper.Capture(verify, "mssql").Hash.Should().Be(_oldHash);
    }

    [Test]
    public async Task It_holds_metadata_writer_until_restamp_validation_and_commit_complete()
    {
        using var blocker = new SqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText =
                "SELECT COUNT_BIG(*) FROM [dms].[SchemaComponent] WITH (TABLOCKX, HOLDLOCK);";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var restampApplication = $"dms1303-restamp-writer-{Guid.NewGuid():N}";
        var restampConnectionString = new SqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = restampApplication,
        };
        using var monitor = new SqlConnection(_connectionString);
        await monitor.OpenAsync();
        var restampTask = new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Mssql,
            restampConnectionString.ConnectionString,
            30,
            _target,
            true,
            CancellationToken.None
        );

        var writerApplication = $"dms1303-metadata-writer-{Guid.NewGuid():N}";
        var writerConnectionString = new SqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = writerApplication,
        };
        using var writer = new SqlConnection(writerConnectionString.ConnectionString);
        await writer.OpenAsync();
        await using var writerTransaction = (SqlTransaction)await writer.BeginTransactionAsync();
        await using var writerCommand = writer.CreateCommand();
        writerCommand.Transaction = writerTransaction;
        writerCommand.CommandText = "UPDATE [dms].[EffectiveSchema] SET [AppliedAt] = [AppliedAt];";
        Task<int>? writerUpdate = null;
        var blockerReleased = false;
        var writerTransactionCompleted = false;

        try
        {
            await SchemaRestampTestHelper.WaitForLockWaitAsync(monitor, "mssql", restampApplication);
            writerUpdate = writerCommand.ExecuteNonQueryAsync();
            await SchemaRestampTestHelper.WaitForLockWaitAsync(monitor, "mssql", writerApplication);

            await blockerTransaction.RollbackAsync();
            blockerReleased = true;
            var result = await restampTask;
            result.Changed.Should().BeTrue();
            result.PreviousHash.Should().Be(_oldHash);
            await writerUpdate.WaitAsync(TimeSpan.FromSeconds(20));
            await writerTransaction.RollbackAsync();
            writerTransactionCompleted = true;
        }
        finally
        {
            if (!blockerReleased)
            {
                await blockerTransaction.RollbackAsync();
            }
            if (writerUpdate is not null && !writerUpdate.IsCompleted)
            {
                try
                {
                    await writerUpdate.WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (Exception exception)
                {
                    TestContext.WriteLine($"Writer cleanup failed: {exception.Message}");
                }
            }
            if (!writerTransactionCompleted)
            {
                try
                {
                    await writerTransaction.RollbackAsync();
                }
                catch (Exception exception)
                {
                    TestContext.WriteLine($"Writer transaction cleanup failed: {exception.Message}");
                }
            }
            if (!restampTask.IsCompleted)
            {
                try
                {
                    await restampTask;
                }
                catch (Exception exception)
                {
                    TestContext.WriteLine($"Restamp cleanup failed: {exception.Message}");
                }
            }
        }

        using var verify = new SqlConnection(_connectionString);
        await verify.OpenAsync();
        SchemaRestampTestHelper.Capture(verify, "mssql").Hash.Should().Be(_target.EffectiveSchemaHash);
    }

    [Test]
    public async Task It_refuses_a_missing_singleton_without_creating_one()
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM [dms].[SchemaComponent]; DELETE FROM [dms].[EffectiveSchema];";
            await command.ExecuteNonQueryAsync();
        }
        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Mssql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );
        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Validation);
        ProvisionTestHelper.GetDmsTableCount(connection, "mssql", "EffectiveSchema").Should().Be(0);
    }
}
