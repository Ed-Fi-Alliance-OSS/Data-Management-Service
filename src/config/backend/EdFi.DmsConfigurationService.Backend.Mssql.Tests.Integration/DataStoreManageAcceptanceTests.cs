// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private static DmsDataStoreProvisionerReadiness CreateReadiness(string state = "Ready")
    {
        DmsDataStoreProvisionerReadiness readiness = new();
        if (state == "Ready")
        {
            readiness.Record(new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow));
        }

        if (state == "Disabled")
        {
            readiness.Record(new DmsProvisionerReadiness.Disabled());
        }

        if (state == "NotReady")
        {
            readiness.Record(
                new DmsProvisionerReadiness.NotReady([
                    new(DmsProvisioningFailureCode.StartupValidationFailed, "TestProbe"),
                ])
            );
        }

        return readiness;
    }

    private static ManagedDataStoreCreateCommand CreateCommand(
        string name = "Store",
        string provider = "postgresql"
    ) =>
        new(
            CatalogName.Trim(name),
            DmsDatabaseTemplate.Minimal,
            ManagedDataStoreNameFormatter.Format(
                name,
                DmsDatabaseTemplate.Minimal,
                ProtectedDatabaseNames.Create("cms", [])
            ),
            Guid.NewGuid(),
            new(
                provider,
                "tcp",
                "db.example",
                provider == "postgresql" ? 5432 : 1433,
                null,
                provider == "postgresql" ? "postgres" : "master"
            )
        );

    private static TenantContextProvider CreateTenantContext(long? tenantId) =>
        new()
        {
            Context = tenantId is long id
                ? new TenantContext.Multitenant(id, "acceptance-test")
                : new TenantContext.NotMultitenant(),
        };

    private ServiceProvider AcceptanceServices(long? tenantId = null)
    {
        ServiceCollection services = new();
        services.AddJobServices();
        services.AddJobHandler<
            AcceptanceOnlyCreateHandler,
            CreateManagedDataStorePayload,
            CreateManagedDataStorePayloadValidator
        >(ManagedJobTypes.Create, 1);
        services.AddJobHandler<
            AcceptanceOnlyDeleteHandler,
            DeleteManagedDataStorePayload,
            DeleteManagedDataStorePayloadValidator
        >(ManagedJobTypes.Delete, ManagedJobTypes.PayloadVersion);
        services.AddSingleton<IJobRepository>(
            new JobRepository(
                Options.Create(
                    new DatabaseOptions
                    {
                        DatabaseConnection = _database.ConnectionString,
                        EncryptionKey = "unused",
                    }
                ),
                new TestAuditContext(),
                CreateTenantContext(tenantId)
            )
        );
        return services.BuildServiceProvider();
    }

    public sealed class AcceptanceOnlyCreateHandler : IJobHandler<CreateManagedDataStorePayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            CreateManagedDataStorePayload payload,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Acceptance-only handler must never execute");
    }

    private DataStoreManageRepository ManagedRepository(
        IJobEnqueuer enqueuer,
        long? tenantId = null,
        IDmsDataStoreProvisionerReadiness? readiness = null,
        string? applicationName = null,
        ProtectedDatabaseNames? protectedNames = null
    )
    {
        SqlConnectionStringBuilder builder = new(_database.ConnectionString);
        if (applicationName is not null)
        {
            builder.ApplicationName = applicationName;
        }

        return new(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = builder.ConnectionString,
                    EncryptionKey = "unused",
                }
            ),
            CreateTenantContext(tenantId),
            readiness ?? CreateReadiness(),
            enqueuer,
            protectedNames ?? ProtectedDatabaseNames.Create("cms", [])
        );
    }

    private sealed class EnqueuerDecorator(
        Func<JobEnqueueCommand, DbTransaction, CancellationToken, Task<JobEnqueueResult>> enqueue
    ) : IJobEnqueuer
    {
        public Task<JobEnqueueResult> EnqueueAsync(
            JobEnqueueCommand command,
            DbTransaction? transaction,
            CancellationToken cancellationToken
        ) =>
            enqueue(
                command,
                transaction ?? throw new AssertionException("Acceptance must supply its transaction."),
                cancellationToken
            );
    }

    private async Task AssertAcceptanceCounts(int aggregates, int jobs)
    {
        await using var connection = await Open();
        (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStoreManage";"""))
            .Should()
            .Be(aggregates);
        (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."Job";""")).Should().Be(jobs);
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM dmscs."DataStoreManage" WHERE "CreateJobId" = 'pending-enqueue';"""
            )
        )
            .Should()
            .Be(0);
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_rolls_back_the_job_when_the_final_link_update_affects_no_aggregate()
    {
        using var services = AcceptanceServices();
        var real = services.GetRequiredService<IJobEnqueuer>();
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                JobEnqueueResult result = await real.EnqueueAsync(command, transaction, token);
                result.Should().BeOfType<JobEnqueueResult.Success>();
                (
                    await transaction.Connection!.ExecuteAsync(
                        """DELETE FROM dmscs."DataStoreManage";""",
                        transaction: transaction
                    )
                )
                    .Should()
                    .Be(1);
                return result;
            }
        );
        (await ManagedRepository(decorator).AcceptCreateAsync(CreateCommand(), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.FailureUnknown>();
        await AssertAcceptanceCounts(0, 0);
        (await ManagedRepository(real).AcceptCreateAsync(CreateCommand(), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_serializes_real_acceptance_with_ordinary_insert_and_rename_in_both_orders(
        [Values("insert", "rename")] string operation,
        [Values(false, true)] bool ordinaryFirst,
        [Values(false, true)] bool keyDistinct,
        [Values(false, true)] bool multitenant
    )
    {
        long? tenant = multitenant ? await Tenant("selected") : null;
        int existingId = (await Repository(tenant).InsertDataStore(Insert("existing")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        using var services = AcceptanceServices(tenant);
        var real = services.GetRequiredService<IJobEnqueuer>();
        string ordinaryName = keyDistinct ? "café" : "Store";
        string managedName = keyDistinct ? "cafe" : "store";
        string applicationName = "acceptance-" + Guid.NewGuid().ToString("N");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DataStoreManageCreateResult>? managed = null;
        Task<object>? ordinary = null;
        bool waited = false;
        if (ordinaryFirst)
        {
            using ManualResetEventSlim release = new(false);
            var audit = A.Fake<IAuditContext>();
            A.CallTo(() => audit.GetCurrentTimestamp()).Returns(DateTime.UtcNow);
            A.CallTo(() => audit.GetCurrentUser())
                .Invokes(() =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(15)))
                    {
                        throw new TimeoutException("Ordinary writer was not released.");
                    }
                })
                .Returns("acceptance-test");
            var repository = Repository(tenant, audit: audit);
            ordinary = Task.Run(async () =>
                operation == "insert"
                    ? (object)await repository.InsertDataStore(Insert(ordinaryName))
                    : await repository.UpdateDataStore(Rename(existingId, ordinaryName))
            );
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                managed = ManagedRepository(real, tenant, applicationName: applicationName)
                    .AcceptCreateAsync(CreateCommand(managedName), default);
                waited = await ObserveGateWait(applicationName, managed);
            }
            finally
            {
                release.Set();
                try
                {
                    await ordinary;
                }
                finally
                {
                    if (managed is not null)
                    {
                        await managed;
                    }
                }
            }
        }
        else
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var decorator = new EnqueuerDecorator(
                async (command, transaction, token) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                    return await real.EnqueueAsync(command, transaction, token);
                }
            );
            managed = ManagedRepository(decorator, tenant)
                .AcceptCreateAsync(CreateCommand(managedName), default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var repository = Repository(tenant, applicationName);
                ordinary = Mutate();
                async Task<object> Mutate() =>
                    operation == "insert"
                        ? await repository.InsertDataStore(Insert(ordinaryName))
                        : await repository.UpdateDataStore(Rename(existingId, ordinaryName));
                waited = await ObserveGateWait(applicationName, ordinary);
            }
            finally
            {
                release.TrySetResult();
                try
                {
                    await managed;
                }
                finally
                {
                    if (ordinary is not null)
                    {
                        await ordinary;
                    }
                }
            }
        }
        waited.Should().BeTrue("the contender must wait for the actual writer's transaction");
        (await managed!)
            .Should()
            .BeOfType(
                ordinaryFirst && !keyDistinct
                    ? typeof(DataStoreManageCreateResult.NameReserved)
                    : typeof(DataStoreManageCreateResult.Success)
            );
        bool ordinarySucceeds = ordinaryFirst || keyDistinct;
        Type expectedOrdinary = (operation, ordinarySucceeds) switch
        {
            ("insert", true) => typeof(DataStoreInsertResult.Success),
            ("insert", false) => typeof(DataStoreInsertResult.FailureDuplicateName),
            (_, true) => typeof(DataStoreUpdateResult.Success),
            _ => typeof(DataStoreUpdateResult.FailureDuplicateName),
        };
        (await ordinary!).Should().BeOfType(expectedOrdinary);
        await AssertAcceptanceCounts(
            ordinaryFirst && !keyDistinct ? 0 : 1,
            ordinaryFirst && !keyDistinct ? 0 : 1
        );
    }

    [TestCase("name", false), TestCase("name", true)]
    [TestCase("target", false), TestCase("target", true)]
    [Category("DataStoreManageAcceptance")]
    public async Task It_serializes_competing_managed_names_and_cross_tenant_targets(
        string collision,
        bool firstHasTenant
    )
    {
        long selected = await Tenant("selected");
        long other = await Tenant("other");
        long? firstTenant = firstHasTenant ? selected : null;
        long? secondTenant = collision == "name" ? firstTenant : other;
        using var firstServices = AcceptanceServices(firstTenant);
        using var secondServices = AcceptanceServices(secondTenant);
        var firstReal = firstServices.GetRequiredService<IJobEnqueuer>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                return await firstReal.EnqueueAsync(command, transaction, token);
            }
        );
        Task<DataStoreManageCreateResult> first = ManagedRepository(decorator, firstTenant)
            .AcceptCreateAsync(CreateCommand(), default);
        Task<DataStoreManageCreateResult>? second = null;
        bool waited = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string applicationName = "acceptance-" + Guid.NewGuid().ToString("N");
            second = ManagedRepository(
                    secondServices.GetRequiredService<IJobEnqueuer>(),
                    secondTenant,
                    applicationName: applicationName
                )
                .AcceptCreateAsync(CreateCommand(collision == "name" ? "store" : "EdFi_Ods_Store"), default);
            waited = await ObserveGateWait(applicationName, second);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await first;
            }
            finally
            {
                if (second is not null)
                {
                    await second;
                }
            }
        }
        waited.Should().BeTrue();
        (await first).Should().BeOfType<DataStoreManageCreateResult.Success>();
        (await second!)
            .Should()
            .BeOfType(
                collision == "name"
                    ? typeof(DataStoreManageCreateResult.NameReserved)
                    : typeof(DataStoreManageCreateResult.TargetReserved)
            );
        await AssertAcceptanceCounts(1, 1);
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_allows_equal_names_in_different_tenants_when_the_target_is_distinct()
    {
        long selected = await Tenant("selected");
        using var firstServices = AcceptanceServices();
        using var secondServices = AcceptanceServices(selected);
        var command = CreateCommand();
        (
            await ManagedRepository(firstServices.GetRequiredService<IJobEnqueuer>())
                .AcceptCreateAsync(command, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
        var second = command with
        {
            ExpectedSourceIdentity = Guid.NewGuid(),
            Endpoint = command.Endpoint with { Host = "other.example" },
        };
        (
            await ManagedRepository(secondServices.GetRequiredService<IJobEnqueuer>(), selected)
                .AcceptCreateAsync(second, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
        await AssertAcceptanceCounts(2, 2);
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_atomically_accepts_one_aggregate_and_real_validated_job(
        [Values(false, true)] bool multitenant,
        [Values("postgresql", "mssql")] string targetProvider
    )
    {
        long? tenant = multitenant ? await Tenant("selected") : null;
        using var services = AcceptanceServices(tenant);
        var command = CreateCommand(provider: targetProvider);
        var ready = new DmsDataStoreProvisionerReadiness();
        ready.Record(new DmsProvisionerReadiness.Ready(targetProvider, DateTimeOffset.UtcNow));
        await using var connection = await Open();
        DateTime before = await connection.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        var result = (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), tenant, ready)
                .AcceptCreateAsync(command, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>()
            .Subject;
        DateTime after = await connection.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        var row =
            (IDictionary<string, object>)
                await connection.QuerySingleAsync("""SELECT * FROM dmscs."DataStoreManage";""");
        row["Id"].Should().Be(result.Id);
        row["TenantId"].Should().Be(tenant);
        row["Name"].Should().Be(command.Name);
        row["Status"].Should().Be("PendingCreate");
        row["Template"].Should().Be("Minimal");
        row["TargetDatabaseName"].Should().Be(command.TargetName.Value);
        row["ExpectedSourceIdentity"].Should().Be(command.ExpectedSourceIdentity);
        row["EndpointDescriptor"].Should().Be(command.Endpoint.CanonicalEncoding);
        row["CreateJobId"].Should().Be(result.JobId).And.NotBe("pending-enqueue");
        foreach (
            string field in new[]
            {
                "DataStoreId",
                "HistoricalDataStoreId",
                "HistoricalDataStoreName",
                "DeleteJobId",
                "DeletedAt",
                "LastFailureCategory",
                "PinnedContractVersion",
            }
        )
        {
            row[field].Should().BeNull();
        }

        row["LastAttempt"].Should().Be(0);
        row["CreatedAt"]
            .Should()
            .Be(row["UpdatedAt"])
            .And.Be(row["LastRefreshed"])
            .And.Be(row["LastModifiedDate"]);
        ((DateTime)row["CreatedAt"]).Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        var job =
            (IDictionary<string, object>)await connection.QuerySingleAsync("""SELECT * FROM dmscs."Job";""");
        job["JobId"].Should().Be(result.JobId);
        job["TenantId"].Should().Be(tenant);
        job["JobType"].Should().Be(ManagedJobTypes.Create);
        job["PayloadVersion"].Should().Be((short)1);
        job["Status"].Should().Be("Pending");
        JobPayloadSerializer
            .Deserialize<CreateManagedDataStorePayload>((string)job["Payload"])
            .Should()
            .BeOfType<JobPayloadReadResult<CreateManagedDataStorePayload>.Success>()
            .Subject.Payload.ManageId.Should()
            .Be(result.Id);
        await AssertAcceptanceCounts(1, 1);
    }

    [TestCase("type"), TestCase("version"), TestCase("malformed"), TestCase("invalid")]
    [Category("DataStoreManageAcceptance")]
    public async Task It_rolls_back_the_insert_when_real_job_validation_rejects_the_envelope(string failure)
    {
        using var services = AcceptanceServices();
        var real = services.GetRequiredService<IJobEnqueuer>();
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                (
                    await transaction.Connection!.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."DataStoreManage";""",
                        transaction: transaction
                    )
                )
                    .Should()
                    .Be(1);
                return await real.EnqueueAsync(
                    failure switch
                    {
                        "type" => command with { JobType = "Unknown.Create" },
                        "version" => command with { PayloadVersion = 2 },
                        "malformed" => command with { PayloadJson = "{" },
                        _ => command with { PayloadJson = "{\"manageId\":0}" },
                    },
                    transaction,
                    token
                );
            }
        );
        var result = (await ManagedRepository(decorator).AcceptCreateAsync(CreateCommand(), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.EnqueueFailed>()
            .Subject;
        result
            .Failure.Should()
            .BeOfType(
                failure switch
                {
                    "type" => typeof(JobEnqueueResult.FailureUnsupportedType),
                    "version" => typeof(JobEnqueueResult.FailureUnsupportedVersion),
                    _ => typeof(JobEnqueueResult.FailurePayloadInvalid),
                }
            );
        await AssertAcceptanceCounts(0, 0);
        (await ManagedRepository(real).AcceptCreateAsync(CreateCommand(), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
    }

    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
    [Category("DataStoreManageAcceptance")]
    public async Task It_rolls_back_after_row_or_enqueue_on_exception_or_cancellation(
        bool afterEnqueue,
        bool cancel
    )
    {
        using var services = AcceptanceServices();
        var real = services.GetRequiredService<IJobEnqueuer>();
        using CancellationTokenSource cancellation = new();
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                (
                    await transaction.Connection!.ExecuteScalarAsync<int>(
                        """SELECT COUNT(*) FROM dmscs."DataStoreManage";""",
                        transaction: transaction
                    )
                )
                    .Should()
                    .Be(1);
                if (afterEnqueue)
                {
                    (await real.EnqueueAsync(command, transaction, token))
                        .Should()
                        .BeOfType<JobEnqueueResult.Success>();
                }

                if (cancel)
                {
                    await cancellation.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }
                throw new InvalidOperationException("Injected after acceptance write");
            }
        );
        var action = () =>
            ManagedRepository(decorator).AcceptCreateAsync(CreateCommand(), cancellation.Token);
        if (cancel)
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            (await action())
                .Should()
                .BeOfType<DataStoreManageCreateResult.FailureUnknown>()
                .Subject.Diagnostic.ToString()
                .Should()
                .NotContain("Injected after acceptance write");
        }

        await AssertAcceptanceCounts(0, 0);
        (await ManagedRepository(real).AcceptCreateAsync(CreateCommand(), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_rolls_back_the_job_when_final_job_link_update_fails_before_commit()
    {
        using var services = AcceptanceServices();
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER dmscs.reject_acceptance_link ON dmscs.DataStoreManage AFTER UPDATE AS
            BEGIN THROW 50001, 'Injected link update failure', 1; END;
            """
        );
        try
        {
            var action = () =>
                ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                    .AcceptCreateAsync(CreateCommand(), default);
            (await action())
                .Should()
                .BeOfType<DataStoreManageCreateResult.FailureUnknown>()
                .Subject.Diagnostic.ToString()
                .Should()
                .NotContain("Injected link update failure");
            await AssertAcceptanceCounts(0, 0);
        }
        finally
        {
            await connection.ExecuteAsync("""DROP TRIGGER dmscs.reject_acceptance_link;""");
        }
        (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                .AcceptCreateAsync(CreateCommand(), default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
    }

    [TestCase("StartupPending"), TestCase("NotReady"), TestCase("Disabled")]
    [Category("DataStoreManageAcceptance")]
    public async Task It_refuses_closed_readiness_for_direct_and_orchestrated_acceptance_without_reservations(
        string state
    )
    {
        using var services = AcceptanceServices();
        var readiness = CreateReadiness(state);
        var repository = ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), readiness: readiness);
        var source = A.Fake<IDmsAdminConnectionSource>();
        var acceptance = new ManagedDataStoreCreateAcceptance(
            readiness,
            source,
            PostgresqlManagedEndpoint.TryParse,
            ProtectedDatabaseNames.Create("cms", []),
            repository
        );
        (await acceptance.AcceptAsync(new() { Name = "Store", DatabaseTemplate = "Minimal" }, default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.ProvisioningUnavailable>();
        (await repository.AcceptCreateAsync(CreateCommand(), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.ProvisioningUnavailable>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustNotHaveHappened();
        await AssertAcceptanceCounts(0, 0);
        if (state != "StartupPending")
        {
            var rerecord = () =>
                readiness.Record(new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow));
            rerecord.Should().Throw<InvalidOperationException>();
        }
        (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), readiness: CreateReadiness())
                .AcceptCreateAsync(CreateCommand(), default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_rechecks_readiness_after_the_orchestrator_before_the_first_write()
    {
        using var services = AcceptanceServices();
        var readiness = A.Fake<IDmsDataStoreProvisionerReadiness>();
        A.CallTo(() => readiness.Current)
            .ReturnsNextFromSequence(
                new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow),
                new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow),
                new DmsProvisionerReadiness.Disabled()
            );
        var source = A.Fake<IDmsAdminConnectionSource>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._))
            .Returns(new DmsAdminConnectionResolution.Resolved(new("Host=db.example")));
        var acceptance = new ManagedDataStoreCreateAcceptance(
            readiness,
            source,
            PostgresqlManagedEndpoint.TryParse,
            ProtectedDatabaseNames.Create("cms", []),
            ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), readiness: readiness)
        );
        (await acceptance.AcceptAsync(new() { Name = "Store", DatabaseTemplate = "Minimal" }, default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.ProvisioningUnavailable>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => readiness.Current).MustHaveHappened(3, Times.Exactly);
        await AssertAcceptanceCounts(0, 0);
    }

    [TestCase("name"), TestCase("identity"), TestCase("target"), TestCase("endpoint"), TestCase("template")]
    [
        TestCase("provider"),
        TestCase("transport"),
        TestCase("maintenance"),
        TestCase("port"),
        TestCase("generated-target")
    ]
    [Category("DataStoreManageAcceptance")]
    public async Task It_rejects_invalid_direct_repository_commands_without_writes(string field)
    {
        using var services = AcceptanceServices();
        var command = CreateCommand();
        command = field switch
        {
            "name" => command with { Name = "bad-name" },
            "identity" => command with { ExpectedSourceIdentity = Guid.Empty },
            "target" => command with { TargetName = default },
            "endpoint" => command with { Endpoint = command.Endpoint with { Host = "" } },
            "provider" => command with { Endpoint = CreateCommand(provider: "mssql").Endpoint },
            "transport" => command with { Endpoint = command.Endpoint with { Transport = "unknown" } },
            "maintenance" => command with
            {
                Endpoint = command.Endpoint with { MaintenanceDatabase = "custom" },
            },
            "port" => command with { Endpoint = command.Endpoint with { Port = 0 } },
            "generated-target" => command with { TargetName = Target("different") },
            _ => command with { Template = null! },
        };
        (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                .AcceptCreateAsync(command, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
        await AssertAcceptanceCounts(0, 0);
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_rechecks_the_deployment_protected_set_for_direct_repository_callers()
    {
        using var services = AcceptanceServices();
        var command = CreateCommand();
        var repository = ManagedRepository(
            services.GetRequiredService<IJobEnqueuer>(),
            protectedNames: ProtectedDatabaseNames.Create(command.TargetName.Value, [])
        );
        (await repository.AcceptCreateAsync(command, default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
        await AssertAcceptanceCounts(0, 0);
    }

    [TestCase("postgresql"), TestCase("mssql")]
    [Category("DataStoreManageAcceptance")]
    public async Task It_persists_the_single_resolved_snapshot_even_after_the_source_rotates(
        string targetProvider
    )
    {
        using var services = AcceptanceServices();
        var ready = new DmsDataStoreProvisionerReadiness();
        ready.Record(new DmsProvisionerReadiness.Ready(targetProvider, DateTimeOffset.UtcNow));
        var source = A.Fake<IDmsAdminConnectionSource>();
        string snapshot =
            targetProvider == "postgresql"
                ? "Host=ORIGINAL.EXAMPLE;Username=admin;Password=snapshot-secret"
                : "Server=tcp:ORIGINAL.EXAMPLE,1433;User ID=admin;Password=snapshot-secret";
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._))
            .ReturnsLazily(() => new DmsAdminConnectionResolution.Resolved(new(snapshot)));
        ManagedDataStoreEndpointParser parser =
            targetProvider == "postgresql"
                ? PostgresqlManagedEndpoint.TryParse
                : MssqlManagedEndpoint.TryParse;
        var decorator = new EnqueuerDecorator(
            async (command, transaction, token) =>
            {
                snapshot =
                    targetProvider == "postgresql"
                        ? "Host=rotated.example"
                        : "Server=tcp:rotated.example,1433";
                return await services
                    .GetRequiredService<IJobEnqueuer>()
                    .EnqueueAsync(command, transaction, token);
            }
        );
        var acceptance = new ManagedDataStoreCreateAcceptance(
            ready,
            source,
            parser,
            ProtectedDatabaseNames.Create("cms", []),
            ManagedRepository(decorator, readiness: ready)
        );
        (await acceptance.AcceptAsync(new() { Name = "Store", DatabaseTemplate = "Minimal" }, default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        var subsequent = (DmsAdminConnectionResolution.Resolved)await source.ResolveAsync(default);
        parser(subsequent.Connection.Value, out var rotated).Should().BeTrue();
        rotated!.Host.Should().Be("rotated.example");
        await using var connection = await Open();
        string endpoint = await connection.QuerySingleAsync<string>(
            """SELECT "EndpointDescriptor" FROM dmscs."DataStoreManage";"""
        );
        var expected = CreateCommand(provider: targetProvider).Endpoint with { Host = "original.example" };
        endpoint.Should().Be(expected.CanonicalEncoding);
        endpoint.Should().NotContain("snapshot-secret").And.NotContain("admin").And.NotContain("rotated");
        await AssertAcceptanceCounts(1, 1);
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_rolls_back_the_aggregate_when_the_real_job_repository_returns_a_safe_failure()
    {
        using var services = AcceptanceServices();
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER dmscs.reject_acceptance_job ON dmscs.Job AFTER INSERT AS
            BEGIN THROW 50001, 'job-insert-sensitive-sentinel', 1; END;
            """
        );
        try
        {
            var result = (
                await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                    .AcceptCreateAsync(CreateCommand(), default)
            )
                .Should()
                .BeOfType<DataStoreManageCreateResult.EnqueueFailed>()
                .Subject;
            result
                .Failure.Should()
                .BeOfType<JobEnqueueResult.FailureUnknown>()
                .Subject.Diagnostic.ToString()
                .Should()
                .NotContain("job-insert-sensitive-sentinel");
            await AssertAcceptanceCounts(0, 0);
        }
        finally
        {
            await connection.ExecuteAsync("""DROP TRIGGER dmscs.reject_acceptance_job;""");
        }
        (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                .AcceptCreateAsync(CreateCommand(), default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
    }

    [Test, Category("DataStoreManageAcceptance")]
    public async Task It_resolves_outside_the_catalog_transaction_then_observes_an_ordinary_commit()
    {
        using var services = AcceptanceServices();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DmsAdminConnectionResolution> resolved = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var source = A.Fake<IDmsAdminConnectionSource>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._))
            .Invokes(() => entered.TrySetResult())
            .Returns(resolved.Task);
        var acceptance = new ManagedDataStoreCreateAcceptance(
            CreateReadiness(),
            source,
            PostgresqlManagedEndpoint.TryParse,
            ProtectedDatabaseNames.Create("cms", []),
            ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
        );
        Task<DataStoreManageCreateResult> pending = acceptance.AcceptAsync(
            new() { Name = "store", DatabaseTemplate = "Minimal" },
            default
        );
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await Repository().InsertDataStore(Insert("Store")).WaitAsync(TimeSpan.FromSeconds(5)))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>();
        }
        finally
        {
            resolved.TrySetResult(new DmsAdminConnectionResolution.Resolved(new("Host=db.example")));
        }
        (await pending).Should().BeOfType<DataStoreManageCreateResult.NameReserved>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await AssertAcceptanceCounts(0, 0);
    }
}
