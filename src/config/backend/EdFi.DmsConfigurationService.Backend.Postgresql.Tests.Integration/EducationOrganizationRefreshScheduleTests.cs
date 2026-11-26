// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The refresh schedule reconciler against the real tenant and schedule repositories, on a database deployed with every
/// migration and holding only this fixture's tenants, with the dispatcher's materialization driven directly.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("EducationOrganizationSchedule")]
public class Given_refresh_schedule_reconciliation
{
    private const string ScheduledRefresh = "EducationOrganization.ScheduledRefresh";
    private const string RefreshAll = """{"scope":1,"dataStoreId":0}""";

    private JobUpgradeTestDatabase _database = null!;

    private sealed record ScheduleRow(
        long Id,
        long? TenantId,
        string ScheduleType,
        string JobType,
        string Payload,
        int IntervalMinutes,
        bool Enabled,
        DateTime NextRunAt
    );

    private sealed record JobRow(
        long? TenantId,
        string JobType,
        string Payload,
        string Status,
        long? SourceScheduleId
    );

    /// <summary>A schedule repository that runs a callback before its schedules are listed, once.</summary>
    private sealed class PausingScheduleRepository(IJobScheduleRepository inner, Func<Task> beforeList)
        : IJobScheduleRepository
    {
        private Func<Task>? _beforeList = beforeList;

        public Task<JobScheduleUpsertResult> Upsert(
            JobScheduleUpsertCommand command,
            CancellationToken cancellationToken
        ) => inner.Upsert(command, cancellationToken);

        public Task<JobScheduleDisableResult> Disable(
            string scheduleType,
            CancellationToken cancellationToken
        ) => inner.Disable(scheduleType, cancellationToken);

        public Task<JobScheduleDisableResult> DisableById(long id, CancellationToken cancellationToken) =>
            inner.DisableById(id, cancellationToken);

        public async Task<JobScheduleListResult> ListByType(
            string scheduleType,
            CancellationToken cancellationToken
        )
        {
            Func<Task>? pending = Interlocked.Exchange(ref _beforeList, null);
            if (pending is not null)
            {
                await pending();
            }
            return await inner.ListByType(scheduleType, cancellationToken);
        }

        public Task<JobScheduleMaterializeResult> MaterializeNextDue(
            string owner,
            int leaseSeconds,
            string newJobId,
            JobClaimPolicy policy,
            CancellationToken cancellationToken
        ) => inner.MaterializeNextDue(owner, leaseSeconds, newJobId, policy, cancellationToken);
    }

    /// <summary>A reconciler with its own service provider, as one replica would have.</summary>
    private sealed class Replica : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Replica(
            string connectionString,
            bool multitenant,
            int interval,
            Func<IJobScheduleRepository, IJobScheduleRepository>? wrap = null
        )
        {
            IOptions<DatabaseOptions> database = Options.Create(
                new DatabaseOptions { DatabaseConnection = connectionString, EncryptionKey = "unused" }
            );
            ServiceCollection services = new();
            services.AddLogging();
            services.AddScoped<ITenantContextProvider, TenantContextProvider>();
            services.AddSingleton<IAuditContext>(new TestAuditContext());
            services.AddSingleton(database);
            services.AddTransient<ITenantRepository, TenantRepository>();
            services.AddTransient<IJobScheduleRepository>(provider =>
            {
                IJobScheduleRepository repository = ActivatorUtilities.CreateInstance<JobScheduleRepository>(
                    provider
                );
                return wrap is null ? repository : wrap(repository);
            });
            services.AddEducationOrganizationRefresh(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?> { ["JobSettings:SchedulerEnabled"] = "false" }
                    )
                    .Build()
            );
            _provider = services.BuildServiceProvider();
            Reconciler = new EducationOrganizationRefreshScheduleReconciler(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(
                    new EducationOrganizationRefreshOptions { EdOrgsRefreshIntervalInMins = interval }
                ),
                new JobRuntimeEnvironment(multitenant),
                TimeProvider.System,
                NullLogger<EducationOrganizationRefreshScheduleReconciler>.Instance
            );
        }

        public EducationOrganizationRefreshScheduleReconciler Reconciler { get; }

        public Task<ReconciliationSummary> ReconcileAsync() =>
            Reconciler.ReconcileAsync(CancellationToken.None);

        public void Dispose() => _provider.Dispose();
    }

    [OneTimeSetUp]
    public void Deploy()
    {
        _database = new();
        _database.DeployThrough(42);
    }

    [OneTimeTearDown]
    public Task Drop() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [SetUp]
    public async Task Clear()
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """DELETE FROM dmscs."Job"; DELETE FROM dmscs."JobSchedule"; DELETE FROM dmscs."Tenant";"""
        );
    }

    private async Task<NpgsqlConnection> Open()
    {
        NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private Replica NewReplica(
        bool multitenant,
        int interval = 60,
        Func<IJobScheduleRepository, IJobScheduleRepository>? wrap = null
    ) => new(_database.ConnectionString, multitenant, interval, wrap);

    private async Task<long> InsertTenantAsync(string name)
    {
        await using var connection = await Open();
        return await connection.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = name }
        );
    }

    private async Task<ScheduleRow[]> SchedulesAsync()
    {
        await using var connection = await Open();
        return
        [
            .. await connection.QueryAsync<ScheduleRow>(
                """
                SELECT "Id", "TenantId", "ScheduleType", "JobType", "Payload", "IntervalMinutes", "Enabled", "NextRunAt"
                FROM dmscs."JobSchedule" ORDER BY "Id";
                """
            ),
        ];
    }

    private async Task<JobRow[]> JobsAsync()
    {
        await using var connection = await Open();
        return
        [
            .. await connection.QueryAsync<JobRow>(
                """
                SELECT "TenantId", "JobType", "Payload", "Status", "SourceScheduleId"
                FROM dmscs."Job" ORDER BY "Id";
                """
            ),
        ];
    }

    private async Task MakeDueAsync(long scheduleId)
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."JobSchedule" SET "NextRunAt" = (now() AT TIME ZONE 'UTC') - interval '1 second' WHERE "Id" = @Id;""",
            new { Id = scheduleId }
        );
    }

    private async Task SetJobStatusAsync(string status)
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            UPDATE dmscs."Job"
            SET "Status" = @Status,
                "FinishedAt" = CASE WHEN @Status IN ('Completed', 'Error') THEN now() AT TIME ZONE 'UTC' END,
                "NextAttemptAt" = CASE WHEN @Status IN ('Completed', 'Error') THEN NULL ELSE "NextAttemptAt" END;
            """,
            new { Status = status }
        );
    }

    /// <summary>The dispatcher's materialization, with management disabled: the refresh is not a managed job type.</summary>
    private async Task<JobScheduleMaterializeResult> MaterializeAsync()
    {
        JobScheduleRepository repository = new(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = _database.ConnectionString,
                    EncryptionKey = "unused",
                }
            ),
            new TestAuditContext(),
            new TenantContextProvider()
        );
        return await repository.MaterializeNextDue(
            "schedule-test",
            JobScheduleDispatcherService.LeaseSeconds,
            Guid.NewGuid().ToString("N"),
            JobClaimPolicy.ExcludeManagedDataStores,
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_keeps_one_stable_schedule_per_tenant_and_applies_an_interval_change()
    {
        long first = await InsertTenantAsync("schedule-first");
        long second = await InsertTenantAsync("schedule-second");
        using (var replica = NewReplica(multitenant: true))
        {
            (await replica.ReconcileAsync()).Upserted.Should().Be(2);
        }
        ScheduleRow[] created = await SchedulesAsync();
        created.Select(row => row.TenantId).Should().Equal(first, second);
        created
            .Should()
            .AllSatisfy(row =>
            {
                row.ScheduleType.Should().Be(ScheduledRefresh);
                row.JobType.Should().Be("EducationOrganization.Refresh");
                row.Payload.Should().Be(RefreshAll);
                row.IntervalMinutes.Should().Be(60);
                row.Enabled.Should().BeTrue();
            });

        using (var again = NewReplica(multitenant: true))
        {
            await again.ReconcileAsync();
        }
        (await SchedulesAsync()).Should().Equal(created, "an identical reconciliation changes nothing");

        using (var changed = NewReplica(multitenant: true, interval: 15))
        {
            await changed.ReconcileAsync();
        }
        ScheduleRow[] updated = await SchedulesAsync();
        updated.Select(row => row.Id).Should().Equal(created.Select(row => row.Id));
        updated.Select(row => row.IntervalMinutes).Should().Equal(15, 15);
        updated.Should().OnlyContain(row => row.Enabled);
    }

    [Test]
    public async Task It_keeps_the_schedule_of_a_tenant_created_while_another_replica_reconciles()
    {
        long first = await InsertTenantAsync("race-first");
        long? late = null;
        using var other = NewReplica(multitenant: true);
        // The paused replica has enumerated only the first tenant. Before it decides its schedules, another tenant
        // is created and another replica schedules it.
        using var paused = NewReplica(
            multitenant: true,
            wrap: repository => new PausingScheduleRepository(
                repository,
                async () =>
                {
                    late = await InsertTenantAsync("race-late");
                    (await other.ReconcileAsync()).Upserted.Should().Be(2);
                }
            )
        );

        ReconciliationSummary summary = await paused.ReconcileAsync();

        summary.Upserted.Should().Be(1);
        summary.Disabled.Should().Be(0);
        summary.Kept.Should().Be(2);
        (await SchedulesAsync())
            .Select(row => (row.TenantId, row.Enabled))
            .Should()
            .Equal((first, true), (late, true));
    }

    [Test]
    public async Task It_switches_tenancy_modes_keeping_every_row_and_job()
    {
        long first = await InsertTenantAsync("mode-first");
        long second = await InsertTenantAsync("mode-second");
        using (var multi = NewReplica(multitenant: true))
        {
            await multi.ReconcileAsync();
        }
        ScheduleRow[] tenantSchedules = await SchedulesAsync();
        (await MaterializeAsync()).Should().BeOfType<JobScheduleMaterializeResult.Materialized>();
        JobRow[] jobs = await JobsAsync();
        jobs.Should().ContainSingle();

        using (var single = NewReplica(multitenant: false))
        {
            ReconciliationSummary summary = await single.ReconcileAsync();
            summary.Disabled.Should().Be(2);
        }
        ScheduleRow[] afterSingle = await SchedulesAsync();
        afterSingle
            .Select(row => (row.Id, row.TenantId, row.Enabled))
            .Should()
            .Equal(
                (tenantSchedules[0].Id, first, false),
                (tenantSchedules[1].Id, second, false),
                (afterSingle[2].Id, (long?)null, true)
            );
        (await JobsAsync()).Should().Equal(jobs);

        using (var back = NewReplica(multitenant: true))
        {
            ReconciliationSummary summary = await back.ReconcileAsync();
            summary.Disabled.Should().Be(1);
        }
        (await SchedulesAsync())
            .Select(row => (row.Id, row.TenantId, row.Enabled))
            .Should()
            .Equal(
                (tenantSchedules[0].Id, first, true),
                (tenantSchedules[1].Id, second, true),
                (afterSingle[2].Id, (long?)null, false)
            );
        (await JobsAsync()).Should().Equal(jobs);
    }

    [Test]
    public async Task It_turns_a_due_occurrence_into_a_refresh_job_and_skips_occurrences_while_it_is_active()
    {
        long tenant = await InsertTenantAsync("occurrence");
        using (var replica = NewReplica(multitenant: true))
        {
            await replica.ReconcileAsync();
        }
        long scheduleId = (await SchedulesAsync()).Single().Id;

        // The first occurrence is due at once.
        (await MaterializeAsync())
            .Should()
            .BeOfType<JobScheduleMaterializeResult.Materialized>();
        (await JobsAsync())
            .Should()
            .Equal(new JobRow(tenant, "EducationOrganization.Refresh", RefreshAll, "Pending", scheduleId));

        foreach (string active in new[] { "Pending", "InProgress" })
        {
            await SetJobStatusAsync(active);
            await MakeDueAsync(scheduleId);
            (await MaterializeAsync())
                .Should()
                .BeOfType<JobScheduleMaterializeResult.SkippedActiveJob>($"the refresh job is {active}");
            (await JobsAsync()).Should().ContainSingle();
        }

        await SetJobStatusAsync("Completed");
        await MakeDueAsync(scheduleId);
        (await MaterializeAsync()).Should().BeOfType<JobScheduleMaterializeResult.Materialized>();
        (await JobsAsync())
            .Select(job => (job.Status, job.SourceScheduleId))
            .Should()
            .Equal(("Completed", scheduleId), ("Pending", scheduleId));
    }
}
