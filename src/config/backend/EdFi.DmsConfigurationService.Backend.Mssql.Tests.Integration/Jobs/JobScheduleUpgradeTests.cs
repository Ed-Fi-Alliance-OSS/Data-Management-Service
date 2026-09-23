// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0031, the state a real
/// deployment from before DMS-1437 upgrades from, and checks that the upgrade adds exactly the
/// JobSchedule script and table and that a repeat deploy adds nothing.
/// </summary>
[TestFixture]
[Category("MssqlIntegration")]
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
    private int _constraintsAfterUpgrade;
    private int _indexesAfterUpgrade;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );

        _connectionString = CreateIsolatedDatabaseConnectionString();

        DeploySuccessfully(new Deploy.DatabaseDeploy { ScriptFilter = IsBeforeJobScheduleScript });
        _tableExistedBeforeUpgrade = await TableExistsAsync();
        _journalBeforeUpgrade = await JournalAsync();

        DeploySuccessfully(new Deploy.DatabaseDeploy());
        _journalAfterUpgrade = await JournalAsync();
        _tableExistsAfterUpgrade = await TableExistsAsync();

        await using (SqlConnection connection = new(_connectionString))
        {
            await connection.OpenAsync();
            _constraintsAfterUpgrade = await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM sys.objects
                WHERE parent_object_id = OBJECT_ID(N'dmscs.JobSchedule')
                  AND name IN (N'PK_JobSchedule', N'FK_JobSchedule_Tenant', N'CK_JobSchedule_Payload_Object',
                               N'CK_JobSchedule_IntervalMinutes');
                """
            );
            _indexesAfterUpgrade = await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dmscs.JobSchedule')
                  AND name IN (N'UX_JobSchedule_Tenant_Type', N'UX_JobSchedule_SingleTenant_Type',
                               N'IX_JobSchedule_Due', N'IX_JobSchedule_TenantId');
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

        await using SqlConnection connection = new(CreateMasterConnectionString());
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            IF DB_ID('{_databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END;
            """
        );
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
                "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0032_Create_JobSchedule_Table.sql"
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
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT CAST(CASE WHEN OBJECT_ID(N'dmscs.JobSchedule', N'U') IS NULL THEN 0 ELSE 1 END AS bit);"
        );
    }

    private async Task<string[]> JournalAsync()
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        return (
            await connection.QueryAsync<string>(
                "SELECT ScriptName FROM dbo.dmscs_SchemaVersions ORDER BY Id;"
            )
        ).ToArray();
    }

    private string CreateIsolatedDatabaseConnectionString()
    {
        SqlConnectionStringBuilder builder = new(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = $"dms1437_upgrade_{Guid.NewGuid():N}",
            Pooling = false,
        };

        _databaseName = builder.InitialCatalog;
        return builder.ConnectionString;
    }

    private static string CreateMasterConnectionString() =>
        new SqlConnectionStringBuilder(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        }.ConnectionString;
}
