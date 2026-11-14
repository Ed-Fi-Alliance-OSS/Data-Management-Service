// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private async Task<(int Id, ClaimedJob Claim)> SeedRetry(bool delete, long? tenantId = null)
    {
        if (delete)
        {
            var seed = await SeedDeletion(tenantId);
            return (seed.Command.Id, seed.Claim);
        }
        var create = await SeedPublication(tenantId);
        return (create.Command.Id, create.Claim);
    }

    private static Task<JobWriteResult> Retry(JobLeaseRepository repository, ClaimedJob claim) =>
        repository.FailTransient(claim.Id, claim.LeaseOwner, claim.FencingToken, 45, default);

    private static async Task AssertRetryCommitted(
        DbConnection connection,
        int id,
        ClaimedJob claim,
        bool delete
    )
    {
        var aggregate = await DeleteRow(connection, id);
        aggregate["Status"].Should().Be(delete ? "DeleteFailed" : "CreateFailed");
        aggregate["LastAttempt"].Should().Be(claim.AttemptCount);
        aggregate["LastFailureCategory"].Should().Be("Transient");
        aggregate["LastFailureProvisioningCode"].Should().BeNull();
        aggregate["LastFailureJobCode"].Should().Be(JobErrorCode.HandlerFailed.Code);
        var job = await connection.QuerySingleAsync<RetryJobRow>(
            """SELECT "Status", "LeaseOwner", "LeaseExpiresAt", "FinishedAt", "NextAttemptAt", "LastModifiedAt" FROM dmscs."Job" WHERE "Id" = @Id;""",
            new { claim.Id }
        );
        job.Status.Should().Be("Pending");
        job.LeaseOwner.Should().BeNull();
        job.LeaseExpiresAt.Should().BeNull();
        job.FinishedAt.Should().BeNull();
        job.NextAttemptAt.Should().Be(job.LastModifiedAt.AddSeconds(45));
        aggregate["UpdatedAt"].Should().Be(job.LastModifiedAt);
        aggregate["LastModifiedDate"].Should().Be(job.LastModifiedAt);
        aggregate["LastRefreshed"].Should().Be(job.LastModifiedAt);
    }

    private sealed record RetryJobRow(
        string Status,
        string? LeaseOwner,
        DateTime? LeaseExpiresAt,
        DateTime? FinishedAt,
        DateTime NextAttemptAt,
        DateTime LastModifiedAt
    );

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_commits_retry_and_failed_together_preserving_identity_pin_and_links(
        [Values(false, true)] bool delete,
        [Values(false, true)] bool multitenant,
        [Values("Pending", "InProgress", "Failed")] string startingState
    )
    {
        long? tenantId = multitenant ? await Tenant("retry-tenant") : null;
        var seed = await SeedRetry(delete, tenantId);
        await using var connection = await Open();
        string operation = delete ? "Delete" : "Create";
        string status = startingState == "Pending" ? "Pending" + operation : operation + startingState;
        await connection.ExecuteAsync(
            """
            UPDATE dmscs."DataStoreManage" SET "Status" = @Status, "LastAttempt" = @Attempt,
                "LastFailureCategory" = @Category, "LastFailureJobCode" = @Code WHERE "Id" = @Id;
            UPDATE dmscs."Job" SET "Payload" = '{}', "PayloadVersion" = 99 WHERE "Id" = @JobRowId;
            """,
            new
            {
                seed.Id,
                Status = status,
                Attempt = startingState == "Pending" ? 0 : seed.Claim.AttemptCount,
                Category = startingState == "Failed" ? "Transient" : null,
                Code = startingState == "Failed" ? JobErrorCode.HandlerFailed.Code : null,
                JobRowId = seed.Claim.Id,
            }
        );
        var before = await DeleteRow(connection, seed.Id);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        (await Retry(repository, seed.Claim)).Should().BeOfType<JobWriteResult.Success>();
        await AssertRetryCommitted(connection, seed.Id, seed.Claim, delete);
        var after = await DeleteRow(connection, seed.Id);
        foreach (
            string field in new[]
            {
                "Status",
                "LastAttempt",
                "LastFailureCategory",
                "LastFailureProvisioningCode",
                "LastFailureJobCode",
                "UpdatedAt",
                "LastModifiedDate",
                "LastRefreshed",
            }
        )
        {
            before.Remove(field);
            after.Remove(field);
        }
        after
            .Should()
            .BeEquivalentTo(
                before,
                "retry preserves every identity, pin, link and creation/deletion timestamp"
            );
        (await PublicationRepository(tenantId).GetByIdAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>();

        // A real second claim must advance the retained attempt rather than erase the retry projection.
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "NextAttemptAt" = (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second' WHERE "Id" = @Id;""",
            new { seed.Claim.Id }
        );
        var second = (await repository.ClaimNext("retry-worker", 300, 5, default))
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        second.AttemptCount.Should().Be(seed.Claim.AttemptCount + 1);
        (await Retry(repository, second)).Should().BeOfType<JobWriteResult.Success>();
        await AssertRetryCommitted(connection, seed.Id, second, delete);
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_rolls_back_both_outcomes_when_interrupted_between_writes_or_before_commit(
        [Values(false, true)] bool delete,
        [Values("GuardedWrite", "WriteRetryAggregate")] string interruptedAfter
    )
    {
        var seed = await SeedRetry(delete);
        string before = await DeleteAcceptanceSnapshot();
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                AfterOperation = (operation, _) =>
                    operation == interruptedAfter
                        ? Task.FromException(new InvalidOperationException("Injected retry interruption."))
                        : Task.CompletedTask,
            },
        };
        (await Retry(repository, seed.Claim)).Should().BeOfType<JobWriteResult.ResultUnknown>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_rolls_back_pending_when_the_database_rejects_the_failed_projection(
        [Values(false, true)] bool delete
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """ALTER TABLE dmscs."DataStoreManage" ADD CONSTRAINT "CK_RetryProjectionTest" CHECK ("Status" NOT IN ('CreateFailed', 'DeleteFailed'));"""
        );
        try
        {
            string before = await DeleteAcceptanceSnapshot();
            (await Retry(new(PublicationOptions, PublicationTimings), seed.Claim))
                .Should()
                .BeOfType<JobWriteResult.ResultUnknown>();
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
        finally
        {
            await connection.ExecuteAsync(
                """ALTER TABLE dmscs."DataStoreManage" DROP CONSTRAINT "CK_RetryProjectionTest";"""
            );
        }
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_keeps_both_committed_outcomes_when_the_commit_acknowledgement_is_lost(
        [Values(false, true)] bool delete
    )
    {
        var seed = await SeedRetry(delete);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                AfterOperation = (operation, _) =>
                    operation == "Commit"
                        ? Task.FromException(
                            new InvalidOperationException("Injected lost commit acknowledgement.")
                        )
                        : Task.CompletedTask,
            },
        };
        (await Retry(repository, seed.Claim)).Should().BeOfType<JobWriteResult.ResultUnknown>();
        await using var connection = await Open();
        await AssertRetryCommitted(connection, seed.Id, seed.Claim, delete);
        string committed = await DeleteAcceptanceSnapshot();
        (await Retry(repository, seed.Claim)).Should().BeOfType<JobWriteResult.OwnershipLost>();
        (await DeleteAcceptanceSnapshot()).Should().Be(committed);
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_does_not_project_without_live_current_ownership(
        [Values(false, true)] bool delete,
        [Values("owner", "token", "expired")] string stale
    )
    {
        var seed = await SeedRetry(delete);
        if (stale == "expired")
        {
            await using var connection = await Open();
            await connection.ExecuteAsync(
                """UPDATE dmscs."Job" SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second' WHERE "Id" = @Id;""",
                new { seed.Claim.Id }
            );
        }
        string before = await DeleteAcceptanceSnapshot();
        List<string> operations = [];
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                BeforeOperation = (operation, _) =>
                {
                    operations.Add(operation);
                    return Task.CompletedTask;
                },
            },
        };
        (
            await repository.FailTransient(
                seed.Claim.Id,
                stale == "owner" ? "stale-worker" : seed.Claim.LeaseOwner,
                seed.Claim.FencingToken + (stale == "token" ? 1 : 0),
                45,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.OwnershipLost>();
        operations.Should().NotContain("RetryTenantGate");
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_preserves_recoverable_aggregate_state_when_shutdown_releases_the_job(
        [Values(false, true)] bool delete
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        var before = await DeleteRow(connection, seed.Id);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        (
            await repository.ReleaseToPending(
                seed.Claim.Id,
                seed.Claim.LeaseOwner,
                seed.Claim.FencingToken,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.Success>();
        (await DeleteRow(connection, seed.Id)).Should().BeEquivalentTo(before);
        (
            await connection.ExecuteScalarAsync<string>(
                """SELECT "Status" FROM dmscs."Job" WHERE "Id" = @Id;""",
                new { seed.Claim.Id }
            )
        )
            .Should()
            .Be("Pending");
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_requires_exact_persisted_partition_current_job_and_operation(
        [Values(false, true)] bool delete,
        [Values("tenant", "job", "job-case", "job-space", "type", "type-case", "type-space", "ordinary")]
            string mismatch
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        string jobColumn = delete ? "DeleteJobId" : "CreateJobId";
        if (mismatch == "tenant")
        {
            long tenantId = await Tenant("other-retry-tenant");
            await connection.ExecuteAsync(
                """UPDATE dmscs."Job" SET "TenantId" = @TenantId WHERE "Id" = @Id;""",
                new { seed.Claim.Id, TenantId = tenantId }
            );
        }
        else if (mismatch.StartsWith("job", StringComparison.Ordinal))
        {
            // Use a lettered sentinel so the case mismatch is deterministic even for an all-digit generated ID.
            await connection.ExecuteAsync(
                """UPDATE dmscs."Job" SET "JobId" = 'RetryOperation' WHERE "Id" = @Id;""",
                new { seed.Claim.Id }
            );
            string jobId = mismatch switch
            {
                "job-case" => "retryoperation",
                "job-space" => "RetryOperation ",
                _ => "new-operation",
            };
            await connection.ExecuteAsync(
                $"UPDATE dmscs.\"DataStoreManage\" SET \"{jobColumn}\" = @JobId WHERE \"Id\" = @Id;",
                new { seed.Id, JobId = jobId }
            );
        }
        else
        {
            string type = mismatch switch
            {
                "type" => delete ? ManagedJobTypes.Create : ManagedJobTypes.Delete,
                "type-case" => seed.Claim.JobType.ToLowerInvariant(),
                "type-space" => seed.Claim.JobType + " ",
                _ => "ordinary-job",
            };
            await connection.ExecuteAsync(
                """UPDATE dmscs."Job" SET "JobType" = @Type WHERE "Id" = @Id;""",
                new { seed.Claim.Id, Type = type }
            );
        }
        var before = await DeleteRow(connection, seed.Id);
        List<string> operations = [];
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                BeforeOperation = (operation, _) =>
                {
                    operations.Add(operation);
                    return Task.CompletedTask;
                },
            },
        };
        (await Retry(repository, seed.Claim)).Should().BeOfType<JobWriteResult.Success>();
        (await DeleteRow(connection, seed.Id)).Should().BeEquivalentTo(before);
        if (mismatch is "type-case" or "type-space" or "ordinary")
        {
            operations.Should().NotContain("RetryTenantGate");
            operations.Should().NotContain("LockRetryAggregate");
        }
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_preserves_created_deleted_and_newer_delete_operations(
        [Values("Created", "Deleted", "newer-delete")] string protectedState
    )
    {
        ClaimedJob claim;
        int id;
        if (protectedState == "Deleted")
        {
            var deletion = await SeedDeletion();
            await PublicationRepository().CompleteDeleteAsync(deletion.Command, deletion.Execution, default);
            claim = deletion.Claim;
            id = deletion.Command.Id;
        }
        else
        {
            var creation = await SeedPublication();
            await PublicationRepository().PublishCreateAsync(creation.Command, creation.Execution, default);
            claim = creation.Claim;
            id = creation.Command.Id;
            if (protectedState == "newer-delete")
            {
                using var services = AcceptanceServices(null);
                (
                    await ManagedRepository(services.GetRequiredService<IJobEnqueuer>())
                        .AcceptDeleteAsync(id, default)
                )
                    .Should()
                    .BeOfType<DataStoreManageDeleteResult.Success>();
            }
        }
        await using var connection = await Open();
        var before = await DeleteRow(connection, id);
        (await Retry(new(PublicationOptions, PublicationTimings), claim))
            .Should()
            .BeOfType<JobWriteResult.Success>();
        (await DeleteRow(connection, id)).Should().BeEquivalentTo(before);
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_refuses_to_regress_a_matching_aggregate_attempt([Values(false, true)] bool delete)
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStoreManage" SET "LastAttempt" = @Attempt WHERE "Id" = @Id;""",
            new { seed.Id, Attempt = seed.Claim.AttemptCount + 1 }
        );
        string before = await DeleteAcceptanceSnapshot();
        (await Retry(new(PublicationOptions, PublicationTimings), seed.Claim))
            .Should()
            .BeOfType<JobWriteResult.FailureUnknown>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_projects_an_unstarted_create_without_a_payload_or_pin()
    {
        using var services = AcceptanceServices(null);
        var ready = new DmsDataStoreProvisionerReadiness();
        ready.Record(new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow));
        var accepted = (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), readiness: ready)
                .AcceptCreateAsync(CreateCommand("Unstarted", "postgresql"), default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>()
            .Subject;
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        var claim = (await repository.ClaimNext("pre-handler-worker", 300, 5, default))
            .Should()
            .BeOfType<JobClaimResult.Claimed>()
            .Subject.Job;
        await using var connection = await Open();
        await connection.ExecuteAsync(
            """UPDATE dmscs."Job" SET "Payload" = '{}', "PayloadVersion" = 99 WHERE "Id" = @Id;""",
            new { claim.Id }
        );
        (await Retry(repository, claim)).Should().BeOfType<JobWriteResult.Success>();
        await AssertRetryCommitted(connection, accepted.Id, claim, false);
        (await DeleteRow(connection, accepted.Id))["PinnedContractVersion"].Should().BeNull();
        (await PublicationRepository().GetByIdAsync(accepted.Id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>();
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_rechecks_database_time_after_waiting_for_the_gate_or_aggregate(
        [Values("gate", "aggregate")] string heldLock
    )
    {
        var seed = await SeedRetry(false);
        await using var observer = await Open();
        var before = await DeleteRow(observer, seed.Id);
        await using var blocker = await Open();
        await using var transaction = await blocker.BeginTransactionAsync();
        if (heldLock == "gate")
        {
            await DataStoreCatalog.AcquireTenantGateAsync(transaction, null, default);
        }
        else
        {
            await blocker.ExecuteScalarAsync<int>(
                """SELECT "Id" FROM dmscs."DataStoreManage" WHERE "Id" = @Id FOR UPDATE;""",
                new { seed.Id },
                transaction
            );
        }
        DateTime expires = await observer.ExecuteScalarAsync<DateTime>(
            """UPDATE dmscs."Job" SET "LeaseExpiresAt" = (clock_timestamp() AT TIME ZONE 'UTC') + interval '2 seconds' WHERE "Id" = @Id RETURNING "LeaseExpiresAt";""",
            new { seed.Claim.Id }
        );
        var enteringWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                BeforeOperation = (operation, _) =>
                {
                    if (operation == (heldLock == "gate" ? "RetryTenantGate" : "LockRetryAggregate"))
                    {
                        enteringWait.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
            },
        };
        Task<JobWriteResult> retry = Retry(repository, seed.Claim);
        try
        {
            await enteringWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Observe the database clock, not a guessed delay, while the requested lock remains held.
            while (
                await observer.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp() AT TIME ZONE 'UTC';")
                < expires
            )
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            await transaction.RollbackAsync();
        }
        (await retry).Should().BeOfType<JobWriteResult.OwnershipLost>();
        (await DeleteRow(observer, seed.Id)).Should().BeEquivalentTo(before);
        (
            await observer.ExecuteScalarAsync<string>(
                """SELECT "Status" FROM dmscs."Job" WHERE "Id" = @Id;""",
                new { seed.Claim.Id }
            )
        )
            .Should()
            .Be("InProgress");
    }

    [Test, Category("DataStoreManageRetryOutcome")]
    public async Task It_does_not_retain_aggregate_locks_for_an_unrelated_tenant()
    {
        long otherTenant = await Tenant("other-held-retry-tenant");
        var other = await SeedPublication(otherTenant, "Other");
        long tenant = await Tenant("held-retry-tenant");
        var seed = await SeedPublication(tenant, "Current");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            BeforeOwnershipCommit = async _ =>
            {
                held.TrySetResult();
                await release.Task;
            },
        };
        Task<JobWriteResult> retry = Retry(repository, seed.Claim);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var connection = await Open();
            (
                await connection.ExecuteAsync(
                    """UPDATE dmscs."DataStoreManage" SET "Name" = 'OtherRetryProbe' WHERE "Id" = @Id;""",
                    new { other.Command.Id },
                    commandTimeout: 2
                )
            )
                .Should()
                .Be(1);
        }
        finally
        {
            release.TrySetResult();
        }
        (await retry).Should().BeOfType<JobWriteResult.Success>();
    }
}
