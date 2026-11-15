// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private async Task<DeletionSeed> SeedDeleteHandler(long? tenantId = null)
    {
        var seed = await SeedDeletion(tenantId, "mssql");
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStoreManage" SET "Status" = 'PendingDelete', "LastAttempt" = 0 WHERE "Id" = @Id;""",
            new { seed.Command.Id }
        );
        return seed;
    }

    private async Task<JobExecutionContext> ReclaimDeleteHandler(DeletionSeed seed)
    {
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "LeaseExpiresAt" = DATEADD(second, -1, SYSUTCDATETIME()), "NextAttemptAt" = DATEADD(second, -1, SYSUTCDATETIME()) WHERE "Id" = @Id;""",
            new { seed.Claim.Id }
        );
        var claim = (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).ClaimNext(
                "delete-handler-recovery",
                300,
                5,
                default
            )
        )
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        claim.JobId.Should().Be(seed.Claim.JobId);
        return seed.Execution with
        {
            Attempt = claim.AttemptCount,
            Fence = new MssqlJobFenceFactory(PublicationOptions, PublicationTimings).Create(claim, new()),
        };
    }

    [
        TestCase(false, false),
        TestCase(true, false),
        TestCase(false, true),
        Category("DataStoreManageDeleteHandler")
    ]
    public async Task It_recovers_delete_and_preserves_a_replacement_after_tombstone_replay(
        bool rollback,
        bool ordinaryAbsent
    )
    {
        var seed = await SeedDeleteHandler();
        var repository = PublicationRepository();
        await using var connection = await Open();
        int client = await SeedDeletionClient(connection);
        foreach (string table in new[] { "ApiClientDataStore", "DataStoreContext", "DataStoreDerivative" })
        {
            await InsertDeletionDependent(
                connection,
                null,
                table,
                seed.Command.HistoricalDataStoreId,
                client
            );
        }
        if (ordinaryAbsent)
        {
            await connection.ExecuteAsync(
                """
                UPDATE dmscs."DataStoreManage" SET "DataStoreId" = NULL WHERE "Id" = @Id;
                DELETE FROM dmscs."DataStore" WHERE "Id" = @HistoricalDataStoreId;
                """,
                new { seed.Command.Id, seed.Command.HistoricalDataStoreId }
            );
        }
        var provisioner = A.Fake<IDmsDataStoreProvisioner>();
        int physicalCalls = 0;
        A.CallTo(() =>
                provisioner.DeleteAsync(
                    A<DmsDataStoreDeleteRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreDeleteRequest request,
                    ManagedDataStoreEndpoint endpoint,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> admit,
                    CancellationToken token
                ) =>
                {
                    request.TargetName.Should().Be(seed.Command.Identity.TargetName);
                    request.ExpectedSourceIdentity.Should().Be(seed.Command.Identity.ExpectedSourceIdentity);
                    endpoint.Should().Be(seed.Command.Identity.Endpoint);
                    await admit(endpoint, token);
                    physicalCalls++;
                    return physicalCalls > 1 || ordinaryAbsent
                        ? (DmsDataStoreDeleteResult)new DmsDataStoreDeleteResult.AlreadyAbsent()
                        : new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean);
                }
            );
        var handler = new DeleteManagedDataStoreHandler(repository, provisioner);
        var execution = seed.Execution;
        if (rollback)
        {
            int fences = 0;
            var failed = execution with
            {
                Fence = new PublicationFenceDecorator(
                    execution.Fence,
                    (_, _) =>
                    {
                        if (++fences == 3)
                        {
                            throw new IOException("test finalization rollback");
                        }
                        return Task.CompletedTask;
                    }
                ),
            };
            var action = () => handler.ExecuteAsync(failed, new(seed.Command.Id), default);
            await action.Should().ThrowAsync<IOException>();
            (await DeleteRow(connection, seed.Command.Id))["Status"].Should().Be("DeleteInProgress");
            (
                await connection.ExecuteScalarAsync<int>(
                    """SELECT COUNT(*) FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                    new { Id = seed.Command.HistoricalDataStoreId }
                )
            )
                .Should()
                .Be(1);
            await AssertDeletionDependents(connection, seed.Command.HistoricalDataStoreId, 1);
            execution = await ReclaimDeleteHandler(seed);
        }
        await handler.ExecuteAsync(execution, new(seed.Command.Id), default);
        var tombstone = await DeleteRow(connection, seed.Command.Id);
        tombstone["Status"].Should().Be("Deleted");
        tombstone["DataStoreId"].Should().BeNull();
        tombstone["HistoricalDataStoreId"].Should().Be(seed.Command.HistoricalDataStoreId);
        await AssertDeletionDependents(connection, seed.Command.HistoricalDataStoreId, 0);
        int replacement = (await Repository().InsertDataStore(Insert("Store")))
            .Should()
            .BeOfType<DataStoreInsertResult.Success>()
            .Subject.Id;
        await Reserve(
            connection,
            "Store",
            status: "Created",
            dataStoreId: replacement,
            targetName: seed.Command.Identity.TargetName.Value
        );
        execution = await ReclaimDeleteHandler(seed);
        string beforeReplay = await DeleteAcceptanceSnapshot();
        await handler.ExecuteAsync(execution, new(seed.Command.Id), default);
        (await DeleteAcceptanceSnapshot()).Should().Be(beforeReplay);
        physicalCalls.Should().Be(rollback ? 2 : 1);
        var lateDelete = () =>
            repository.AdmitDeleteAsync(
                new(seed.Command, false),
                seed.Command.Identity.Endpoint,
                execution,
                default
            );
        await lateDelete.Should().ThrowAsync<JobPermanentException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(beforeReplay);
        A.CallTo(() => provisioner.DeleteAsync(A<DmsDataStoreDeleteRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test, Category("DataStoreManageDeleteHandler")]
    public async Task It_rolls_back_delete_start_if_the_fence_rejects_commit()
    {
        var seed = await SeedDeleteHandler();
        string before = await DeleteAcceptanceSnapshot();
        var execution = seed.Execution with
        {
            Fence = new PublicationFenceDecorator(
                seed.Execution.Fence,
                (_, _) => throw new JobLeaseLostException()
            ),
        };
        var action = () => PublicationRepository().StartDeleteAsync(seed.Command.Id, execution, default);
        await action.Should().ThrowAsync<JobLeaseLostException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [TestCase("tenant"), TestCase("job"), TestCase("id"), Category("DataStoreManageDeleteHandler")]
    public async Task It_refuses_a_different_delete_partition_or_operation(string mismatch)
    {
        long tenantId = await Tenant("delete-handler");
        var seed = await SeedDeleteHandler(tenantId);
        var repository = PublicationRepository(mismatch == "tenant" ? null : tenantId);
        var execution = mismatch == "job" ? seed.Execution with { JobId = "wrong-delete" } : seed.Execution;
        string before = await DeleteAcceptanceSnapshot();
        var action = () =>
            repository.StartDeleteAsync(
                mismatch == "id" ? seed.Command.Id + 10000 : seed.Command.Id,
                execution,
                default
            );
        await action.Should().ThrowAsync<JobPermanentException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [
        TestCase("identity"),
        TestCase("pin"),
        TestCase("endpoint"),
        TestCase("link"),
        Category("DataStoreManageDeleteHandler")
    ]
    public async Task It_refuses_changed_delete_evidence_before_physical_admission(string change)
    {
        var seed = await SeedDeleteHandler();
        var repository = PublicationRepository();
        var operation = await repository.StartDeleteAsync(seed.Command.Id, seed.Execution, default);
        await using var connection = await Open();
        if (change != "endpoint")
        {
            string sql = change switch
            {
                "identity" =>
                    """UPDATE dmscs."DataStoreManage" SET "ExpectedSourceIdentity" = @Identity WHERE "Id" = @Id;""",
                "pin" =>
                    """UPDATE dmscs."DataStoreManage" SET "PinnedPackageVersion" = 'changed' WHERE "Id" = @Id;""",
                _ => """UPDATE dmscs."DataStoreManage" SET "DataStoreId" = NULL WHERE "Id" = @Id;""",
            };
            await connection.ExecuteAsync(sql, new { seed.Command.Id, Identity = Guid.NewGuid() });
        }
        var actual =
            change == "endpoint"
                ? operation.Command.Identity.Endpoint with
                {
                    Host = "changed.example",
                }
                : operation.Command.Identity.Endpoint;
        string before = await DeleteAcceptanceSnapshot();
        var action = () => repository.AdmitDeleteAsync(operation, actual, seed.Execution, default);
        await action.Should().ThrowAsync<JobPermanentException>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [TestCase(false), TestCase(true), Category("DataStoreManageDeleteHandler")]
    public async Task It_resumes_delete_after_retry_or_shutdown_and_rejects_the_old_lease(bool shutdown)
    {
        var seed = await SeedDeleteHandler();
        var repository = PublicationRepository();
        var operation = await repository.StartDeleteAsync(seed.Command.Id, seed.Execution, default);
        var leases = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        var result = shutdown
            ? await leases.ReleaseToPending(
                seed.Claim.Id,
                seed.Claim.LeaseOwner,
                seed.Claim.FencingToken,
                default
            )
            : await leases.FailManaged(
                seed.Claim.Id,
                seed.Claim.LeaseOwner,
                seed.Claim.FencingToken,
                ManagedDataStoreFailure.FromProvisioner(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.LockTimeout,
                        DmsProvisioningDiagnostic.ForOperation("Delete")
                    )
                ),
                1,
                default
            );
        result.Should().BeOfType<JobWriteResult.Success>();
        await using var connection = await Open();
        (await DeleteRow(connection, seed.Command.Id))["Status"]
            .Should()
            .Be(shutdown ? "DeleteInProgress" : "DeleteFailed");
        var execution = await ReclaimDeleteHandler(seed);
        var resumed = await repository.StartDeleteAsync(seed.Command.Id, execution, default);
        resumed.Should().Be(operation);
        var stale = () =>
            repository.AdmitDeleteAsync(
                operation,
                operation.Command.Identity.Endpoint,
                seed.Execution,
                default
            );
        await stale.Should().ThrowAsync<JobLeaseLostException>();
        await repository.AdmitDeleteAsync(resumed, resumed.Command.Identity.Endpoint, execution, default);
    }

    [
        TestCase(DmsProvisioningFailureCode.TargetUnowned),
        TestCase(DmsProvisioningFailureCode.TargetEndpointUnverifiable),
        Category("DataStoreManageDeleteHandler")
    ]
    public async Task It_keeps_catalog_and_dependents_when_delete_requires_operator_inspection(
        DmsProvisioningFailureCode code
    )
    {
        var seed = await SeedDeleteHandler();
        await using var connection = await Open();
        int client = await SeedDeletionClient(connection);
        foreach (string table in new[] { "ApiClientDataStore", "DataStoreContext", "DataStoreDerivative" })
        {
            await InsertDeletionDependent(
                connection,
                null,
                table,
                seed.Command.HistoricalDataStoreId,
                client
            );
        }
        var provisioner = A.Fake<IDmsDataStoreProvisioner>();
        var failure = DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation("Delete"));
        A.CallTo(() =>
                provisioner.DeleteAsync(
                    A<DmsDataStoreDeleteRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new DmsDataStoreDeleteResult.Failure(failure, DmsCleanupReport.Clean));
        var handler = new DeleteManagedDataStoreHandler(PublicationRepository(), provisioner);
        var action = () => handler.ExecuteAsync(seed.Execution, new(seed.Command.Id), default);
        await action.Should().ThrowAsync<ManagedDataStoreProvisioningException>();
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).FailManaged(
                seed.Claim.Id,
                seed.Claim.LeaseOwner,
                seed.Claim.FencingToken,
                ManagedDataStoreFailure.FromProvisioner(failure),
                null,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.Success>();
        var row = await DeleteRow(connection, seed.Command.Id);
        row["Status"].Should().Be("DeleteError");
        row["DataStoreId"].Should().Be(seed.Command.HistoricalDataStoreId);
        row["LastFailureProvisioningCode"].Should().Be(code.ToString());
        await AssertDeletionDependents(connection, seed.Command.HistoricalDataStoreId, 1);
        var restart = () =>
            PublicationRepository().StartDeleteAsync(seed.Command.Id, seed.Execution, default);
        await restart.Should().ThrowAsync<JobLeaseLostException>();
    }
}
