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
    }

    [Test]
    public async Task It_commits_only_the_parent_and_child_hash_changes()
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var before = SchemaRestampTestHelper.Capture(connection, "mssql");
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
