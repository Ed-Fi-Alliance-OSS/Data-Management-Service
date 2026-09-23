// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0031, the state a real
/// deployment from before DMS-1437 upgrades from, and checks that the upgrade adds exactly the
/// JobSchedule script and table and that a repeat deploy adds nothing.
/// </summary>
[TestFixture]
public class Given_a_database_deployed_before_the_JobSchedule_script
{
    private const string ScriptsSegment = ".Deploy.Scripts.";
    private const int FirstJobScheduleScript = 32;

    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private bool _tableExistedBeforeUpgrade;
    private string[] _journalBeforeUpgrade = [];
    private string[] _journalAfterUpgrade = [];
    private string[] _journalAfterRepeatDeploy = [];
    private bool _tableExistsAfterUpgrade;
    private long _constraintsAfterUpgrade;
    private long _indexesAfterUpgrade;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        _connectionString = CreateIsolatedDatabaseConnectionString();

        DeploySuccessfully(new Deploy.DatabaseDeploy { ScriptFilter = IsBeforeJobScheduleScript });
        _tableExistedBeforeUpgrade = await TableExistsAsync();
        _journalBeforeUpgrade = await JournalAsync();

        DeploySuccessfully(new Deploy.DatabaseDeploy());
        _journalAfterUpgrade = await JournalAsync();
        _tableExistsAfterUpgrade = await TableExistsAsync();

        await using (NpgsqlConnection connection = new(_connectionString))
        {
            await connection.OpenAsync();
            _constraintsAfterUpgrade = await connection.ExecuteScalarAsync<long>(
                """
                SELECT count(*) FROM pg_constraint
                WHERE conrelid = '"dmscs"."JobSchedule"'::regclass
                  AND conname IN ('PK_JobSchedule', 'FK_JobSchedule_Tenant', 'CK_JobSchedule_Payload_Object',
                                  'CK_JobSchedule_IntervalMinutes');
                """
            );
            _indexesAfterUpgrade = await connection.ExecuteScalarAsync<long>(
                """
                SELECT count(*) FROM pg_indexes
                WHERE schemaname = 'dmscs' AND tablename = 'JobSchedule'
                  AND indexname IN ('UX_JobSchedule_Tenant_Type', 'UX_JobSchedule_SingleTenant_Type',
                                    'IX_JobSchedule_Due', 'IX_JobSchedule_TenantId');
                """
            );
        }

        DeploySuccessfully(new Deploy.DatabaseDeploy());
        _journalAfterRepeatDeploy = await JournalAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTeardown()
    {
        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        await using NpgsqlConnection connection = new(CreateMaintenanceConnectionString());
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE);""");
    }

    [Test]
    public void It_deploys_the_earlier_scripts_without_the_JobSchedule_table()
    {
        _tableExistedBeforeUpgrade.Should().BeFalse();
        _journalBeforeUpgrade.Should().NotBeEmpty();
        _journalBeforeUpgrade.Should().OnlyContain(name => ScriptNumber(name) < FirstJobScheduleScript);
    }

    [Test]
    public void It_adds_the_JobSchedule_table_with_its_constraints_and_indexes()
    {
        _tableExistsAfterUpgrade.Should().BeTrue();
        _constraintsAfterUpgrade.Should().Be(4);
        _indexesAfterUpgrade.Should().Be(4);
    }

    [Test]
    public void It_journals_exactly_the_JobSchedule_script()
    {
        _journalAfterUpgrade
            .Except(_journalBeforeUpgrade)
            .Should()
            .Equal(
                "EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.Scripts.0032_Create_JobSchedule_Table.sql"
            );
    }

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(_journalAfterUpgrade);

    private static bool IsBeforeJobScheduleScript(string scriptName) =>
        ScriptNumber(scriptName) < FirstJobScheduleScript;

    private static int ScriptNumber(string scriptName)
    {
        int start = scriptName.IndexOf(ScriptsSegment, StringComparison.Ordinal) + ScriptsSegment.Length;
        return int.Parse(scriptName.AsSpan(start, 4), CultureInfo.InvariantCulture);
    }

    private void DeploySuccessfully(Deploy.DatabaseDeploy deploy)
    {
        DatabaseDeployResult result = deploy.DeployDatabase(_connectionString);

        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Assert.Fail($"Database deploy failed: {failure.Error}");
        }
    }

    private async Task<bool> TableExistsAsync()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'dmscs' AND table_name = 'JobSchedule');
            """
        );
    }

    private async Task<string[]> JournalAsync()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        return (
            await connection.QueryAsync<string>(
                """SELECT scriptname FROM public."dmscs_SchemaVersions" ORDER BY schemaversionsid;"""
            )
        ).ToArray();
    }

    private string CreateIsolatedDatabaseConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = $"dms1437_upgrade_{Guid.NewGuid():N}",
            Pooling = false,
        };

        _databaseName = builder.Database!;
        return builder.ConnectionString;
    }

    private static string CreateMaintenanceConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = "postgres",
            Pooling = false,
        };

        return builder.ConnectionString;
    }
}
