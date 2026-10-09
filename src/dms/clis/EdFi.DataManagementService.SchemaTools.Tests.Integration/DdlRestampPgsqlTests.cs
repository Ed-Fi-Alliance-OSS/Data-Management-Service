// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Restamping;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_SchemaRestamp_Pgsql_Compatible_Transition
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private EffectiveSchemaInfo _target = null!;
    private string? _restrictedRole;
    private readonly string _oldHash = new('a', 64);

    [SetUp]
    public async Task Setup()
    {
        _databaseName = PostgresTestDatabaseHelper.GenerateUniqueDatabaseName();
        _connectionString = PostgresTestDatabaseHelper.BuildConnectionString(_databaseName);
        _target = SchemaRestampTestHelper.BuildTarget(
            CliTestHelper.GetMinimalSchemaPath(),
            SchemaRestampTestHelper.ExtensionPath
        );
        var provision = ProvisionTestHelper.RunProvision(
            "pgsql",
            _connectionString,
            [CliTestHelper.GetMinimalSchemaPath(), SchemaRestampTestHelper.ExtensionPath],
            createDatabase: true
        );
        provision.ExitCode.Should().Be(0, provision.Error);
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.SetOldHash(connection, "pgsql", _oldHash);
        SchemaRestampTestHelper.InsertComponents(connection, "pgsql", _target, _oldHash);
    }

    [TearDown]
    public void TearDown()
    {
        PostgresTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        if (_restrictedRole is not null)
        {
            using var connection = new NpgsqlConnection(
                PostgresTestDatabaseHelper.BuildConnectionString("postgres")
            );
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP ROLE IF EXISTS \"{_restrictedRole}\";";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public async Task It_commits_only_the_parent_and_child_hash_changes()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        ProvisionTestHelper.InsertRowsThatMustSurviveRerun(connection, "pgsql");
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");
        var mutableStateBefore = ProvisionTestHelper.ReadDocumentCacheMutableStateSnapshot(
            connection,
            "pgsql"
        );
        var result = await new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Pgsql,
            _connectionString,
            30,
            _target,
            true,
            CancellationToken.None
        );
        var after = SchemaRestampTestHelper.Capture(connection, "pgsql");

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
            .ReadDocumentCacheMutableStateSnapshot(connection, "pgsql")
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
            "pgsql",
            true,
            CliTestHelper.GetMinimalSchemaPath(),
            SchemaRestampTestHelper.ExtensionPath
        );

        result.ExitCode.Should().Be(0, result.Error);
        result.Output.Should().Contain("Effective schema re-stamp committed.");
        result.Output.Should().Contain(_target.EffectiveSchemaHash);
        result.Output.Should().Contain("Run 'ddl provision' separately");
        result.Error.Should().NotContain(_connectionString);
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.Capture(connection, "pgsql").Hash.Should().Be(_target.EffectiveSchemaHash);
    }

    [Test]
    public void It_shows_re_stamp_help_and_contains_parser_values_in_the_process()
    {
        var help = CliTestHelper.RunCli("ddl", "re-stamp", "--help");
        help.ExitCode.Should().Be(0, help.Error);
        help.Output.Should().Contain("--migration-completed");
        help.Output.Should().Contain("--connection-string");

        const string secret = "process-secret-sentinel";
        var invalid = CliTestHelper.RunCli(
            "ddl",
            "re-stamp",
            "--connection-string",
            secret,
            "--unknown-option",
            secret
        );
        invalid.ExitCode.Should().Be(1);
        (invalid.Output + invalid.Error).Should().NotContain(secret);
    }

    [Test]
    public async Task It_refuses_a_changed_stamp_without_confirmation_and_preserves_metadata()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");
        before.Hash.Should().NotBe(_target.EffectiveSchemaHash);

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Pgsql,
                _connectionString,
                30,
                _target,
                migrationCompleted: false,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.ConfirmationRequired);
        SchemaRestampTestHelper.Capture(connection, "pgsql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_refuses_a_changed_stamp_without_confirmation_through_the_shipped_cli()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");
        before.Hash.Should().NotBe(_target.EffectiveSchemaHash);

        var result = SchemaRestampTestHelper.RunRestamp(
            _connectionString,
            "pgsql",
            false,
            CliTestHelper.GetMinimalSchemaPath(),
            SchemaRestampTestHelper.ExtensionPath
        );

        result.ExitCode.Should().Be(1);
        result.Error.Should().Contain("migration-completed confirmation is required");
        result.Output.Should().BeEmpty();
        SchemaRestampTestHelper.Capture(connection, "pgsql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_validates_a_match_without_confirmation_or_dml()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.SetOldHash(connection, "pgsql", _target.EffectiveSchemaHash);
        SchemaRestampTestHelper.InsertComponents(connection, "pgsql", _target, _target.EffectiveSchemaHash);
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");

        var result = await new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Pgsql,
            _connectionString,
            30,
            _target,
            false,
            CancellationToken.None
        );
        var after = SchemaRestampTestHelper.Capture(connection, "pgsql");

        result.Changed.Should().BeFalse();
        after.Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_performs_no_metadata_dml_when_a_valid_match_has_dml_rejecting_triggers()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.SetOldHash(connection, "pgsql", _target.EffectiveSchemaHash);
        SchemaRestampTestHelper.InsertComponents(connection, "pgsql", _target, _target.EffectiveSchemaHash);
        SchemaRestampTestHelper.InstallDmlRejectingTriggers(connection, "pgsql");
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");

        var result = await new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Pgsql,
            _connectionString,
            30,
            _target,
            false,
            CancellationToken.None
        );

        result.Changed.Should().BeFalse();
        SchemaRestampTestHelper.Capture(connection, "pgsql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_rolls_back_deleted_children_when_the_parent_update_fails()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.InstallParentUpdateRejectingTrigger(connection, "pgsql");
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Pgsql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.TransactionFailed);
        SchemaRestampTestHelper.Capture(connection, "pgsql").Should().BeEquivalentTo(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_restores_the_complete_metadata_snapshot_when_component_reinsertion_fails(
        bool rejectLaterComponent
    )
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var components = _target.SchemaComponentsInEndpointOrder;
        var rejected = rejectLaterComponent ? components[^1] : components[0];
        SchemaRestampTestHelper.InstallComponentInsertRejectingTrigger(
            connection,
            "pgsql",
            rejected.ProjectEndpointName
        );
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Pgsql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.TransactionFailed);
        SchemaRestampTestHelper.Capture(connection, "pgsql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_serializes_competing_same_target_transitions()
    {
        var restamper = new SchemaRestamper(NullLogger.Instance);
        var results = await Task.WhenAll(
            restamper.RestampAsync(
                SqlDialect.Pgsql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            ),
            restamper.RestampAsync(
                SqlDialect.Pgsql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            )
        );

        results.Count(result => result.Changed).Should().Be(1);
        results.Count(result => !result.Changed).Should().Be(1);
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.Capture(connection, "pgsql").Hash.Should().Be(_target.EffectiveSchemaHash);
    }

    [TestCase("format")]
    [TestCase("count")]
    [TestCase("seed")]
    [TestCase("component")]
    public async Task It_rejects_corrupt_compatibility_metadata_without_mutating_it(string field)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.CorruptMetadata(connection, "pgsql", field);
        var before = SchemaRestampTestHelper.Capture(connection, "pgsql");

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Pgsql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Validation);
        SchemaRestampTestHelper.Capture(connection, "pgsql").Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_times_out_while_another_transaction_holds_the_metadata_lock()
    {
        using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "LOCK TABLE dms.\"EffectiveSchema\" IN ACCESS EXCLUSIVE MODE;";
            await lockCommand.ExecuteNonQueryAsync();
        }

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Pgsql,
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
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        SchemaRestampTestHelper.Capture(connection, "pgsql").Hash.Should().Be(_oldHash);
    }

    [Test]
    public async Task It_reports_restricted_metadata_privileges_without_committing_a_transition()
    {
        _restrictedRole = $"restamp_reader_{Guid.NewGuid():N}";
        var password = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        using (var admin = new NpgsqlConnection(_connectionString))
        {
            await admin.OpenAsync();
            using var command = admin.CreateCommand();
            command.CommandText = $"""
                CREATE ROLE "{_restrictedRole}" LOGIN PASSWORD '{password}';
                GRANT CONNECT ON DATABASE "{_databaseName}" TO "{_restrictedRole}";
                GRANT USAGE ON SCHEMA dms TO "{_restrictedRole}";
                GRANT SELECT ON ALL TABLES IN SCHEMA dms TO "{_restrictedRole}";
                """;
            await command.ExecuteNonQueryAsync();
        }
        var restricted = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Username = _restrictedRole,
            Password = password,
        };

        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Pgsql,
                restricted.ConnectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.PermissionDenied);
        using var verify = new NpgsqlConnection(_connectionString);
        await verify.OpenAsync();
        SchemaRestampTestHelper.Capture(verify, "pgsql").Hash.Should().Be(_oldHash);
    }

    [Test]
    public async Task It_cancels_while_waiting_for_a_metadata_lock_and_preserves_the_old_stamp()
    {
        using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "LOCK TABLE dms.\"SchemaComponent\" IN ACCESS EXCLUSIVE MODE;";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var applicationName = $"dms1303-restamp-cancel-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = applicationName,
        };
        using var cancellation = new CancellationTokenSource();
        using var monitor = new NpgsqlConnection(_connectionString);
        await monitor.OpenAsync();
        var restamp = new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Pgsql,
            connectionString.ConnectionString,
            30,
            _target,
            true,
            cancellation.Token
        );

        try
        {
            await SchemaRestampTestHelper.WaitForLockWaitAsync(monitor, "pgsql", applicationName);
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

        using var verify = new NpgsqlConnection(_connectionString);
        await verify.OpenAsync();
        SchemaRestampTestHelper.Capture(verify, "pgsql").Hash.Should().Be(_oldHash);
    }

    [Test]
    public async Task It_holds_metadata_writer_until_restamp_validation_and_commit_complete()
    {
        using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText = "LOCK TABLE dms.\"SchemaComponent\" IN ACCESS EXCLUSIVE MODE;";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var restampApplication = $"dms1303-restamp-writer-{Guid.NewGuid():N}";
        var restampConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = restampApplication,
        };
        using var monitor = new NpgsqlConnection(_connectionString);
        await monitor.OpenAsync();
        var restampTask = new SchemaRestamper(NullLogger.Instance).RestampAsync(
            SqlDialect.Pgsql,
            restampConnectionString.ConnectionString,
            30,
            _target,
            true,
            CancellationToken.None
        );
        var writerApplication = $"dms1303-metadata-writer-{Guid.NewGuid():N}";
        var writerConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = writerApplication,
        };
        using var writer = new NpgsqlConnection(writerConnectionString.ConnectionString);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await using var writerCommand = writer.CreateCommand();
        writerCommand.Transaction = writerTransaction;
        writerCommand.CommandText = "UPDATE dms.\"EffectiveSchema\" SET \"AppliedAt\" = \"AppliedAt\";";
        Task<int>? writerUpdate = null;
        var blockerReleased = false;
        var writerTransactionCompleted = false;

        try
        {
            await SchemaRestampTestHelper.WaitForLockWaitAsync(monitor, "pgsql", restampApplication);
            writerUpdate = writerCommand.ExecuteNonQueryAsync();
            await SchemaRestampTestHelper.WaitForLockWaitAsync(monitor, "pgsql", writerApplication);

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

        using var verify = new NpgsqlConnection(_connectionString);
        await verify.OpenAsync();
        SchemaRestampTestHelper.Capture(verify, "pgsql").Hash.Should().Be(_target.EffectiveSchemaHash);
    }

    [Test]
    public async Task It_refuses_a_missing_singleton_without_creating_one()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM dms.\"SchemaComponent\"; DELETE FROM dms.\"EffectiveSchema\";";
            await command.ExecuteNonQueryAsync();
        }
        Func<Task> action = () =>
            new SchemaRestamper(NullLogger.Instance).RestampAsync(
                SqlDialect.Pgsql,
                _connectionString,
                30,
                _target,
                true,
                CancellationToken.None
            );
        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Validation);
        ProvisionTestHelper.GetDmsTableCount(connection, "pgsql", "EffectiveSchema").Should().Be(0);
    }
}
