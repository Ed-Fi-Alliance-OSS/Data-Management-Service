// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private static Task<JobWriteResult> Terminal(JobLeaseRepository repository, ClaimedJob claim) =>
        repository.FailTerminal(
            claim.Id,
            claim.LeaseOwner,
            claim.FencingToken,
            JobErrorCode.InvalidPayload,
            default
        );

    private static async Task AssertTerminalCommitted(
        DbConnection connection,
        int id,
        ClaimedJob claim,
        bool delete
    )
    {
        var aggregate = await DeleteRow(connection, id);
        aggregate["Status"].Should().Be(delete ? "DeleteError" : "CreateError");
        aggregate["LastAttempt"].Should().Be(claim.AttemptCount);
        aggregate["LastFailureCategory"].Should().Be("Permanent");
        aggregate["LastFailureProvisioningCode"].Should().BeNull();
        aggregate["LastFailureJobCode"].Should().Be(JobErrorCode.InvalidPayload.Code);
        var job = await connection.QuerySingleAsync<RetryJobRow>(
            "SELECT Status, LeaseOwner, LeaseExpiresAt, FinishedAt, NextAttemptAt, LastModifiedAt FROM dmscs.Job WHERE Id = @Id;",
            new { claim.Id }
        );
        job.Status.Should().Be("Error");
        job.LeaseOwner.Should().BeNull();
        job.LeaseExpiresAt.Should().BeNull();
        job.FinishedAt.Should().Be(job.LastModifiedAt);
        aggregate["UpdatedAt"].Should().Be(job.LastModifiedAt);
        aggregate["LastModifiedDate"].Should().Be(job.LastModifiedAt);
        aggregate["LastRefreshed"].Should().Be(job.LastModifiedAt);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_rolls_back_both_outcomes_when_interrupted_between_writes_or_before_commit(
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
        (await Terminal(repository, seed.Claim)).Should().BeOfType<JobWriteResult.ResultUnknown>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_rolls_back_pending_when_the_database_rejects_the_failed_projection(
        [Values(false, true)] bool delete,
        [Values(false, true)] bool exhausted
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        if (exhausted)
        {
            await ExpireTerminalClaim(connection, seed.Claim);
        }
        await connection.ExecuteAsync(
            """ALTER TABLE dmscs."DataStoreManage" ADD CONSTRAINT "CK_RetryProjectionTest" CHECK ("Status" NOT IN ('CreateError', 'DeleteError'));"""
        );
        try
        {
            string before = await DeleteAcceptanceSnapshot();
            var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings);
            if (exhausted)
            {
                (await repository.Exhaust(1, JobErrorCode.AttemptsExhausted, default))
                    .Should()
                    .BeOfType<JobExhaustResult.FailureUnknown>();
            }
            else
            {
                (await Terminal(repository, seed.Claim)).Should().BeOfType<JobWriteResult.ResultUnknown>();
            }
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
        }
        finally
        {
            await connection.ExecuteAsync(
                """ALTER TABLE dmscs."DataStoreManage" DROP CONSTRAINT "CK_RetryProjectionTest";"""
            );
        }
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_keeps_both_committed_outcomes_when_the_commit_acknowledgement_is_lost(
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
        (await Terminal(repository, seed.Claim)).Should().BeOfType<JobWriteResult.ResultUnknown>();
        await using var connection = await Open();
        await AssertTerminalCommitted(connection, seed.Id, seed.Claim, delete);
        string committed = await DeleteAcceptanceSnapshot();
        (await Terminal(repository, seed.Claim)).Should().BeOfType<JobWriteResult.OwnershipLost>();
        (await DeleteAcceptanceSnapshot()).Should().Be(committed);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_does_not_project_without_live_current_ownership(
        [Values(false, true)] bool delete,
        [Values("owner", "token", "expired")] string stale
    )
    {
        var seed = await SeedRetry(delete);
        if (stale == "expired")
        {
            await using var connection = await Open();
            await connection.ExecuteAsync(
                """UPDATE dmscs."Job" SET "LeaseExpiresAt" = DATEADD(second, -1, SYSUTCDATETIME()) WHERE "Id" = @Id;""",
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
            await repository.FailTerminal(
                seed.Claim.Id,
                stale == "owner" ? "stale-worker" : seed.Claim.LeaseOwner,
                seed.Claim.FencingToken + (stale == "token" ? 1 : 0),
                JobErrorCode.InvalidPayload,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.OwnershipLost>();
        operations.Should().NotContain("RetryTenantGate");
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_requires_exact_persisted_partition_current_job_and_operation(
        [Values(false, true)] bool delete,
        [Values(false, true)] bool exhausted,
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
        if (exhausted)
        {
            await ExpireTerminalClaim(connection, seed.Claim);
            (await repository.Exhaust(1, JobErrorCode.AttemptsExhausted, default))
                .Should()
                .BeOfType<JobExhaustResult.Success>()
                .Which.ExhaustedCount.Should()
                .Be(1);
        }
        else
        {
            (await Terminal(repository, seed.Claim)).Should().BeOfType<JobWriteResult.Success>();
        }
        (await DeleteRow(connection, seed.Id)).Should().BeEquivalentTo(before);
        if (mismatch is "type-case" or "type-space" or "ordinary")
        {
            operations.Should().NotContain("RetryTenantGate");
            operations.Should().NotContain("LockRetryAggregate");
        }
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_preserves_created_deleted_and_newer_delete_operations(
        [Values("terminal", "managed-terminal", "exhausted")] string outcome,
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
                // The provisioner is ready for the seeded record's own target provider.
                (
                    await ManagedRepository(
                            services.GetRequiredService<IJobEnqueuer>(),
                            readiness: ReadyFor("mssql")
                        )
                        .AcceptDeleteAsync(id, default)
                )
                    .Should()
                    .BeOfType<DataStoreManageDeleteResult.Success>();
            }
        }
        await using var connection = await Open();
        var before = await DeleteRow(connection, id);
        await TerminalOrExhaust(claim, outcome);
        (await DeleteRow(connection, id)).Should().BeEquivalentTo(before);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_rechecks_database_time_after_waiting_for_the_gate_or_aggregate(
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
                """SELECT Id FROM dmscs.DataStoreManage WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id;""",
                new { seed.Id },
                transaction
            );
        }
        DateTime expires = await observer.ExecuteScalarAsync<DateTime>(
            """UPDATE dmscs.Job SET LeaseExpiresAt = DATEADD(second, 2, SYSUTCDATETIME()) OUTPUT inserted.LeaseExpiresAt WHERE Id = @Id;""",
            new { seed.Claim.Id }
        );
        var enteringWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                BeforeOperation = (operation, _) =>
                {
                    if (operation == (heldLock == "gate" ? "RetryTenantGate" : "FindRetryAggregate"))
                    {
                        enteringWait.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
            },
        };
        Task<JobWriteResult> retry = Terminal(repository, seed.Claim);
        try
        {
            await enteringWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Observe the database clock, not a guessed delay, while the requested lock remains held.
            while (await observer.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();") < expires)
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

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_projects_an_unstarted_create_without_a_payload_or_pin()
    {
        using var services = AcceptanceServices(null);
        var ready = new DmsDataStoreProvisionerReadiness();
        ready.Record(new DmsProvisionerReadiness.Ready("mssql", DateTimeOffset.UtcNow));
        var accepted = (
            await ManagedRepository(services.GetRequiredService<IJobEnqueuer>(), readiness: ready)
                .AcceptCreateAsync(CreateCommand("Unstarted", "mssql"), default)
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
        (await Terminal(repository, claim)).Should().BeOfType<JobWriteResult.Success>();
        await AssertTerminalCommitted(connection, accepted.Id, claim, false);
        (await DeleteRow(connection, accepted.Id))["PinnedContractVersion"].Should().BeNull();
        (await PublicationRepository().GetByIdAsync(accepted.Id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>();
    }

    private async Task TerminalOrExhaust(ClaimedJob claim, string outcome)
    {
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        await using var connection = await Open();
        string otherJobs = await OtherJobs(connection, claim.Id);
        if (outcome == "exhausted")
        {
            await connection.ExecuteAsync(
                """UPDATE dmscs.Job SET LeaseExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE Id = @Id;""",
                new { claim.Id }
            );
            // The operation's CMS success already committed, so its last attempt completes instead of failing.
            (await repository.Exhaust(claim.AttemptCount, JobErrorCode.AttemptsExhausted, default))
                .Should()
                .Be(new JobExhaustResult.Success(0, 1));
        }
        else
        {
            // A terminal write by the job's current owner, such as a final attempt whose tenant lookup failed
            // before the handler ran, completes the committed operation instead of recording Error.
            var result =
                outcome == "terminal"
                    ? await Terminal(repository, claim)
                    : await repository.FailManaged(
                        claim.Id,
                        claim.LeaseOwner,
                        claim.FencingToken,
                        ManagedDataStoreFailure.FromJob(JobErrorCode.AttemptsExhausted),
                        null,
                        default
                    );
            result.Should().BeOfType<JobWriteResult.Success>().Which.SettledAsCompleted.Should().BeTrue();
        }
        await AssertSettled(connection, claim, fenced: outcome == "exhausted");
        (await OtherJobs(connection, claim.Id)).Should().Be(otherJobs);
    }

    private async Task AssertSettled(DbConnection connection, ClaimedJob claim, bool fenced = true)
    {
        var job = await connection.QuerySingleAsync<(
            string Status,
            string? ErrorMessage,
            int AttemptCount,
            long FencingToken,
            string? LeaseOwner,
            DateTime? FinishedAt
        )>(
            """SELECT Status, ErrorMessage, AttemptCount, FencingToken, LeaseOwner, FinishedAt FROM dmscs.Job WHERE Id = @Id;""",
            new { claim.Id }
        );
        job.Status.Should().Be("Completed");
        job.ErrorMessage.Should().BeNull();
        job.AttemptCount.Should().Be(claim.AttemptCount);
        // The exhaustion sweep advances the fence; the owner's own write keeps it.
        job.FencingToken.Should().Be(claim.FencingToken + (fenced ? 1 : 0));
        job.LeaseOwner.Should().BeNull();
        job.FinishedAt.Should().NotBeNull();
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).Complete(
                claim.Id,
                claim.LeaseOwner,
                claim.FencingToken,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.OwnershipLost>();
    }

    private static async Task<string> OtherJobs(DbConnection connection, long id) =>
        JsonSerializer.Serialize(
            (
                await connection.QueryAsync(
                    """SELECT * FROM dmscs.Job WHERE Id <> @Id ORDER BY Id;""",
                    new { Id = id }
                )
            )
                .Select(row => ((IDictionary<string, object>)row).ToDictionary())
                .ToArray()
        );

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_terminal_or_exhaustion_completes_only_the_exact_committed_operation(
        [Values(false, true)] bool exhausted,
        [Values("tenant", "job-case", "type", "unlinked")] string mismatch
    )
    {
        var creation = await SeedPublication();
        await PublicationRepository().PublishCreateAsync(creation.Command, creation.Execution, default);
        await using var connection = await Open();
        if (exhausted)
        {
            await ExpireTerminalClaim(connection, creation.Claim);
        }
        switch (mismatch)
        {
            case "tenant":
                await connection.ExecuteAsync(
                    """UPDATE dmscs.Job SET TenantId = @TenantId WHERE Id = @Id;""",
                    new { creation.Claim.Id, TenantId = await Tenant("other-settle-tenant") }
                );
                break;
            case "job-case":
                await connection.ExecuteAsync(
                    """UPDATE dmscs.Job SET JobId = N'SettleOperation' WHERE Id = @Id;""",
                    new { creation.Claim.Id }
                );
                await connection.ExecuteAsync(
                    """UPDATE dmscs.DataStoreManage SET CreateJobId = N'settleoperation' WHERE Id = @Id;""",
                    new { creation.Command.Id }
                );
                break;
            case "type":
                await connection.ExecuteAsync(
                    """UPDATE dmscs.Job SET JobType = @Type WHERE Id = @Id;""",
                    new { creation.Claim.Id, Type = ManagedJobTypes.Delete }
                );
                break;
            default:
                // Created without its publication link is not proof of a committed publication.
                await connection.ExecuteAsync(
                    """UPDATE dmscs.DataStoreManage SET HistoricalDataStoreId = NULL WHERE Id = @Id;""",
                    new { creation.Command.Id }
                );
                break;
        }
        var before = await DeleteRow(connection, creation.Command.Id);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings);
        if (exhausted)
        {
            (await repository.Exhaust(1, JobErrorCode.AttemptsExhausted, default))
                .Should()
                .Be(new JobExhaustResult.Success(1, 0));
        }
        else
        {
            // Without exact proof of the committed operation, a terminal write still fails the job.
            (await Terminal(repository, creation.Claim))
                .Should()
                .BeOfType<JobWriteResult.Success>()
                .Which.SettledAsCompleted.Should()
                .BeFalse();
        }
        (
            await connection.QuerySingleAsync<(string Status, string? ErrorMessage)>(
                """SELECT Status, ErrorMessage FROM dmscs.Job WHERE Id = @Id;""",
                new { creation.Claim.Id }
            )
        )
            .Should()
            .Be(
                ("Error", (exhausted ? JobErrorCode.AttemptsExhausted : JobErrorCode.InvalidPayload).Message)
            );
        (await DeleteRow(connection, creation.Command.Id)).Should().BeEquivalentTo(before);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_exhaustion_completes_committed_success_beside_failed_ordinary_unfinished_and_live_jobs(
        [Values(null, "SettleCommittedJobs", "WriteRetryAggregate")] string? interruptedAfter
    )
    {
        var committed = await SeedPublication(name: "SettledMixed");
        await PublicationRepository().PublishCreateAsync(committed.Command, committed.Execution, default);
        var unfinished = await SeedRetry(false);
        var live = await SeedPublication(name: "LiveMixed");
        await PublicationRepository().PublishCreateAsync(live.Command, live.Execution, default);
        await using var connection = await Open();
        await ExpireTerminalClaim(connection, committed.Claim);
        await ExpireTerminalClaim(connection, unfinished.Claim);
        string ordinary = Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(
            """
            INSERT INTO dmscs.Job (JobId, JobType, PayloadVersion, Payload, Status, CreatedAt, NextAttemptAt, AttemptCount)
            VALUES (@Id, N'Unrelated.Unknown', 1, N'{}', N'Pending', SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
            """,
            new { Id = ordinary }
        );
        string before = await DeleteAcceptanceSnapshot();
        var liveJob = await connection.QuerySingleAsync<(string Status, long FencingToken)>(
            """SELECT Status, FencingToken FROM dmscs.Job WHERE Id = @Id;""",
            new { live.Claim.Id }
        );
        var committedRow = await DeleteRow(connection, committed.Command.Id);
        var liveRow = await DeleteRow(connection, live.Command.Id);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                AfterOperation = (operation, _) =>
                    operation == interruptedAfter
                        ? Task.FromException(new InvalidOperationException("Injected exhaust interruption."))
                        : Task.CompletedTask,
            },
        };

        var result = await repository.Exhaust(1, JobErrorCode.AttemptsExhausted, default);

        if (interruptedAfter is not null)
        {
            // Nothing in the batch commits, the completion included.
            result.Should().BeOfType<JobExhaustResult.FailureUnknown>();
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
            return;
        }
        result.Should().Be(new JobExhaustResult.Success(2, 1));
        await AssertSettled(connection, committed.Claim);
        (await DeleteRow(connection, committed.Command.Id)).Should().BeEquivalentTo(committedRow);
        (await DeleteRow(connection, unfinished.Id))["Status"].Should().Be("CreateError");
        (
            await connection.QueryAsync<string>(
                """SELECT Status FROM dmscs.Job WHERE Id = @Id OR JobId = @Ordinary;""",
                new { unfinished.Claim.Id, Ordinary = ordinary }
            )
        )
            .Should()
            .Equal("Error", "Error");
        (
            await connection.QuerySingleAsync<(string Status, long FencingToken)>(
                """SELECT Status, FencingToken FROM dmscs.Job WHERE Id = @Id;""",
                new { live.Claim.Id }
            )
        )
            .Should()
            .Be(liveJob);
        (await DeleteRow(connection, live.Command.Id)).Should().BeEquivalentTo(liveRow);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_retains_bounded_terminal_evidence_and_every_identity_field(
        [Values(false, true)] bool delete,
        [Values("Pending", "InProgress", "Failed")] string startingState,
        [Values(
            "InvalidPayload",
            "UnsupportedPayloadVersion",
            "TenantUnavailable",
            "AttemptsExhausted",
            "ConsumerRejected"
        )]
            string code
    )
    {
        long tenantId = await Tenant("terminal-tenant");
        var seed = await SeedRetry(delete, tenantId);
        await using var connection = await Open();
        string operation = delete ? "Delete" : "Create";
        await connection.ExecuteAsync(
            """
            UPDATE dmscs.DataStoreManage SET Status = @Status WHERE Id = @Id;
            UPDATE dmscs.Job SET Payload = '{}', PayloadVersion = 99 WHERE Id = @JobRowId;
            """,
            new
            {
                seed.Id,
                Status = startingState == "Pending" ? "Pending" + operation : operation + startingState,
                JobRowId = seed.Claim.Id,
            }
        );
        var before = await DeleteRow(connection, seed.Id);
        var error =
            JobErrorCode.Infrastructure.FirstOrDefault(x => x.Code == code)
            ?? new JobErrorCode(code, "The consumer rejected the job.");
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).FailTerminal(
                seed.Claim.Id,
                seed.Claim.LeaseOwner,
                seed.Claim.FencingToken,
                error,
                default
            )
        )
            .Should()
            .BeOfType<JobWriteResult.Success>();
        var after = await DeleteRow(connection, seed.Id);
        after["Status"].Should().Be(operation + "Error");
        after["LastAttempt"].Should().Be(seed.Claim.AttemptCount);
        after["LastFailureCategory"]
            .Should()
            .Be(code == "AttemptsExhausted" ? "OperatorInspectionRequired" : "Permanent");
        after["LastFailureJobCode"].Should().Be(code == "ConsumerRejected" ? "HandlerFailed" : code);
        after["LastFailureProvisioningCode"].Should().BeNull();
        (
            await connection.ExecuteScalarAsync<string>(
                """SELECT ErrorMessage FROM dmscs.Job WHERE Id = @Id;""",
                new { seed.Claim.Id }
            )
        )
            .Should()
            .Be(error.Message);
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
        after.Should().BeEquivalentTo(before);
        (await PublicationRepository(tenantId).GetByIdAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>();
    }

    private static Task ExpireTerminalClaim(DbConnection connection, ClaimedJob claim) =>
        connection.ExecuteAsync(
            """UPDATE dmscs.Job SET LeaseExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE Id = @Id;""",
            new { claim.Id }
        );

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_exhausts_the_last_attempt_before_dispatch_and_retains_inspection_evidence(
        [Values(false, true)] bool delete,
        [Values("Pending", "InProgress", "Failed")] string startingState
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        string operation = delete ? "Delete" : "Create";
        await connection.ExecuteAsync(
            """
            UPDATE dmscs.DataStoreManage SET Status = @Status, LastAttempt = 0 WHERE Id = @Id;
            UPDATE dmscs.Job SET Payload = '{}', PayloadVersion = 99 WHERE Id = @JobRowId;
            """,
            new
            {
                seed.Id,
                Status = startingState == "Pending" ? "Pending" + operation : operation + startingState,
                JobRowId = seed.Claim.Id,
            }
        );
        await ExpireTerminalClaim(connection, seed.Claim);
        var before = await DeleteRow(connection, seed.Id);
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).Exhaust(
                1,
                JobErrorCode.AttemptsExhausted,
                default
            )
        )
            .Should()
            .BeOfType<JobExhaustResult.Success>()
            .Which.ExhaustedCount.Should()
            .Be(1);
        var after = await DeleteRow(connection, seed.Id);
        after["Status"].Should().Be(operation + "Error");
        after["LastAttempt"].Should().Be(1);
        after["LastFailureCategory"].Should().Be("OperatorInspectionRequired");
        after["LastFailureJobCode"].Should().Be("AttemptsExhausted");
        DateTime finished = await connection.ExecuteScalarAsync<DateTime>(
            """SELECT FinishedAt FROM dmscs.Job WHERE Id = @Id;""",
            new { seed.Claim.Id }
        );
        after["UpdatedAt"].Should().Be(finished);
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
        after.Should().BeEquivalentTo(before);
        // Real retention removes the only job evidence; the aggregate must remain independently readable.
        (await new JobRetentionRepository(PublicationOptions).DeleteFinishedOlderThan(0, 1000, default))
            .Should()
            .BeOfType<JobRetentionResult.Success>();
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM dmscs.Job WHERE Id = @Id;""",
                new { seed.Claim.Id }
            )
        )
            .Should()
            .Be(0);
        (await PublicationRepository().GetByIdAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>();
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_rolls_back_exhaustion_if_projection_fails_and_keeps_committed_unknown_outcomes(
        [Values(false, true)] bool delete,
        [Values("ExhaustBatch", "WriteRetryAggregate", "Commit")] string interruptedAfter
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        await ExpireTerminalClaim(connection, seed.Claim);
        string before = await DeleteAcceptanceSnapshot();
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                AfterOperation = (operation, _) =>
                    operation == interruptedAfter
                        ? Task.FromException(new InvalidOperationException("Injected exhaust interruption."))
                        : Task.CompletedTask,
            },
        };
        (await repository.Exhaust(1, JobErrorCode.AttemptsExhausted, default))
            .Should()
            .BeOfType<JobExhaustResult.FailureUnknown>();
        if (interruptedAfter != "Commit")
        {
            (await DeleteAcceptanceSnapshot()).Should().Be(before);
            return;
        }
        var aggregate = await DeleteRow(connection, seed.Id);
        aggregate["Status"].Should().Be(delete ? "DeleteError" : "CreateError");
        aggregate["LastFailureCategory"].Should().Be("OperatorInspectionRequired");
        (
            await connection.ExecuteScalarAsync<string>(
                """SELECT Status FROM dmscs.Job WHERE Id = @Id;""",
                new { seed.Claim.Id }
            )
        )
            .Should()
            .Be("Error");
        string committed = await DeleteAcceptanceSnapshot();
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).Exhaust(
                1,
                JobErrorCode.AttemptsExhausted,
                default
            )
        )
            .Should()
            .BeOfType<JobExhaustResult.Success>()
            .Which.ExhaustedCount.Should()
            .Be(0);
        (await DeleteAcceptanceSnapshot()).Should().Be(committed);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_prevents_retention_from_erasing_an_uncommitted_terminal_projection(
        [Values(false, true)] bool exhausted
    )
    {
        var seed = await SeedRetry(false);
        await using var connection = await Open();
        if (exhausted)
        {
            await ExpireTerminalClaim(connection, seed.Claim);
        }
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                AfterOperation = async (operation, token) =>
                {
                    if (operation == (exhausted ? "ExhaustBatch" : "GuardedWrite"))
                    {
                        held.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                },
            },
        };
        Task<JobExhaustResult>? exhaust = exhausted
            ? repository.Exhaust(1, JobErrorCode.AttemptsExhausted, default)
            : null;
        Task<JobWriteResult>? terminal = exhausted ? null : Terminal(repository, seed.Claim);
        var retention = new JobRetentionRepository(PublicationOptions);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await retention.DeleteFinishedOlderThan(0, 1000, default))
                .Should()
                .BeOfType<JobRetentionResult.Success>()
                .Which.DeletedCount.Should()
                .Be(0);
        }
        finally
        {
            release.TrySetResult();
        }
        if (exhaust is not null)
        {
            (await exhaust).Should().BeOfType<JobExhaustResult.Success>();
        }
        if (terminal is not null)
        {
            (await terminal).Should().BeOfType<JobWriteResult.Success>();
        }
        (await retention.DeleteFinishedOlderThan(0, 1000, default))
            .Should()
            .BeOfType<JobRetentionResult.Success>()
            .Which.DeletedCount.Should()
            .Be(1);
        var aggregate = await DeleteRow(connection, seed.Id);
        aggregate["Status"].Should().Be("CreateError");
        aggregate["LastFailureJobCode"].Should().Be(exhausted ? "AttemptsExhausted" : "InvalidPayload");
        (await PublicationRepository().GetByIdAsync(seed.Id, default))
            .Should()
            .BeOfType<DataStoreManageGetResult.Success>();
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_commits_full_managed_exhaust_batches_with_latency_and_honors_cancellation_between_batches(
        [Values(false, true)] bool cancelBetweenBatches,
        [Values(0, 2, 1000)] int tenantCount
    )
    {
        var seed = await SeedRetry(false);
        await using var connection = await Open();
        await ExpireTerminalClaim(connection, seed.Claim);
        await connection.ExecuteAsync(
            """
            INSERT INTO dmscs.Tenant (Name)
            SELECT concat('batch-tenant-', n) FROM (SELECT TOP (@TenantCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbers;
            INSERT INTO dmscs.Job (JobId, TenantId, JobType, PayloadVersion, Payload, Status, AttemptCount, NextAttemptAt)
            SELECT concat('batch-', n), (SELECT Id FROM dmscs.Tenant WHERE Name = concat('batch-tenant-', n % (@TenantCount + 1))), @JobType, 1, '{}', 'Pending', 1, @Now FROM (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbers;
            INSERT INTO dmscs.DataStoreManage (Name, TenantId, Template, Status, TargetDatabaseName, ExpectedSourceIdentity,
                EndpointDescriptor, CreateJobId, CreatedAt, UpdatedAt, LastRefreshed, LastModifiedDate)
            SELECT concat('Batch', n), (SELECT Id FROM dmscs.Tenant WHERE Name = concat('batch-tenant-', n % (@TenantCount + 1))), 'Minimal', 'PendingCreate', concat('dms_batch_', n), ExpectedSourceIdentity,
                EndpointDescriptor, concat('batch-', n), @Now, @Now, @Now, @Now
            FROM dmscs.DataStoreManage CROSS JOIN (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbers WHERE Id = @Id;
            """,
            new
            {
                seed.Id,
                TenantCount = tenantCount,
                JobType = ManagedJobTypes.Create,
                Now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            }
        );
        using var stopping = new CancellationTokenSource();
        List<int> batches = [];
        List<int> operationsPerBatch = [];
        int operations = 0;
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                // Controlled per-operation latency makes a per-job/per-tenant round-trip loop exceed
                // the unchanged five-second batch deadline, even on a local database.
                BeforeOperation = async (_, token) =>
                {
                    operations++;
                    await Task.Delay(TimeSpan.FromMilliseconds(3), token);
                },
            },
            AfterExhaustBatch = count =>
            {
                batches.Add(count);
                operationsPerBatch.Add(operations);
                operations = 0;
                if (cancelBetweenBatches)
                {
                    stopping.Cancel();
                }
            },
        };
        (await repository.Exhaust(1, JobErrorCode.AttemptsExhausted, stopping.Token))
            .Should()
            .BeOfType<JobExhaustResult.Success>()
            .Which.ExhaustedCount.Should()
            .Be(cancelBetweenBatches ? 1000 : 1001);
        batches.Should().Equal(cancelBetweenBatches ? new[] { 1000 } : new[] { 1000, 1 });
        // Includes open/begin/configure/commit. Command count must not grow with selected jobs or tenants.
        operationsPerBatch.Should().OnlyContain(count => count <= 12);
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM dmscs.DataStoreManage WHERE Status = 'CreateError' AND LastAttempt = 1 AND LastFailureJobCode = 'AttemptsExhausted' AND LastFailureCategory = 'OperatorInspectionRequired';"""
            )
        )
            .Should()
            .Be(cancelBetweenBatches ? 1000 : 1001);
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(*) FROM dmscs.Job j JOIN dmscs.DataStoreManage m ON j.JobId COLLATE Latin1_General_100_BIN2 = m.CreateJobId AND DATALENGTH(j.JobId) = DATALENGTH(m.CreateJobId) WHERE j.Status = 'Error' AND m.Status <> 'CreateError';"""
            )
        )
            .Should()
            .Be(0);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_keeps_managed_exhaustion_excluded_and_live_leases_untouched(
        [Values(false, true)] bool live
    )
    {
        var seed = await SeedRetry(false);
        if (!live)
        {
            await using var connection = await Open();
            await ExpireTerminalClaim(connection, seed.Claim);
        }
        string before = await DeleteAcceptanceSnapshot();
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).Exhaust(
                1,
                JobErrorCode.AttemptsExhausted,
                live ? JobClaimPolicy.All : JobClaimPolicy.ExcludeManagedDataStores,
                default
            )
        )
            .Should()
            .BeOfType<JobExhaustResult.Success>()
            .Which.ExhaustedCount.Should()
            .Be(0);
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_exhaustion_uses_the_existing_single_tenant_gate_and_rolls_back_on_timeout(
        [Values(false, true)] bool multitenant
    )
    {
        long? tenantId = multitenant ? await Tenant("exhaust-gate") : null;
        var seed = await SeedRetry(false, tenantId);
        await using var observer = await Open();
        await ExpireTerminalClaim(observer, seed.Claim);
        string before = await DeleteAcceptanceSnapshot();
        await using var blocker = await Open();
        await using var transaction = await blocker.BeginTransactionAsync();
        await DataStoreCatalog.AcquireTenantGateAsync(transaction, tenantId, default);
        try
        {
            (
                await new JobLeaseRepository(PublicationOptions, PublicationTimings).Exhaust(
                    1,
                    JobErrorCode.AttemptsExhausted,
                    default
                )
            )
                .Should()
                .BeOfType<JobExhaustResult.FailureUnknown>();
        }
        finally
        {
            await transaction.RollbackAsync();
        }
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_rolls_back_exhaustion_for_duplicate_links_or_inconsistent_attempts(
        [Values(false, true)] bool delete,
        [Values("duplicate", "newer-attempt")] string corruption
    )
    {
        var seed = await SeedRetry(delete);
        await using var connection = await Open();
        await ExpireTerminalClaim(connection, seed.Claim);
        if (corruption == "duplicate")
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO dmscs.DataStoreManage (Name, TenantId, Template, Status, TargetDatabaseName, ExpectedSourceIdentity, EndpointDescriptor, CreateJobId, DeleteJobId, CreatedAt, UpdatedAt, LastRefreshed, LastModifiedDate)
                SELECT 'DuplicateLink', TenantId, Template, Status, 'duplicate_link', ExpectedSourceIdentity, EndpointDescriptor, CreateJobId, DeleteJobId, CreatedAt, UpdatedAt, LastRefreshed, LastModifiedDate
                FROM dmscs.DataStoreManage WHERE Id = @Id;
                """,
                new { seed.Id }
            );
        }
        else
        {
            await connection.ExecuteAsync(
                """UPDATE dmscs.DataStoreManage SET LastAttempt = 2 WHERE Id = @Id;""",
                new { seed.Id }
            );
        }
        string before = await DeleteAcceptanceSnapshot();
        (
            await new JobLeaseRepository(PublicationOptions, PublicationTimings).Exhaust(
                1,
                JobErrorCode.AttemptsExhausted,
                default
            )
        )
            .Should()
            .BeOfType<JobExhaustResult.FailureUnknown>();
        (await DeleteAcceptanceSnapshot()).Should().Be(before);
    }

    [Test, Category("DataStoreManageTerminalOutcome")]
    public async Task It_bulk_exhaustion_does_not_retain_aggregate_locks_for_an_unrelated_tenant()
    {
        long otherTenant = await Tenant("other-held-exhaust-tenant");
        var other = await SeedPublication(otherTenant, "OtherExhaust");
        long tenant = await Tenant("held-exhaust-tenant");
        var first = await SeedPublication(tenant, "FirstExhaust");
        var second = await SeedPublication(tenant, "SecondExhaust");
        await using var connection = await Open();
        await ExpireTerminalClaim(connection, first.Claim);
        await ExpireTerminalClaim(connection, second.Claim);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new JobLeaseRepository(PublicationOptions, PublicationTimings)
        {
            SessionHooks = new()
            {
                AfterOperation = async (operation, token) =>
                {
                    if (operation == "WriteRetryAggregate")
                    {
                        // Hold the completed set update and its locks while the unrelated row is renamed.
                        held.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                },
            },
        };
        Task<JobExhaustResult> exhaust = repository.Exhaust(1, JobErrorCode.AttemptsExhausted, default);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            (
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """UPDATE dmscs."DataStoreManage" SET "Name" = 'OtherExhaustProbe' WHERE "Id" = @Id;""",
                        new { other.Command.Id },
                        commandTimeout: 2,
                        cancellationToken: timeout.Token
                    )
                )
            )
                .Should()
                .Be(1);
        }
        finally
        {
            release.TrySetResult();
            await exhaust;
        }
        (await exhaust).Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(2);
        (await DeleteRow(connection, first.Command.Id))["Status"].Should().Be("CreateError");
        (await DeleteRow(connection, second.Command.Id))["Status"].Should().Be("CreateError");
        (await DeleteRow(connection, other.Command.Id))["Name"].Should().Be("OtherExhaustProbe");
    }
}
