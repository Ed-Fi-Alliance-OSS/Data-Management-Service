// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

internal static class JobTableObjects
{
    public static readonly string[] Names =
    [
        "CK_Job_AttemptCount",
        "CK_Job_NextAttemptAt_Active",
        "CK_Job_Occurrence_Pairing",
        "CK_Job_Payload_Object",
        "CK_Job_Status",
        "FK_Job_JobSchedule",
        "FK_Job_Tenant",
        "IX_Job_Claim",
        "IX_Job_Retention",
        "IX_Job_TenantId",
        "PK_Job",
        "UX_Job_JobId",
        "UX_Job_SourceScheduleId_ScheduledOccurrence",
    ];
}

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0032 through script 0033 only,
/// and checks that the upgrade adds exactly the Job script and table and that a repeat deploy adds nothing.
/// </summary>
[TestFixture]
public class Given_a_database_deployed_through_the_JobSchedule_script
{
    private readonly JobUpgradeTestDatabase _database = new();
    private bool _tableExistedBeforeUpgrade;
    private string[] _journalBeforeUpgrade = [];
    private string[] _journalAfterUpgrade = [];
    private string[] _journalAfterRepeatDeploy = [];
    private bool _tableExistsAfterUpgrade;
    private string[] _objectsAfterUpgrade = [];

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        _database.DeployThrough(JobUpgradeTestDatabase.JobScheduleScript);
        _tableExistedBeforeUpgrade = await _database.TableExistsAsync("Job");
        _journalBeforeUpgrade = await _database.JournalAsync();

        _database.DeployThrough(JobUpgradeTestDatabase.JobScript);
        _journalAfterUpgrade = await _database.JournalAsync();
        _tableExistsAfterUpgrade = await _database.TableExistsAsync("Job");
        _objectsAfterUpgrade = await _database.ConstraintAndIndexNamesAsync("Job");

        _database.DeployThrough(JobUpgradeTestDatabase.JobScript);
        _journalAfterRepeatDeploy = await _database.JournalAsync();
    }

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database.DropAsync();

    [Test]
    public void It_deploys_through_the_JobSchedule_script_without_the_Job_table()
    {
        _tableExistedBeforeUpgrade.Should().BeFalse();
        _journalBeforeUpgrade.Should().Contain(JobUpgradeTestDatabase.JobScheduleScriptName);
        _journalBeforeUpgrade
            .Should()
            .OnlyContain(name =>
                JobUpgradeTestDatabase.ScriptNumber(name) <= JobUpgradeTestDatabase.JobScheduleScript
            );
    }

    [Test]
    public void It_adds_the_Job_table_with_its_constraints_and_indexes()
    {
        _tableExistsAfterUpgrade.Should().BeTrue();
        _objectsAfterUpgrade.Should().BeEquivalentTo(JobTableObjects.Names);
    }

    [Test]
    public void It_journals_exactly_the_Job_script() =>
        _journalAfterUpgrade
            .Except(_journalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.JobScriptName);

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(_journalAfterUpgrade);
}

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0031, before DMS-1437, through
/// both DMS-1437 migrations in one deploy, and checks that exactly scripts 0032 and 0033 are journaled,
/// in that order, that both tables exist, and that a repeat deploy adds nothing.
/// </summary>
[TestFixture]
public class Given_a_pre_ticket_database_upgraded_through_both_job_migrations
{
    private readonly JobUpgradeTestDatabase _database = new();
    private bool _scheduleTableExistedBeforeUpgrade;
    private bool _jobTableExistedBeforeUpgrade;
    private string[] _journalBeforeUpgrade = [];
    private string[] _journalAfterUpgrade = [];
    private string[] _journalAfterRepeatDeploy = [];
    private bool _scheduleTableExistsAfterUpgrade;
    private string[] _jobObjectsAfterUpgrade = [];

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        _database.DeployThrough(JobUpgradeTestDatabase.PreTicketScript);
        _scheduleTableExistedBeforeUpgrade = await _database.TableExistsAsync("JobSchedule");
        _jobTableExistedBeforeUpgrade = await _database.TableExistsAsync("Job");
        _journalBeforeUpgrade = await _database.JournalAsync();

        _database.DeployThrough(JobUpgradeTestDatabase.JobScript);
        _journalAfterUpgrade = await _database.JournalAsync();
        _scheduleTableExistsAfterUpgrade = await _database.TableExistsAsync("JobSchedule");
        _jobObjectsAfterUpgrade = await _database.ConstraintAndIndexNamesAsync("Job");

        _database.DeployThrough(JobUpgradeTestDatabase.JobScript);
        _journalAfterRepeatDeploy = await _database.JournalAsync();
    }

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database.DropAsync();

    [Test]
    public void It_starts_without_either_job_table()
    {
        _scheduleTableExistedBeforeUpgrade.Should().BeFalse();
        _jobTableExistedBeforeUpgrade.Should().BeFalse();
    }

    [Test]
    public void It_journals_exactly_both_job_scripts_in_order() =>
        _journalAfterUpgrade
            .Except(_journalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.JobScheduleScriptName, JobUpgradeTestDatabase.JobScriptName);

    [Test]
    public void It_creates_both_tables()
    {
        _scheduleTableExistsAfterUpgrade.Should().BeTrue();
        _jobObjectsAfterUpgrade.Should().BeEquivalentTo(JobTableObjects.Names);
    }

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(_journalAfterUpgrade);
}
