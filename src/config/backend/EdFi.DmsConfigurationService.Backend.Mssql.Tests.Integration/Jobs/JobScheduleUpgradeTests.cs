// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0032, the state a real
/// deployment from before DMS-1437 upgrades from, through script 0033 only, and checks that the
/// upgrade adds exactly the JobSchedule script and table and that a repeat deploy adds nothing.
/// </summary>
[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_database_deployed_before_the_JobSchedule_script
{
    private JobUpgradeTestDatabase _database = null!;
    private bool _tableExistedBeforeUpgrade;
    private string[] _journalBeforeUpgrade = [];
    private string[] _journalAfterUpgrade = [];
    private string[] _journalAfterRepeatDeploy = [];
    private bool _tableExistsAfterUpgrade;
    private bool _jobTableExistsAfterUpgrade;
    private string[] _objectsAfterUpgrade = [];

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();

        _database.DeployThrough(JobUpgradeTestDatabase.PreTicketScript);
        _tableExistedBeforeUpgrade = await _database.TableExistsAsync("JobSchedule");
        _journalBeforeUpgrade = await _database.JournalAsync();

        _database.DeployThrough(JobUpgradeTestDatabase.JobScheduleScript);
        _journalAfterUpgrade = await _database.JournalAsync();
        _tableExistsAfterUpgrade = await _database.TableExistsAsync("JobSchedule");
        _jobTableExistsAfterUpgrade = await _database.TableExistsAsync("Job");
        _objectsAfterUpgrade = await _database.ConstraintAndIndexNamesAsync("JobSchedule");

        _database.DeployThrough(JobUpgradeTestDatabase.JobScheduleScript);
        _journalAfterRepeatDeploy = await _database.JournalAsync();
    }

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [Test]
    public void It_deploys_the_earlier_scripts_without_the_JobSchedule_table()
    {
        _tableExistedBeforeUpgrade.Should().BeFalse();
        _journalBeforeUpgrade.Should().NotBeEmpty();
        _journalBeforeUpgrade
            .Should()
            .OnlyContain(name =>
                JobUpgradeTestDatabase.ScriptNumber(name) <= JobUpgradeTestDatabase.PreTicketScript
            );
    }

    [Test]
    public void It_adds_the_JobSchedule_table_with_its_constraints_and_indexes()
    {
        _tableExistsAfterUpgrade.Should().BeTrue();
        _jobTableExistsAfterUpgrade.Should().BeFalse("the upgrade is bounded through script 0033");
        _objectsAfterUpgrade
            .Should()
            .BeEquivalentTo(
                "CK_JobSchedule_IntervalMinutes",
                "CK_JobSchedule_Payload_Object",
                "FK_JobSchedule_Tenant",
                "IX_JobSchedule_Due",
                "IX_JobSchedule_TenantId",
                "PK_JobSchedule",
                "UX_JobSchedule_SingleTenant_Type",
                "UX_JobSchedule_Tenant_Type"
            );
    }

    [Test]
    public void It_journals_exactly_the_JobSchedule_script() =>
        _journalAfterUpgrade
            .Except(_journalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.JobScheduleScriptName);

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(_journalAfterUpgrade);
}
