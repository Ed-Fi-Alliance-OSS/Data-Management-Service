// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

[TestFixture]
public class Given_selective_managed_job_suppression : JobLeaseRepositoryTests.JobLeaseTestBase
{
    private static readonly JobClaimPolicy Disabled = JobClaimPolicy.ExcludeManagedDataStores;
    private const int MaxAttempts = 5;

    private async Task<string> Snapshot(long[] ids) =>
        JsonSerializer.Serialize(
            (
                await Connection!.QueryAsync(
                    """SELECT * FROM dmscs."Job" WHERE "Id" = ANY(@Ids) ORDER BY "Id";""",
                    new { Ids = ids }
                )
            ).Select(row => ((IDictionary<string, object>)row).ToDictionary())
        );

    [TestCase(ManagedJobTypes.Create, JobStatuses.Pending, 0)]
    [TestCase(ManagedJobTypes.Delete, JobStatuses.Pending, 5)]
    [TestCase(ManagedJobTypes.Create, JobStatuses.InProgress, 5)]
    [TestCase(ManagedJobTypes.Delete, JobStatuses.InProgress, 2)]
    public async Task It_leaves_every_managed_column_unchanged_and_claims_the_other_job(
        string managedType,
        string status,
        int attempts
    )
    {
        long managed = await SeedJobAsync(
            status: status,
            nextAttemptOffset: -100,
            attemptCount: attempts,
            leaseOwner: status == JobStatuses.InProgress ? "old-worker" : null,
            leaseOffset: status == JobStatuses.InProgress ? -10 : null,
            fencingToken: 12,
            jobType: managedType
        );
        long other = await SeedJobAsync();
        string before = await Snapshot([managed]);
        var repository = Repository();
        (await repository.Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, Disabled, default))
            .Should()
            .Be(new JobExhaustResult.Success(0));
        ClaimedJob claim = Claimed(await repository.ClaimNext("worker", 300, MaxAttempts, Disabled, default));
        claim.Id.Should().Be(other);
        (await repository.Complete(claim.Id, claim.LeaseOwner, claim.FencingToken, default))
            .Should()
            .BeOfType<JobWriteResult.Success>();
        (await repository.ClaimNext("worker", 300, MaxAttempts, Disabled, default))
            .Should()
            .BeOfType<JobClaimResult.NoneAvailable>();
        (await Snapshot([managed])).Should().Be(before);

        // A fresh host's all-jobs policy resumes the same durable record, with no reset.
        if (attempts < MaxAttempts)
        {
            ClaimedJob resumed = Claimed(
                await repository.ClaimNext("restarted", 300, MaxAttempts, JobClaimPolicy.All, default)
            );
            resumed.Id.Should().Be(managed);
            resumed.AttemptCount.Should().Be(attempts + 1);
            resumed.FencingToken.Should().Be(13);
            resumed.Reclaimed.Should().Be(status == JobStatuses.InProgress);
        }
        else
        {
            (
                await repository.Exhaust(
                    MaxAttempts,
                    JobErrorCode.AttemptsExhausted,
                    JobClaimPolicy.All,
                    default
                )
            )
                .Should()
                .Be(new JobExhaustResult.Success(1));
        }
    }

    [TestCase("manageddatastore.create")]
    [TestCase("ManagedDataStore.Create ")]
    [TestCase("ManagedDataStore.Delete ")]
    [TestCase("Unrelated.Unknown")]
    public async Task It_keeps_near_matches_and_unrelated_types_eligible_for_claim_and_exhaust(string type)
    {
        long claimable = await SeedJobAsync(jobType: type);
        long exhausted = await SeedJobAsync(jobType: type, attemptCount: MaxAttempts);
        (await Repository().Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, Disabled, default))
            .Should()
            .Be(new JobExhaustResult.Success(1));
        Claimed(await Repository().ClaimNext("worker", 300, MaxAttempts, Disabled, default))
            .Id.Should()
            .Be(claimable);
        (await RowAsync(exhausted)).Status.Should().Be(JobStatuses.Error);
    }

    [TestCase(0)]
    [TestCase(5)]
    public async Task It_skips_a_locked_managed_oldest_row_without_starving_other_jobs(int attempts)
    {
        long managed = await SeedJobAsync(
            nextAttemptOffset: -100,
            attemptCount: attempts,
            jobType: ManagedJobTypes.Create
        );
        long other = await SeedJobAsync();
        string before = await Snapshot([managed]);
        await using (var rowLock = await JobLeaseRepositoryTests.RowLock.AcquireAsync(managed))
        {
            Claimed(
                await Repository()
                    .ClaimNext("worker", 300, MaxAttempts, Disabled, default)
                    .WaitAsync(TimeSpan.FromSeconds(10))
            )
                .Id.Should()
                .Be(other);
            (
                await Repository()
                    .Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, Disabled, default)
                    .WaitAsync(TimeSpan.FromSeconds(10))
            )
                .Should()
                .Be(new JobExhaustResult.Success(0));
        }
        (await Snapshot([managed])).Should().Be(before);
    }

    [Test]
    public async Task It_claims_each_unrelated_job_once_under_concurrency_and_leaves_managed_jobs_unchanged()
    {
        List<long> managed = [];
        List<long> other = [];
        for (int index = 0; index < 4; index++)
        {
            managed.Add(
                await SeedJobAsync(
                    nextAttemptOffset: -100 - index,
                    jobType: index % 2 == 0 ? ManagedJobTypes.Create : ManagedJobTypes.Delete
                )
            );
        }
        for (int index = 0; index < 8; index++)
        {
            other.Add(await SeedJobAsync());
        }
        string before = await Snapshot([.. managed]);
        JobClaimResult[] results = await Task.WhenAll(
            Enumerable
                .Range(0, 12)
                .Select(worker =>
                    Repository().ClaimNext($"worker-{worker}", 300, MaxAttempts, Disabled, default)
                )
        );
        results
            .Should()
            .OnlyContain(result =>
                result is JobClaimResult.Claimed || result is JobClaimResult.NoneAvailable
            );
        long[] claimed = results.OfType<JobClaimResult.Claimed>().Select(result => result.Job.Id).ToArray();
        claimed.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(other);
        (await Snapshot([.. managed])).Should().Be(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_does_not_lock_excluded_rows_during_claim_or_exhaust(bool exhaust)
    {
        int attempts = exhaust ? MaxAttempts : 0;
        long managed = await SeedJobAsync(
            nextAttemptOffset: -100,
            attemptCount: attempts,
            jobType: ManagedJobTypes.Create
        );
        long other = await SeedJobAsync(attemptCount: attempts);
        DateTime originalNextAttempt = (await RowAsync(managed)).NextAttemptAt!.Value;
        long advisoryKey = Random.Shared.Next(1, int.MaxValue);
        string trigger = $"suppression_probe_{Guid.NewGuid():N}";
        await using NpgsqlConnection blocker = new(Configuration.DatabaseOptions.Value.DatabaseConnection);
        await blocker.OpenAsync();
        await blocker.ExecuteAsync("SELECT pg_advisory_lock(@Key);", new { Key = advisoryKey });
        // The trigger pauses inside the real autocommitted statement, after candidate locks have been taken.
        // Only generated identifiers and database IDs enter this test-only DDL.
        await Connection!.ExecuteAsync(
            $"""
            CREATE FUNCTION dmscs.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $probe$
            BEGIN PERFORM pg_advisory_xact_lock({advisoryKey}::bigint); RETURN NEW; END; $probe$;
            CREATE TRIGGER {trigger} BEFORE UPDATE ON dmscs."Job"
            FOR EACH ROW WHEN (NEW."Id" = {other}) EXECUTE FUNCTION dmscs.{trigger}();
            """
        );
        async Task RunOperation()
        {
            if (exhaust)
            {
                (await Repository().Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, Disabled, default))
                    .Should()
                    .Be(new JobExhaustResult.Success(1));
            }
            else
            {
                Claimed(await Repository().ClaimNext("worker", 300, MaxAttempts, Disabled, default))
                    .Id.Should()
                    .Be(other);
            }
        }
        Task operation = RunOperation();
        try
        {
            using CancellationTokenSource bound = new(TimeSpan.FromSeconds(2));
            while (
                !await Connection!.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND @Blocker = ANY(pg_blocking_pids(pid)));",
                        new { Blocker = blocker.ProcessID },
                        cancellationToken: bound.Token
                    )
                )
            )
            {
                operation.IsCompleted.Should().BeFalse("the trigger must pause the operation before commit");
                await Task.Delay(TimeSpan.FromMilliseconds(20), bound.Token);
            }
            // This deliberate probe mutation must finish before the trigger releases the statement's locks.
            (
                await blocker.ExecuteAsync(
                    """SET lock_timeout = '1s'; UPDATE dmscs."Job" SET "ModifiedBy" = 'exclusion-lock-probe', "NextAttemptAt" = "NextAttemptAt" + interval '1 second' WHERE "Id" = @Id;""",
                    new { Id = managed },
                    commandTimeout: 2
                )
            )
                .Should()
                .Be(1);
            operation.IsCompleted.Should().BeFalse("the operation still waits on the advisory gate");
        }
        finally
        {
            await blocker.ExecuteAsync("SELECT pg_advisory_unlock(@Key);", new { Key = advisoryKey });
            try
            {
                await operation;
            }
            finally
            {
                await Connection!.ExecuteAsync(
                    $"""DROP TRIGGER {trigger} ON dmscs."Job"; DROP FUNCTION dmscs.{trigger}();"""
                );
            }
        }
        var row = await RowAsync(managed);
        row.ModifiedBy.Should().Be("exclusion-lock-probe");
        row.NextAttemptAt.Should().Be(originalNextAttempt.AddSeconds(1));
        row.Status.Should().Be(JobStatuses.Pending);
        row.AttemptCount.Should().Be(attempts);
        row.FencingToken.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_exhausts_bounded_mixed_batches_without_changing_managed_rows(
        bool cancelAfterFirstBatch
    )
    {
        await SeedOverLimitJobsAsync(3000);
        await Connection!.ExecuteAsync(
            """UPDATE dmscs."Job" SET "JobType" = CASE WHEN "Id" % 4 = 0 THEN @Create ELSE @Delete END WHERE "Id" % 2 = 0;""",
            new { Create = ManagedJobTypes.Create, Delete = ManagedJobTypes.Delete }
        );
        long[] managed = (
            await Connection!.QueryAsync<long>(
                """SELECT "Id" FROM dmscs."Job" WHERE "Id" % 2 = 0 ORDER BY "Id";"""
            )
        ).ToArray();
        string before = await Snapshot(managed);
        List<int> batches = [];
        using CancellationTokenSource stop = new();
        JobLeaseRepository repository = new(
            Configuration.DatabaseOptions,
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10))
        )
        {
            AfterExhaustBatch = count =>
            {
                batches.Add(count);
                if (cancelAfterFirstBatch)
                {
                    stop.Cancel();
                }
            },
        };
        (await repository.Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, Disabled, stop.Token))
            .Should()
            .Be(new JobExhaustResult.Success(cancelAfterFirstBatch ? 1000 : 1500));
        batches.Should().OnlyContain(count => count <= 1000);
        batches[0].Should().Be(1000);
        (await Snapshot(managed)).Should().Be(before);
        if (cancelAfterFirstBatch)
        {
            (await Repository().Exhaust(MaxAttempts, JobErrorCode.AttemptsExhausted, Disabled, default))
                .Should()
                .Be(new JobExhaustResult.Success(500));
            (await Snapshot(managed)).Should().Be(before);
        }
    }

    [Test]
    public async Task It_preserves_managed_rows_when_an_unrelated_completion_is_cancelled_and_rolled_back()
    {
        long managed = await SeedJobAsync(
            nextAttemptOffset: -100,
            jobType: ManagedJobTypes.Create,
            fencingToken: 7
        );
        long other = await SeedJobAsync();
        string managedBefore = await Snapshot([managed]);
        ClaimedJob claim = Claimed(
            await Repository().ClaimNext("worker", 300, MaxAttempts, Disabled, default)
        );
        claim.Id.Should().Be(other);
        string otherBefore = await Snapshot([other]);
        using CancellationTokenSource cancellation = new();
        JobLeaseRepository interrupted = new(
            Configuration.DatabaseOptions,
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10))
        )
        {
            BeforeOwnershipCommit = _ =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        (await interrupted.Complete(other, claim.LeaseOwner, claim.FencingToken, cancellation.Token))
            .Should()
            .BeOfType<JobWriteResult.ResultUnknown>();
        (await Snapshot([managed])).Should().Be(managedBefore);
        (await Snapshot([other])).Should().Be(otherBefore);
        (await Repository().Complete(other, claim.LeaseOwner, claim.FencingToken, default))
            .Should()
            .BeOfType<JobWriteResult.Success>();
    }
}

[TestFixture]
public class Given_disabled_management_job_runtime : JobRuntimeIntegrationTests.RuntimeTestBase
{
    private static readonly JobClaimPolicy Disabled = JobClaimPolicy.ExcludeManagedDataStores;

    private async Task<string> Snapshot(long id) =>
        System.Text.Json.JsonSerializer.Serialize(
            (
                (IDictionary<string, object>)
                    await Connection!.QuerySingleAsync(
                        """SELECT * FROM dmscs."Job" WHERE "Id" = @Id;""",
                        new { Id = id }
                    )
            ).ToDictionary()
        );

    [TestCase("manageddatastore.create")]
    [TestCase("ManagedDataStore.Create ")]
    [TestCase("ManagedDataStore.Delete ")]
    [TestCase("Unrelated.Unknown")]
    public async Task It_preserves_existing_terminal_validation_for_unknown_types(string jobType)
    {
        long managed = await SeedJobAsync(nextAttemptOffset: -100, jobType: ManagedJobTypes.Create);
        string before = await Snapshot(managed);
        long unknown = await SeedJobAsync(jobType: jobType);
        JobOptions settings = WithoutRenewals();
        using var runtime = new JobRuntimeIntegrationTests.RuntimeHarness(
            settings,
            Repository(settings.LeaseTimings),
            FenceFactory(settings.LeaseTimings)
        );
        var claimed = Claimed(await Repository().ClaimNext("worker", 300, 5, Disabled, default));
        claimed.Id.Should().Be(unknown);
        await runtime.ExecuteAsync(claimed);
        var row = await RowAsync(unknown);
        row.Status.Should().Be(JobStatuses.Error);
        row.ErrorMessage.Should().Be(JobErrorCode.UnsupportedJobType.Message);
        runtime.Script.JobIds.Should().BeEmpty();
        (await Snapshot(managed)).Should().Be(before);
    }

    [Test]
    public async Task It_runs_the_worker_and_materializes_an_unrelated_recurring_schedule()
    {
        long managed = await SeedJobAsync(nextAttemptOffset: -100, jobType: ManagedJobTypes.Create);
        long exhaustedManaged = await SeedJobAsync(
            nextAttemptOffset: -100,
            attemptCount: 5,
            jobType: ManagedJobTypes.Delete
        );
        string before = await Snapshot(managed);
        string exhaustedBefore = await Snapshot(exhaustedManaged);
        JobScheduleRepository schedules = new(
            Configuration.DatabaseOptions,
            new TestAuditContext(),
            new TenantContextProvider()
        );
        (
            await schedules.Upsert(
                new(
                    "Runtime.Schedule",
                    JobRuntimeIntegrationTests.RuntimeHarness.JobType,
                    1,
                    """{"dataStoreId":1}""",
                    5,
                    true
                ),
                default
            )
        )
            .Should()
            .BeOfType<JobScheduleUpsertResult.Success>();
        string jobId = Guid.NewGuid().ToString("N");
        (await schedules.MaterializeNextDue("dispatcher", 30, jobId, default))
            .Should()
            .BeOfType<JobScheduleMaterializeResult.Materialized>();
        long id = await Connection!.ExecuteScalarAsync<long>(
            """SELECT "Id" FROM dmscs."Job" WHERE "JobId" = @JobId;""",
            new { JobId = jobId }
        );

        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool ownershipWrite = false;
        JobOptions settings = WithoutRenewals();
        JobLeaseRepository leases = new(Configuration.DatabaseOptions, settings.LeaseTimings)
        {
            BeforeOwnershipCommit = _ =>
            {
                ownershipWrite = true;
                return Task.CompletedTask;
            },
            SessionHooks = new()
            {
                AfterOperation = (operation, _) =>
                {
                    if (operation == "Commit" && ownershipWrite)
                    {
                        committed.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
            },
        };
        using var runtime = new JobRuntimeIntegrationTests.RuntimeHarness(
            settings,
            leases,
            FenceFactory(settings.LeaseTimings)
        );
        using JobWorkerService worker = new(
            runtime.Leases,
            runtime.Executor,
            Options.Create(settings),
            TimeProvider.System,
            NullLogger<JobWorkerService>.Instance,
            Disabled
        );
        try
        {
            await worker.StartAsync(default);
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(20));
        }
        (await RowAsync(id)).Status.Should().Be(JobStatuses.Completed);
        runtime.Script.JobIds.Should().Equal(jobId);
        (await Snapshot(managed)).Should().Be(before);
        (await Snapshot(exhaustedManaged)).Should().Be(exhaustedBefore);
        (await schedules.ListByType("Runtime.Schedule", default))
            .Should()
            .BeOfType<JobScheduleListResult.Success>()
            .Subject.Schedules.Should()
            .ContainSingle()
            .Which.Enabled.Should()
            .BeTrue();
    }
}
