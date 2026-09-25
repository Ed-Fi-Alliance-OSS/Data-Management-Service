// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LeaseRow = EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs.JobLeaseRepositoryTests.LeaseRow;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

/// <summary>
/// The job runtime against a real database (spec step 3.7, §2 rows 9, 10, 11 and 19): real repositories, a real
/// executor, and hosted services constructed explicitly with short leases, fake handlers, and faults injected before or
/// after a commit through <see cref="JobDatabaseSessionHooks"/>.
/// </summary>
public class JobRuntimeIntegrationTests
{
    private const string OwnerA = "worker-a";
    private const string OwnerB = "worker-b";
    private const int MaxAttempts = 5;

    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    public sealed record RuntimePayload(long DataStoreId);

    public sealed class RuntimeValidator : IJobPayloadValidator<RuntimePayload>
    {
        public IReadOnlyList<string> Validate(RuntimePayload payload) => [];
    }

    /// <summary>What the test's handler does, and the job identifiers it ran for.</summary>
    public sealed class RuntimeScript
    {
        public Func<JobExecutionContext, CancellationToken, Task> Run { get; set; } =
            (_, _) => Task.CompletedTask;

        public ConcurrentQueue<string> JobIds { get; } = new();
    }

    public sealed class RuntimeHandler(RuntimeScript script) : IJobHandler<RuntimePayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            RuntimePayload payload,
            CancellationToken cancellationToken
        )
        {
            script.JobIds.Enqueue(context.JobId);
            return script.Run(context, cancellationToken);
        }
    }

    /// <summary>A lease repository that counts the outcome writes passed through to the real one.</summary>
    public sealed class CountingLeaseRepository(IJobLeaseRepository inner) : IJobLeaseRepository
    {
        private int _outcomeWrites;

        public int OutcomeWrites => Volatile.Read(ref _outcomeWrites);

        public Task<JobClaimResult> ClaimNext(
            string owner,
            int leaseSeconds,
            int maxAttempts,
            CancellationToken cancellationToken
        ) => inner.ClaimNext(owner, leaseSeconds, maxAttempts, cancellationToken);

        public Task<JobExhaustResult> Exhaust(
            int maxAttempts,
            JobErrorCode errorCode,
            CancellationToken cancellationToken
        ) => inner.Exhaust(maxAttempts, errorCode, cancellationToken);

        public Task<JobWriteResult> Renew(
            long id,
            string owner,
            long fencingToken,
            int leaseSeconds,
            CancellationToken cancellationToken
        ) => inner.Renew(id, owner, fencingToken, leaseSeconds, cancellationToken);

        public Task<JobWriteResult> Complete(
            long id,
            string owner,
            long fencingToken,
            CancellationToken cancellationToken
        ) => Outcome(() => inner.Complete(id, owner, fencingToken, cancellationToken));

        public Task<JobWriteResult> FailTransient(
            long id,
            string owner,
            long fencingToken,
            int backoffSeconds,
            CancellationToken cancellationToken
        ) => Outcome(() => inner.FailTransient(id, owner, fencingToken, backoffSeconds, cancellationToken));

        public Task<JobWriteResult> FailTerminal(
            long id,
            string owner,
            long fencingToken,
            JobErrorCode errorCode,
            CancellationToken cancellationToken
        ) => Outcome(() => inner.FailTerminal(id, owner, fencingToken, errorCode, cancellationToken));

        public Task<JobWriteResult> ReleaseToPending(
            long id,
            string owner,
            long fencingToken,
            CancellationToken cancellationToken
        ) => Outcome(() => inner.ReleaseToPending(id, owner, fencingToken, cancellationToken));

        private Task<JobWriteResult> Outcome(Func<Task<JobWriteResult>> write)
        {
            Interlocked.Increment(ref _outcomeWrites);
            return write();
        }
    }

    /// <summary>
    /// A real executor, worker, and schedule dispatcher over the given lease repository and fence factory, with the
    /// test's handler registered for <see cref="JobType"/> and the system clock.
    /// </summary>
    public sealed class RuntimeHarness : IDisposable
    {
        public const string JobType = "Runtime.Probe";

        private readonly ServiceProvider _provider;

        public RuntimeHarness(JobOptions settings, IJobLeaseRepository leases, IJobFenceFactory fences)
        {
            Settings = settings;
            Leases = new CountingLeaseRepository(leases);

            ServiceCollection services = new();
            services.AddSingleton(Script);
            services.AddScoped<ITenantContextProvider, TenantContextProvider>();
            services.AddScoped<IJobScheduleRepository>(provider => new JobScheduleRepository(
                Configuration.DatabaseOptions,
                new TestAuditContext(),
                provider.GetRequiredService<ITenantContextProvider>()
            ));
            services.AddJobHandler<RuntimeHandler, RuntimePayload, RuntimeValidator>(JobType, 1);
            _provider = services.BuildServiceProvider();

            Executor = new JobExecutor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Leases,
                fences,
                _provider.GetRequiredService<IJobHandlerRegistry>(),
                _provider.GetRequiredService<IJobErrorCodeRegistry>(),
                Options.Create(settings),
                new JobRuntimeEnvironment(MultiTenancy: false),
                TimeProvider.System,
                Metrics,
                NullLogger<JobExecutor>.Instance
            );
        }

        public JobOptions Settings { get; }
        public RuntimeScript Script { get; } = new();
        public CountingLeaseRepository Leases { get; }
        public JobMetrics Metrics { get; } = new();
        public JobExecutor Executor { get; }

        public JobWorkerService Worker() =>
            new(
                Leases,
                Executor,
                Options.Create(Settings),
                TimeProvider.System,
                NullLogger<JobWorkerService>.Instance
            );

        public JobScheduleDispatcherService Dispatcher() =>
            new(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Metrics,
                Options.Create(Settings),
                TimeProvider.System,
                NullLogger<JobScheduleDispatcherService>.Instance
            );

        public Task<JobExecutionResult> ExecuteAsync(ClaimedJob job) =>
            Executor.ExecuteAsync(job, CancellationToken.None).WaitAsync(_wait);

        public void Dispose()
        {
            Metrics.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>
    /// Session hooks that throw a lost-connection failure from the first <c>Commit</c> after they are armed, before
    /// the commit is sent or after it has completed.
    /// </summary>
    public sealed class CommitFault
    {
        private int _armed;
        private int _fired;

        private CommitFault(bool afterCommit)
        {
            Func<string, CancellationToken, Task> fault = (operation, _) =>
            {
                if (
                    operation == "Commit"
                    && Volatile.Read(ref _armed) == 1
                    && Interlocked.Exchange(ref _fired, 1) == 0
                )
                {
                    throw new IOException("Injected: the connection was lost around the commit.");
                }

                return Task.CompletedTask;
            };
            Hooks = afterCommit
                ? new JobDatabaseSessionHooks { AfterOperation = fault }
                : new JobDatabaseSessionHooks { BeforeOperation = fault };
        }

        /// <summary>The commit never reaches the database: the transaction rolls back.</summary>
        public static CommitFault BeforeCommit() => new(afterCommit: false);

        /// <summary>The commit takes effect, but its acknowledgement is lost.</summary>
        public static CommitFault AfterCommit() => new(afterCommit: true);

        public JobDatabaseSessionHooks Hooks { get; }

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public void Arm() => Volatile.Write(ref _armed, 1);
    }

    public abstract class RuntimeTestBase : JobLeaseRepositoryTests.JobLeaseTestBase
    {
        /// <summary>No renewal runs within a test: a commit fault armed for the outcome write can only meet it.</summary>
        protected static JobOptions WithoutRenewals() =>
            new()
            {
                LeaseDuration = TimeSpan.FromSeconds(60),
                RenewalInterval = TimeSpan.FromSeconds(60),
                PollInterval = TimeSpan.FromMilliseconds(200),
                MaxAttempts = MaxAttempts,
                MaxConcurrentJobs = 1,
            };

        protected static JobLeaseRepository HookedRepository(
            JobOptions settings,
            JobDatabaseSessionHooks hooks
        ) => new(Configuration.DatabaseOptions, settings.LeaseTimings) { SessionHooks = hooks };

        protected static PostgresqlJobFenceFactory HookedFenceFactory(
            JobOptions settings,
            JobDatabaseSessionHooks hooks
        ) => new(Configuration.DatabaseOptions, settings.LeaseTimings) { SessionHooks = hooks };

        protected static async Task<ClaimedJob> ClaimAsync(JobOptions settings, string owner) =>
            Claimed(
                await Repository(settings.LeaseTimings)
                    .ClaimNext(
                        owner,
                        (int)settings.LeaseDuration.TotalSeconds,
                        MaxAttempts,
                        CancellationToken.None
                    )
            );

        protected Task<long> SeedRuntimeJobAsync() => SeedJobAsync(jobType: RuntimeHarness.JobType);

        protected async Task<DateTime> DatabaseUtcNowAsync() =>
            await Connection!.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp() AT TIME ZONE 'UTC';");

        /// <summary>A fenced write that inserts its marker only when a previous attempt has not persisted it.</summary>
        protected static async Task ReconcilingFencedWriteAsync(
            DbTransaction transaction,
            CancellationToken cancellationToken
        ) =>
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO "dmscs"."OwnershipToken" ("Description")
                    SELECT 'fenced-write'
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "dmscs"."OwnershipToken" WHERE "Description" = 'fenced-write');
                    """,
                    transaction: transaction,
                    cancellationToken: cancellationToken
                )
            );

        /// <summary>Polls <paramref name="condition"/> until it holds, failing after <see cref="_wait"/>.</summary>
        protected static async Task EventuallyAsync(Func<Task<bool>> condition, string because)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!await condition())
            {
                if (elapsed.Elapsed > _wait)
                {
                    Assert.Fail($"Timed out waiting until {because}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    [TestFixture]
    public class Given_an_abandoned_in_progress_job_after_a_restart : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private ClaimedJob _abandoned = null!;
        private ClaimedJob _reclaimed = null!;
        private JobExecutionResult _result = null!;
        private JobWriteResult _lateCompletion = null!;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            JobOptions settings = WithoutRenewals();
            _harness = new RuntimeHarness(settings, Repository(settings.LeaseTimings), FenceFactory());
            long id = await SeedRuntimeJobAsync();

            // The first owner claims and crashes: nothing renews or finishes its lease, which then expires.
            _abandoned = await ClaimAsync(settings, OwnerA);
            await ExpireLeaseAsync(id);

            // After the restart, the next claim takes the job over and runs it.
            _reclaimed = await ClaimAsync(settings, OwnerB);
            _result = await _harness.ExecuteAsync(_reclaimed);
            _lateCompletion = await Repository()
                .Complete(id, OwnerA, _abandoned.FencingToken, CancellationToken.None);
            _row = await RowAsync(id);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_reclaims_with_the_next_attempt_and_fencing_token()
        {
            _reclaimed.Reclaimed.Should().BeTrue();
            _reclaimed.AttemptCount.Should().Be(2);
            _reclaimed.FencingToken.Should().Be(_abandoned.FencingToken + 1);
        }

        [Test]
        public void It_completes_the_reclaimed_execution() =>
            _result.Outcome.Should().Be(JobExecutionOutcome.Completed);

        [Test]
        public void It_rejects_the_crashed_owners_late_completion() =>
            _lateCompletion.Should().BeOfType<JobWriteResult.OwnershipLost>();

        [Test]
        public void It_leaves_the_job_completed_by_the_new_owner()
        {
            _row.Status.Should().Be(JobStatuses.Completed);
            _row.AttemptCount.Should().Be(2);
            _row.FencingToken.Should().Be(_reclaimed.FencingToken);
        }
    }

    [TestFixture]
    public class Given_an_execution_that_outlives_its_original_lease : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private ClaimedJob _claimed = null!;
        private JobClaimResult _competingClaim = null!;
        private DateTime? _leaseAfterRenewals;
        private DateTime _databaseTimeAtCompetingClaim;
        private JobExecutionResult _result = null!;
        private long _fencedWrites;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            // A 5 s lease renewed every 2 s: the handler runs until the original lease is 2 s past its expiry.
            JobOptions settings = WithoutRenewals();
            settings.LeaseDuration = TimeSpan.FromSeconds(5);
            settings.RenewalInterval = TimeSpan.FromSeconds(2);
            _harness = new RuntimeHarness(settings, Repository(settings.LeaseTimings), FenceFactory());
            long id = await SeedRuntimeJobAsync();
            _claimed = await ClaimAsync(settings, OwnerA);

            _harness.Script.Run = async (context, cancellationToken) =>
            {
                while (await DatabaseUtcNowAsync() < _claimed.LeaseExpiresAt.AddSeconds(2))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }

                _databaseTimeAtCompetingClaim = await DatabaseUtcNowAsync();
                _competingClaim = await Repository()
                    .ClaimNext(OwnerB, 5, MaxAttempts, CancellationToken.None);
                _leaseAfterRenewals = (await RowAsync(id)).LeaseExpiresAt;
                await context.Fence.ExecuteAsync(FencedWriteAsync, cancellationToken);
            };

            _result = await _harness.ExecuteAsync(_claimed);
            _fencedWrites = await FencedWriteCountAsync();
            _row = await RowAsync(id);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_keeps_the_lease_live_past_its_original_expiry() =>
            _leaseAfterRenewals.Should().BeAfter(_databaseTimeAtCompetingClaim);

        [Test]
        public void It_gives_a_competing_claimer_nothing() =>
            _competingClaim.Should().BeOfType<JobClaimResult.NoneAvailable>();

        [Test]
        public void It_commits_the_fenced_write_after_the_original_lease_expired() =>
            _fencedWrites.Should().Be(1);

        [Test]
        public void It_completes_on_the_first_attempt()
        {
            _result.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _row.Status.Should().Be(JobStatuses.Completed);
            _row.AttemptCount.Should().Be(1);
            _row.FencingToken.Should().Be(_claimed.FencingToken);
        }
    }

    [TestFixture]
    public class Given_a_completion_whose_commit_acknowledgement_is_lost : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private CommitFault _fault = null!;
        private JobExecutionResult _result = null!;
        private LeaseRow _row = null!;
        private JobClaimResult _laterClaim = null!;

        [SetUp]
        public async Task Setup()
        {
            JobOptions settings = WithoutRenewals();
            _fault = CommitFault.AfterCommit();
            _harness = new RuntimeHarness(settings, HookedRepository(settings, _fault.Hooks), FenceFactory());
            long id = await SeedRuntimeJobAsync();
            ClaimedJob claimed = await ClaimAsync(settings, OwnerA);

            // Armed as the handler returns, so the next commit is the outcome write's.
            _harness.Script.Run = (_, _) =>
            {
                _fault.Arm();
                return Task.CompletedTask;
            };

            _result = await _harness.ExecuteAsync(claimed);
            _row = await RowAsync(id);
            _laterClaim = await Repository().ClaimNext(OwnerB, 60, MaxAttempts, CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_injected_the_fault() => _fault.Fired.Should().BeTrue();

        [Test]
        public void It_ends_the_execution_uncertain_without_retrying_the_write()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "WriteOutcomeUnknown"));
            _harness.Leases.OutcomeWrites.Should().Be(1);
        }

        [Test]
        public void It_leaves_the_committed_completion_terminal()
        {
            _row.Status.Should().Be(JobStatuses.Completed);
            _row.FinishedAt.Should().NotBeNull();
            _row.LeaseOwner.Should().BeNull();
        }

        [Test]
        public void It_is_never_claimed_again() =>
            _laterClaim.Should().BeOfType<JobClaimResult.NoneAvailable>();
    }

    [TestFixture]
    public class Given_a_completion_whose_commit_never_reaches_the_database : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private CommitFault _fault = null!;
        private ClaimedJob _claimed = null!;
        private JobExecutionResult _result = null!;
        private LeaseRow _rowAfterFault = null!;
        private ClaimedJob _reclaimed = null!;
        private JobExecutionResult _recovery = null!;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            JobOptions settings = WithoutRenewals();
            _fault = CommitFault.BeforeCommit();
            _harness = new RuntimeHarness(settings, HookedRepository(settings, _fault.Hooks), FenceFactory());
            long id = await SeedRuntimeJobAsync();
            _claimed = await ClaimAsync(settings, OwnerA);
            _harness.Script.Run = (_, _) =>
            {
                _fault.Arm();
                return Task.CompletedTask;
            };

            _result = await _harness.ExecuteAsync(_claimed);
            _rowAfterFault = await RowAsync(id);

            // Recovery by state: the rolled-back row is still in progress, so lease expiry makes it reclaimable.
            await ExpireLeaseAsync(id);
            _reclaimed = await ClaimAsync(settings, OwnerB);
            _recovery = await _harness.ExecuteAsync(_reclaimed);
            _row = await RowAsync(id);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_ends_the_first_execution_uncertain() =>
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "WriteOutcomeUnknown"));

        [Test]
        public void It_leaves_the_row_in_progress_under_the_first_owner()
        {
            _rowAfterFault.Status.Should().Be(JobStatuses.InProgress);
            _rowAfterFault.LeaseOwner.Should().Be(OwnerA);
            _rowAfterFault.FencingToken.Should().Be(_claimed.FencingToken);
            _rowAfterFault.FinishedAt.Should().BeNull();
        }

        [Test]
        public void It_reclaims_the_job_after_lease_expiry()
        {
            _reclaimed.Reclaimed.Should().BeTrue();
            _reclaimed.AttemptCount.Should().Be(2);
            _reclaimed.FencingToken.Should().Be(_claimed.FencingToken + 1);
        }

        [Test]
        public void It_completes_the_job_on_the_next_attempt()
        {
            _recovery.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _row.Status.Should().Be(JobStatuses.Completed);
            _row.AttemptCount.Should().Be(2);
        }
    }

    [TestFixture]
    public class Given_a_shutdown_release_followed_by_a_reclaim : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private LeaseRow _rowAfterRelease = null!;
        private ClaimedJob _next = null!;
        private JobWriteResult _lateCompletion = null!;
        private Exception? _zombieFence;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            JobOptions settings = WithoutRenewals();
            _harness = new RuntimeHarness(settings, Repository(settings.LeaseTimings), FenceFactory());
            long id = await SeedRuntimeJobAsync();

            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _harness.Script.Run = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            };

            string releasedOwner;
            using (JobWorkerService worker = _harness.Worker())
            {
                releasedOwner = worker.Owner;
                await worker.StartAsync(CancellationToken.None);
                await started.Task.WaitAsync(_wait);
                await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
            }

            _rowAfterRelease = await RowAsync(id);
            _next = await ClaimAsync(settings, OwnerB);

            // The released execution, had it survived, would still hold its old claim.
            ClaimedJob releasedClaim = _next with
            {
                LeaseOwner = releasedOwner,
                FencingToken = _next.FencingToken - 1,
                AttemptCount = 1,
            };
            _lateCompletion = await Repository()
                .Complete(id, releasedClaim.LeaseOwner, releasedClaim.FencingToken, CancellationToken.None);
            _zombieFence = await ThrownByAsync(() =>
                FenceFactory()
                    .Create(releasedClaim, new JobExecutionOwnership())
                    .ExecuteAsync(FencedWriteAsync, CancellationToken.None)
            );
            _fencedWrites = await FencedWriteCountAsync();
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_releases_the_job_on_shutdown_keeping_the_attempt()
        {
            _rowAfterRelease.Status.Should().Be(JobStatuses.Pending);
            _rowAfterRelease.AttemptCount.Should().Be(1);
            _rowAfterRelease.LeaseOwner.Should().BeNull();
            _harness.Leases.OutcomeWrites.Should().Be(1);
        }

        [Test]
        public void It_lets_another_claimer_take_the_next_attempt()
        {
            _next.Reclaimed.Should().BeFalse();
            _next.AttemptCount.Should().Be(2);
            _next.LeaseOwner.Should().Be(OwnerB);
        }

        [Test]
        public void It_rejects_the_released_owners_late_completion() =>
            _lateCompletion.Should().BeOfType<JobWriteResult.OwnershipLost>();

        [Test]
        public void It_fences_out_a_write_under_the_released_claim()
        {
            _zombieFence.Should().BeOfType<JobLeaseLostException>();
            _fencedWrites.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_due_schedule_run_by_the_dispatcher_and_the_worker : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private ScheduledJob[] _jobs = [];
        private DateTime _nextRunAt;
        private DateTime _databaseTimeBeforeDispatch;

        public sealed class ScheduledJob
        {
            public string JobId { get; set; } = "";
            public string Status { get; set; } = "";
            public int AttemptCount { get; set; }
        }

        [SetUp]
        public async Task Setup()
        {
            JobOptions settings = WithoutRenewals();
            _harness = new RuntimeHarness(settings, Repository(settings.LeaseTimings), FenceFactory());
            _databaseTimeBeforeDispatch = await DatabaseUtcNowAsync();
            long scheduleId = await Connection!.ExecuteScalarAsync<long>(
                """
                INSERT INTO "dmscs"."JobSchedule" (
                    "ScheduleType", "JobType", "PayloadVersion", "Payload", "IntervalMinutes", "Enabled", "NextRunAt")
                VALUES ('Runtime.Nightly', @JobType, 1, '{"dataStoreId":1}', 60, TRUE,
                    (clock_timestamp() AT TIME ZONE 'UTC') - interval '1 second')
                RETURNING "Id";
                """,
                new { JobType = RuntimeHarness.JobType }
            );

            using (JobScheduleDispatcherService dispatcher = _harness.Dispatcher())
            using (JobWorkerService worker = _harness.Worker())
            {
                await dispatcher.StartAsync(CancellationToken.None);
                await worker.StartAsync(CancellationToken.None);
                await EventuallyAsync(
                    async () =>
                        await CountAsync($"\"SourceScheduleId\" = {scheduleId} AND \"Status\" = 'Completed'")
                        == 1,
                    "the scheduled job completed"
                );
                await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
                await dispatcher.StopAsync(CancellationToken.None).WaitAsync(_wait);
            }

            _jobs = (
                await Connection!.QueryAsync<ScheduledJob>(
                    """
                    SELECT "JobId", "Status", "AttemptCount" FROM "dmscs"."Job"
                    WHERE "SourceScheduleId" = @ScheduleId;
                    """,
                    new { ScheduleId = scheduleId }
                )
            ).ToArray();
            _nextRunAt = await Connection!.ExecuteScalarAsync<DateTime>(
                """SELECT "NextRunAt" FROM "dmscs"."JobSchedule" WHERE "Id" = @ScheduleId;""",
                new { ScheduleId = scheduleId }
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_enqueues_one_occurrence_and_completes_it_on_the_first_attempt() =>
            _jobs
                .Should()
                .ContainSingle()
                .Which.Should()
                .Match<ScheduledJob>(job => job.Status == JobStatuses.Completed && job.AttemptCount == 1);

        [Test]
        public void It_runs_the_occurrence_through_the_registered_handler() =>
            _harness.Script.JobIds.Should().Equal(_jobs.Select(job => job.JobId));

        [Test]
        public void It_advances_the_schedule() =>
            _nextRunAt.Should().BeAfter(_databaseTimeBeforeDispatch.AddMinutes(59));
    }

    [TestFixture]
    public class Given_a_fence_commit_that_never_reaches_the_database : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private CommitFault _fault = null!;
        private ClaimedJob _claimed = null!;
        private Exception? _seenByHandler;
        private JobExecutionResult _result = null!;
        private long _fencedWrites;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            JobOptions settings = WithoutRenewals();
            _fault = CommitFault.BeforeCommit();
            _fault.Arm();
            _harness = new RuntimeHarness(
                settings,
                Repository(settings.LeaseTimings),
                HookedFenceFactory(settings, _fault.Hooks)
            );
            long id = await SeedRuntimeJobAsync();
            _claimed = await ClaimAsync(settings, OwnerA);
            _harness.Script.Run = async (context, cancellationToken) =>
            {
                _seenByHandler = await ThrownByAsync(() =>
                    context.Fence.ExecuteAsync(FencedWriteAsync, cancellationToken)
                );
                throw _seenByHandler!;
            };

            _result = await _harness.ExecuteAsync(_claimed);
            _fencedWrites = await FencedWriteCountAsync();
            _row = await RowAsync(id);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_throws_lease_lost_from_the_fence() =>
            _seenByHandler.Should().BeOfType<JobLeaseLostException>();

        [Test]
        public void It_ends_the_execution_uncertain_about_the_fence_commit() =>
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "FenceCommitUnknown"));

        [Test]
        public void It_issues_no_outcome_write() => _harness.Leases.OutcomeWrites.Should().Be(0);

        [Test]
        public void It_persists_no_fenced_write() => _fencedWrites.Should().Be(0);

        [Test]
        public void It_leaves_the_row_in_progress_for_lease_expiry()
        {
            _row.Status.Should().Be(JobStatuses.InProgress);
            _row.LeaseOwner.Should().Be(OwnerA);
            _row.FencingToken.Should().Be(_claimed.FencingToken);
        }
    }

    [TestFixture]
    public class Given_a_fence_commit_whose_acknowledgement_is_lost : RuntimeTestBase
    {
        private RuntimeHarness _harness = null!;
        private CommitFault _fault = null!;
        private JobExecutionResult _result = null!;
        private int _outcomeWritesAfterFault;
        private long _fencedWritesAfterFault;
        private LeaseRow _rowAfterFault = null!;
        private ClaimedJob _reclaimed = null!;
        private JobExecutionResult _recovery = null!;
        private long _fencedWrites;
        private LeaseRow _row = null!;

        [SetUp]
        public async Task Setup()
        {
            JobOptions settings = WithoutRenewals();
            _fault = CommitFault.AfterCommit();
            _fault.Arm();
            _harness = new RuntimeHarness(
                settings,
                Repository(settings.LeaseTimings),
                HookedFenceFactory(settings, _fault.Hooks)
            );
            long id = await SeedRuntimeJobAsync();
            ClaimedJob claimed = await ClaimAsync(settings, OwnerA);
            _harness.Script.Run = (context, cancellationToken) =>
                context.Fence.ExecuteAsync(ReconcilingFencedWriteAsync, cancellationToken);

            _result = await _harness.ExecuteAsync(claimed);
            _outcomeWritesAfterFault = _harness.Leases.OutcomeWrites;
            _fencedWritesAfterFault = await FencedWriteCountAsync();
            _rowAfterFault = await RowAsync(id);

            // The next execution after lease expiry repeats the work, which finds the persisted write.
            await ExpireLeaseAsync(id);
            _reclaimed = await ClaimAsync(settings, OwnerB);
            _recovery = await _harness.ExecuteAsync(_reclaimed);
            _fencedWrites = await FencedWriteCountAsync();
            _row = await RowAsync(id);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_persists_the_fenced_write() => _fencedWritesAfterFault.Should().Be(1);

        [Test]
        public void It_ends_the_execution_uncertain_without_an_outcome_write()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "FenceCommitUnknown"));
            _outcomeWritesAfterFault.Should().Be(0);
            _rowAfterFault.Status.Should().Be(JobStatuses.InProgress);
        }

        [Test]
        public void It_reclaims_after_lease_expiry()
        {
            _reclaimed.Reclaimed.Should().BeTrue();
            _reclaimed.AttemptCount.Should().Be(2);
        }

        [Test]
        public void It_reconciles_idempotently_and_completes()
        {
            _recovery.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _fencedWrites.Should().Be(1);
            _row.Status.Should().Be(JobStatuses.Completed);
            _row.AttemptCount.Should().Be(2);
        }
    }

    [TestFixture]
    public class Given_a_renewal_waiting_behind_a_fence_commit : RuntimeTestBase
    {
        private long _commitRequested;
        private long _renewalRequested;
        private long _commitCompleted;
        private long _admitted;
        private bool _admittedBeforeCommitCompleted;
        private JobWriteResult _renewal = null!;
        private long _fencedWrites;

        [SetUp]
        public async Task Setup()
        {
            await SeedRuntimeJobAsync();
            ClaimedJob job = Claimed(
                await Repository().ClaimNext(OwnerA, 30, MaxAttempts, CancellationToken.None)
            );
            JobExecutionOwnership ownership = new();
            TaskCompletionSource commitRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource renewalWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IDisposable>? renewalEntry = null;

            JobDatabaseSessionHooks hooks = new()
            {
                BeforeOperation = async (operation, _) =>
                {
                    if (operation != "Commit")
                    {
                        return;
                    }

                    _commitRequested = Stopwatch.GetTimestamp();
                    commitRequested.TrySetResult();
                    await renewalWaiting.Task.WaitAsync(_wait);

                    // Long enough that a renewal admitted while the commit is pending would be seen admitted.
                    await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
                },
                AfterOperation = (operation, _) =>
                {
                    if (operation == "Commit")
                    {
                        _commitCompleted = Stopwatch.GetTimestamp();
                        _admittedBeforeCommitCompleted = renewalEntry!.IsCompleted;
                    }

                    return Task.CompletedTask;
                },
            };

            Task fenced = HookedFenceFactory(WithoutRenewals(), hooks)
                .Create(job, ownership)
                .ExecuteAsync(FencedWriteAsync, CancellationToken.None);

            await commitRequested.Task.WaitAsync(_wait);
            renewalEntry = ownership.EnterForRenewalAsync(CancellationToken.None);
            _renewalRequested = Stopwatch.GetTimestamp();
            renewalWaiting.TrySetResult();

            using (await renewalEntry.WaitAsync(_wait))
            {
                _admitted = Stopwatch.GetTimestamp();
                _renewal = await Repository()
                    .Renew(job.Id, OwnerA, job.FencingToken, 30, CancellationToken.None);
            }

            await fenced.WaitAsync(_wait);
            _fencedWrites = await FencedWriteCountAsync();
        }

        [Test]
        public void It_requests_the_renewal_while_the_fence_commit_is_pending()
        {
            _renewalRequested.Should().BeGreaterThan(_commitRequested);
            _renewalRequested.Should().BeLessThan(_commitCompleted);
        }

        [Test]
        public void It_admits_the_renewal_only_after_the_commit_completed()
        {
            _admittedBeforeCommitCompleted.Should().BeFalse();
            _admitted.Should().BeGreaterThan(_commitCompleted);
        }

        [Test]
        public void It_renews_the_lease_after_the_fence() =>
            _renewal.Should().BeOfType<JobWriteResult.Success>();

        [Test]
        public void It_commits_the_fenced_write() => _fencedWrites.Should().Be(1);
    }
}
